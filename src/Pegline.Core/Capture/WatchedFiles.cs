namespace Pegline.Core.Capture;

/// <summary>Which files a watched folder collects, and how a saved screenshot is matched to its clipboard copy.</summary>
public static class WatchedFiles
{
    public const long MaxImageBytes = 64L * 1024 * 1024;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif", ".tif", ".tiff" };

    /// <summary>Screen recordings (Snipping Tool, OBS, Xbox Game Bar, QuickTime exports).</summary>
    public const long MaxVideoBytes = 2L * 1024 * 1024 * 1024;

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mov", ".m4v" };

    public static bool IsVideo(string path) =>
        VideoExtensions.Contains(Path.GetExtension(path))
        && !Path.GetFileName(path).StartsWith('.')
        && !Path.GetFileName(path).StartsWith("~$", StringComparison.Ordinal);

    public static bool IsMedia(string path) => IsImage(path) || IsVideo(path);

    public static bool IsImage(string path) =>
        ImageExtensions.Contains(Path.GetExtension(path))
        && !Path.GetFileName(path).StartsWith('.')
        && !Path.GetFileName(path).StartsWith("~$", StringComparison.Ordinal);
}

/// <summary>
/// Windows often delivers one screenshot twice: Win+Shift+S / Win+PrtScn put it on the clipboard and also save
/// it to the Screenshots folder. The two encodings differ byte for byte, so a file that arrives shortly after a
/// clipboard image with the same pixel size is treated as that image (its path is attached instead of a duplicate).
/// </summary>
public sealed class ScreenshotPairing(TimeSpan window)
{
    private readonly List<(Guid Id, int Width, int Height, DateTimeOffset At)> _recent = [];

    public ScreenshotPairing() : this(TimeSpan.FromSeconds(15))
    {
    }

    public void ClipboardImage(Guid id, int width, int height, DateTimeOffset at)
    {
        _recent.RemoveAll(r => at - r.At > window);
        _recent.Add((id, width, height, at));
    }

    /// <summary>The clipboard image this file most likely is, consuming the match; null for a new image.</summary>
    public Guid? MatchFile(int width, int height, DateTimeOffset at)
    {
        _recent.RemoveAll(r => at - r.At > window);
        var index = _recent.FindLastIndex(r => r.Width == width && r.Height == height);
        if (index < 0) return null;
        var id = _recent[index].Id;
        _recent.RemoveAt(index);
        return id;
    }
}
