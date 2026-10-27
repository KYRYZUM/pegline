namespace Pegline.Core.Library;

public enum LibraryScopeKind { Saved, Favorites, Unorganized, Recent, Trash, Folder, Tag }

/// <summary>The Library sidebar selection: a fixed view, one folder or one tag.</summary>
public sealed record LibraryScope(LibraryScopeKind Kind, Guid? FolderId = null, string? Tag = null)
{
    public static readonly LibraryScope Saved = new(LibraryScopeKind.Saved);
    public static readonly LibraryScope Favorites = new(LibraryScopeKind.Favorites);
    public static readonly LibraryScope Unorganized = new(LibraryScopeKind.Unorganized);
    public static readonly LibraryScope Recent = new(LibraryScopeKind.Recent);
    public static readonly LibraryScope Trash = new(LibraryScopeKind.Trash);

    public static LibraryScope ForFolder(Guid id) => new(LibraryScopeKind.Folder, FolderId: id);

    public static LibraryScope ForTag(string tag) => new(LibraryScopeKind.Tag, Tag: tag);
}

public enum LibrarySort { Newest, Oldest, Title, TitleDescending, Copied, Relevance }

public enum LibrarySearchMode { All, Text, Visual }
