using System.Text.Json;
using Pegline.Core.Input;
using Pegline.Core.Library;
using Pegline.Core.Models;
using Pegline.Core.Storage;

namespace Pegline.Core.Tests;

/// <summary>Runs the shared Library keyboard contract against <see cref="LibraryModel"/> and a real <see cref="LibraryStore"/>.</summary>
public sealed class LibraryContractTests : IDisposable
{
    private static readonly JsonDocument Contract = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Contracts", "library-keyboard.json")));
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pegline-library-" + Guid.NewGuid().ToString("N"));
    private DateTimeOffset _now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    public static TheoryData<string> Scenarios()
    {
        var data = new TheoryData<string>();
        foreach (var scenario in Contract.RootElement.GetProperty("scenarios").EnumerateArray())
            data.Add(scenario.GetProperty("id").GetString()!);
        return data;
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void Scenario(string id)
    {
        var root = Contract.RootElement;
        var scenario = root.GetProperty("scenarios").EnumerateArray().Single(s => s.GetProperty("id").GetString() == id);
        using var store = new LibraryStore(_directory, () => _now = _now.AddMilliseconds(1));
        var names = Seed(store, root.GetProperty("records"));
        var byGuid = names.ToDictionary(p => p.Value, p => p.Key);
        var model = new LibraryModel(store, () => _now) { Columns = scenario.GetProperty("columns").GetInt32() };
        if (scenario.TryGetProperty("searchMode", out var mode) && mode.GetString() == "text") model.SetSearchMode(LibrarySearchMode.Text);

        var step = 0;
        foreach (var action in scenario.GetProperty("steps").EnumerateArray())
        {
            step++;
            var name = action.GetProperty("action").GetString()!;
            switch (name)
            {
                case "open": model.Open(); break;
                case "close": break; // the model outlives its window
                case "mode": model.ListMode = action.GetProperty("list").GetBoolean(); break;
                case "select": model.Select(names[action.GetProperty("id").GetString()!]); break;
                case "searchHandoff":
                    model.SetSearch(action.GetProperty("value").GetString()!);
                    model.FocusContent();
                    break;
                case "scope":
                    model.ChangeScope(action.GetProperty("scope").GetString() == "trash" ? LibraryScope.Trash : LibraryScope.Saved);
                    model.FocusContent();
                    break;
                case "remap":
                    var key = action.GetProperty("key");
                    model.Keymap.Remap(action.GetProperty("binding").GetString()!, key.ValueKind == JsonValueKind.Null ? null : key.GetString());
                    break;
                case "key":
                    model.HandleKey(Gesture(action), action.GetProperty("editing").GetBoolean());
                    break;
                default: throw new InvalidOperationException($"Unknown contract action {name}");
            }

            var expect = action.GetProperty("expect");
            var where = $"{id} step {step} ({name})";
            var focused = expect.GetProperty("focused");
            Assert.True((focused.ValueKind == JsonValueKind.Null ? null : focused.GetString()) == (model.Focused is { } f ? byGuid[f] : null),
                $"{where}: focused {(model.Focused is { } g ? byGuid[g] : "null")}");
            Assert.True(Names(expect.GetProperty("selected")).SequenceEqual(model.Selected.OrderBy(i => model.IndexOf(i)).Select(i => byGuid[i])),
                $"{where}: selected {string.Join(",", model.Selected.Select(i => byGuid[i]))}");
            Assert.True(Names(root.GetProperty("views").GetProperty(expect.GetProperty("visible").GetString()!)).SequenceEqual(model.Ids.Select(i => byGuid[i])),
                $"{where}: visible {string.Join(",", model.Ids.Select(i => byGuid[i]))}");
            Assert.Equal(expect.GetProperty("scope").GetString(), model.Query.Scope.Kind == LibraryScopeKind.Trash ? "trash" : "saved");
            Assert.Equal(expect.GetProperty("kind").GetString(), model.Query.Kind switch { ItemKind.Image => "image", ItemKind.Text => "text", ItemKind.Link => "link", _ => "all" });
            Assert.Equal(expect.GetProperty("search").GetString(), model.Query.Search);
            Assert.Equal(expect.GetProperty("list").GetBoolean(), model.ListMode);
            Assert.True(Names(expect.GetProperty("trashed")).ToHashSet().SetEquals(store.Items.Where(i => i.IsTrashed).Select(i => byGuid[i.Id])), $"{where}: trashed");
            Assert.True(Names(expect.GetProperty("pinned")).ToHashSet().SetEquals(store.Items.Where(i => i.Pinned).Select(i => byGuid[i.Id])), $"{where}: pinned");
            Assert.Equal(expect.GetProperty("pinnedOnly").GetBoolean(), model.ShowingPinnedOnly);
        }
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

    private Dictionary<string, Guid> Seed(LibraryStore store, JsonElement records)
    {
        var list = records.EnumerateArray().ToList();
        var names = new Dictionary<string, Guid>();
        var shade = 0;
        // The contract lists records newest first.
        foreach (var record in Enumerable.Reverse(list))
        {
            var name = record.GetProperty("id").GetString()!;
            var text = record.GetProperty("text").GetString()!;
            var item = record.GetProperty("kind").GetString() switch
            {
                "image" => store.InsertImage(TestImages.Png(4 + shade, 4, (byte)(20 * ++shade))),
                "text" => store.InsertText(text),
                _ => store.InsertLink("https://example.com/" + name),
            };
            store.SetDetails(item.Id, name: record.GetProperty("title").GetString()!, note: item.Kind == ItemKind.Text ? null : text);
            store.Retain([item.Id]);
            if (record.GetProperty("pinned").GetBoolean()) store.TogglePin([item.Id], true);
            if (!record.GetProperty("onShelf").GetBoolean()) store.TakeDown([item.Id]);
            names[name] = item.Id;
        }
        return names;
    }

    private static string Gesture(JsonElement action)
    {
        var parts = action.GetProperty("modifiers").EnumerateArray().Select(m => m.GetString() switch
        {
            "primary" or "control" => "Ctrl",
            "alt" => "Alt",
            "shift" => "Shift",
            var other => throw new InvalidOperationException($"Unknown modifier {other}"),
        }).ToList();
        parts.Add(action.GetProperty("code").GetString()!);
        return Keymap.Canonical(string.Join('+', parts));
    }

    private static IEnumerable<string> Names(JsonElement array) => array.EnumerateArray().Select(e => e.GetString()!);
}
