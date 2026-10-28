using Pegline.Core.Input;
using Pegline.Core.Library;
using Pegline.Core.Models;
using Pegline.Core.Storage;

namespace Pegline.Core.Tests;

/// <summary>The Library's keyboard, selection, search hand-off and recoverable Trash, run on a real <see cref="LibraryStore"/>.</summary>
public sealed class LibraryKeyboardTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pegline-library-" + Guid.NewGuid().ToString("N"));
    private DateTimeOffset _now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    /// <summary>
    /// Eleven saved records, newest first: photos "a", notes "b", bookmarks "c". a1 and b2 are pinned, a2 is off the
    /// shelves; the three "Apple …" titles are what the search tests look for.
    /// </summary>
    private static readonly (string Name, ItemKind Kind, string Title, bool Pinned, bool OnShelf)[] Records =
    [
        ("a1", ItemKind.Image, "Apple photo", true, true),
        ("b1", ItemKind.Text, "Apple notes", false, true),
        ("c1", ItemKind.Link, "Bookmark one", false, true),
        ("a2", ItemKind.Image, "Photo two", false, false),
        ("b2", ItemKind.Text, "Second note", true, true),
        ("c2", ItemKind.Link, "Apple store", false, true),
        ("a3", ItemKind.Image, "Photo three", false, true),
        ("b3", ItemKind.Text, "Third note", false, true),
        ("c3", ItemKind.Link, "Bookmark three", false, true),
        ("a4", ItemKind.Image, "Photo four", false, true),
        ("b4", ItemKind.Text, "Fourth note", false, true),
    ];

    private sealed class Library(LibraryStore store, LibraryModel model, Dictionary<string, Guid> ids)
    {
        private readonly Dictionary<Guid, string> _names = ids.ToDictionary(p => p.Value, p => p.Key);

        public LibraryStore Store => store;

        public LibraryModel Model => model;

        public Guid this[string name] => ids[name];

        public string? Focused => model.Focused is { } id ? _names[id] : null;

        public string Selected => string.Join(",", model.Selected.OrderBy(model.IndexOf).Select(id => _names[id]));

        public string Shown => string.Join(",", model.Ids.Select(id => _names[id]));

        public string Trashed => string.Join(",", store.Items.Where(i => i.IsTrashed).Select(i => _names[i.Id]).Order());

        public string Pinned => string.Join(",", store.Items.Where(i => i.Pinned).Select(i => _names[i.Id]).Order());

        public void Key(string gesture, bool editing = false) => model.HandleKey(Keymap.Canonical(gesture), editing);

        /// <summary>Asserts the focused record and, by default, that it alone is selected.</summary>
        public void Expect(string? focused, string? selected = null)
        {
            Assert.Equal(focused, Focused);
            Assert.Equal(selected ?? focused ?? "", Selected);
        }
    }

    private Library Open(int columns)
    {
        var store = new LibraryStore(_directory, () => _now = _now.AddMilliseconds(1));
        var ids = new Dictionary<string, Guid>();
        var shade = 0;
        foreach (var (name, kind, title, pinned, onShelf) in Enumerable.Reverse(Records))
        {
            var item = kind switch
            {
                ItemKind.Image => store.InsertImage(TestImages.Png(5 + shade, 3, (byte)(18 * ++shade))),
                ItemKind.Text => store.InsertText("body of " + name),
                _ => store.InsertLink("https://example.org/" + name),
            };
            store.SetDetails(item.Id, name: title, note: kind == ItemKind.Text ? null : "body of " + name);
            store.Retain([item.Id]);
            if (pinned) store.TogglePin([item.Id], true);
            if (!onShelf) store.TakeDown([item.Id]);
            ids[name] = item.Id;
        }
        var model = new LibraryModel(store, () => _now) { Columns = columns };
        model.Open();
        return new Library(store, model, ids);
    }

    private const string Everything = "a1,b1,c1,a2,b2,c2,a3,b3,c3,a4,b4";

    [Fact]
    public void GridArrowsMoveByCellAndRowRangesAndPages()
    {
        var library = Open(columns: 4);
        using var _ = library.Store;
        library.Expect("a1");
        library.Key("ArrowRight");
        library.Expect("b1");
        library.Key("ArrowDown");
        library.Expect("c2");
        library.Key("Shift+ArrowDown");
        library.Expect("a4", "c2,a3,b3,c3,a4");

        // A plain arrow leaves the range for the neighbouring card.
        library.Key("ArrowLeft");
        library.Expect("c3");
        library.Key("ArrowUp");
        library.Expect("b2");
        library.Key("ArrowUp");
        library.Expect("a1");
        library.Key("ArrowUp");
        library.Key("ArrowLeft");
        library.Expect("a1");

        library.Key("Ctrl+ArrowRight");
        library.Expect("b4");
        library.Key("ArrowDown");
        library.Expect("b4");
        library.Key("ArrowUp");
        library.Expect("a3");

        library.Key("Alt+ArrowLeft");
        library.Expect("a1");
        library.Key("Alt+ArrowRight");
        library.Expect("a4");
        library.Key("Shift+ArrowLeft");
        library.Expect("c3", "c3,a4");
        library.Key("Ctrl+KeyA");
        library.Expect("c3", Everything);
        library.Key("ArrowRight");
        library.Expect("a4");
    }

    [Fact]
    public void TheTopRowStaysAndAShortLastRowTakesItsLastCard()
    {
        var library = Open(columns: 4);
        using var _ = library.Store;
        for (var i = 0; i < 3; i++) library.Key("ArrowRight");
        library.Expect("a2");
        library.Key("ArrowUp");
        library.Expect("a2");
        library.Key("ArrowDown");
        library.Expect("b3");
        library.Key("ArrowDown");
        library.Expect("b4");
        library.Key("ArrowDown");
        library.Expect("b4");
        library.Key("ArrowUp");
        library.Expect("a3");
    }

    [Fact]
    public void ListModeMovesOneRecordAtATimeAndReopeningKeepsTheFocus()
    {
        var library = Open(columns: 4);
        using var _ = library.Store;
        library.Model.ListMode = true;
        library.Key("ArrowDown");
        library.Expect("b1");
        library.Key("ArrowRight");
        library.Expect("c1");
        library.Key("ArrowUp");
        library.Expect("b1");
        library.Key("ArrowLeft");
        library.Expect("a1");
        library.Key("Ctrl+ArrowRight");
        library.Expect("b4");

        library.Model.Open();
        library.Expect("b4");
        Assert.True(library.Model.ListMode);
    }

    [Fact]
    public void KindShortcutsFilterAndFocusTheFirstResult()
    {
        var library = Open(columns: 2);
        using var _ = library.Store;
        library.Key("Ctrl+Digit2");
        Assert.Equal(ItemKind.Text, library.Model.Query.Kind);
        Assert.Equal("b1,b2,b3,b4", library.Shown);
        library.Expect("b1");
        library.Key("ArrowDown");
        library.Expect("b3");

        library.Key("Ctrl+Digit3");
        Assert.Equal("c1,c2,c3", library.Shown);
        library.Expect("c1");
        library.Key("Ctrl+Digit1");
        Assert.Equal("a1,a2,a3,a4", library.Shown);
        library.Expect("a1");
        library.Key("Ctrl+Digit0");
        Assert.Null(library.Model.Query.Kind);
        Assert.Equal(Everything, library.Shown);
        library.Expect("a1");
    }

    [Fact]
    public void SearchHandsOffToTheFirstResultAndAnEmptyResultFocusesNothing()
    {
        var library = Open(columns: 4);
        using var _ = library.Store;
        library.Model.SetSearchMode(LibrarySearchMode.Text);
        library.Key("Ctrl+ArrowRight");
        library.Key("Ctrl+KeyF");
        library.Expect("b4");

        void Search(string text)
        {
            library.Model.SetSearch(text);
            library.Model.FocusContent();
        }

        Search("apple");
        Assert.Equal("a1,b1,c2", library.Shown);
        library.Expect("a1");
        library.Key("ArrowRight");
        library.Expect("b1");

        Search("Second note");
        Assert.Equal("b2", library.Shown);
        library.Expect("b2");

        Search("nothing is called this");
        Assert.Equal("", library.Shown);
        library.Expect(null);
        library.Key("Ctrl+ArrowRight");
        library.Key("ArrowDown");
        library.Expect(null);

        Search("");
        Assert.Equal(Everything, library.Shown);
        library.Expect("a1");
    }

    [Fact]
    public void KeysTypedIntoAnEditorNeverActOnRecords()
    {
        var library = Open(columns: 4);
        using var _ = library.Store;
        library.Model.Select(library["b1"]);
        foreach (var key in new[] { "ArrowRight", "KeyP", "KeyS", "Delete", "Ctrl+KeyA", "Backspace", "Ctrl+KeyZ", "Ctrl+Enter" })
            library.Key(key, editing: true);
        library.Expect("b1");
        Assert.Equal("", library.Trashed);
        Assert.Equal("a1,b2", library.Pinned);
    }

    [Fact]
    public void PinnedOnlyViewEmptiesWhenItsLastPinIsRemoved()
    {
        var library = Open(columns: 4);
        using var _ = library.Store;
        library.Key("KeyP");
        Assert.Equal("b2", library.Pinned);
        library.Expect("a1");

        library.Key("KeyF");
        Assert.True(library.Model.ShowingPinnedOnly);
        Assert.Equal("b2", library.Shown);
        library.Expect("b2");
        library.Key("KeyP");
        Assert.Equal("", library.Pinned);
        Assert.Equal("", library.Shown);
        library.Expect(null);

        library.Key("KeyF");
        Assert.False(library.Model.ShowingPinnedOnly);
        library.Expect("a1");
    }

    [Fact]
    public void AMovedKeyDoesNotBringTheDefaultBack()
    {
        var library = Open(columns: 4);
        using var _ = library.Store;
        library.Model.Keymap.Remap("next", "KeyJ");
        library.Key("ArrowRight");
        library.Expect("a1");
        library.Key("KeyJ");
        library.Expect("b1");

        library.Model.Keymap.Remap("next", "");
        library.Key("KeyJ");
        library.Key("ArrowRight");
        library.Expect("b1");

        library.Model.Keymap.Remap("next", null);
        library.Key("ArrowRight");
        library.Expect("c1");
    }

    [Fact]
    public void TrashingABatchFocusesTheNextRecordAndUndoBringsItBack()
    {
        var library = Open(columns: 4);
        using var _ = library.Store;
        library.Key("ArrowRight");
        library.Key("Shift+ArrowRight");
        library.Expect("c1", "b1,c1");

        library.Key("KeyS");
        Assert.Equal("b1,c1", library.Trashed);
        Assert.Equal("a1,a2,b2,c2,a3,b3,c3,a4,b4", library.Shown);
        library.Expect("a2");

        library.Key("Backspace");
        Assert.Equal("", library.Trashed);
        library.Expect("b1");

        library.Key("Delete");
        Assert.Equal("b1", library.Trashed);
        library.Expect("c1");
        library.Key("Ctrl+KeyZ");
        Assert.Equal("", library.Trashed);
        library.Expect("b1");
    }

    [Fact]
    public void InTrashKeysOnlyRestoreAndNeverDeleteForGood()
    {
        var library = Open(columns: 4);
        using var _ = library.Store;
        library.Key("ArrowRight");
        library.Key("KeyS");
        library.Expect("c1");

        library.Model.ChangeScope(LibraryScope.Trash);
        library.Model.FocusContent();
        Assert.Equal("b1", library.Shown);
        library.Expect("b1");
        library.Key("Delete");
        library.Key("KeyS");
        Assert.Equal("b1", library.Trashed);
        Assert.NotNull(library.Store.Find(library["b1"]));
        library.Expect("b1");

        library.Key("Backspace");
        Assert.Equal("", library.Trashed);
        Assert.Equal("", library.Shown);
        library.Expect(null);
    }

    [Fact]
    public void TrashScopeListsOnlyTrashAndRestoreReturnsItems()
    {
        using var store = new LibraryStore(_directory, () => _now = _now.AddMilliseconds(1));
        var note = store.InsertText("keep");
        store.Retain([note.Id]);
        var model = new LibraryModel(store, () => _now);
        model.Open();
        model.Perform("trash");
        model.ChangeScope(LibraryScope.Trash);
        model.FocusContent();
        Assert.Equal([note.Id], model.Ids);
        model.Restore();
        Assert.Empty(model.Ids);
        Assert.False(store.Find(note.Id)!.IsTrashed);
    }

    [Fact]
    public void ScopesSeparateSavedRecentFoldersAndTags()
    {
        using var store = new LibraryStore(_directory, () => _now = _now.AddMilliseconds(1));
        var recent = store.InsertText("recent");
        var saved = store.InsertText("saved");
        var tagged = store.InsertText("tagged");
        var parent = store.CreateFolder("Work");
        var child = store.CreateFolder("Design", parent.Id);
        var filed = store.InsertText("filed");
        store.Retain([saved.Id]);
        store.AddTags([tagged.Id], ["idea"]);
        store.AddToFolder([filed.Id], child.Id);
        var model = new LibraryModel(store, () => _now);

        model.ChangeScope(LibraryScope.Recent);
        Assert.Equal([recent.Id], model.Ids);
        model.ChangeScope(LibraryScope.Unorganized);
        Assert.Equal([saved.Id], model.Ids);
        model.ChangeScope(LibraryScope.ForFolder(parent.Id));
        Assert.Equal([filed.Id], model.Ids); // a top-level folder includes its subfolders
        model.ChangeScope(LibraryScope.ForTag("IDEA"));
        Assert.Equal([tagged.Id], model.Ids);
        model.ChangeScope(LibraryScope.Favorites);
        Assert.Equal([filed.Id], model.Ids); // pinning to a folder pins
    }

    [Fact]
    public void SearchIsDiacriticInsensitiveAndSwitchesToBestMatch()
    {
        using var store = new LibraryStore(_directory, () => _now = _now.AddMilliseconds(1));
        var item = store.InsertText("Alışveriş listesi");
        store.Retain([item.Id]);
        var model = new LibraryModel(store, () => _now);
        model.SetSearch("ALISVERIS");
        model.FocusContent();
        Assert.Equal([item.Id], model.Ids);
        Assert.Equal(LibrarySort.Relevance, model.Query.Sort);
        model.SetSearch("");
        model.FocusContent();
        Assert.Equal(LibrarySort.Newest, model.Query.Sort);
    }

    [Fact]
    public void DomainFilterRespectsHostBoundaries()
    {
        using var store = new LibraryStore(_directory, () => _now = _now.AddMilliseconds(1));
        var video = store.InsertLink("https://www.youtube.com/watch?v=1");
        var music = store.InsertLink("https://music.youtube.com/x");
        var other = store.InsertLink("https://notyoutube.com/");
        store.Retain([video.Id, music.Id, other.Id]);
        var model = new LibraryModel(store, () => _now);
        model.SetDomain("youtube.com");
        Assert.Equal([music.Id, video.Id], model.Ids);
    }

    [Fact]
    public void NewNoteOpensInTextsAndStaysOffTheShelves()
    {
        using var store = new LibraryStore(_directory, () => _now = _now.AddMilliseconds(1));
        var model = new LibraryModel(store, () => _now);
        var note = model.NewNote("Untitled note");
        Assert.Equal(note.Id, model.Focused);
        Assert.Equal(ItemKind.Text, model.Query.Kind);
        Assert.False(store.Find(note.Id)!.IsOnShelf);
    }
}
