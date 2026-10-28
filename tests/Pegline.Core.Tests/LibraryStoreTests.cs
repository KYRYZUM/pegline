using System.Text.Json;
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

    public static TheoryData<string> CleanupScenarios()
    {
        var data = new TheoryData<string>();
        foreach (var s in CleanupContract.RootElement.GetProperty("scenarios").EnumerateArray()) data.Add(s.GetProperty("id").GetString()!);
        return data;
    }

    private static readonly JsonDocument CleanupContract = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Contracts", "history-cleanup.json")));

    [Theory]
    [MemberData(nameof(CleanupScenarios))]
    public void HistoryCleanupContract(string id)
    {
        using var store = Open();
        var root = CleanupContract.RootElement;
        var scenario = root.GetProperty("scenarios").EnumerateArray().Single(s => s.GetProperty("id").GetString() == id);
        var records = new Dictionary<string, Guid>();
        var folder = store.CreateFolder("Saved");
        foreach (var record in root.GetProperty("records").EnumerateArray())
        {
            var name = record.GetProperty("id").GetString()!;
            var item = Insert(store, record.GetProperty("kind").GetString()!, name);
            records[name] = item.Id;
            switch (record.TryGetProperty("save", out var save) ? save.GetString() : null)
            {
                case "unpin": store.TogglePin([item.Id], true); store.TogglePin([item.Id], false); break;
                case "pin": store.TogglePin([item.Id], true); break;
                case "folder": store.AddToFolder([item.Id], folder.Id, pin: false); break;
                case "tag": store.AddTags([item.Id], ["kept"]); break;
                case "trash": store.MoveToTrash([item.Id]); break;
            }
        }
        var kind = scenario.GetProperty("kind").GetString() switch
        {
            "image" => ItemKind.Image,
            "text" => ItemKind.Text,
            "link" => (ItemKind?)ItemKind.Link,
            _ => null,
        };
        var snapshot = store.SnapshotUnsavedHistory(kind);
        Assert.Equal(Names(scenario, "captured").Select(n => records[n]).Order(), snapshot.Entries.Select(e => e.Id).Order());

        foreach (var name in Names(scenario, "retainAfterSnapshot")) store.TogglePin([records[name]], true);
        foreach (var name in Names(scenario, "recopyAfterSnapshot")) Insert(store, name.Split('-')[1], name);
        var later = scenario.TryGetProperty("newCopyAfterSnapshot", out var n) && n.GetBoolean() ? store.InsertText("later copy") : null;
        var cancel = scenario.TryGetProperty("cancel", out var c) && c.GetBoolean();
        var before = store.Items.Select(i => (i.Id, i.TrashedAt)).ToList();

        var trashed = cancel ? [] : store.ClearUnsavedHistory(snapshot);

        Assert.Equal(Names(scenario, "trashed").Select(x => records[x]).Order(), trashed.Order());
        if (cancel) Assert.Equal(before, store.Items.Select(i => (i.Id, i.TrashedAt)).ToList());
        if (later is not null) Assert.False(store.Find(later.Id)!.IsTrashed);
        Assert.False(store.Find(records["pinned-image"])!.IsTrashed);
        Assert.False(store.Find(records["saved-once"])!.IsTrashed);

        // One recoverable transaction.
        if (trashed.Count > 0)
        {
            store.Restore(trashed);
            Assert.All(trashed, x => Assert.True(store.Find(x)!.IsOnShelf));
        }
    }

    private static ClipItem Insert(LibraryStore store, string kind, string name) => kind switch
    {
        "image" => store.InsertImage(TestImages.Png(name.Length + 1, 2)),
        "link" => store.InsertLink("https://example.com/" + name),
        _ => store.InsertText("text " + name),
    };

    private static IEnumerable<string> Names(JsonElement scenario, string property) =>
        scenario.TryGetProperty(property, out var value) ? value.EnumerateArray().Select(e => e.GetString()!) : [];
}
