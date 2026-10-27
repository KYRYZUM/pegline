using System.Globalization;
using Pegline.Core.Input;
using Pegline.Core.Library;
using Pegline.Core.Models;

namespace Pegline.Core.Actions;

public enum ActionOrigin { Line, ShelfPreview, Library, LibraryPreview, Application }

/// <summary>One palette row. <see cref="Symbol"/> is a semantic icon name the UI maps to a glyph.</summary>
public sealed record ActionCommand(
    string Id,
    string Title,
    string Subtitle,
    string KeyHint,
    string Symbol,
    bool IsEnabled,
    string Group,
    IReadOnlyList<string> Keywords,
    string DisabledReason,
    bool IsActive);

/// <summary>
/// What the palette acts on. The caller captures its origin and selection before showing the
/// palette, so commands stay independent of window focus.
/// </summary>
public sealed class ActionContext
{
    public ActionOrigin Origin { get; init; } = ActionOrigin.Line;
    public int SelectedCount { get; init; }
    public ItemKind? Kind { get; init; }
    public bool HasFile { get; init; }
    public bool IsRecording { get; init; }
    public bool IsDeleted { get; init; }
    public bool AllPinned { get; init; }
    public bool HasItems { get; init; }
    public bool HasLatestImage { get; init; }
    public bool CanUndoShelfRemoval { get; init; }
    public bool CanUndoLibraryTrash { get; init; }
    public bool IsMarkupEditing { get; init; }
    public bool AnnotationSelected { get; init; }
    public bool CanUndoMarkup { get; init; }
    public bool CanRedoMarkup { get; init; }
    public bool HasAnnotations { get; init; }
    public bool IsTextEditing { get; init; }
    public bool InInbox { get; init; }
    public bool ActiveFavorites { get; init; }
    public bool LineRevealed { get; init; }
    public bool LibraryAvailable { get; init; } = true;
    public bool CanCheckUpdates { get; init; }
    public bool CanInstallUpdate { get; init; }
    public string GlobalToggleHint { get; init; } = "";
    public string CopyLatestHint { get; init; } = "";
    public IReadOnlySet<ItemKind> VisibleKinds { get; init; } = new HashSet<ItemKind>();
    public LibraryScope LibraryScope { get; init; } = LibraryScope.Saved;
    public IReadOnlyList<Folder> Folders { get; init; } = [];
    public IReadOnlyList<string> Tags { get; init; } = [];
    public IReadOnlyList<Folder> ItemFolders { get; init; } = [];
    public IReadOnlyList<string> ItemTags { get; init; } = [];
    public ItemKind? LibraryKind { get; init; }
    public bool LibraryListMode { get; init; }
    public bool LibraryInspectorVisible { get; init; } = true;
    public bool LibrarySidebarVisible { get; init; } = true;
    public LibrarySort LibrarySort { get; init; } = LibrarySort.Newest;
    public LibrarySearchMode LibrarySearchMode { get; init; } = LibrarySearchMode.All;
    public int? LibraryDays { get; init; }
    public bool HasLibraryFilters { get; init; }
    public bool CanTakeDownLeft { get; init; }
    public bool CanTakeDownRight { get; init; }

    public bool IsLibrary => Origin is ActionOrigin.Library or ActionOrigin.LibraryPreview;
    public bool IsPreview => Origin is ActionOrigin.ShelfPreview or ActionOrigin.LibraryPreview;
    public bool HasSelection => SelectedCount > 0;
    public bool SingleSelection => SelectedCount == 1;
    public bool IsImage => SingleSelection && Kind == ItemKind.Image && HasFile && !IsRecording && !IsDeleted;
    public bool IsText => SingleSelection && Kind == ItemKind.Text && !IsDeleted;
    public bool IsLink => SingleSelection && Kind == ItemKind.Link && !IsDeleted;
}

/// <summary>Builds the context-aware, searchable Actions palette (Ctrl+K).</summary>
public static class ActionCatalog
{
    public const string MenuPrefix = "menu.";
    public const string OpenFolderPrefix = "folder.open.";
    public const string OpenTagPrefix = "tag.open.";
    public const string AssignFolderPrefix = "folder.assign.";
    public const string MoveFolderPrefix = "folder.move.";
    public const string UnassignFolderPrefix = "folder.unassign.";
    public const string RemoveTagPrefix = "tag.unassign.";

    /// <summary>Frequently used operations lead the unfiltered list.</summary>
    private static readonly string[] Leading =
        ["search", "library", "copy", "paste", "preview", "pin", "folder", "name", "tag", "crop", "marker", "markup", "edit", "copyOCR", "pinnedOnly"];

    /// <summary>Every other browsing shortcut stays discoverable, navigation included.</summary>
    private static readonly string[] Remaining =
    [
        "previous", "next", "up", "down", "first", "last", "previousPage", "nextPage", "selectAll", "close", "switchShelf",
        "toggleShelves", "export", "trash", "remove", "undo", "toggleLinks", "toggleTexts", "toggleImages", "librarySearch",
        "allTypes", "images", "texts", "links", "open", "select", "pen", "rectangle", "ellipse", "arrow", "line",
        "annotationText", "finishEditing", "focusNextPane", "focusPreviousPane", "undoTrash", "extendPrevious", "extendNext",
        "extendUp", "extendDown", "select1", "select2", "select3", "select4", "select5", "select6", "select7", "select8", "select9",
    ];

