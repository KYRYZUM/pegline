namespace Pegline.Core.Input;

/// <summary>Where a browsing shortcut is listened for. Two actions may share a key only when their places never overlap.</summary>
[Flags]
public enum ShortcutScope
{
    None = 0,
    Shelves = 1,
    Library = 2,
    Preview = 4,
    Markup = 8,
    TextEditing = 16,
}

/// <summary>How the Settings window lists, explains and checks shortcuts.</summary>
public static class ShortcutCatalog
{
    /// <summary>System-wide shortcuts, in the order Settings shows them.</summary>
    public static readonly IReadOnlyList<string> GlobalActions = ["toggle", "search", "library", "copyLatest", "screenshot"];

    /// <summary>Offered by the Library row's quick button.</summary>
    public const string LibraryPreset = "Ctrl+Alt+L";

    public static string GlobalTitle(string action) => action switch
    {
        "toggle" => "Show or hide the shelves",
        "search" => "Search shelves from other apps",
        "library" => "Open Library",
        "copyLatest" => "Copy latest image",
        "screenshot" => "Capture screen",
        _ => action,
    };

    public static string GlobalDescription(string action) => action switch
    {
        "toggle" => "Brings the shelves down over any app, and back up.",
        "search" => "Opens the shelves with search ready.",
        "library" => "Opens Library from other apps. The L key works on the shelves.",
        "copyLatest" => "Puts the newest image on the clipboard without opening anything.",
        "screenshot" => "Captures the display under the pointer and opens it for cropping.",
        _ => "",
    };

    public static readonly IReadOnlyList<(string Title, IReadOnlyList<string> Actions)> Groups =
    [
        ("Browsing", [
            "library", "copy", "paste", "preview", "pin", "pinnedOnly", "search", "actions", "close", "folder", "name", "tag", "export",
            "trash", "remove", "undo", "edit", "finishEditing", "open", "copyOCR", "markup", "crop",
            "toggleShelves", "toggleImages", "toggleTexts", "toggleLinks", "switchShelf",
        ]),
        ("Navigation and selection", [
            "previous", "next", "up", "down", "first", "last", "previousPage", "nextPage", "selectAll",
            "extendPrevious", "extendNext", "extendUp", "extendDown",
            "select1", "select2", "select3", "select4", "select5", "select6", "select7", "select8", "select9",
        ]),
        ("Library actions", [
            "librarySearch", "allTypes", "images", "texts", "links", "focusNextPane", "focusPreviousPane", "undoTrash", "newNote", "newFolder",
        ]),
        ("Image markup tools", ["select", "pen", "marker", "rectangle", "ellipse", "arrow", "line", "annotationText"]),
    ];

    private const ShortcutScope Browsing = ShortcutScope.Shelves | ShortcutScope.Library | ShortcutScope.Preview;

    public static ShortcutScope ScopeOf(string action) => action switch
    {
        "library" or "search" or "selectAll" or "pinnedOnly" or "extendPrevious" or "extendNext" or "extendUp" or "extendDown"
            => ShortcutScope.Shelves | ShortcutScope.Library,
        "remove" or "undo" or "switchShelf" or "toggleShelves" or "toggleImages" or "toggleTexts" or "toggleLinks"
            => ShortcutScope.Shelves,
        _ when action.Length == 7 && action.StartsWith("select", StringComparison.Ordinal) && char.IsAsciiDigit(action[6])
            => ShortcutScope.Shelves,
        "librarySearch" or "allTypes" or "images" or "texts" or "links" or "focusNextPane" or "focusPreviousPane" or "undoTrash"
            or "newNote" or "newFolder" => ShortcutScope.Library,
        "select" or "pen" or "marker" or "rectangle" or "ellipse" or "arrow" or "line" or "annotationText" => ShortcutScope.Markup,
        "finishEditing" => ShortcutScope.TextEditing,
        "close" or "actions" => Browsing | ShortcutScope.Markup,
        _ => Browsing,
    };

    /// <summary>English names of the places an action works, for the row's second line.</summary>
    public static IReadOnlyList<string> ScopeTitles(string action)
    {
        var scope = ScopeOf(action);
        var titles = new List<string>();
        if (scope.HasFlag(ShortcutScope.Shelves)) titles.Add("Shelves");
        if (scope.HasFlag(ShortcutScope.Library)) titles.Add("Library");
        if (scope.HasFlag(ShortcutScope.Preview)) titles.Add("Preview");
        if (scope.HasFlag(ShortcutScope.Markup)) titles.Add("Image markup");
        if (scope.HasFlag(ShortcutScope.TextEditing)) titles.Add("Text editing");
        return titles;
    }

    /// <summary>Other actions that would answer the same key in a place this action also works.</summary>
    public static IReadOnlyList<string> Conflicts(Keymap keymap, string action, string gesture)
    {
        gesture = Keymap.Canonical(gesture);
        if (gesture.Length == 0) return [];
        var scope = ScopeOf(action);
        return [.. Keymap.Defaults.Keys.Where(other => other != action
            && (ScopeOf(other) & scope) != 0
            && keymap.GesturesFor(other).Contains(gesture))];
    }

    /// <summary>A system-wide shortcut needs a key and Ctrl, Alt or Win, so plain typing never triggers it.</summary>
    public static bool IsValidGlobal(string gesture)
    {
        var parts = Keymap.Canonical(gesture).Split('+', StringSplitOptions.RemoveEmptyEntries);
        var modifiers = parts.Where(p => p is "Ctrl" or "Alt" or "Shift" or "Win").ToList();
        return parts.Length > modifiers.Count && modifiers.Any(m => m is "Ctrl" or "Alt" or "Win");
    }

    /// <summary>The other system-wide action already using the gesture, if any.</summary>
    public static string? GlobalConflict(IReadOnlyDictionary<string, string> shortcuts, string action, string gesture)
    {
        gesture = Keymap.Canonical(gesture);
        if (gesture.Length == 0) return null;
        return shortcuts.FirstOrDefault(p => p.Key != action && Keymap.Canonical(p.Value) == gesture).Key;
    }
}
