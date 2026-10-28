using Pegline.Core.Models;
using Pegline.Core.Storage;

namespace Pegline.Core.Tests;

public sealed class LibraryStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pegline-tests-" + Guid.NewGuid().ToString("N"));
    private DateTimeOffset _now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private LibraryStore Open() => new(_directory, () => _now = _now.AddMilliseconds(1));

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void RecopiedEquivalentTextRefreshesOneEntryAndKeepsTheNewBody()
    {
        using var store = Open();
        var first = store.InsertText("café\nsecond line");
        store.TogglePin([first.Id]);
        var second = store.InsertText("\r\ncafé\r\nsecond line\r\n");

        Assert.Equal(first.Id, second.Id);
        Assert.Single(store.Items);
        Assert.Equal("\r\ncafé\r\nsecond line\r\n", second.Text);
        Assert.True(second.Pinned);
        Assert.NotEqual(first.Id, store.InsertText("Café\nsecond line").Id);
        Assert.Equal(2, store.Items.Count);
    }

    [Fact]
    public void EditTextRejectsBlankContentAndPersists()
    {
        using (var store = Open())
        {
            var item = store.InsertText("draft");
            Assert.False(store.EditText(item.Id, "   \n"));
            Assert.True(store.EditText(item.Id, "final"));
            Assert.False(store.EditText(store.InsertLink("https://example.com").Id, "x"));
        }
        using var reopened = Open();
        Assert.Contains(reopened.Items, i => i.Text == "final");
    }

    [Fact]
    public void RetainKeepsItemsPastRetention()
    {
        using var store = Open();
        var item = store.InsertText("keep me");
        store.Retain([item.Id]);
        Assert.True(store.Find(item.Id)!.IsSaved);
        _now = _now.AddDays(60);
        store.Expire();
        Assert.False(store.Find(item.Id)!.IsTrashed);
    }

    [Fact]
    public void LinksAndImagesDeduplicateByAddressAndContent()
    {
        using var store = Open();
        var link = store.InsertLink("https://example.com");
        Assert.Equal(link.Id, store.InsertLink("https://example.com/").Id);
        var png = TestImages.Png(4, 3);
        var image = store.InsertImage(png);
        Assert.Equal(image.Id, store.InsertImage(png).Id);
        Assert.Equal((4, 3), (image.PixelWidth, image.PixelHeight));
        Assert.Equal(2, store.Items.Count);
        Assert.True(File.Exists(store.MediaPath(image)));
    }

    [Fact]
    public void StateSurvivesReopening()
    {
        Guid id;
        Guid folderId;
        using (var store = Open())
        {
            var item = store.InsertText("persist me");
            folderId = store.CreateFolder("Work").Id;
            store.AddToFolder([item.Id], folderId);
            store.AddTags([item.Id], ["alpha", "beta"]);
            store.NameAndPin(item.Id, "Named");
            id = item.Id;
        }
        using (var store = Open())
        {
            var item = Assert.Single(store.Items);
            Assert.Equal(id, item.Id);
            Assert.Equal("Named", item.Name);
            Assert.True(item.Pinned);
            Assert.Equal([folderId], item.FolderIds);
            Assert.Equal(["alpha", "beta"], item.Tags);
            Assert.Equal("Work", Assert.Single(store.Folders).Name);
        }
    }

    [Fact]
    public void TakeDownKeepsSavedRecordsAndUndoRestoresTheLatestBatch()
    {
        using var store = Open();
        var saved = store.InsertText("saved");
        store.TogglePin([saved.Id]);
        var temporary = store.InsertText("temporary");

        store.TakeDown([saved.Id, temporary.Id]);
        Assert.True(store.Find(saved.Id)!.HiddenFromShelf);
        Assert.False(store.Find(saved.Id)!.IsTrashed);
        Assert.True(store.Find(temporary.Id)!.IsTrashed);

        Assert.Equal(2, store.UndoTakeDown().Count);
        Assert.True(store.Find(saved.Id)!.IsOnShelf);
        Assert.True(store.Find(temporary.Id)!.IsOnShelf);
        Assert.False(store.CanUndoTakeDown);
    }

    [Fact]
    public void TakeDownUndoIsBoundedToThirtyTwoBatches()
    {
        using var store = Open();
        for (var i = 0; i < 40; i++)
            store.TakeDown([store.InsertText("item " + i).Id]);
        var restored = 0;
        while (store.UndoTakeDown().Count > 0) restored++;
        Assert.Equal(LibraryStore.MaxTakeDownUndo, restored);
    }

    [Fact]
    public void TrashUndoIgnoresItemsChangedAfterward()
    {
        using var store = Open();
        var a = store.InsertText("a");
        var b = store.InsertText("b");
        store.MoveToTrash([a.Id, b.Id]);
        store.Restore([b.Id]);
        Assert.Equal([a.Id], store.UndoTrash());
        Assert.False(store.CanUndoTrash);
    }

    [Fact]
    public void ExpiryTrashesOnlyUnsavedRecordsPastTheirKindsRetention()
    {
        using var store = Open();
        store.RetentionDays = new Dictionary<ItemKind, int> { [ItemKind.Text] = 1, [ItemKind.Link] = 0, [ItemKind.Image] = 30 };
        var old = store.InsertText("old");
        var kept = store.InsertText("kept");
        store.AddTags([kept.Id], ["keep"]);
        var link = store.InsertLink("https://example.com/forever");
        _now = _now.AddDays(2);
        store.Expire();
        Assert.True(store.Find(old.Id)!.IsTrashed);
        Assert.False(store.Find(kept.Id)!.IsTrashed);
        Assert.False(store.Find(link.Id)!.IsTrashed);
    }

    [Fact]
    public void PermanentDeletionRemovesUnreferencedMedia()
    {
        using var store = Open();
        store.Media.CleanupGrace = TimeSpan.Zero;
        var image = store.InsertImage(TestImages.Png(2, 2));
        var path = store.MediaPath(image);
        Assert.Throws<InvalidOperationException>(() => store.DeletePermanently([image.Id]));
        store.MoveToTrash([image.Id]);
        store.DeletePermanently([image.Id]);
        Assert.Empty(store.Items);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void RemovingAFolderKeepsItsMembersSaved()
    {
        using var store = Open();
        var parent = store.CreateFolder("Parent");
        var child = store.CreateFolder("Child", parent.Id);
        Assert.Throws<InvalidOperationException>(() => store.CreateFolder("Grandchild", child.Id));
        var item = store.InsertText("member");
        store.AddToFolder([item.Id], child.Id, pin: false);
        store.RemoveFolder(parent.Id);
        Assert.Empty(store.Folders);
        Assert.Empty(store.Find(item.Id)!.FolderIds);
        Assert.True(store.Find(item.Id)!.IsSaved);
    }

    [Fact]
    public void TagRenameRejectsFoldedDuplicates()
    {
        using var store = Open();
        var item = store.InsertText("tagged");
        store.AddTags([item.Id], ["Café", "work"]);
        Assert.Throws<InvalidOperationException>(() => store.RenameTag("work", "cafe"));
        store.RenameTag("work", "office");
        Assert.Equal(["Café", "office"], store.Find(item.Id)!.Tags);
    }

    /// <summary>
    /// A history with one unsaved copy of each kind next to records the user kept in every way there is (pinned once,
    /// pinned, filed, tagged) and one already in Trash. "Clear unsaved copies" may only ever take the unsaved three.
    /// </summary>
    private sealed class History
    {
        public History(LibraryStore store)
        {
            Store = store;
            var folder = store.CreateFolder("Kept");
            Photo = store.InsertImage(TestImages.Png(3, 3, 40)).Id;
            Note = store.InsertText("loose note").Id;
            Link = store.InsertLink("https://example.org/loose").Id;
            var once = store.InsertText("pinned for a while");
            store.TogglePin([once.Id], true);
            store.TogglePin([once.Id], false);
            OncePinned = once.Id;
            PinnedPhoto = store.InsertImage(TestImages.Png(4, 3, 90)).Id;
            store.TogglePin([PinnedPhoto], true);
            var filed = store.InsertLink("https://example.org/filed");
            store.AddToFolder([filed.Id], folder.Id, pin: false);
            Filed = filed.Id;
            var tagged = store.InsertText("tagged note");
            store.AddTags([tagged.Id], ["kept"]);
            Tagged = tagged.Id;
            var binned = store.InsertLink("https://example.org/binned");
            store.MoveToTrash([binned.Id]);
            Binned = binned.Id;
        }

        public LibraryStore Store { get; }
        public Guid Photo { get; }
        public Guid Note { get; }
        public Guid Link { get; }
        public Guid OncePinned { get; }
        public Guid PinnedPhoto { get; }
        public Guid Filed { get; }
        public Guid Tagged { get; }
        public Guid Binned { get; }

        public void AssertKeptRecordsUntouched()
        {
            foreach (var id in new[] { OncePinned, PinnedPhoto, Filed, Tagged })
                Assert.False(Store.Find(id)!.IsTrashed);
            Assert.True(Store.Find(Binned)!.IsTrashed);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData(ItemKind.Image)]
    [InlineData(ItemKind.Text)]
    [InlineData(ItemKind.Link)]
    public void ClearingUnsavedCopiesTakesOnlyTheChosenKind(ItemKind? kind)
    {
        using var store = Open();
        var history = new History(store);
        var unsaved = new Dictionary<ItemKind, Guid> { [ItemKind.Image] = history.Photo, [ItemKind.Text] = history.Note, [ItemKind.Link] = history.Link };
        var expected = unsaved.Where(p => kind is null || p.Key == kind).Select(p => p.Value).Order().ToList();

        var snapshot = store.SnapshotUnsavedHistory(kind);
        Assert.Equal(expected, snapshot.Entries.Select(e => e.Id).Order());
        var trashed = store.ClearUnsavedHistory(snapshot);

        Assert.Equal(expected, trashed.Order());
        history.AssertKeptRecordsUntouched();

        // It is one Trash transaction, and restoring it puts the same records back on the shelves.
        store.Restore(trashed);
        Assert.All(trashed, id => Assert.True(store.Find(id)!.IsOnShelf));
    }

    [Fact]
    public void ARecordKeptWhileConfirmingAndALaterCopyAreSpared()
    {
        using var store = Open();
        var history = new History(store);
        var snapshot = store.SnapshotUnsavedHistory(null);

        store.TogglePin([history.Note], true);
        var later = store.InsertText("copied after the question");
        var trashed = store.ClearUnsavedHistory(snapshot);

        Assert.Equal(new[] { history.Photo, history.Link }.Order(), trashed.Order());
        Assert.False(store.Find(later.Id)!.IsTrashed);
        Assert.False(store.Find(history.Note)!.IsTrashed);
        history.AssertKeptRecordsUntouched();
    }

    [Fact]
    public void CopyingTheSameContentAgainWhileConfirmingSparesIt()
    {
        using var store = Open();
        var history = new History(store);
        var snapshot = store.SnapshotUnsavedHistory(null);

        // A recopy reuses the record, which makes it newer than the question that was asked.
        store.InsertText("loose note");
        store.InsertLink("https://example.org/loose");
        var trashed = store.ClearUnsavedHistory(snapshot);

        Assert.Equal([history.Photo], trashed);
        history.AssertKeptRecordsUntouched();
    }

    [Fact]
    public void AskingWithoutConfirmingChangesNothing()
    {
        using var store = Open();
        var history = new History(store);
        var before = store.Items.Select(i => (i.Id, i.TrashedAt, i.UpdatedAt)).ToList();
        _ = store.SnapshotUnsavedHistory(null);
        Assert.Equal(before, store.Items.Select(i => (i.Id, i.TrashedAt, i.UpdatedAt)).ToList());
        history.AssertKeptRecordsUntouched();
    }
}