    public static readonly IReadOnlyList<string> MenuActions =
    [
        "settings", "settingsGeneral", "settingsHistory", "settingsSources", "settingsShortcuts", "settingsAbout", "clearUnsavedHistory",
        "newNote", "newFolder", "newSubfolder", "renameFolder", "removeFolder", "renameTag", "removeTag", "retain", "restore", "editDetails",
        "scopeSaved", "scopeFavorites", "scopeUnorganized", "scopeRecent", "scopeTrash",
        "grid", "list", "toggleSidebar", "toggleInspector", "toggleFullscreen", "filters", "clearFilters",
        "sortNewest", "sortOldest", "sortTitle", "sortTitleDescending", "sortCopied", "sortRelevance",
        "searchAll", "searchText", "searchVisual", "daysAny", "days7", "days30",
        "refreshLink", "openFile", "revealFile", "saveToDesktop", "takeDownLeft", "takeDownRight", "trimVideo",
        "resetZoom", "markupUndo", "markupRedo", "markupDelete", "markupClear", "markupSave", "markupFinish", "markupStyles",
        "toggleLine", "copyLatestImage", "openScreenshotsFolder", "checkUpdates", "installUpdate", "quit",
    ];

    private static readonly string[] MarkupTools = ["select", "pen", "marker", "rectangle", "ellipse", "arrow", "line", "annotationText"];

    public static IReadOnlyList<ActionCommand> Entries(ActionContext c, Keymap keymap, Func<string, string>? translate = null, Func<string, string>? display = null)
    {
        var t = translate ?? (s => s);
        var show = display ?? (s => s);
        var result = new List<ActionCommand>();

        foreach (var action in Leading.Concat(Remaining))
        {
            var (enabled, reason) = KeyboardAvailability(action, c);
            var (group, symbol, keywords) = KeyboardMetadata(action);
            var title = action switch
            {
                "search" => "Search contents",
                "librarySearch" => "Search library",
                "pin" when c.AllPinned => "Unpin",
                _ => KeyboardTitle(action),
            };
            var scope = KeyboardScope(action, c, t);
            var groupTitle = t(group);
            result.Add(new ActionCommand(action, t(title), scope == groupTitle ? "" : scope, KeyboardHint(action, c, keymap, show),
                symbol, enabled, groupTitle, keywords, enabled ? "" : t(reason), KeyboardActive(action, c)));
        }

        foreach (var action in MenuActions)
        {
            var (enabled, reason) = MenuAvailability(action, c);
            var (title, group, symbol, keywords) = MenuMetadata(action, t);
            if (action == "toggleLine") title = t(c.LineRevealed ? "Hide shelves" : "Show shelves");
            if (action == "toggleInspector") title = t(c.LibraryInspectorVisible ? "Hide details" : "Show details");
            var hint = action switch
            {
                "newNote" or "newFolder" => show(keymap.Primary(action)),
                _ => MenuHint(action, c, show),
            };
            result.Add(new ActionCommand(MenuPrefix + action, title, "", hint, symbol, enabled, t(group), keywords,
                enabled ? "" : t(reason), MenuActive(action, c)));
        }

        var canAssign = c.HasSelection && !c.IsDeleted;
        foreach (var folder in c.Folders)
        {
            var id = folder.Id.ToString();
            result.Add(new ActionCommand(OpenFolderPrefix + id, t("Open folder") + ": " + folder.Name, t("Library"), "", "folder",
                c.LibraryAvailable, t("Folders"), ["folder", "klasör", "open", "aç"], c.LibraryAvailable ? "" : t("Library unavailable"),
                c.LibraryScope.Kind == LibraryScopeKind.Folder && c.LibraryScope.FolderId == folder.Id));
            result.Add(new ActionCommand(AssignFolderPrefix + id, t("Add to folder") + ": " + folder.Name, t("Selected item"), "", "folderAdd",
                canAssign, t("Organization"), ["add", "folder", "organize", "ekle", "klasör", "düzenle"], canAssign ? "" : t("Select an item first"), false));
            var canMove = c.IsLibrary && canAssign && c.LibraryScope.Kind == LibraryScopeKind.Folder && c.LibraryScope.FolderId != folder.Id;
            result.Add(new ActionCommand(MoveFolderPrefix + id, t("Move to folder") + ": " + folder.Name, t("Selected item"), "", "folder",
                canMove, t("Organization"), ["move", "folder", "taşı", "klasör"], canMove ? "" : t("Select items in a different folder first"), false));
        }
        foreach (var tag in c.Tags)
        {
            var active = c.LibraryScope.Kind == LibraryScopeKind.Tag && c.LibraryScope.Tag == tag;
            result.Add(new ActionCommand(OpenTagPrefix + tag, t("Tags") + ": #" + tag, t("Library"), "", "tag",
                c.LibraryAvailable, t("Tags"), ["tag", "etiket", "open", "aç"], c.LibraryAvailable ? "" : t("Library unavailable"), active));
        }
        var single = c.SingleSelection && !c.IsDeleted;
        foreach (var folder in c.ItemFolders)
            result.Add(new ActionCommand(UnassignFolderPrefix + folder.Id, t("Remove from folder") + ": " + folder.Name, t("Selected item"), "", "folderRemove",
                single, t("Organization"), ["remove", "folder", "klasörden", "çıkar"], single ? "" : t("Select one item first"), false));
        foreach (var tag in c.ItemTags)
            result.Add(new ActionCommand(RemoveTagPrefix + tag, t("Remove tag") + ": #" + tag, t("Selected item"), "", "tagRemove",
                single, t("Organization"), ["remove", "tag", "etiket", "kaldır"], single ? "" : t("Select one item first"), false));
        return result;
    }

