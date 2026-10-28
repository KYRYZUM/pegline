using Pegline.Core.Capture;
using Pegline.Core.Models;
using Pegline.Core.Storage;
using Pegline.Core.Transfer;

namespace Pegline.Core.Tests;

public sealed class RecordingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pegline-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private static string FakeVideo(LibraryStore store, byte seed) => store.Media.Put([.. Enumerable.Repeat(seed, 4096)], ".mp4");

    [Theory]
    [InlineData(@"C:\Videos\Screen Recordings\Recording 2026-10-28.mp4", true)]
    [InlineData(@"C:\Videos\clip.MOV", true)]
    [InlineData(@"C:\Videos\clip.m4v", true)]
    [InlineData(@"C:\Videos\.partial.mp4", false)]
    [InlineData(@"C:\Videos\song.mp3", false)]
    [InlineData(@"C:\Videos\clip.mkv", false)]
    public void RecordingsAreMp4AndMovFiles(string path, bool video) => Assert.Equal(video, WatchedFiles.IsVideo(path));

    [Fact]
    public void ARecordingIsAnImageShelfItemWithItsPosterAndLength()
    {
        using var store = new LibraryStore(_directory);
        var video = FakeVideo(store, 1);
        var item = store.InsertRecording(video, TestImages.Png(16, 9), 12.5, @"C:\Videos\a.mp4");
        Assert.Equal((ItemKind.Image, true, 16, 9, 12.5), (item.Kind, item.Recording, item.PixelWidth, item.PixelHeight, item.ShownDuration));
        Assert.False(item.NeedsOcr); // recordings are not read as pictures

        // The same video again moves its record up instead of adding a copy.
        var again = store.InsertRecording(video, TestImages.Png(16, 9), 12.5);
        Assert.Equal(item.Id, again.Id);
        Assert.Single(store.Items);
        Assert.Throws<ArgumentException>(() => store.InsertRecording(video, TestImages.Png(16, 9), 0));
    }

    [Fact]
    public void TrimmingKeepsTheOriginalAndCanBeUndone()
    {
        using var store = new LibraryStore(_directory);
        var original = FakeVideo(store, 1);
        var item = store.InsertRecording(original, TestImages.Png(16, 9), 20);
        var trimmed = FakeVideo(store, 2);

        store.SetRecordingTrim(item.Id, trimmed, TestImages.Png(16, 9, shade: 5), 3, 8);
        var cut = store.Find(item.Id)!;
        Assert.Equal((original, trimmed, trimmed, 5.0), (cut.VideoFile, cut.RenderedVideoFile, cut.CurrentVideoFile, cut.ShownDuration));
        Assert.NotEqual(cut.ImageFile, cut.DisplayFile); // poster follows the trimmed start
        Assert.True(File.Exists(store.Media.PathOf(original)));

        store.SetRecordingTrim(item.Id, null, null, null, null);
        var whole = store.Find(item.Id)!;
        Assert.Equal((original, 20.0, (double?)null), (whole.CurrentVideoFile, whole.ShownDuration, whole.TrimStartSeconds));
        Assert.Throws<InvalidOperationException>(() => store.SetRecordingTrim(store.InsertText("x").Id, null, null, null, null));
    }

    [Fact]
    public void RecordingsTravelAsNamedVideoFiles()
    {
        using var store = new LibraryStore(_directory);
        var item = store.InsertRecording(FakeVideo(store, 3), TestImages.Png(16, 9), 4, @"C:\Videos\Demo take.mov");
        var files = new ItemFiles(store, Path.Combine(_directory, "transfer"));
        var file = files.Materialize(item);
        Assert.Equal("Demo take.mp4", file.Name);
        Assert.EndsWith(".mp4", file.Path);
        Assert.StartsWith("Recording ", ItemFiles.ExportName(store.InsertRecording(FakeVideo(store, 4), TestImages.Png(16, 9), 4), ".mp4"));
    }

    [Fact]
    public void FreshlyStoredMediaSurvivesCleanupUntilItIsAttached()
    {
        using var store = new LibraryStore(_directory);
        var loose = store.Media.PutPng(TestImages.Png(3, 3));
        Assert.Equal(0, store.Media.DeleteUnreferenced(new HashSet<string>()));
        Assert.True(File.Exists(store.Media.PathOf(loose)));
        store.Media.CleanupGrace = TimeSpan.Zero;
        Assert.Equal(1, store.Media.DeleteUnreferenced(new HashSet<string>()));
    }
}
