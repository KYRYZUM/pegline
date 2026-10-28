using System.IO;
using System.Windows.Threading;
using Pegline.Core.Models;
using Pegline.Core.Storage;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Pegline.App.Capture;

/// <summary>
/// Local text recognition with Windows' own OCR (Windows.Media.Ocr). Nothing leaves the device. Turkish is preferred
/// when its recognizer is installed (it reads English too), then English, then the user's profile languages.
/// New images are queued in the background; the recognized text joins shelf and Library search.
/// </summary>
internal sealed class OcrService
{
    private const int QueueLimit = 256;
    private readonly LibraryStore _store;
    private readonly Dispatcher _dispatcher;
    private readonly Queue<Guid> _queue = new();
    private readonly HashSet<Guid> _queued = [];
    private readonly Lock _gate = new();
    private readonly Lazy<OcrEngine?> _engine = new(CreateEngine);
    // One OcrEngine runs one recognition at a time; the background queue and Ctrl+Shift+C take turns.
    private readonly SemaphoreSlim _busy = new(1, 1);
    private bool _running;

    public OcrService(LibraryStore store, Dispatcher dispatcher)
    {
        _store = store;
        _dispatcher = dispatcher;
    }

    /// <summary>True when Windows has an OCR language installed.</summary>
    public bool IsAvailable => _engine.Value is not null;

    public string Language => _engine.Value?.RecognizerLanguage.LanguageTag ?? "";

    private static OcrEngine? CreateEngine()
    {
        try
        {
            foreach (var tag in new[] { "tr-TR", "tr", "en-US", "en-GB", "en" })
            {
                var language = new Language(tag);
                if (OcrEngine.IsLanguageSupported(language) && OcrEngine.TryCreateFromLanguage(language) is { } engine) return engine;
            }
            return OcrEngine.TryCreateFromUserProfileLanguages();
        }
        catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            App.Log(ex);
            return null;
        }
    }

    /// <summary>Queues images whose current pixels have not been recognized yet (newest first).</summary>
    public void Enqueue(IEnumerable<ClipItem> items)
    {
        lock (_gate)
        {
            foreach (var item in items.Where(i => i.NeedsOcr).OrderByDescending(i => i.CreatedAt))
            {
                if (_queued.Count >= QueueLimit) break;
                if (_queued.Add(item.Id)) _queue.Enqueue(item.Id);
            }
            if (_running || _queue.Count == 0) return;
            _running = true;
        }
        _ = Task.Run(DrainAsync);
    }

    public void Enqueue(ClipItem item) => Enqueue([item]);

    /// <summary>The text in an image, recognizing it now when needed. Null when OCR is unavailable or failed.</summary>
    public async Task<string?> TextOfAsync(Guid id)
    {
        if (_store.Find(id) is not { Kind: ItemKind.Image, Recording: false } item) return null;
        if (!item.NeedsOcr) return item.OcrText;
        return await Task.Run(() => RecognizeAsync(item)).ConfigureAwait(true);
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            Guid id;
            lock (_gate)
            {
                if (_queue.Count == 0)
                {
                    _running = false;
                    return;
                }
                id = _queue.Dequeue();
                _queued.Remove(id);
            }
            if (_store.Find(id) is { NeedsOcr: true } item) await RecognizeAsync(item).ConfigureAwait(false);
        }
    }

    private async Task<string?> RecognizeAsync(ClipItem item)
    {
        var source = item.DisplayFile;
        var text = await RecognizeFileAsync(_store.Media.PathOf(source)).ConfigureAwait(false);
        if (text is not null) await _dispatcher.InvokeAsync(() => _store.SetOcrText(item.Id, text, source));
        return text;
    }

    /// <summary>The lines Windows OCR reads in an image file; null when OCR is unavailable or the file is unreadable.</summary>
    public async Task<string?> RecognizeFileAsync(string path)
    {
        if (_engine.Value is not { } engine) return null;
        try
        {
            if (!File.Exists(path)) return null;
            var file = await StorageFile.GetFileFromPathAsync(path);
            using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.Read);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var transform = new BitmapTransform();
            var longest = Math.Max(decoder.OrientedPixelWidth, decoder.OrientedPixelHeight);
            if (longest > OcrEngine.MaxImageDimension)
            {
                var scale = (double)OcrEngine.MaxImageDimension / longest;
                transform.ScaledWidth = (uint)Math.Max(1, decoder.PixelWidth * scale);
                transform.ScaledHeight = (uint)Math.Max(1, decoder.PixelHeight * scale);
                transform.InterpolationMode = BitmapInterpolationMode.Fant;
            }
            using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
            await _busy.WaitAsync().ConfigureAwait(false);
            OcrResult result;
            try
            {
                result = await engine.RecognizeAsync(bitmap);
            }
            finally
            {
                _busy.Release();
            }
            return string.Join("\n", result.Lines.Select(l => l.Text));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            App.Log(ex);
            return null;
        }
    }
}