    // ----- Keyboard actions -----

    public static string KeyboardTitle(string action) => action switch
    {
        "library" => "Open Library",
        "previous" => "Previous item",
        "next" => "Next item",
        "up" => "Move up",
        "down" => "Move down",
        "first" => "First item",
        "last" => "Last item",
        "previousPage" => "Previous page",
        "nextPage" => "Next page",
        "copy" => "Copy",
        "paste" => "Paste into previous app",
        "selectAll" => "Select all",
        "pin" => "Pin",
        "pinnedOnly" => "Show pinned items",
        "preview" => "Preview",
        "close" => "Close",
        "switchShelf" => "Switch shelf",
        "toggleShelves" => "Toggle all shelves",
        "export" => "Export copies",
        "trash" => "Move to Trash",
        "remove" => "Take down",
        "undo" => "Undo take down",
        "toggleLinks" => "Toggle Links",
        "toggleTexts" => "Toggle Texts",
        "toggleImages" => "Toggle Images",
        "folder" => "Pin to folder",
        "name" => "Name and pin",
        "tag" => "Add tags",
        "search" => "Search shelves",
        "librarySearch" => "Search Library",
        "allTypes" => "All types",
        "images" => "Images",
        "texts" => "Texts",
        "links" => "Links",
        "open" => "Open in browser",
        "edit" => "Edit text",
        "copyOCR" => "Copy text from image",
        "actions" => "Actions",
        "markup" => "Image markup",
        "crop" => "Crop image",
        "select" => "Select annotation",
        "pen" => "Pen",
        "marker" => "Marker",
        "rectangle" => "Rectangle",
        "ellipse" => "Ellipse",
        "arrow" => "Arrow",
        "line" => "Line",
        "annotationText" => "Text annotation",
        "finishEditing" => "Finish editing",
        "focusNextPane" => "Focus next pane",
        "focusPreviousPane" => "Focus previous pane",
        "undoTrash" => "Undo move to Trash",
        "newNote" => "New note",
        "newFolder" => "New folder",
        "extendPrevious" => "Extend selection left",
        "extendNext" => "Extend selection right",
        "extendUp" => "Extend selection up",
        "extendDown" => "Extend selection down",
        _ when IsNumbered(action) => "Select item " + action[^1],
        _ => action,
    };

    private static bool IsNumbered(string action) =>
        action.Length == 7 && action.StartsWith("select", StringComparison.Ordinal) && char.IsAsciiDigit(action[6]);

    private static (bool, string) Require(bool enabled, string reason) => (enabled, enabled ? "" : reason);

    private static (bool Enabled, string Reason) KeyboardAvailability(string action, ActionContext c)
    {
        var selected = c.HasSelection && !c.IsDeleted;
        var browsing = c.Origin != ActionOrigin.Application;
        const string noItems = "No items in this view";
        switch (action)
        {
            case "library" or "librarySearch" or "allTypes" or "images" or "texts" or "links":
                return Require(c.LibraryAvailable, "Library unavailable");
            case "search" or "toggleShelves" or "toggleImages" or "toggleTexts" or "toggleLinks":
                return (true, "");
            case "copy" or "paste" or "preview" or "pin" or "folder" or "name" or "tag":
                if (action == "paste" && c.IsTextEditing) return (false, "Finish editing first");
                return Require(selected, "Select an item first");
            case "open": return Require(c.IsLink, "Select a link first");
            case "crop" or "markup" or "copyOCR": return Require(c.IsImage, "Select one image first");
            case var tool when MarkupTools.Contains(tool): return Require(c.IsImage, "Select one image first");
            case "edit": return Require(c.IsText, "Select a text item first");
            case "finishEditing": return Require(c.IsTextEditing, "Start editing text first");
            case "pinnedOnly": return Require(browsing, "Open a shelf or Library first");
            case "export": return Require(c.HasSelection, "Select an item first");
            case "trash": return Require(selected, "Select an item first");
            case "remove": return Require(!c.IsLibrary && selected, c.IsLibrary ? "Available on the shelves" : "Select an item first");
            case "undo": return Require(!c.IsLibrary && c.CanUndoShelfRemoval, "Nothing to undo");
            case "undoTrash": return Require(c.IsLibrary && c.CanUndoLibraryTrash, "Nothing to undo");
            case "focusNextPane" or "focusPreviousPane": return Require(c.Origin == ActionOrigin.Library, "Library unavailable");
            case "newNote" or "newFolder": return Require(c.LibraryAvailable, "Library unavailable");
            case "close": return Require(browsing && (c.LineRevealed || c.IsLibrary || c.IsPreview), "Open a shelf or Library first");
            case "switchShelf": return Require(c.Origin == ActionOrigin.Line, "Available on the shelves");
            case "selectAll" or "extendPrevious" or "extendNext" or "extendUp" or "extendDown":
                return Require(browsing && !c.IsPreview && c.HasItems, c.IsPreview ? "Available on the shelves or in Library" : noItems);
            case var numbered when IsNumbered(numbered): return Require(c.Origin == ActionOrigin.Line && c.HasItems, "Available on the shelves");
            case "first" or "last" or "previousPage" or "nextPage" or "previous" or "next" or "up" or "down":
                return Require(browsing && c.HasItems, noItems);
            default: return (false, "");
        }
    }

