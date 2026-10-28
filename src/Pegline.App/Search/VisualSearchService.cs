using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Pegline.App.Platform;
using Pegline.Core.Models;
using Pegline.Core.Search;
using Pegline.Core.Storage;

namespace Pegline.App.Search;

/// <summary>
/// Offline visual search with Apple's MobileCLIP-S2 on CPU ONNX Runtime. Images are indexed one at a time on a
/// low-priority thread that pauses while a full-screen app (a game, a video) is in front; the image model is
/// unloaded when idle and the text model a while after the last search. Nothing leaves the computer.
/// </summary>
internal sealed class VisualSearchService : IVisualSearch, IDisposable
{
    private static readonly TimeSpan ImageIdle = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan TextIdle = TimeSpan.FromMinutes(10);
    private readonly LibraryStore _store;
    private readonly Dispatcher _dispatcher;
    private readonly string _models;
    private readonly string _cacheDirectory;
    private readonly Func<bool> _enabled;
    private readonly Lock _gate = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Dictionary<string, float[]> _queries = [];
    private readonly SemaphoreSlim _textGate = new(1, 1);
    private readonly Thread _worker;
    private readonly Timer _textTimer;
    private readonly HashSet<string> _failed = [];
    private List<(Guid Id, string Key)> _wanted = [];
    private HashSet<string> _live = [];
    private bool _rescan;
    private bool _disposed;
    private VectorCache? _cache;
    private ClipTokenizer? _tokenizer;
    private InferenceSession? _image;
    private InferenceSession? _text;
    private int _readyState; // 0 unknown, 1 ready, -1 unavailable
    private DispatcherOperation? _refresh;

    public VisualSearchService(LibraryStore store, Dispatcher dispatcher, string models, string cacheDirectory, Func<bool> enabled)
    {
        _store = store;
        _dispatcher = dispatcher;
        _models = models;
        _cacheDirectory = cacheDirectory;
        _enabled = enabled;
        _textTimer = new Timer(_ => UnloadText(), null, Timeout.Infinite, Timeout.Infinite);
        _worker = new Thread(Work) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Pegline visual index" };
        _store.Changed += (_, _) => ScheduleRefresh();
    }

    /// <summary>The bundled models are present and intact (false until checked, and in builds without them).</summary>
    public bool IsAvailable => _readyState == 1;

    /// <summary>The models are bundled but failed their integrity check.</summary>
    public bool IsBroken => _readyState == -1 && IsBundled;

    /// <summary>The build carries the model folder at all.</summary>
    public bool IsBundled => File.Exists(Path.Combine(_models, SearchModelConfig.Source));

    /// <summary>Images left to read and the size of the current batch; (0, 0) when idle.</summary>
    public (int Done, int Total) Progress { get; private set; }

    /// <summary>The worker has gone through the latest list of images (development checks wait for it).</summary>
    internal bool Settled { get; private set; }

    /// <summary>Progress or availability changed (UI thread).</summary>
    public event EventHandler? StatusChanged;

    /// <summary>New images became searchable (UI thread, throttled).</summary>
    public event EventHandler? Indexed;

    /// <summary>Indexing waits while a full-screen app is in front (development checks turn this off).</summary>
    internal bool PauseForFullscreen { get; set; } = true;

    public void Start()
    {
        if (_worker.IsAlive || _readyState == -1) return;
        if (!IsBundled)
        {
            _readyState = -1;
            return;
        }
        _worker.Start();
        Refresh();
    }

    private void ScheduleRefresh()
    {
        if (_refresh is { Status: DispatcherOperationStatus.Pending }) return;
        _refresh = _dispatcher.BeginInvoke(Refresh, DispatcherPriority.ApplicationIdle);
    }

