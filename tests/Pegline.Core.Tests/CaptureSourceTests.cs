using Pegline.Core.Capture;
using Pegline.Core.Library;
using Pegline.Core.Models;
using Pegline.Core.Storage;

namespace Pegline.Core.Tests;

public sealed class CaptureSourceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pegline-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    [Theory]
    [InlineData(@"C:\Shots\Screenshot 2026-10-28 140501.png", true)]
    [InlineData(@"C:\Shots\photo.JPEG", true)]
    [InlineData(@"C:\Shots\clip.webp", true)]
    [InlineData(@"C:\Shots\notes.txt", false)]
    [InlineData(@"C:\Shots\.hidden.png", false)]
    [InlineData(@"C:\Shots\~$temp.png", false)]
    [InlineData(@"C:\Shots\recording.mp4", false)]
    public void WatchedFoldersCollectOnlyImages(string path, bool collected) => Assert.Equal(collected, WatchedFiles.IsImage(path));

    [Fact]
    public void ACopiedPictureWinsOverTheAddressOrCaptionNextToIt()
    {
        // Browser "Copy image": picture + HTML (+ sometimes its address as text).
        Assert.True(ClipboardAdmission.PrefersImage(true, null, ClipboardAdmission.IsDocumentCopy(["HTML Format", "PNG"])));
        Assert.True(ClipboardAdmission.PrefersImage(true, "https://example.com/cat.png", false));
        Assert.True(ClipboardAdmission.PrefersImage(true, "Look at this", false));
        // Excel/Word cells come with a bitmap rendering; the text is what was copied.
        Assert.False(ClipboardAdmission.PrefersImage(true, "Q3\t42", ClipboardAdmission.IsDocumentCopy(["Rich Text Format", "Csv", "XML Spreadsheet"])));
        // A picture copied out of a document has no text of its own.
        Assert.True(ClipboardAdmission.PrefersImage(true, null, true));
        Assert.False(ClipboardAdmission.PrefersImage(false, "text", false));
    }

    [Fact]
    public void OnlyImageFilesAreTakenFromCopiedFiles()
    {
        var copied = ClipboardAdmission.CopiedImages([@"C:\a\photo.jpg", @"C:\a\notes.docx", @"C:\a\PHOTO.JPG", @"C:\a\shot.png"]);
        Assert.Equal([@"C:\a\photo.jpg", @"C:\a\shot.png"], copied);
        Assert.Equal(ClipboardAdmission.MaxCopiedFiles, ClipboardAdmission.CopiedImages(Enumerable.Range(0, 50).Select(i => $@"C:\a\{i}.png")).Count);
    }

    [Fact]
    public void AScreenshotFileIsPairedWithItsRecentClipboardCopyOnce()
    {
        var now = new DateTimeOffset(2026, 10, 28, 14, 0, 0, TimeSpan.Zero);
        var pairing = new ScreenshotPairing(TimeSpan.FromSeconds(15));
        var shot = Guid.NewGuid();
        pairing.ClipboardImage(shot, 1920, 1080, now);

        Assert.Null(pairing.MatchFile(800, 600, now.AddSeconds(1)));
        Assert.Equal(shot, pairing.MatchFile(1920, 1080, now.AddSeconds(2)));
        Assert.Null(pairing.MatchFile(1920, 1080, now.AddSeconds(3))); // consumed

        pairing.ClipboardImage(Guid.NewGuid(), 1920, 1080, now);
        Assert.Null(pairing.MatchFile(1920, 1080, now.AddSeconds(30))); // too late: a new screenshot
    }

    [Fact]
    public void RecognizedTextIsIgnoredWhenTheImageChangedMeanwhile()
    {
        using var store = new LibraryStore(_directory);
        var image = store.InsertImage(TestImages.Png(6, 4));
        Assert.True(image.NeedsOcr);
        var source = image.DisplayFile;

        store.SaveImageEdit(image.Id, TestImages.Png(3, 4, shade: 7), "[]", new CropRect(0, 0, 0.5, 1));
        Assert.False(store.SetOcrText(image.Id, "stale", source));
        Assert.Equal("", store.Find(image.Id)!.OcrText);

        var edited = store.Find(image.Id)!;
        Assert.True(store.SetOcrText(image.Id, "  Invoice 42  ", edited.DisplayFile));
        Assert.Equal("Invoice 42", store.Find(image.Id)!.OcrText);
        Assert.False(store.Find(image.Id)!.NeedsOcr);

        // A later edit makes the text stale again.
        store.SaveImageEdit(image.Id, null, "[]", null);
        Assert.True(store.Find(image.Id)!.NeedsOcr);
        Assert.Equal("", store.Find(image.Id)!.OcrText);
    }

    [Fact]
    public void RecognizedTextBecomesSearchableWithoutAnotherEdit()
    {
        using var store = new LibraryStore(_directory);
        var image = store.InsertImage(TestImages.Png(6, 4));
        store.Retain([image.Id]);
        var engine = new LibraryQueryEngine();
        var query = new LibraryQuery { Search = "fatura" };
        Assert.Empty(engine.Run(store.Items, store.Folders, query, DateTimeOffset.Now));

        store.SetOcrText(image.Id, "Fatura No 42", store.Find(image.Id)!.DisplayFile);

        Assert.Equal([image.Id], engine.Run(store.Items, store.Folders, query, DateTimeOffset.Now));
    }

    [Fact]
    public void AScreenshotFileAttachesItsPathOnlyOnce()
    {
        using var store = new LibraryStore(_directory);
        var image = store.InsertImage(TestImages.Png(6, 4));
        store.AttachSource(image.Id, @"C:\Shots\a.png");
        store.AttachSource(image.Id, @"C:\Shots\b.png");
        Assert.Equal(@"C:\Shots\a.png", store.Find(image.Id)!.SourcePath);
    }
}