    private static string KeyboardScope(string action, ActionContext c, Func<string, string> t)
    {
        const string onShelves = "On the shelves";
        switch (action)
        {
            case "search":
                return c.IsLibrary ? t("Search this view") + " · " + t("Library") : t("Search visible shelves: text, links and image OCR");
            case "librarySearch": return t("Search this view") + " · " + t("Library");
            case "library": return t(onShelves);
            case "crop" or "markup": return t("Image markup");
            case var tool when MarkupTools.Contains(tool): return t("Image markup");
            case "edit" or "finishEditing": return t("Text preview");
            case "allTypes" or "images" or "texts" or "links" or "focusNextPane" or "focusPreviousPane" or "undoTrash" or "newNote" or "newFolder":
                return t("Library");
            case "first" or "last" or "previousPage" or "nextPage": return t(c.IsLibrary ? "Library" : onShelves);
            case "toggleShelves" or "toggleImages" or "toggleTexts" or "toggleLinks" or "switchShelf" or "remove" or "undo":
                return t(onShelves);
            case var numbered when IsNumbered(numbered): return t(onShelves);
            default:
                if (c.SelectedCount > 1) return string.Format(CultureInfo.CurrentCulture, t("{0} selected"), c.SelectedCount);
                return t(c.IsLibrary ? "Library" : c.IsPreview ? "Preview" : onShelves);
        }
    }

    private static string KeyboardHint(string action, ActionContext c, Keymap keymap, Func<string, string> show)
    {
        string Key(string name) => show(keymap.Primary(name));
        var preview = c.Origin == ActionOrigin.Application ? "" : Key("preview");

        if (action == "search" && c.IsLibrary) return Key("librarySearch");
        if (action is "librarySearch" or "allTypes" or "images" or "texts" or "links") return Key(action);
        if (action is "crop" or "markup")
        {
            if (c.Origin == ActionOrigin.Library || c.IsPreview) return Key(action);
            return Sequence(preview, Key(action));
        }
        if (MarkupTools.Contains(action))
        {
            if (c.IsMarkupEditing) return Key(action);
            return c.IsPreview ? Sequence(Key("markup"), Key(action)) : Sequence(preview, Key("markup"), Key(action));
        }
        if (action == "edit" && !c.IsPreview)
            return c.Origin == ActionOrigin.Library ? Key(action) : Sequence(preview, Key("edit"));
        if (action == "open" && !c.IsLibrary && !c.IsPreview) return Sequence(preview, Key("open"));
        if (action == "finishEditing") return c.IsTextEditing ? Key(action) : "";

        IEnumerable<string> gestures = keymap.GesturesFor(action);
        if (c.IsTextEditing)
        {
            // While typing, only shortcuts with Ctrl reach browsing commands.
            if (action is not ("pin" or "pinnedOnly")) return "";
            gestures = gestures.Where(g => g.StartsWith("Ctrl+", StringComparison.Ordinal));
        }
        return string.Join(" / ", gestures.Distinct().Select(show));
    }

    private static string Sequence(params string[] keys) => keys.All(k => k.Length > 0) ? string.Join(" → ", keys) : "";

    private static bool KeyboardActive(string action, ActionContext c) => action switch
    {
        "pin" => c.AllPinned && c.HasSelection,
        "pinnedOnly" => c.ActiveFavorites,
        "toggleImages" => c.VisibleKinds.Contains(ItemKind.Image),
        "toggleTexts" => c.VisibleKinds.Contains(ItemKind.Text),
        "toggleLinks" => c.VisibleKinds.Contains(ItemKind.Link),
        "markup" => c.IsMarkupEditing,
        "allTypes" => c.IsLibrary && c.LibraryKind is null,
        "images" => c.IsLibrary && c.LibraryKind == ItemKind.Image,
        "texts" => c.IsLibrary && c.LibraryKind == ItemKind.Text,
        "links" => c.IsLibrary && c.LibraryKind == ItemKind.Link,
        _ => false,
    };

