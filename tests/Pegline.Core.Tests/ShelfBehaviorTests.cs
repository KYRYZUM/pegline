using Pegline.Core.Input;
using Pegline.Core.Models;
using Pegline.Core.Shelves;

namespace Pegline.Core.Tests;

/// <summary>Which card the shelves focus as they open, close, show and hide rows, and as the arrow keys move.</summary>
public class ShelfBehaviorTests
{
    /// <summary>Shelves over named cards: "p" photos, "n" notes, "w" web links, newest first within each row.</summary>
    private sealed class Shelves
    {
        private readonly Dictionary<Guid, string> _names = [];

        public Shelves(int photos, int notes, int links, ItemKind[]? visible = null, ItemKind[]? pinnedOnly = null)
        {
            var items = new List<ClipItem>();
            var now = DateTimeOffset.Now;
            void Add(ItemKind kind, string prefix, int count)
            {
                for (var i = 1; i <= count; i++)
                {
                    var item = new ClipItem { Kind = kind, Text = $"{prefix}{i} body", CreatedAt = now.AddMinutes(-items.Count) };
                    item.UpdatedAt = item.CreatedAt;
                    if (kind == ItemKind.Link) item.Url = $"https://example.org/{prefix}{i}";
                    items.Add(item);
                    _names[item.Id] = prefix + i;
                }
            }
            Add(ItemKind.Image, "p", photos);
            Add(ItemKind.Text, "n", notes);
            Add(ItemKind.Link, "w", links);
            if (visible is not null) Nav.SetVisibleKinds(visible);
            if (pinnedOnly is not null) Nav.SetPinnedOnly(pinnedOnly);
            Nav.SetItems(items);
        }

        public ShelfNavigator Nav { get; } = new();

        public Keymap Keys { get; } = new();

        public string? Focused => Nav.Focused is { } id ? _names[id] : null;

        public string Selected => string.Join(",", Nav.Selection.Select(id => _names[id]));

        /// <summary>Routes a key the way the shelf window does: the search editor keeps every key for typing.</summary>
        public void Press(string key)
        {
            if (Nav.SearchEditing) return;
            foreach (var action in Keys.Resolve(Keymap.NormalizeKey(key)))
            {
                Action? move = action switch
                {
                    "previous" => Nav.Previous,
                    "next" => Nav.Next,
                    "up" => Nav.Up,
                    "down" => Nav.Down,
                    _ => null,
                };
                if (move is null) continue;
                move();
                return;
            }
        }

        /// <summary>Asserts the focused card, that it is the only selected card, and the active row.</summary>
        public void Expect(string? focused, ItemKind kind)
        {
            Assert.Equal(focused, Focused);
            Assert.Equal(focused ?? "", Selected);
            Assert.Equal(kind, Nav.ActiveKind);
        }
    }

    [Fact]
    public void OpeningStartsOnTheNewestPhotoEveryTime()
    {
        var shelves = new Shelves(3, 2, 2);
        shelves.Nav.Open();
        shelves.Expect("p1", ItemKind.Image);
        shelves.Press("ArrowDown");
        shelves.Press("ArrowRight");
        shelves.Expect("n2", ItemKind.Text);

        shelves.Nav.Close();
        shelves.Expect(null, ItemKind.Text);
        shelves.Nav.Open();
        shelves.Expect("p1", ItemKind.Image);
    }

    [Fact]
    public void OpeningWithPhotosHiddenStartsOnTheFirstShownRow()
    {
        var shelves = new Shelves(3, 2, 2, visible: [ItemKind.Text, ItemKind.Link]);
        shelves.Nav.Open();
        shelves.Expect("n1", ItemKind.Text);
        shelves.Press("ArrowDown");
        shelves.Expect("w1", ItemKind.Link);
        shelves.Press("ArrowUp");
        shelves.Expect("n1", ItemKind.Text);
    }

    [Fact]
    public void OpeningWithoutPhotosStartsOnNotes()
    {
        var shelves = new Shelves(0, 2, 2);
        shelves.Nav.Open();
        shelves.Expect("n1", ItemKind.Text);
        shelves.Press("ArrowDown");
        shelves.Expect("w1", ItemKind.Link);
    }

    [Fact]
    public void APinnedOnlyPhotoRowWithoutPinsIsPassedOver()
    {
        var shelves = new Shelves(3, 2, 2, pinnedOnly: [ItemKind.Image]);
        shelves.Nav.Open();
        shelves.Expect("n1", ItemKind.Text);
    }

    [Fact]
    public void UpAndDownKeepTheColumnAndStopAtTheEdges()
    {
        var shelves = new Shelves(5, 2, 3);
        shelves.Nav.Open();
        for (var i = 0; i < 4; i++) shelves.Press("ArrowRight");
        shelves.Expect("p5", ItemKind.Image);

        // The notes row is shorter: its last card stands in for the column.
        shelves.Press("ArrowDown");
        shelves.Expect("n2", ItemKind.Text);
        shelves.Press("ArrowDown");
        shelves.Expect("w2", ItemKind.Link);
        shelves.Press("ArrowDown");
        shelves.Expect("w2", ItemKind.Link);

        shelves.Press("ArrowUp");
        shelves.Expect("n2", ItemKind.Text);
        shelves.Press("ArrowUp");
        shelves.Expect("p2", ItemKind.Image);
        shelves.Press("ArrowUp");
        shelves.Expect("p2", ItemKind.Image);
        shelves.Press("ArrowLeft");
        shelves.Expect("p1", ItemKind.Image);
    }

