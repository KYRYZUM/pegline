using Pegline.Core.Actions;
using Pegline.Core.Input;
using Pegline.Core.Models;
using Pegline.Core.Shelves;

namespace Pegline.Core.Tests;

public class ActionCatalogTests
{
    private static readonly Keymap Keys = new();

    private static ActionCommand Find(IReadOnlyList<ActionCommand> entries, string id) => entries.Single(e => e.Id == id);

    [Fact]
    public void LeadingCommandsOpenTheUnfilteredList()
    {
        var entries = ActionCatalog.Entries(new ActionContext { SelectedCount = 1, Kind = ItemKind.Text, HasItems = true }, Keys);
        Assert.Equal(["search", "library", "copy", "paste", "preview"], entries.Take(5).Select(e => e.Id));
        Assert.DoesNotContain(entries, e => e.Id is "actions" or "newNote" or "newFolder");
        Assert.Contains(entries, e => e.Id == "menu.newNote");
    }

    [Fact]
    public void SelectionDependentCommandsExplainWhyTheyAreDisabled()
    {
        var entries = ActionCatalog.Entries(new ActionContext { HasItems = true }, Keys);
        var copy = Find(entries, "copy");
        Assert.False(copy.IsEnabled);
        Assert.Equal("Select an item first", copy.DisabledReason);
        Assert.False(Find(entries, "open").IsEnabled);
        Assert.True(Find(entries, "search").IsEnabled);
    }

    [Fact]
    public void KindSpecificCommandsFollowTheSelection()
    {
        var link = ActionCatalog.Entries(new ActionContext { SelectedCount = 1, Kind = ItemKind.Link, HasItems = true }, Keys);
        Assert.True(Find(link, "open").IsEnabled);
        Assert.False(Find(link, "edit").IsEnabled);
        Assert.False(Find(link, "crop").IsEnabled);

        var image = ActionCatalog.Entries(new ActionContext { SelectedCount = 1, Kind = ItemKind.Image, HasFile = true, HasItems = true }, Keys);
        Assert.True(Find(image, "crop").IsEnabled);
        Assert.True(Find(image, "menu.resetZoom").IsEnabled);

        var batch = ActionCatalog.Entries(new ActionContext { SelectedCount = 2, Kind = ItemKind.Image, HasFile = true, HasItems = true }, Keys);
        Assert.False(Find(batch, "crop").IsEnabled);
        Assert.Equal("2 selected", Find(batch, "copy").Subtitle);
    }

    [Fact]
    public void PreviewOnlyCommandsTeachTheKeySequenceFromTheShelves()
    {
        var line = ActionCatalog.Entries(new ActionContext { SelectedCount = 1, Kind = ItemKind.Text, HasItems = true }, Keys);
        Assert.Equal("Space → E", Find(line, "edit").KeyHint);
        Assert.Equal("Space → Ctrl+O", Find(line, "open").KeyHint);
        Assert.Equal("Space → M → B", Find(line, "pen").KeyHint);

        var preview = ActionCatalog.Entries(new ActionContext { Origin = ActionOrigin.ShelfPreview, SelectedCount = 1, Kind = ItemKind.Text, HasItems = true }, Keys);
        Assert.Equal("E", Find(preview, "edit").KeyHint);
        Assert.Equal("Ctrl+C / Enter", Find(preview, "copy").KeyHint);
    }

    [Fact]
    public void TextEditingKeepsOnlyCtrlShortcutsForPinning()
    {
        var entries = ActionCatalog.Entries(new ActionContext { Origin = ActionOrigin.ShelfPreview, SelectedCount = 1, Kind = ItemKind.Text, IsTextEditing = true }, Keys);
        Assert.Equal("Ctrl+P", Find(entries, "pin").KeyHint);
        Assert.Equal("", Find(entries, "copy").KeyHint);
        Assert.False(Find(entries, "paste").IsEnabled);
        Assert.True(Find(entries, "finishEditing").IsEnabled);
    }

    [Fact]
    public void PinBecomesUnpinWhenEverySelectedItemIsPinned()
    {
        var entries = ActionCatalog.Entries(new ActionContext { SelectedCount = 1, Kind = ItemKind.Text, AllPinned = true }, Keys);
        var pin = Find(entries, "pin");
        Assert.Equal("Unpin", pin.Title);
        Assert.True(pin.IsActive);
    }

    [Fact]
    public void FoldersAndTagsBecomeCommands()
    {
        var work = new Folder { Name = "Work" };
        var entries = ActionCatalog.Entries(new ActionContext
        {
            SelectedCount = 1,
            Kind = ItemKind.Text,
            Folders = [work],
            Tags = ["design"],
            ItemFolders = [work],
            ItemTags = ["design"],
        }, Keys);
        Assert.True(Find(entries, ActionCatalog.AssignFolderPrefix + work.Id).IsEnabled);
        Assert.False(Find(entries, ActionCatalog.MoveFolderPrefix + work.Id).IsEnabled);
        Assert.Equal("Tags: #design", Find(entries, ActionCatalog.OpenTagPrefix + "design").Title);
        Assert.True(Find(entries, ActionCatalog.RemoveTagPrefix + "design").IsEnabled);
        Assert.True(Find(entries, ActionCatalog.UnassignFolderPrefix + work.Id).IsEnabled);
    }