    private static (string Group, string Symbol, string[] Keywords) KeyboardMetadata(string action)
    {
        const string selection = "Selected item", navigation = "Navigation and selection", organization = "Organization", markup = "Image markup";
        return action switch
        {
            "search" => ("Search", "search", ["search", "find", "ara", "arama", "OCR", "text", "link", "image", "görsel", "metin"]),
            "librarySearch" => ("Search", "search", ["search", "find", "library", "ara", "arama", "kütüphane"]),
            "library" => ("Library", "library", ["library", "open", "kütüphane", "aç"]),
            "copy" => (selection, "copy", ["copy", "clipboard", "kopyala", "pano"]),
            "paste" => (selection, "paste", ["paste", "previous app", "yapıştır", "uygulama"]),
            "preview" => (selection, "preview", ["preview", "space", "önizleme", "büyüt"]),
            "pin" => (organization, "pin", ["pin", "unpin", "favorite", "favourite", "sabitle", "favori", "kaldır"]),
            "folder" => (organization, "folder", ["pin", "folder", "sabitle", "klasör"]),
            "name" => (organization, "rename", ["name", "rename", "pin", "title", "isim", "ad", "sabitle", "başlık"]),
            "tag" => (organization, "tag", ["tag", "label", "etiket", "organize", "düzenle"]),
            "pinnedOnly" => ("View", "pinned", ["pinned", "favorite", "favourite", "filter", "sabitlenen", "favori", "filtre"]),
            "copyOCR" => (selection, "ocr", ["OCR", "image", "text", "copy", "görsel", "metin", "kopyala"]),
            "edit" => (selection, "edit", ["text", "edit", "metin", "düzenle"]),
            "finishEditing" => (selection, "check", ["text", "finish", "save", "metin", "tamamla", "kaydet", "bitti"]),
            "open" => (selection, "openExternal", ["link", "open", "browser", "video", "bağlantı", "aç", "tarayıcı"]),
            "export" => (selection, "export", ["export", "save", "copies", "dışa aktar", "kaydet", "kopyalar"]),
            "trash" => (selection, "trash", ["delete", "trash", "sil", "çöp", "recoverable"]),
            "remove" => (selection, "remove", ["remove", "delete", "take down", "sil", "askıdan", "indir"]),
            "undo" => (selection, "undo", ["undo", "restore", "backspace", "geri al", "silme"]),
            "undoTrash" => ("Library", "undo", ["undo", "trash", "backspace", "geri al", "çöp"]),
            "focusNextPane" or "focusPreviousPane" => (navigation, "panes", ["focus", "pane", "keyboard", "tab", "odak", "bölme", "klavye"]),
            "newNote" => ("Library", "edit", ["new", "note", "yeni", "not"]),
            "newFolder" => ("Library", "folderAdd", ["new", "folder", "yeni", "klasör"]),
            "markup" => (markup, "markup", ["markup", "annotate", "draw", "edit image", "işaretle", "çiz", "görsel düzenle"]),
            "crop" => (markup, "crop", ["crop", "trim", "kırp", "kirp", "kes"]),
            "select" => (markup, "cursor", ["select", "annotation", "seç", "işaretleme"]),
            "pen" => (markup, "pen", ["pen", "draw", "pencil", "kalem", "çiz"]),
            "marker" => (markup, "highlighter", ["marker", "highlight", "highlighter", "işaretleyici", "vurgula", "fosforlu"]),
            "rectangle" => (markup, "rectangle", ["rectangle", "shape", "dikdörtgen", "şekil"]),
            "ellipse" => (markup, "ellipse", ["ellipse", "circle", "shape", "elips", "daire", "şekil"]),
            "arrow" => (markup, "arrow", ["arrow", "draw", "ok", "çiz"]),
            "line" => (markup, "lineTool", ["line", "draw", "çizgi", "çiz"]),
            "annotationText" => (markup, "font", ["text", "annotation", "add", "metin", "yazı", "ekle"]),
            "allTypes" => ("Library views", "library", ["all", "types", "filter", "tümü", "tür", "filtre"]),
            "images" => ("Library views", "image", ["images", "photos", "filter", "görsel", "fotoğraf", "filtre"]),
            "texts" => ("Library views", "text", ["texts", "notes", "filter", "metin", "not", "filtre"]),
            "links" => ("Library views", "link", ["links", "URLs", "filter", "bağlantı", "filtre"]),
            "toggleImages" => ("Shelves", "image", ["image", "photo", "shelf", "toggle", "görsel", "fotoğraf", "askı", "göster", "gizle"]),
            "toggleTexts" => ("Shelves", "text", ["text", "shelf", "toggle", "metin", "askı", "göster", "gizle"]),
            "toggleLinks" => ("Shelves", "link", ["link", "shelf", "toggle", "bağlantı", "askı", "göster", "gizle"]),
            "toggleShelves" => ("Shelves", "shelves", ["shelf", "toggle", "visible", "askı", "göster", "gizle"]),
            "switchShelf" => ("Shelves", "switch", ["shelf", "switch", "tab", "askı", "değiştir", "geç"]),
            "close" => ("View", "close", ["close", "dismiss", "escape", "kapat", "çık"]),
            "previous" => (navigation, "left", ["navigate", "move", "previous", "left", "gez", "git", "önceki", "sol"]),
            "next" => (navigation, "right", ["navigate", "move", "next", "right", "gez", "git", "sonraki", "sağ"]),
            "first" => (navigation, "first", ["navigate", "first", "newest", "beginning", "git", "ilk", "en yeni", "baş"]),
            "last" => (navigation, "last", ["navigate", "last", "oldest", "end", "git", "son", "en eski"]),
            "previousPage" => (navigation, "pagePrevious", ["previous", "page", "left", "önceki", "sayfa", "sol"]),
            "nextPage" => (navigation, "pageNext", ["next", "page", "right", "sonraki", "sayfa", "sağ"]),
            "up" => (navigation, "up", ["navigate", "move", "up", "above", "shelf", "git", "yukarı", "üst", "askı"]),
            "down" => (navigation, "down", ["navigate", "move", "down", "below", "shelf", "git", "aşağı", "alt", "askı"]),
            _ => (navigation, "selection", ["select", "selection", "all", "seç", "seçim", "tümü"]),
        };
    }

    // ----- Menu actions -----

