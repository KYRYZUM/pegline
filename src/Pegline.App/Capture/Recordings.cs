using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Pegline.Core.Capture;
using Pegline.Core.Models;
using Pegline.Core.Storage;
using Windows.Media.Editing;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace Pegline.App.Capture;

/// <summary>
/// Screen recordings (MP4 / MOV) with Windows' own media editing: import into the managed store, a poster
/// frame and duration, and trimming into a new file. Originals are only read; a trim never replaces them.
/// </summary>
internal static class Recordings
{
    private const int MaxPosterSide = 1920;

    public sealed record Probe(double DurationSeconds, byte[] PosterPng);

    /// <summary>Copies a recording into the library and adds (or moves up) its record. Null when it is not a playable video.</summary>
    public static async Task<ClipItem?> ImportAsync(LibraryStore store, Dispatcher dispatcher, string path)
    {
        try
        {
            var extension = Path.GetExtension(path).Equals(".mov", StringComparison.OrdinalIgnoreCase) ? ".mov" : ".mp4";
            var video = await store.Media.ImportAsync(path, extension, WatchedFiles.MaxVideoBytes).ConfigureAwait(false);
            var probe = await ProbeAsync(store.Media.PathOf(video), TimeSpan.FromSeconds(0.5)).ConfigureAwait(false);
            return await dispatcher.InvokeAsync(() => store.InsertRecording(video, probe.PosterPng, probe.DurationSeconds, path));
        }
        catch (Exception ex) when (IsMediaFailure(ex))
        {
            App.Log(ex);
            return null;
        }
    }

    /// <summary>Duration and a still frame (as PNG) near <paramref name="at"/>.</summary>
    public static async Task<Probe> ProbeAsync(string path, TimeSpan at)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        var clip = await MediaClip.CreateFromFileAsync(file);
        var duration = clip.OriginalDuration;
        if (duration <= TimeSpan.Zero) throw new InvalidDataException("The recording has no length.");
        var encoding = clip.GetVideoEncodingProperties();
        double width = encoding.Width > 0 ? encoding.Width : 1280, height = encoding.Height > 0 ? encoding.Height : 720;
        var scale = Math.Min(1, MaxPosterSide / Math.Max(width, height));
        var composition = new MediaComposition();
        composition.Clips.Add(clip);
        var time = at < duration ? at : TimeSpan.FromTicks(duration.Ticks / 2);
        using var frame = await composition.GetThumbnailAsync(time, (int)(width * scale), (int)(height * scale), VideoFramePrecision.NearestFrame);
        await using var stream = frame.AsStreamForRead();
        var bytes = new MemoryStream();
        await stream.CopyToAsync(bytes);
        bytes.Position = 0;
        var decoded = BitmapDecoder.Create(bytes, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(decoded));
        using var png = new MemoryStream();
        encoder.Save(png);
        return new Probe(duration.TotalSeconds, png.ToArray());
    }

    /// <summary>Writes the part between <paramref name="start"/> and <paramref name="end"/> (seconds) to a new MP4.</summary>
    public static async Task RenderTrimAsync(string source, double start, double end, string output, IProgress<double>? progress, CancellationToken cancel)
    {
        var file = await StorageFile.GetFileFromPathAsync(source);
        var clip = await MediaClip.CreateFromFileAsync(file);
        clip.TrimTimeFromStart = TimeSpan.FromSeconds(start);
        clip.TrimTimeFromEnd = clip.OriginalDuration - TimeSpan.FromSeconds(end);
        var composition = new MediaComposition();
        composition.Clips.Add(clip);
        var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(output)!);
        var target = await folder.CreateFileAsync(Path.GetFileName(output), CreationCollisionOption.ReplaceExisting);
        var operation = composition.RenderToFileAsync(target, MediaTrimmingPreference.Precise);
        if (progress is not null) operation.Progress = (_, value) => progress.Report(value / 100);
        var result = await operation.AsTask(cancel);
        if (result != TranscodeFailureReason.None) throw new IOException("The trimmed recording could not be written: " + result);
    }

    public static bool IsMediaFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException
            or FileFormatException or InvalidOperationException or System.Runtime.InteropServices.COMException;
}