    /// <summary>Hands the worker the images that should be searchable; it reads the ones not indexed yet.</summary>
    public void Refresh()
    {
        if (_readyState == -1 || _disposed) return;
        var wanted = new List<(Guid, string)>();
        var live = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in _store.Items)
        {
            if (item.Kind != ItemKind.Image || item.Recording || VectorCache.KeyOf(item.DisplayFile) is not { } key) continue;
            live.Add(key);
            if (!item.IsTrashed) wanted.Add((item.Id, key));
        }
        lock (_gate)
        {
            _wanted = wanted;
            _live = live;
            _rescan = true;
            Settled = false;
        }
        _wake.Set();
    }

    // ----- Indexing -----

    private void Work()
    {
        if (!EnsureReady()) return;
        while (!_disposed)
        {
            var woke = _wake.WaitOne(_image is null ? Timeout.InfiniteTimeSpan : ImageIdle);
            if (_disposed) return;
            if (!woke)
            {
                // Idle: give the image model's memory back.
                lock (_gate)
                {
                    _image?.Dispose();
                    _image = null;
                }
                continue;
            }
            List<(Guid Id, string Key)> wanted;
            HashSet<string> live;
            lock (_gate)
            {
                if (!_rescan) continue;
                _rescan = false;
                wanted = _wanted;
                live = _live;
            }
            var cache = _cache!;
            try
            {
                cache.Prune(live);
            }
            catch (IOException ex)
            {
                App.Log(ex);
            }
            var todo = wanted.Where(w => !_failed.Contains(w.Key) && !cache.Has(w.Key)).DistinctBy(w => w.Key).ToList();
            if (todo.Count == 0)
            {
                lock (_gate) Settled = !_rescan;
                continue;
            }
            var done = 0;
            var lastReport = DateTime.MinValue;
            Report(0, todo.Count);
            foreach (var (id, key) in todo)
            {
                // Never compete with a game or a full-screen video; a newer list of images restarts the batch.
                while (!_disposed && ((PauseForFullscreen && Win32.IsFullscreenForeground()) || !_enabled())) Thread.Sleep(5000);
                if (_disposed) return;
                lock (_gate)
                    if (_rescan) break;
                Index(cache, id, key);
                done++;
                if (DateTime.UtcNow - lastReport > TimeSpan.FromSeconds(1.5) || done == todo.Count)
                {
                    lastReport = DateTime.UtcNow;
                    Report(done, todo.Count);
                    _dispatcher.BeginInvoke(() => Indexed?.Invoke(this, EventArgs.Empty));
                }
            }
            Report(0, 0);
            lock (_gate)
            {
                if (_rescan) _wake.Set();
                else Settled = true;
            }
        }
    }

    private void Index(VectorCache cache, Guid id, string key)
    {
        try
        {
            var path = _store.Media.PathOf(key + ".png");
            if (Decode(path) is not { } image) return;
            var views = new List<float[]>();
            var tensors = VisualRanking.Tensors(image.Pixels, image.Width, image.Height);
            if (tensors.Count > 0)
            {
                InferenceSession session;
                lock (_gate) session = _image ??= Session(SearchModelConfig.ImageModel, threads: 1);
                foreach (var tensor in tensors)
                    if (Infer(session, "image", new DenseTensor<float>(tensor, [1, 3, VisualRanking.Side, VisualRanking.Side])) is { } vector)
                        views.Add(vector);
            }
            // The record may have been edited or deleted while it was read.
            if (_store.Find(id) is not { } item || VectorCache.KeyOf(item.DisplayFile) != key) return;
            cache.Save(key, views);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or ArgumentException or OnnxRuntimeException or InvalidOperationException or FileFormatException)
        {
            App.Log(ex);
            _failed.Add(key);
        }
    }

    /// <summary>The picture reduced to at most 1024 pixels on its longest side, as straight-alpha BGRA.</summary>
    private static (byte[] Pixels, int Width, int Height)? Decode(string path)
    {
        if (!File.Exists(path)) return null;
        var bytes = File.ReadAllBytes(path);
        var (width, height) = MediaStore.ReadPngSize(bytes);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        image.StreamSource = new MemoryStream(bytes);
        if (width >= height && width > VisualRanking.MaxSource) image.DecodePixelWidth = VisualRanking.MaxSource;
        else if (height > width && height > VisualRanking.MaxSource) image.DecodePixelHeight = VisualRanking.MaxSource;
        image.EndInit();
        BitmapSource source = image.Format == PixelFormats.Bgra32 ? image : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        int w = Math.Min(source.PixelWidth, VisualRanking.MaxSource), h = Math.Min(source.PixelHeight, VisualRanking.MaxSource);
        if (w < 1 || h < 1) return null;
        var pixels = new byte[w * h * 4];
        source.CopyPixels(new System.Windows.Int32Rect(0, 0, w, h), pixels, w * 4, 0);
        return (pixels, w, h);
    }

    private void Report(int done, int total) => _dispatcher.BeginInvoke(() =>
    {
        Progress = (done, total);
        StatusChanged?.Invoke(this, EventArgs.Empty);
    });

    // ----- Search -----

    public async Task<IReadOnlyList<Guid>> SearchAsync(string text, IReadOnlyList<Guid> eligible, CancellationToken cancel)
    {
        if (_readyState == -1 || text.Length is 0 or > ClipTokenizer.MaxQueryChars || text.Contains('\0')) return [];
        // Keys are read here, on the UI thread; the ranking runs in the background.
        var entries = new List<(Guid Id, string Key)>();
        foreach (var id in eligible)
            if (_store.Find(id) is { Kind: ItemKind.Image, IsTrashed: false } item && VectorCache.KeyOf(item.DisplayFile) is { } key)
                entries.Add((id, key));
        if (entries.Count == 0) return [];
        IReadOnlyList<Guid> ranked;
        try
        {
            ranked = await Task.Run(() => Rank(text, entries, cancel), cancel);
        }
        catch (Exception ex) when (ex is OnnxRuntimeException or IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
        {
            App.Log(ex);
            return [];
        }
        // A crop, markup or deletion can change an image while it is ranked; never return a stale identity.
        var keys = entries.ToDictionary(e => e.Id, e => e.Key);
        return [.. ranked.Where(id => _store.Find(id) is { IsTrashed: false } item && VectorCache.KeyOf(item.DisplayFile) == keys[id])];
    }

    private async Task<IReadOnlyList<Guid>> Rank(string text, List<(Guid Id, string Key)> entries, CancellationToken cancel)
    {
        if (!EnsureReady()) return [];
        var query = await QueryVector(text, cancel);
        if (query is null) return [];
        var scores = new List<(Guid, double)>();
        foreach (var (id, key) in entries)
        {
            cancel.ThrowIfCancellationRequested();
            if (_cache!.Load(key) is { Count: > 0 } views && VisualRanking.Score(query, views) is { } score) scores.Add((id, score));
        }
        return VisualRanking.Matches(scores);
    }

    private async Task<float[]?> QueryVector(string text, CancellationToken cancel)
    {
        await _textGate.WaitAsync(cancel);
        try
        {
            if (_queries.TryGetValue(text, out var cached)) return cached;
            _text ??= Session(SearchModelConfig.TextModel, threads: Math.Clamp(Environment.ProcessorCount / 2, 1, 4));
            _textTimer.Change(TextIdle, Timeout.InfiniteTimeSpan);
            var vector = Infer(_text, "text", new DenseTensor<long>(_tokenizer!.Tensor(text), [1, ClipTokenizer.Context]));
            if (vector is null) return null;
            if (_queries.Count >= 16) _queries.Remove(_queries.Keys.First());
            _queries[text] = vector;
            return vector;
        }
        finally
        {
            _textGate.Release();
        }
    }

    private void UnloadText()
    {
        if (!_textGate.Wait(0)) return;
        try
        {
            _text?.Dispose();
            _text = null;
        }
        finally
        {
            _textGate.Release();
        }
    }

    // ----- Models -----

    /// <summary>Checks the bundled files once (size and SHA-256 of each) and loads the tokenizer.</summary>
    private bool EnsureReady()
    {
        lock (_gate)
        {
            if (_readyState != 0) return _readyState == 1;
            var ok = false;
            try
            {
                if (SearchModelConfig.Pinned().Verify(_models) is { } model)
                {
                    _tokenizer = ClipTokenizer.FromFiles(Path.Combine(_models, SearchModelConfig.Vocab), Path.Combine(_models, SearchModelConfig.Merges));
                    _cache = new VectorCache(_cacheDirectory, model);
                    ok = true;
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
            {
                App.Log(ex);
            }
            _readyState = ok ? 1 : -1;
        }
        _dispatcher.BeginInvoke(() => StatusChanged?.Invoke(this, EventArgs.Empty));
        return _readyState == 1;
    }

    private InferenceSession Session(string file, int threads)
    {
        using var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            IntraOpNumThreads = threads,
            InterOpNumThreads = 1,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
        };
        return new InferenceSession(Path.Combine(_models, file), options);
    }

    private static float[]? Infer<T>(InferenceSession session, string input, DenseTensor<T> tensor)
    {
        using var results = session.Run([NamedOnnxValue.CreateFromTensor(input, tensor)]);
        var output = results.FirstOrDefault(r => r.Name == "embedding") ?? results.First();
        return VisualRanking.Normalized(output.AsTensor<float>().ToArray());
    }

#if DEBUG
    /// <summary>Development aid: every indexed image's raw score for a description.</summary>
    internal async Task<IReadOnlyList<(Guid Id, double Score)>> ScoresAsync(string text)
    {
        if (!EnsureReady() || await QueryVector(text, CancellationToken.None) is not { } query) return [];
        return [.. _store.Items.Where(i => i.Kind == ItemKind.Image && !i.IsTrashed)
            .Select(i => (i.Id, Views: VectorCache.KeyOf(i.DisplayFile) is { } key ? _cache!.Load(key) : null))
            .Where(v => v.Views is { Count: > 0 })
            .Select(v => (v.Id, VisualRanking.Score(query, v.Views!) ?? double.NaN))
            .OrderByDescending(v => v.Item2)];
    }

    /// <summary>
    /// Development check of the bundled export, as the reference does: a red circle and a blue square, each
    /// described; each description must rank its own picture first. Returns the report.
    /// </summary>
    internal static string SelfCheck(LibraryStore store, string models)
    {
        var service = new VisualSearchService(store, Dispatcher.CurrentDispatcher, models, Path.Combine(Path.GetTempPath(), "pegline-clip-check"), () => true);
        if (!service.EnsureReady()) return "unavailable";
        static (byte[], int, int) Draw(bool circle)
        {
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                context.DrawRectangle(Brushes.White, null, new System.Windows.Rect(0, 0, 256, 256));
                if (circle) context.DrawEllipse(Brushes.Red, null, new System.Windows.Point(128, 128), 80, 80);
                else context.DrawRectangle(Brushes.Blue, null, new System.Windows.Rect(48, 48, 160, 160));
            }
            var bitmap = new RenderTargetBitmap(256, 256, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var pixels = new byte[256 * 256 * 4];
            bitmap.CopyPixels(pixels, 256 * 4, 0);
            return (pixels, 256, 256);
        }
        var image = service.Session(SearchModelConfig.ImageModel, 1);
        var images = new[] { Draw(true), Draw(false) }
            .Select(d => Infer(image, "image", new DenseTensor<float>(VisualRanking.Tensors(d.Item1, d.Item2, d.Item3)[0], [1, 3, 256, 256]))!)
            .ToArray();
        var queries = new[] { "a red circle on a white background", "a blue square on a white background" }
            .Select(q => service.QueryVector(q, CancellationToken.None).GetAwaiter().GetResult()!)
            .ToArray();
        var report = new System.Text.StringBuilder();
        for (var q = 0; q < 2; q++)
            report.AppendLine(string.Join(" ", images.Select(v => VisualRanking.Score(queries[q], [v])!.Value.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture))));
        var passed = VisualRanking.Score(queries[0], [images[0]]) > VisualRanking.Score(queries[0], [images[1]])
            && VisualRanking.Score(queries[1], [images[1]]) > VisualRanking.Score(queries[1], [images[0]]);
        report.AppendLine(passed ? "ranking passed" : "ranking FAILED");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Infer(image, "image", new DenseTensor<float>(VisualRanking.Tensors(Draw(true).Item1, 256, 256)[0], [1, 3, 256, 256]));
        report.AppendLine($"image view: {watch.ElapsedMilliseconds} ms (1 thread)");
        image.Dispose();
        service.Dispose();
        return report.ToString();
    }
#endif

    public void Dispose()
    {
        // The worker may be inside a model run; it stops at the next image and the process frees the rest.
        _disposed = true;
        _wake.Set();
        _textTimer.Dispose();
        UnloadText();
    }
}