    private static (bool Enabled, string Reason) MenuAvailability(string action, ActionContext c)
    {
        switch (action)
        {
            case "newNote" or "newFolder" or "scopeSaved" or "scopeFavorites" or "scopeUnorganized" or "scopeRecent" or "scopeTrash"
                or "grid" or "list" or "toggleSidebar" or "toggleInspector" or "toggleFullscreen" or "filters" or "clearFilters"
                or "sortNewest" or "sortOldest" or "sortTitle" or "sortTitleDescending" or "sortCopied" or "sortRelevance"
                or "searchAll" or "searchText" or "searchVisual" or "daysAny" or "days7" or "days30" or "clearUnsavedHistory":
                return Require(c.LibraryAvailable, "Library unavailable");
            case "newSubfolder":
                var rootFolder = c.LibraryScope.Kind == LibraryScopeKind.Folder
                    && c.Folders.Any(f => f.Id == c.LibraryScope.FolderId && f.ParentId is null);
                return Require(c.IsLibrary && rootFolder, "Select a top-level folder first");
            case "renameFolder" or "removeFolder":
                return Require(c.IsLibrary && c.LibraryScope.Kind == LibraryScopeKind.Folder, "Select a folder first");
            case "renameTag" or "removeTag":
                return Require(c.IsLibrary && c.LibraryScope.Kind == LibraryScopeKind.Tag, "Select a tag first");
            case "retain": return Require(c.HasSelection && !c.IsDeleted, "Select an item first");
            case "editDetails": return Require(c.SingleSelection && !c.IsDeleted, "Select one item first");
            case "restore": return Require(c.HasSelection && c.IsDeleted, "Select an item in Trash first");
            case "refreshLink": return Require(c.IsLink, "Select a link first");
            case "openFile" or "revealFile": return Require(c.SingleSelection && c.Kind == ItemKind.Image && c.HasFile, "Select one image first");
            case "saveToDesktop":
                return Require(!c.IsLibrary && c.SingleSelection && c.Kind == ItemKind.Image && c.InInbox && !c.IsDeleted, "Select an image in the screenshots inbox first");
            case "takeDownLeft": return Require(!c.IsLibrary && c.SingleSelection && c.Kind == ItemKind.Image && c.CanTakeDownLeft, "No items to the left");
            case "takeDownRight": return Require(!c.IsLibrary && c.SingleSelection && c.Kind == ItemKind.Image && c.CanTakeDownRight, "No items to the right");
            case "trimVideo": return Require(c.SingleSelection && c.Kind == ItemKind.Image && c.HasFile && c.IsRecording && !c.IsDeleted, "Select a video first");
            case "resetZoom" or "markupStyles": return Require(c.IsImage, "Select one image first");
            case "markupUndo": return Require((c.IsMarkupEditing || (c.IsPreview && c.IsImage)) && c.CanUndoMarkup, "Nothing to undo");
            case "markupRedo": return Require((c.IsMarkupEditing || (c.IsPreview && c.IsImage)) && c.CanRedoMarkup, "Nothing to redo");
            case "markupDelete": return Require(c.IsMarkupEditing && c.AnnotationSelected, "Select an annotation first");
            case "markupClear": return Require(c.IsMarkupEditing && c.HasAnnotations, "No annotations to clear");
            case "markupSave" or "markupFinish": return Require(c.IsMarkupEditing, "Start image markup first");
            case "copyLatestImage": return Require(c.HasLatestImage, "No images on the shelves");
            case "checkUpdates": return Require(c.CanCheckUpdates, "Update checks unavailable");
            case "installUpdate": return Require(c.CanInstallUpdate, "No update available");
            default: return (true, "");
        }
    }

    private static string MenuHint(string action, ActionContext c, Func<string, string> show) => action switch
    {
        "settings" => show("Ctrl+,"),
        "toggleLine" => c.GlobalToggleHint.Length > 0 ? show(c.GlobalToggleHint) : "",
        "copyLatestImage" => c.CopyLatestHint.Length > 0 ? show(c.CopyLatestHint) : "",
        "markupUndo" => c.IsMarkupEditing || (c.IsPreview && c.CanUndoMarkup) ? show("Ctrl+Z") : "",
        "markupRedo" => c.IsMarkupEditing || (c.IsPreview && c.CanRedoMarkup) ? show("Ctrl+Shift+Z") : "",
        "markupDelete" => c.IsMarkupEditing ? "Backspace / Delete" : "",
        "markupSave" => c.IsMarkupEditing ? show("Ctrl+S") : "",
        "markupFinish" => c.IsMarkupEditing ? show("Ctrl+Enter") + " / Esc" : "",
        _ => "",
    };

