using Pegline.Core.Models;
using Pegline.Core.Text;

namespace Pegline.Core.Library;

/// <summary>What the Library shows: a scope plus filters, search and order.</summary>
public sealed record LibraryQuery
{
    public LibraryScope Scope { get; init; } = LibraryScope.Saved;
    public ItemKind? Kind { get; init; }
    public string Search { get; init; } = "";
    public LibrarySearchMode SearchMode { get; init; } = LibrarySearchMode.All;
    public bool FavoritesOnly { get; init; }
    public IReadOnlySet<string> Tags { get; init; } = new HashSet<string>();
    public int? Days { get; init; }
    public string Domain { get; init; } = "";
    public LibrarySort Sort { get; init; } = LibrarySort.Newest;

    /// <summary>Ids that visual search ranked for the current text, best first (Phase 7).</summary>
    public IReadOnlyList<Guid> VisualIds { get; init; } = [];

    public bool HasFilters => Kind is not null || Search.Length > 0 || SearchMode != LibrarySearchMode.All || FavoritesOnly
        || Tags.Count > 0 || Days is not null || Domain.Length > 0;

    public bool Equals(LibraryQuery? other) =>
        other is not null && Scope == other.Scope && Kind == other.Kind && Search == other.Search && SearchMode == other.SearchMode
        && FavoritesOnly == other.FavoritesOnly && Tags.SetEquals(other.Tags) && Days == other.Days && Domain == other.Domain
        && Sort == other.Sort && VisualIds.SequenceEqual(other.VisualIds);

    public override int GetHashCode() => HashCode.Combine(Scope, Kind, Search, SearchMode, FavoritesOnly, Days, Domain, Sort);
}

/// <summary>Evaluates a <see cref="LibraryQuery"/> against the store's records.</summary>
public sealed class LibraryQueryEngine
{
    private readonly Dictionary<Guid, (DateTimeOffset Updated, string Search, string Title)> _folded = [];

    public IReadOnlyList<Guid> Run(IReadOnlyList<ClipItem> items, IReadOnlyList<Folder> folders, LibraryQuery query, DateTimeOffset now)
    {
        var scopeFolders = query.Scope.Kind == LibraryScopeKind.Folder
            ? folders.Where(f => f.Id == query.Scope.FolderId || f.ParentId == query.Scope.FolderId).Select(f => f.Id).ToHashSet()
            : [];
        var terms = query.SearchMode == LibrarySearchMode.Visual ? [] : Folding.Terms(query.Search);
        var searching = query.Search.Trim().Length > 0;
        var visual = query.SearchMode == LibrarySearchMode.Text ? new HashSet<Guid>() : query.VisualIds.ToHashSet();
        var since = query.Days is { } days ? now.AddDays(-days) : (DateTimeOffset?)null;
        var domain = NormalizeDomain(query.Domain);

        var matches = new List<ClipItem>();
        foreach (var item in items)
        {
            if (!InScope(item, query.Scope, scopeFolders)) continue;
            if (query.Kind is { } kind && item.Kind != kind) continue;
            if (query.FavoritesOnly && !item.Pinned) continue;
            if (query.Tags.Count > 0 && !query.Tags.All(tag => item.Tags.Contains(tag, Folding.Comparer))) continue;
            if (since is { } start && item.CreatedAt < start) continue;
            if (domain.Length > 0 && !MatchesDomain(item, domain)) continue;
            if (searching)
            {
                var text = terms.Length > 0 && terms.All(Folded(item).Search.Contains);
                if (!text && !visual.Contains(item.Id)) continue;
            }
            matches.Add(item);
        }

        IEnumerable<ClipItem> ordered = query.Sort switch
        {
            LibrarySort.Oldest => matches.OrderBy(i => i.CreatedAt),
            LibrarySort.Title => matches.OrderBy(i => Folded(i).Title, StringComparer.CurrentCulture).ThenByDescending(i => i.CreatedAt),
            LibrarySort.TitleDescending => matches.OrderByDescending(i => Folded(i).Title, StringComparer.CurrentCulture).ThenByDescending(i => i.CreatedAt),
            LibrarySort.Copied => matches.OrderByDescending(i => i.LastCopiedAt ?? i.CreatedAt),
            _ => matches.OrderByDescending(i => i.CreatedAt),
        };
        if (query.Sort == LibrarySort.Relevance && searching && visual.Count > 0)
        {
            // Literal text and OCR matches precede visual suggestions, which keep the model's rank.
            var rank = query.VisualIds.Select((id, index) => (id, index)).ToDictionary(p => p.id, p => p.index);
            ordered = matches
                .OrderBy(i => terms.Length > 0 && terms.All(Folded(i).Search.Contains) ? -1 : rank.GetValueOrDefault(i.Id, rank.Count))
                .ThenByDescending(i => i.CreatedAt);
        }
        return [.. ordered.Select(i => i.Id)];
    }

    public static bool InScope(ClipItem item, LibraryScope scope, IReadOnlySet<Guid>? scopeFolders = null)
    {
        if (scope.Kind == LibraryScopeKind.Trash) return item.IsTrashed;
        if (item.IsTrashed) return false;
        return scope.Kind switch
        {
            LibraryScopeKind.Saved => item.IsSaved,
            LibraryScopeKind.Favorites => item.Pinned,
            LibraryScopeKind.Unorganized => item.IsSaved && item.FolderIds.Count == 0 && item.Tags.Count == 0,
            LibraryScopeKind.Recent => !item.IsSaved,
            LibraryScopeKind.Folder => scopeFolders is null
                ? scope.FolderId is { } id && item.FolderIds.Contains(id)
                : item.FolderIds.Any(scopeFolders.Contains),
            LibraryScopeKind.Tag => item.Tags.Contains(scope.Tag ?? "", Folding.Comparer),
            _ => false,
        };
    }

    /// <summary>A record's sortable title: its name or title, else its first line of text or address.</summary>
    public static string TitleOf(ClipItem item)
    {
        if (item.DisplayTitle.Length > 0) return item.DisplayTitle;
        var source = item.Kind == ItemKind.Link ? item.Url : item.Text;
        var line = source.TrimStart().Split('\n', 2)[0].Trim();
        return line.Length > 120 ? line[..120] : line;
    }

    public static string HostOf(ClipItem item) =>
        item.Kind == ItemKind.Link && Uri.TryCreate(item.Url, UriKind.Absolute, out var uri) ? uri.Host.ToLowerInvariant() : "";

    private (DateTimeOffset Updated, string Search, string Title) Folded(ClipItem item)
    {
        if (_folded.TryGetValue(item.Id, out var cached) && cached.Updated == item.UpdatedAt) return cached;
        var text = string.Join('\n', new[]
        {
            item.Name, item.Title, item.Text, item.Url, item.Note, item.OcrText, item.LinkSiteName, item.LinkAuthor,
        }.Concat(item.Tags).Where(s => s.Length > 0));
        var value = (item.UpdatedAt, Folding.Fold(text), Folding.Fold(TitleOf(item)));
        _folded[item.Id] = value;
        return value;
    }

    private static string NormalizeDomain(string domain)
    {
        domain = domain.Trim().Trim('.').ToLowerInvariant();
        return domain.StartsWith("www.", StringComparison.Ordinal) ? domain[4..] : domain;
    }

    /// <summary>Literal host boundaries include subdomains without suffix collisions (youtube.com never matches notyoutube.com).</summary>
    private static bool MatchesDomain(ClipItem item, string domain)
    {
        var host = HostOf(item);
        if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];
        return host == domain || host.EndsWith("." + domain, StringComparison.Ordinal);
    }
}
