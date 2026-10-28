using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Pegline.Core.Capture;

namespace Pegline.App.Capture;

/// <summary>
/// Collects new images saved to the Windows Screenshots folder and up to three chosen folders. Only files that
/// appear while Pegline runs are collected; originals are only read, never moved or deleted.
/// </summary>
internal sealed class FolderWatcher : IDisposable
{
    private static readonly Guid ScreenshotsFolderId = new("b7bede81-df94-4682-a7d8-57a52620b86f");
    private readonly Dispatcher _dispatcher;
    private readonly Action<byte[], string> _collected;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Dictionary<string, CancellationTokenSource> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    /// <param name="collected">Runs on the UI thread with the PNG bytes and the original path.</param>
    public FolderWatcher(Dispatcher dispatcher, Action<byte[], string> collected)
    {
        _dispatcher = dispatcher;
        _collected = collected;
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);

    /// <summary>Where Win+PrtScn and the Snipping Tool save screenshots (it can be relocated, so ask the shell).</summary>
    public static string ScreenshotsFolder
    {
        get
        {
            var id = ScreenshotsFolderId;
            if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var pointer) == 0)
            {
                try
                {
                    if (Marshal.PtrToStringUni(pointer) is { Length: > 0 } path) return path;
                }
                finally
                {
                    Marshal.FreeCoTaskMem(pointer);
                }
            }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
        }
    }

    /// <summary>Replaces the watched set. Missing folders are skipped quietly.</summary>
    public void Configure(IEnumerable<string> folders)
    {
        Stop();
        foreach (var folder in folders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(folder)) continue;
            try
            {
                var watcher = new FileSystemWatcher(folder)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 64 * 1024,
                };
                watcher.Created += (_, e) => Schedule(e.FullPath);
                watcher.Renamed += (_, e) => Schedule(e.FullPath);
                watcher.Changed += (_, e) => Reschedule(e.FullPath);
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
                App.Log(ex);
            }
        }
    }

    public IReadOnlyList<string> Watching => [.. _watchers.Select(w => w.Path)];

    private void Schedule(string path)
    {
        if (!WatchedFiles.IsImage(path)) return;
        CancellationTokenSource token;
        lock (_gate)
        {
            if (_pending.Remove(path, out var previous)) previous.Cancel();
            _pending[path] = token = new CancellationTokenSource();
        }
        _ = CollectAsync(path, token.Token);
    }

    /// <summary>A file still being written restarts its quiet period.</summary>
    private void Reschedule(string path)
    {
        lock (_gate)
            if (!_pending.ContainsKey(path)) return;
        Schedule(path);
    }

    private async Task CollectAsync(string path, CancellationToken cancel)
    {
        try
        {
            await Task.Delay(700, cancel).ConfigureAwait(false);
            for (var attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    var png = ReadAsPng(path);
                    if (png is null) return;
                    lock (_gate) _pending.Remove(path);
                    await _dispatcher.InvokeAsync(() => _collected(png, path));
                    return;
                }
                catch (IOException)
                {
                    // Still locked by the app writing it.
                    await Task.Delay(500, cancel).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or NotSupportedException or FileFormatException or InvalidOperationException or ArgumentException)
        {
            App.Log(ex);
        }
    }

    /// <summary>The image as PNG (other formats are decoded and re-encoded), or null when it is not a usable image.</summary>
    private static byte[]? ReadAsPng(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0 || info.Length > WatchedFiles.MaxImageBytes || info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return null;
        var bytes = File.ReadAllBytes(path);
        if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                Pegline.Core.Storage.MediaStore.ReadPngSize(bytes);
                return bytes;
            }
            catch (InvalidDataException)
            {
                // Mislabeled; fall through to a full decode.
            }
        }
        using var input = new MemoryStream(bytes);
        var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        if ((long)frame.PixelWidth * frame.PixelHeight > Pegline.Core.Storage.MediaStore.MaxPixels) return null;
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(frame));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    private void Stop()
    {
        foreach (var watcher in _watchers) watcher.Dispose();
        _watchers.Clear();
        lock (_gate)
        {
            foreach (var token in _pending.Values) token.Cancel();
            _pending.Clear();
        }
    }

    public void Dispose() => Stop();
}
