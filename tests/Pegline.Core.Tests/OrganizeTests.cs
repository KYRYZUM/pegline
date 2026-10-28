using Pegline.Core.Models;
using Pegline.Core.Organize;
using Pegline.Core.Storage;
using Pegline.Core.Transfer;

namespace Pegline.Core.Tests;

public sealed class OrganizeTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pegline-tests-" + Guid.NewGuid().ToString("N"));
    private DateTimeOffset _now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private LibraryStore Open() => new(Path.Combine(_directory, "data"), () => _now = _now.AddMilliseconds(1));

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void FolderChoicesNestSubfoldersPutRecentsFirstAndEndWithoutFolder()
    {
        using var store = Open();
        var work = store.CreateFolder("Work");
        var invoices = store.CreateFolder("Invoices", work.Id);
        var home = store.CreateFolder("Home");

        var all = FolderChoices.All(store.Folders, [home.Id, Guid.NewGuid()], "Pin without folder");

        Assert.Equal(["Home", "Work", "Work / Invoices", "Pin without folder"], all.Select(c => c.Title));
        Assert.Equal([0, 0, 1, 0], all.Select(c => c.Depth));
        Assert.Null(all[^1].Id);
        Assert.Equal(invoices.Id, Assert.Single(FolderChoices.Filter(all, "work inv")).Id);
        Assert.Equal(2, FolderChoices.Filter(all, "WORK").Count);
    }

    [Fact]
    public void FolderSearchIgnoresAccentsAndTurkishCase()
    {
        using var store = Open();
        store.CreateFolder("Alışveriş");
        var all = FolderChoices.All(store.Folders, [], "Pin without folder");
        Assert.Equal("Alışveriş", Assert.Single(FolderChoices.Filter(all, "ALISVERIS")).Title);
    }

    [Fact]
    public void SiblingFoldersCannotShareANameButCousinsCan()
    {
        using var store = Open();
        var work = store.CreateFolder("Work");
        var home = store.CreateFolder("Home");
        store.CreateFolder("Notes", work.Id);
        store.CreateFolder("notes", home.Id);

        Assert.Throws<InvalidOperationException>(() => store.CreateFolder("WORK"));
        Assert.Throws<InvalidOperationException>(() => store.CreateFolder("Nötes", work.Id));
        Assert.Throws<InvalidOperationException>(() => store.RenameFolder(home.Id, "work"));
        store.RenameFolder(home.Id, "HOME");
        Assert.Contains(store.Folders, f => f.Name == "HOME");
    }

    [Fact]
    public void TagChoicesOfferCreateOnlyForANewValidName()
    {
        string[] tags = ["Idea", "invoice"];

        Assert.Equal(2, TagChoices.Build(tags, "").Count);
        var typed = TagChoices.Build(tags, "#Inv");
        Assert.Equal([new TagChoice("invoice", false), new TagChoice("Inv", true)], typed);
        Assert.Equal([new TagChoice("Idea", false)], TagChoices.Build(tags, "IDEA"));
        Assert.Empty(TagChoices.Build(tags, new string('x', TagChoices.MaxLength + 1)));
        Assert.All(TagChoices.Build(tags, "   #  "), c => Assert.False(c.IsNew));
    }

    [Fact]
    public void NameAndPinWalksTheFrozenBatchAndSkipsItemsRemovedMeanwhile()
    {
        using var store = Open();
        var a = store.InsertText("alpha");
        var b = store.InsertText("beta");
        var c = store.InsertText("gamma");
        var session = new NameAndPinSession(store, [a.Id, b.Id, a.Id, c.Id]);
        Assert.Equal(3, session.Count);

        Assert.False(session.Confirm("  First  "));
        Assert.Equal(b.Id, session.Current);
        store.MoveToTrash([c.Id]);
        Assert.True(session.Confirm("Second"));

        Assert.Equal("First", store.Find(a.Id)!.Name);
        Assert.True(store.Find(a.Id)!.Pinned);
        Assert.Equal("alpha", store.Find(a.Id)!.Text);
        Assert.Equal("Second", store.Find(b.Id)!.Name);
        Assert.Equal("", store.Find(c.Id)!.Name);
        Assert.False(store.Find(c.Id)!.Pinned);
    }

    [Fact]
    public void NameAndPinRefusesAnItemTrashedWhileItsDraftWasOpen()
    {
        using var store = Open();
        var a = store.InsertText("alpha");
        var session = new NameAndPinSession(store, [a.Id]);
        store.MoveToTrash([a.Id]);

        Assert.Throws<InvalidOperationException>(() => session.Confirm("late"));
        Assert.Equal("", store.Find(a.Id)!.Name);
        Assert.Throws<InvalidOperationException>(() => new NameAndPinSession(store, [a.Id]));
    }

    [Fact]
    public void ExportWritesNamedFilesAndNeverOverwrites()
    {
        using var store = Open();
        var note = store.InsertText("Shopping list\nmilk");
        var link = store.InsertLink("https://example.com/page", "Example: Page?");
        var image = store.InsertImage(TestImages.Png(4, 3));
        store.SetDetails(image.Id, name: "Receipt");
        var files = new ItemFiles(store, Path.Combine(_directory, "transfer"));
        var target = Path.Combine(_directory, "out");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "Shopping list.txt"), "keep me");

        var result = files.Export([note, store.Find(link.Id)!, store.Find(image.Id)!], target);

        Assert.Empty(result.Failed);
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(target, "Shopping list.txt")));
        Assert.Equal("Shopping list\nmilk", File.ReadAllText(Path.Combine(target, "Shopping list (2).txt")));
        Assert.Contains("URL=https://example.com/page", File.ReadAllText(Path.Combine(target, "Example Page.url")));
        Assert.True(File.Exists(Path.Combine(target, "Receipt.png")));
        Assert.True(File.Exists(store.MediaPath(store.Find(image.Id)!)));
    }

    [Fact]
    public void ExportReportsMissingFilesInsteadOfSkippingThemSilently()
    {
        using var store = Open();
        var image = store.InsertImage(TestImages.Png(2, 2));
        File.Delete(store.MediaPath(image));
        var target = Path.Combine(_directory, "out");
        Directory.CreateDirectory(target);

        var result = new ItemFiles(store, Path.Combine(_directory, "transfer")).Export([image], target);

        Assert.Equal([image.Id], result.Failed);
        Assert.Empty(result.Copies);
    }

    [Fact]
    public void DragCopiesCarryTheExportName()
    {
        using var store = Open();
        var note = store.InsertText("draft");
        store.SetDetails(note.Id, name: "Plan: v2");
        var files = new ItemFiles(store, Path.Combine(_directory, "transfer"));

        var path = files.ForDrag(store.Find(note.Id)!);

        Assert.Equal("Plan v2.txt", Path.GetFileName(path));
        Assert.Equal("draft", File.ReadAllText(path));
        files.CleanDragCopies();
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData("CON", "_CON")]
    [InlineData("a<b>c", "a b c")]
    [InlineData("trailing dots...", "trailing dots")]
    [InlineData("  ", "")]
    public void FileNamesAreSafeOnWindows(string input, string expected) => Assert.Equal(expected, ItemFiles.Sanitize(input));

    [Fact]
    public void PasteRefusesPasswordFieldsAndChangedTargets()
    {
        var field = new PasteTarget(10, 4, "42.1", Editable: true, Secure: false);

        Assert.Equal(PasteVerdict.Allowed, PastePolicy.Decide(field, field));
        Assert.Equal(PasteVerdict.NoTarget, PastePolicy.Decide(null, field));
        Assert.Equal(PasteVerdict.Secure, PastePolicy.Decide(field with { Secure = true }, field));
        Assert.Equal(PasteVerdict.Secure, PastePolicy.Decide(field, field with { Secure = true }));
        Assert.Equal(PasteVerdict.Changed, PastePolicy.Decide(field, field with { RuntimeId = "42.2" }));
        Assert.Equal(PasteVerdict.Changed, PastePolicy.Decide(field, field with { Window = 11 }));
        Assert.Equal(PasteVerdict.Changed, PastePolicy.Decide(field, null));
        // When UI Automation cannot describe one side, the window identity decides.
        Assert.Equal(PasteVerdict.Allowed, PastePolicy.Decide(field, field with { RuntimeId = "" }));
        // A non-text target such as a canvas or spreadsheet cell is the app's decision.
        Assert.Equal(PasteVerdict.Allowed, PastePolicy.Decide(field with { Editable = false }, field with { Editable = false }));
    }
}