    private static bool MenuActive(string action, ActionContext c)
    {
        if (!c.IsLibrary) return false;
        return action switch
        {
            "scopeSaved" => c.LibraryScope.Kind == LibraryScopeKind.Saved,
            "scopeFavorites" => c.LibraryScope.Kind == LibraryScopeKind.Favorites,
            "scopeUnorganized" => c.LibraryScope.Kind == LibraryScopeKind.Unorganized,
            "scopeRecent" => c.LibraryScope.Kind == LibraryScopeKind.Recent,
            "scopeTrash" => c.LibraryScope.Kind == LibraryScopeKind.Trash,
            "grid" => !c.LibraryListMode,
            "list" => c.LibraryListMode,
            "toggleSidebar" => c.LibrarySidebarVisible,
            "toggleInspector" => c.LibraryInspectorVisible,
            "sortNewest" => c.LibrarySort == LibrarySort.Newest,
            "sortOldest" => c.LibrarySort == LibrarySort.Oldest,
            "sortTitle" => c.LibrarySort == LibrarySort.Title,
            "sortTitleDescending" => c.LibrarySort == LibrarySort.TitleDescending,
            "sortCopied" => c.LibrarySort == LibrarySort.Copied,
            "sortRelevance" => c.LibrarySort == LibrarySort.Relevance,
            "searchAll" => c.LibrarySearchMode == LibrarySearchMode.All,
            "searchText" => c.LibrarySearchMode == LibrarySearchMode.Text,
            "searchVisual" => c.LibrarySearchMode == LibrarySearchMode.Visual,
            "daysAny" => c.LibraryDays is null,
            "days7" => c.LibraryDays == 7,
            "days30" => c.LibraryDays == 30,
            _ => false,
        };
    }

