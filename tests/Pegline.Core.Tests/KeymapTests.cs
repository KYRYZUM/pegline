using Pegline.Core.Input;
using Pegline.Core.Models;
using Pegline.Core.Shelves;

namespace Pegline.Core.Tests;

public class KeymapTests
{
    [Theory]
    [InlineData("control+shift+arrowleft", "Ctrl+Shift+arrowleft")]
    [InlineData("Shift+Ctrl+Left", "Ctrl+Shift+ArrowLeft")]
    [InlineData("KeyX", "X")]
    [InlineData("Digit7", "7")]
    [InlineData("", "")]
    public void GesturesAreCanonical(string input, string expected) => Assert.Equal(expected, Keymap.Canonical(input));

    [Fact]
    public void RemappingReplacesDefaultAliases()
    {
        var keymap = new Keymap();
        Assert.Contains("copy", keymap.Resolve("Enter"));
        Assert.Contains("pin", keymap.Resolve("Ctrl+P"));
        keymap.Remap("copy", "Ctrl+Shift+Y");
        Assert.DoesNotContain("copy", keymap.Resolve("Enter"));
        Assert.DoesNotContain("copy", keymap.Resolve("Ctrl+C"));
        Assert.Contains("copy", keymap.Resolve("Shift+Ctrl+Y"));
        keymap.Remap("copy", "");
        Assert.Empty(keymap.GesturesFor("copy"));
        keymap.Remap("copy", null);
        Assert.Equal(["Ctrl+C", "Enter"], keymap.GesturesFor("copy"));
    }
}

public class ShelfNavigatorTests
{
    private static List<ClipItem> Items(int images, int texts, int links)
    {
        var now = DateTimeOffset.Now;
        var list = new List<ClipItem>();
        var n = 0;
        void Add(ItemKind kind, int count)
        {
            for (var i = 0; i < count; i++)
                list.Add(new ClipItem { Kind = kind, Text = $"{kind} {i}", Url = $"https://example.com/{n}", CreatedAt = now.AddSeconds(-n++) });
        }
        Add(ItemKind.Image, images);
        Add(ItemKind.Text, texts);
        Add(ItemKind.Link, links);
        return list;
    }

    [Fact]
    public void NumbersAndPagesFollowTheVisiblePage()
    {
        var items = Items(20, 0, 0);
        var nav = new ShelfNavigator();
        nav.SetItems(items);
        nav.SetPageSize(ItemKind.Image, 5);
        nav.Open();
        nav.SelectNumber(3);
        Assert.Equal(items[2].Id, nav.Focused);
        nav.NextPage();
        Assert.Equal(5, nav.Offset(ItemKind.Image));
        Assert.Equal(items[7].Id, nav.Focused);
        nav.SelectNumber(1);
        Assert.Equal(items[5].Id, nav.Focused);
        nav.SelectNumber(6);
        Assert.Equal(items[5].Id, nav.Focused);
        nav.Last();
        Assert.Equal(15, nav.Offset(ItemKind.Image));
        nav.NextPage();
        Assert.Equal(items[19].Id, nav.Focused);
    }

    [Fact]
    public void RemovalSelectsTheNextSurvivor()
    {
        var items = Items(5, 0, 0);
        var nav = new ShelfNavigator();
        nav.SetItems(items);
        nav.Open();
        nav.Next();
        nav.ExtendNext();
        Assert.Equal([items[1].Id, items[2].Id], nav.Selection);
        items[1].HiddenFromShelf = true;
        items[2].HiddenFromShelf = true;
        nav.SetItems(items);
        Assert.Equal(items[3].Id, nav.Focused);
        items[3].HiddenFromShelf = true;
        items[4].HiddenFromShelf = true;
        nav.SetItems(items);
        Assert.Equal(items[0].Id, nav.Focused);
    }

    [Fact]
    public void SearchFiltersVisibleRowsAndCancelRestoresThem()
    {
        var items = Items(2, 3, 2);
        items[3].Text = "needle in text";
        var nav = new ShelfNavigator();
        nav.SetItems(items);
        nav.Open();
        nav.OpenSearch();
        nav.SetQuery("NEEDLE");
        Assert.Equal(ItemKind.Text, nav.ActiveKind);
        Assert.Equal(items[3].Id, nav.Focused);
        Assert.Empty(nav.Row(ItemKind.Image));
        nav.CancelSearch();
        Assert.Equal(2, nav.Row(ItemKind.Image).Count);
        Assert.Equal(items[3].Id, nav.Focused);
    }

    [Fact]
    public void HidingTheLastRowRequestsClose()
    {
        var nav = new ShelfNavigator();
        nav.SetVisibleKinds([ItemKind.Text]);
        nav.SetItems(Items(0, 1, 0));
        nav.Open();
        var closed = false;
        nav.CloseRequested += (_, _) => closed = true;
        nav.ToggleKind(ItemKind.Text);
        Assert.True(closed);
        Assert.Equal([ItemKind.Text], nav.VisibleKinds);
    }

    [Fact]
    public void TogglingPinnedOnlyMovesFocusIntoTheFilteredRow()
    {
        var items = Items(3, 0, 0);
        items[2].Pinned = true;
        var nav = new ShelfNavigator();
        nav.SetItems(items);
        nav.Open();
        nav.TogglePinnedOnly();
        Assert.Equal(items[2].Id, nav.Focused);
        Assert.Single(nav.Row(ItemKind.Image));
    }
}
