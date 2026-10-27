using System.Text.Json;
using Pegline.Core.Input;
using Pegline.Core.Models;
using Pegline.Core.Shelves;

namespace Pegline.Core.Tests;

/// <summary>Runs the shared shelf behavior contract against <see cref="ShelfNavigator"/> and <see cref="Keymap"/>.</summary>
public class ShelfContractTests
{
    private static readonly JsonDocument Contract = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Contracts", "shelf-behavior.json")));

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
        var scenario = Contract.RootElement.GetProperty("scenarios").EnumerateArray().Single(s => s.GetProperty("id").GetString() == id);
        var (items, names) = BuildItems(scenario.GetProperty("itemSet").GetString()!);
        var navigator = new ShelfNavigator();
        navigator.SetVisibleKinds(Kinds(scenario.GetProperty("visibleKinds")));
        navigator.SetPinnedOnly(Kinds(scenario.GetProperty("pinnedOnlyKinds")));
        navigator.SetItems(items);
        var keymap = new Keymap();

        var step = 0;
        foreach (var action in scenario.GetProperty("steps").EnumerateArray())
        {
            step++;
            var name = action.GetProperty("action").GetString()!;
            switch (name)
            {
                case "open": navigator.Open(); break;
                case "close": navigator.Close(); break;
                case "openSearch": navigator.OpenSearch(); break;
                case "finishSearch": navigator.FinishSearch(); break;
                case "toggle": navigator.ToggleKind(Kind(action.GetProperty("kind").GetString()!)); break;
                case "left": Press(navigator, keymap, "ArrowLeft"); break;
                case "right": Press(navigator, keymap, "ArrowRight"); break;
                case "up": Press(navigator, keymap, "ArrowUp"); break;
                case "down": Press(navigator, keymap, "ArrowDown"); break;
                case "key": Press(navigator, keymap, action.GetProperty("code").GetString()!); break;
                case "remap":
                    var key = action.GetProperty("key");
                    keymap.Remap(action.GetProperty("binding").GetString()!, key.ValueKind == JsonValueKind.Null ? null : key.GetString());
                    break;
                default: throw new InvalidOperationException($"Unknown contract action {name}");
            }

            var expect = action.GetProperty("expect");
            var where = $"{id} step {step} ({name})";
            Assert.True(Kind(expect.GetProperty("activeKind").GetString()!) == navigator.ActiveKind, $"{where}: active kind {navigator.ActiveKind}");
            var focused = expect.GetProperty("focused");
            var actualFocus = navigator.Focused is { } f ? names[f] : null;
            Assert.True((focused.ValueKind == JsonValueKind.Null ? null : focused.GetString()) == actualFocus, $"{where}: focused {actualFocus}");
            var selected = expect.GetProperty("selected").EnumerateArray().Select(e => e.GetString()!).ToList();
            Assert.Equal(selected, navigator.Selection.Select(s => names[s]).ToList());
            Assert.Equal(Kinds(expect.GetProperty("visibleKinds")), navigator.VisibleKinds.ToList());
            Assert.True(expect.GetProperty("searchEditing").GetBoolean() == navigator.SearchEditing, $"{where}: search editing");
        }
    }

    /// <summary>The same routing the shelf window uses: the search editor keeps typing and caret keys.</summary>
    private static void Press(ShelfNavigator navigator, Keymap keymap, string code)
    {
        if (navigator.SearchEditing) return;
        foreach (var action in keymap.Resolve(Keymap.NormalizeKey(code)))
        {
            switch (action)
            {
                case "previous": navigator.Previous(); return;
                case "next": navigator.Next(); return;
                case "up": navigator.Up(); return;
                case "down": navigator.Down(); return;
            }
        }
    }

    private static (List<ClipItem> Items, Dictionary<Guid, string> Names) BuildItems(string set)
    {
        var items = new List<ClipItem>();
        var names = new Dictionary<Guid, string>();
        var now = DateTimeOffset.Now;
        var index = 0;
        foreach (var entry in Contract.RootElement.GetProperty("itemSets").GetProperty(set).EnumerateArray())
        {
            var item = new ClipItem
            {
                Kind = Kind(entry.GetProperty("kind").GetString()!),
                Text = entry.GetProperty("text").GetString()!,
                CreatedAt = now.AddSeconds(-index++),
            };
            item.UpdatedAt = item.CreatedAt;
            if (item.Kind == ItemKind.Link) item.Url = "https://example.com/" + entry.GetProperty("id").GetString();
            items.Add(item);
            names[item.Id] = entry.GetProperty("id").GetString()!;
        }
        return (items, names);
    }

    private static List<ItemKind> Kinds(JsonElement array) => [.. array.EnumerateArray().Select(e => Kind(e.GetString()!))];

    private static ItemKind Kind(string value) => value switch
    {
        "image" => ItemKind.Image,
        "text" => ItemKind.Text,
        "link" => ItemKind.Link,
        _ => throw new ArgumentException(value),
    };
}