    private static (string Title, string Group, string Symbol, string[] Keywords) MenuMetadata(string action, Func<string, string> t)
    {
        const string app = "Application", library = "Library", views = "Library views", organization = "Organization", markup = "Image markup", selection = "Selected item";
        string Settings(string page) => t(page) + " · " + t("Settings");
        return action switch
        {
            "settings" => (t("Settings…"), app, "settings", ["settings", "preferences", "ayarlar", "tercihler"]),
            "settingsGeneral" => (Settings("General"), app, "sliders", ["appearance", "theme", "color", "size", "language", "sounds", "login", "genel", "görünüm", "renk", "boyut", "dil", "ses", "açılış"]),
            "settingsHistory" => (Settings("History"), app, "history", ["history", "retention", "days", "time", "geçmiş", "süre", "gün"]),
            "clearUnsavedHistory" => (t("Clear unsaved copies…"), app, "trash", ["history", "clear", "cleanup", "unsaved", "geçmiş", "temizle", "kaydedilmemiş"]),
            "settingsSources" => (Settings("Sources"), app, "sources", ["sources", "capture", "clipboard", "screenshots", "watch folder", "kaynak", "yakala", "pano", "ekran görüntüsü", "izle"]),
            "settingsShortcuts" => (Settings("Keyboard shortcuts"), app, "keyboard", ["keyboard", "shortcut", "keys", "remap", "accessibility", "klavye", "kısayol", "tuş", "erişilebilirlik"]),
            "settingsAbout" => (Settings("About"), app, "info", ["about", "version", "updates", "hakkında", "sürüm", "güncelleme"]),
            "newNote" => (t("New note"), library, "edit", ["new", "note", "text", "yeni", "not", "metin"]),
            "newFolder" => (t("New folder"), organization, "folderAdd", ["new", "create", "folder", "yeni", "oluştur", "klasör"]),
            "newSubfolder" => (t("New subfolder"), organization, "folderAdd", ["new", "subfolder", "yeni", "alt klasör"]),
            "renameFolder" => (t("Rename folder"), organization, "rename", ["rename", "folder", "isim", "ad", "klasör"]),
            "removeFolder" => (t("Remove folder"), organization, "folderRemove", ["remove", "delete", "folder", "sil", "kaldır", "klasör"]),
            "renameTag" => (t("Rename tag"), organization, "rename", ["rename", "tag", "isim", "ad", "etiket"]),
            "removeTag" => (t("Remove tag"), organization, "tagRemove", ["remove", "delete", "tag", "sil", "kaldır", "etiket"]),
            "retain" => (t("Save to library"), organization, "save", ["save", "keep", "retain", "permanent", "kaydet", "sakla", "kalıcı"]),
            "restore" => (t("Restore"), organization, "undo", ["restore", "trash", "recover", "geri yükle", "kurtar", "çöp"]),
            "editDetails" => (t("Edit item details"), organization, "edit", ["edit", "details", "title", "note", "name", "düzenle", "ayrıntı", "başlık", "not", "isim"]),
            "scopeSaved" => (t("My library"), views, "library", ["saved", "library", "kaydedilen", "kütüphane"]),
            "scopeFavorites" => (t("Favorites"), views, "pinned", ["favorite", "favourite", "pinned", "favori", "sabitlenen"]),
            "scopeUnorganized" => (t("Unorganized"), views, "inbox", ["unorganized", "inbox", "düzenlenmemiş", "gelen"]),
            "scopeRecent" => (t("Recent copies"), views, "clock", ["recent", "history", "copies", "son", "geçmiş", "kopyalar"]),
            "scopeTrash" => (t("Trash"), views, "trash", ["trash", "deleted", "recover", "çöp", "silinen", "kurtar"]),
            "grid" => (t("Grid"), views, "grid", ["grid", "cards", "ızgara", "kart"]),
            "list" => (t("List"), views, "list", ["list", "table", "liste", "tablo"]),
            "toggleSidebar" => (t("Toggle sidebar"), views, "sidebar", ["toggle", "sidebar", "yan çubuk", "göster", "gizle"]),
            "toggleInspector" => (t("Show details"), views, "inspector", ["inspector", "details", "note", "title", "detay", "ayrıntı", "not", "başlık"]),
            "toggleFullscreen" => (t("Toggle full screen"), views, "fullscreen", ["fullscreen", "window", "tam ekran", "pencere"]),
            "filters" => (t("Filters"), views, "filter", ["filter", "domain", "tags", "date", "filtre", "alan adı", "etiket", "tarih"]),
            "clearFilters" => (t("Clear filters"), views, "filterClear", ["clear", "reset", "filters", "temizle", "sıfırla", "filtre"]),
            "sortNewest" => (t("Newest first"), views, "down", ["sort", "newest", "date", "sırala", "en yeni", "tarih"]),
            "sortOldest" => (t("Oldest first"), views, "up", ["sort", "oldest", "date", "sırala", "en eski", "tarih"]),
            "sortTitle" => (t("Title"), views, "sort", ["sort", "title", "alphabet", "A Z", "sırala", "başlık", "alfabe"]),
            "sortTitleDescending" => (t("Title, Z–A"), views, "sort", ["sort", "title", "descending", "Z A", "sırala", "başlık", "azalan"]),
            "sortCopied" => (t("Last copied"), views, "copy", ["sort", "copied", "last", "sırala", "kopyalanan", "son"]),
            "sortRelevance" => (t("Best match"), views, "search", ["sort", "relevance", "match", "sırala", "alaka", "eşleşme"]),
            "searchAll" => (t("Text and visual content"), views, "ocr", ["search mode", "text", "visual", "arama türü", "metin", "görsel"]),
            "searchText" => (t("Text only (including OCR)"), views, "search", ["search mode", "text", "OCR", "arama türü", "metin"]),
            "searchVisual" => (t("Visual content only"), views, "image", ["search mode", "visual", "image", "arama türü", "görsel"]),
            "daysAny" => (t("Any time"), views, "calendar", ["filter", "date", "any time", "filtre", "tarih", "tüm zamanlar"]),
            "days7" => (t("Last 7 days"), views, "calendar", ["filter", "date", "week", "filtre", "tarih", "hafta"]),
            "days30" => (t("Last 30 days"), views, "calendar", ["filter", "date", "month", "filtre", "tarih", "ay"]),
            "refreshLink" => (t("Refresh link preview"), selection, "refresh", ["refresh", "thumbnail", "link", "title", "yenile", "küçük resim", "bağlantı", "başlık"]),
            "openFile" => (t("Open file"), selection, "openExternal", ["open", "file", "image", "video", "aç", "dosya", "görsel"]),
            "revealFile" => (t("Show in File Explorer"), selection, "folder", ["explorer", "reveal", "file", "dosya", "göster", "gezgin"]),
            "saveToDesktop" => (t("Save to Desktop"), selection, "desktop", ["save", "desktop", "kaydet", "masaüstü"]),
            "takeDownLeft" => (t("Take down everything to the left"), selection, "first", ["remove", "left", "take down", "kaldır", "sol", "askıdan indir"]),
            "takeDownRight" => (t("Take down everything to the right"), selection, "last", ["remove", "right", "take down", "kaldır", "sağ", "askıdan indir"]),
            "trimVideo" => (t("Trim video"), selection, "trim", ["trim", "video", "recording", "cut", "kırp", "kes", "kayıt"]),
            "resetZoom" => (t("Reset zoom"), markup, "refresh", ["reset", "zoom", "fit", "sıfırla", "yakınlaştır", "sığdır"]),
            "markupUndo" => (t("Undo") + " · " + t("Image markup"), markup, "undo", ["undo", "markup", "annotation", "geri al", "işaretleme"]),
            "markupRedo" => (t("Redo") + " · " + t("Image markup"), markup, "redo", ["redo", "markup", "annotation", "yinele", "işaretleme"]),
            "markupDelete" => (t("Delete annotation"), markup, "trash", ["delete", "annotation", "sil", "işaretleme"]),
            "markupClear" => (t("Clear annotations"), markup, "eraser", ["clear", "all", "annotation", "temizle", "tümü", "işaretleme"]),
            "markupSave" => (t("Save") + " · " + t("Image markup"), markup, "save", ["save", "markup", "kaydet", "işaretleme"]),
            "markupFinish" => (t("Done") + " · " + t("Image markup"), markup, "check", ["done", "finish", "markup", "bitti", "tamamla", "işaretleme"]),
            "markupStyles" => (t("Annotation style"), markup, "sliders", ["style", "color", "width", "fill", "font", "annotation", "stil", "renk", "kalınlık", "dolgu", "yazı", "işaretleme"]),
            "toggleLine" => (t("Show shelves"), app, "shelves", ["show", "hide", "shelf", "line", "göster", "gizle", "askı"]),
            "copyLatestImage" => (t("Copy latest image"), app, "copy", ["copy", "latest", "image", "kopyala", "son", "görsel"]),
            "openScreenshotsFolder" => (t("Open screenshots folder"), app, "folder", ["screenshots", "folder", "open", "ekran görüntüsü", "klasör", "aç"]),
            "checkUpdates" => (t("Check for updates…"), app, "refresh", ["check", "update", "release", "kontrol", "güncelleme", "sürüm"]),
            "installUpdate" => (t("Install update…"), app, "download", ["install", "update", "download", "yükle", "güncelleme", "indir"]),
            "quit" => (t("Quit Pegline"), app, "power", ["quit", "exit", "app", "çık", "kapat", "uygulama"]),
            _ => (action, app, "command", []),
        };
    }
}