    [Fact]
    public void EmptyAndHiddenRowsAreSteppedOver()
    {
        var shelves = new Shelves(5, 2, 3, pinnedOnly: [ItemKind.Text]);
        shelves.Nav.Open();
        shelves.Press("ArrowRight");
        shelves.Press("ArrowDown");
        shelves.Expect("w2", ItemKind.Link);
        shelves.Press("ArrowUp");
        shelves.Expect("p2", ItemKind.Image);

        shelves.Nav.ToggleKind(ItemKind.Link);
        Assert.Equal([ItemKind.Image, ItemKind.Text], shelves.Nav.VisibleKinds);
        shelves.Press("ArrowDown");
        shelves.Expect("p2", ItemKind.Image);
    }

    [Fact]
    public void ShowingAnotherRowKeepsTheFocusedCard()
    {
        var shelves = new Shelves(3, 2, 3, visible: [ItemKind.Image]);
        shelves.Nav.Open();
        shelves.Press("ArrowRight");
        shelves.Nav.ToggleKind(ItemKind.Text);
        shelves.Expect("p2", ItemKind.Image);
        Assert.Equal([ItemKind.Image, ItemKind.Text], shelves.Nav.VisibleKinds);
        shelves.Press("ArrowDown");
        shelves.Expect("n2", ItemKind.Text);

        shelves.Nav.ToggleKind(ItemKind.Link);
        shelves.Expect("n2", ItemKind.Text);
        shelves.Press("ArrowDown");
        shelves.Expect("w2", ItemKind.Link);
    }

    [Fact]
    public void HidingTheFocusedRowMovesToTheFirstShownRow()
    {
        var shelves = new Shelves(3, 2, 2);
        shelves.Nav.Open();
        shelves.Press("ArrowDown");
        shelves.Nav.ToggleKind(ItemKind.Text);
        shelves.Expect("p1", ItemKind.Image);
        Assert.Equal([ItemKind.Image, ItemKind.Link], shelves.Nav.VisibleKinds);
    }

    [Fact]
    public void TheSearchEditorKeepsArrowKeysUntilSearchIsFinished()
    {
        var shelves = new Shelves(3, 2, 2);
        shelves.Nav.Open();
        shelves.Nav.OpenSearch();
        Assert.True(shelves.Nav.SearchEditing);
        shelves.Press("ArrowRight");
        shelves.Press("ArrowDown");
        shelves.Expect("p1", ItemKind.Image);

        shelves.Nav.FinishSearch();
        Assert.False(shelves.Nav.SearchEditing);
        shelves.Press("ArrowDown");
        shelves.Expect("n1", ItemKind.Text);
    }

    [Fact]
    public void EmptyShelvesFocusNothing()
    {
        var shelves = new Shelves(0, 0, 0);
        shelves.Nav.Open();
        shelves.Expect(null, ItemKind.Image);
        shelves.Press("ArrowDown");
        shelves.Press("ArrowUp");
        shelves.Press("ArrowRight");
        shelves.Expect(null, ItemKind.Image);
    }

    [Fact]
    public void AMovedArrowKeyWorksAtItsNewKeyOnlyAndComesBackOnReset()
    {
        var shelves = new Shelves(3, 2, 2);
        shelves.Nav.Open();
        shelves.Keys.Remap("down", "KeyJ");
        shelves.Press("ArrowDown");
        shelves.Expect("p1", ItemKind.Image);
        shelves.Press("KeyJ");
        shelves.Expect("n1", ItemKind.Text);

        // Turned off: neither key moves.
        shelves.Keys.Remap("down", "");
        shelves.Press("KeyJ");
        shelves.Press("ArrowDown");
        shelves.Expect("n1", ItemKind.Text);

        shelves.Keys.Remap("down", null);
        shelves.Press("ArrowDown");
        shelves.Expect("w1", ItemKind.Link);
        shelves.Press("ArrowUp");

        // Typed into the search editor, the moved key is only a letter.
        shelves.Keys.Remap("down", "KeyJ");
        shelves.Nav.OpenSearch();
        shelves.Press("KeyJ");
        shelves.Expect("n1", ItemKind.Text);
        shelves.Nav.FinishSearch();
        shelves.Press("KeyJ");
        shelves.Expect("w1", ItemKind.Link);
    }

    [Fact]
    public void ClosingDropsSearchAndSelectionEveryTime()
    {
        var shelves = new Shelves(3, 2, 2);
        shelves.Nav.Open();
        shelves.Press("ArrowDown");
        shelves.Nav.OpenSearch();
        for (var round = 0; round < 3; round++)
        {
            shelves.Nav.Close();
            Assert.False(shelves.Nav.SearchEditing);
            Assert.Null(shelves.Focused);
            Assert.Empty(shelves.Nav.Selection);
            shelves.Nav.Open();
            Assert.False(shelves.Nav.SearchEditing);
            shelves.Expect("p1", ItemKind.Image);
        }
    }

    [Fact]
    public void AHiddenMiddleRowIsJumpedAcross()
    {
        var shelves = new Shelves(3, 2, 2, visible: [ItemKind.Image, ItemKind.Link]);
        shelves.Nav.Open();
        shelves.Press("ArrowDown");
        shelves.Expect("w1", ItemKind.Link);
        shelves.Nav.Close();
        shelves.Nav.Open();
        shelves.Expect("p1", ItemKind.Image);
    }
}
