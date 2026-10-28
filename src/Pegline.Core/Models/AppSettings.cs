namespace Pegline.Core.Models;

public enum ShelfTheme
{
    Soft,
    Neutral,
}

public enum CardSize
{
    Small,
    Medium,
    Large,
}

/// <summary>User preferences. Defaults match the reference product: image/text capture off, link capture and enrichment on.</summary>
public sealed class AppSettings
{
    public static readonly string[] Languages = ["system", "en", "tr", "es", "fr", "pt-BR", "az", "zh-Hans", "ja", "ko"];

    public List<ItemKind> VisibleKinds { get; set; } = [ItemKind.Image, ItemKind.Text, ItemKind.Link];
    public bool CaptureText { get; set; }
    public bool CaptureImages { get; set; }
    public bool CaptureLinks { get; set; } = true;
    public bool FetchLinkMetadata { get; set; } = true;
    public bool IndexImages { get; set; } = true;
    public bool OpenAfterScreenshot { get; set; } = true;
    public bool CaptureScreenshots { get; set; } = true;

    /// <summary>Days of history per kind; 0 keeps forever.</summary>
    public Dictionary<ItemKind, int> RetentionDays { get; set; } = new()
    {
        [ItemKind.Image] = 30,
        [ItemKind.Text] = 30,
        [ItemKind.Link] = 30,
    };

    public Dictionary<string, string> GlobalShortcuts { get; set; } = new()
    {
        ["toggle"] = "Ctrl+Alt+T",
        ["search"] = "Ctrl+Alt+S",
        ["library"] = "",
        ["copyLatest"] = "",
        ["screenshot"] = "Ctrl+Alt+A",
    };

    /// <summary>Overrides of local browsing bindings; an empty string disables the action.</summary>
    public Dictionary<string, string> LocalShortcuts { get; set; } = [];

    public List<string> WatchFolders { get; set; } = [];

    public ShelfTheme Theme { get; set; } = ShelfTheme.Soft;
    public CardSize CardSize { get; set; } = CardSize.Medium;
    public bool Sounds { get; set; }
    public bool LaunchAtLogin { get; set; }
    public string Language { get; set; } = "system";
    public bool AutoCheckUpdates { get; set; } = true;

    public bool LibraryListMode { get; set; }
    public bool LibraryInspectorVisible { get; set; } = true;
    public bool LibrarySidebarVisible { get; set; } = true;
    public List<Guid> RecentPinFolderIds { get; set; } = [];
    public string LastExportFolder { get; set; } = "";

    public const int MaxWatchFolders = 3;
    public static readonly int[] RetentionChoices = [1, 3, 7, 15, 30, 0];
}