    [Fact]
    public void RemappedShortcutsAppearInHints()
    {
        var keys = new Keymap(new Dictionary<string, string> { ["pin"] = "Ctrl+Shift+K" });
        var entries = ActionCatalog.Entries(new ActionContext { SelectedCount = 1, Kind = ItemKind.Text }, keys);
        Assert.Equal("Ctrl+Shift+K", Find(entries, "pin").KeyHint);
    }

    [Fact]
    public void TranslatorLocalizesTitlesGroupsAndReasons()
    {
        var entries = ActionCatalog.Entries(new ActionContext(), Keys, s => s == "Copy" ? "Kopyala" : s == "Select an item first" ? "Önce bir öğe seç" : s);
        var copy = Find(entries, "copy");
        Assert.Equal("Kopyala", copy.Title);
        Assert.Equal("Önce bir öğe seç", copy.DisabledReason);
    }
}

public class CommandFilterTests
{
    private static ActionCommand Command(string id, string title, params string[] keywords) =>
        new(id, title, "", "", "", true, "", keywords, "", false);

    [Fact]
    public void EmptyQueryKeepsCuratedOrder()
    {
        IReadOnlyList<ActionCommand> commands = [Command("b", "Beta"), Command("a", "Alpha")];
        Assert.Equal(["b", "a"], CommandFilter.Filter(commands, "  ").Select(c => c.Id));
    }

    [Fact]
    public void WordStartsOutrankIncidentalSubstrings()
    {
        IReadOnlyList<ActionCommand> commands = [Command("open", "Open in browser"), Command("pen", "Pen")];
        Assert.Equal(["pen", "open"], CommandFilter.Filter(commands, "pen").Select(c => c.Id));
    }

    [Fact]
    public void TitleMatchesOutrankKeywordMatches()
    {
        IReadOnlyList<ActionCommand> commands = [Command("trash", "Move to Trash", "delete"), Command("delete", "Delete annotation")];
        Assert.Equal(["delete", "trash"], CommandFilter.Filter(commands, "delete").Select(c => c.Id));
    }

    [Fact]
    public void EveryTokenMustMatch()
    {
        IReadOnlyList<ActionCommand> commands = [Command("a", "Pin to folder"), Command("b", "Pin")];
        Assert.Equal(["a"], CommandFilter.Filter(commands, "pin fold").Select(c => c.Id));
    }

    [Theory]
    [InlineData("kırp", "kirp")]
    [InlineData("KLASÖR", "klasor")]
    [InlineData("İşaretle", "isaretle")]
    [InlineData("Görsel", "gorsel")]
    public void FoldingIgnoresCaseAndDiacritics(string value, string folded) => Assert.Equal(folded, CommandFilter.Fold(value));

    [Fact]
    public void TurkishKeywordsFindCommands()
    {
        var entries = ActionCatalog.Entries(new ActionContext { SelectedCount = 1, Kind = ItemKind.Image, HasFile = true }, new Keymap());
        Assert.Equal("crop", CommandFilter.Filter(entries, "kirp").First().Id);
        Assert.Equal("search", CommandFilter.Filter(entries, "arama").First().Id);
    }
}

public class PreviewZoomTests
{
    [Fact]
    public void StartsFittedAndClampsToFourTimes()
    {
        var zoom = new PreviewZoom();
        Assert.Equal(100, zoom.Percent);
        Assert.False(zoom.CanPan);
        zoom.Wheel(-5);
        Assert.Equal(1, zoom.Scale);
        for (var i = 0; i < 40; i++) zoom.Wheel(1);
        Assert.Equal(4, zoom.Scale);
        Assert.True(zoom.CanPan);
        zoom.Reset();
        Assert.Equal(1, zoom.Scale);
    }

    [Fact]
    public void TwelveNotchesReachTheMaximum()
    {
        var zoom = new PreviewZoom();
        for (var i = 0; i < 12; i++) zoom.Wheel(1);
        Assert.InRange(zoom.Scale, 3.9, 4);
    }

    [Fact]
    public void IgnoresInvalidInput()
    {
        var zoom = new PreviewZoom();
        zoom.Wheel(double.NaN);
        zoom.Magnify(double.PositiveInfinity);
        Assert.Equal(1, zoom.Scale);
        zoom.Magnify(0.5);
        Assert.Equal(1.5, zoom.Scale, 6);
    }

    [Fact]
    public void AnchoredOffsetKeepsThePointUnderTheCursor()
    {
        // Zooming 1x → 2x around the viewport center of a 400px image keeps its center in place.
        Assert.Equal(200, PreviewZoom.AnchoredOffset(0, 200, 1, 2, 400, 400), 6);
        // Offsets clamp to the scrollable range.
        Assert.Equal(0, PreviewZoom.AnchoredOffset(0, 0, 2, 1, 400, 400), 6);
        Assert.Equal(400, PreviewZoom.AnchoredOffset(400, 400, 2, 2, 400, 400), 6);
    }
}
