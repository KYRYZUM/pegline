using Pegline.Core.Input;
using Pegline.Core.Models;
using Pegline.Core.Search;
using Pegline.Core.Storage;
using Pegline.Core.Text;

namespace Pegline.Core.Library;

/// <summary>
/// Library browsing state: the visible query results, selection, focus and keyboard actions.
/// Actions that need a window (copy, preview, pickers, palette) are raised through <see cref="Requested"/>.
/// </summary>
public sealed class LibraryModel
{
    public const int PageStep = 9;

    private readonly LibraryStore _store;
    private readonly Func<DateTimeOffset> _clock;
    private readonly LibraryQueryEngine _engine = new();
    private readonly HashSet<Guid> _selected = [];
    private Dictionary<Guid, int> _indices = [];
    private List<Guid> _ids = [];
    private Guid? _anchor;

    public LibraryModel(LibraryStore store, Func<DateTimeOffset>? clock = null)
    {
        _store = store;
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    public LibraryQuery Query { get; private set; } = new();

    public IReadOnlyList<Guid> Ids => _ids;

    public IReadOnlySet<Guid> Selected => _selected;

    public Guid? Focused { get; private set; }

    /// <summary>The typed search; it reaches <see cref="Query"/> after a debounce or on <see cref="FocusContent"/>.</summary>
    public string Search { get; private set; } = "";

    public bool ListMode { get; set; }

    /// <summary>Grid columns used by vertical movement; list mode always moves one row.</summary>
    public int Columns { get; set; } = 4;

    public bool ShowingPinnedOnly => Query.Scope.Kind == LibraryScopeKind.Favorites || Query.FavoritesOnly;

    public Keymap Keymap { get; set; } = new();

    /// <summary>An action the window has to carry out (copy, paste, preview, edit, pickers…) for these ids.</summary>
    public event EventHandler<(string Action, IReadOnlyList<Guid> Ids)>? Requested;

    /// <summary>Raised after the visible results, selection or focus change.</summary>
    public event EventHandler? Changed;

    public int? IndexOf(Guid id) => _indices.TryGetValue(id, out var index) ? index : null;

    public ClipItem? FocusedItem => Focused is { } id ? _store.Find(id) : null;

    /// <summary>Selected ids in visible order; the focused item when nothing is selected.</summary>
    public IReadOnlyList<Guid> ActionIds
    {
        get
        {
            if (_selected.Count == 0) return Focused is { } id ? [id] : [];
            if (_selected.Count == _ids.Count) return _ids;
            return [.. _selected.Where(_indices.ContainsKey).OrderBy(id => _indices[id])];
        }
    }

    // ----- Loading -----

    /// <summary>Opening (or re-entering) the Library keeps its selection and focuses the first result when none is focused.</summary>
    public void Open()
    {
        Reload();
        FocusContent();
    }

    /// <summary>Re-runs the query. A reset clears selection and focus (scope and filter changes).</summary>
    public void Reload(bool reset = false)
    {
        SyncVisual();
        Run(reset);
    }

    // ----- Visual search -----

    /// <summary>Ranks images by what they show; null when this build has no model.</summary>
    public IVisualSearch? Visual { get; set; }

    /// <summary>True while visual matches for the current search are being computed.</summary>
    public bool VisualPending { get; private set; }

    /// <summary>Asks again for visual matches (new images were indexed).</summary>
    public void RefreshVisual()
    {
        _visualFor = null;
        Reload();
    }

    /// <summary>Visual results depend on the search and the scope it ranks in, not on order.</summary>
    private static LibraryQuery VisualKey(LibraryQuery query) => query with { VisualIds = [], Sort = LibrarySort.Newest };

    public static bool WantsVisual(LibraryQuery query) =>
        query.Search.Trim().Length > 0 && query.SearchMode != LibrarySearchMode.Text
        && query.Kind is null or ItemKind.Image && query.Scope.Kind != LibraryScopeKind.Trash;

    private LibraryQuery? _visualFor;
    private CancellationTokenSource? _visualCancel;

    private void SyncVisual()
    {
        var key = VisualKey(Query);
        if (key.Equals(_visualFor)) return;
        _visualFor = key;
        _visualCancel?.Cancel();
        VisualPending = false;
        if (Query.VisualIds.Count > 0) Query = Query with { VisualIds = [] };
        if (Visual is not { } visual || !WantsVisual(Query)) return;
        // Rank within the chosen folder, tag, pin and date scope: a strong match elsewhere must not hide one here.
        var corpus = Query with { Search = "", Kind = ItemKind.Image, SearchMode = LibrarySearchMode.All, VisualIds = [] };
        var eligible = _engine.Run(_store.Items, _store.Folders, corpus, _clock());
        if (eligible.Count == 0) return;
        var cancel = _visualCancel = new CancellationTokenSource();
        VisualPending = true;
        _ = FetchVisual(visual, key, Query.Search.Trim(), eligible, cancel.Token);
    }

    private async Task FetchVisual(IVisualSearch visual, LibraryQuery key, string text, IReadOnlyList<Guid> eligible, CancellationToken cancel)
    {
        IReadOnlyList<Guid> ids;
        try
        {
            ids = await visual.SearchAsync(text, eligible, cancel);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (cancel.IsCancellationRequested || !key.Equals(_visualFor)) return;
        VisualPending = false;
        if (ids.Count > 0) Query = Query with { VisualIds = ids };
        Run(reset: false);
    }

    private void Run(bool reset)
    {
        var ids = _engine.Run(_store.Items, _store.Folders, Query, _clock());
        _ids = [.. ids];
        _indices = new Dictionary<Guid, int>(_ids.Count);
        for (var i = 0; i < _ids.Count; i++) _indices[_ids[i]] = i;
        _selected.IntersectWith(_ids);
        if (Focused is { } focused && !_indices.ContainsKey(focused))
        {
            Focused = null;
            _anchor = null;
        }
        if (reset)
        {
            _selected.Clear();
            Focused = null;
            _anchor = null;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Enters the results explicitly: commits a pending search and focuses the first result if needed.</summary>
    public void FocusContent()
    {
        if (ApplyPendingSearch()) Reload(reset: true);
        if (Focused is null && _ids.Count > 0) Select(_ids[0]);
    }

    public void SetSearch(string value) => Search = value;

    /// <summary>Commits the typed search; the first search switches newest-first to best match and back.</summary>
    public bool ApplyPendingSearch()
    {
        if (Query.Search == Search) return false;
        var sort = Query.Sort;
        if (Query.Search.Length == 0 && Search.Length > 0 && sort == LibrarySort.Newest) sort = LibrarySort.Relevance;
        if (Search.Length == 0 && sort == LibrarySort.Relevance) sort = LibrarySort.Newest;
        Query = Query with { Search = Search, Sort = sort };
        return true;
    }

    public void ChangeScope(LibraryScope scope)
    {
        Query = Query with { Scope = scope };
        Reload(reset: true);
    }

    public void SetKind(ItemKind? kind)
    {
        Query = Query with { Kind = kind };
        Reload(reset: true);
    }

    public void SetSort(LibrarySort sort)
    {
        Query = Query with { Sort = sort };
        Reload();
    }

    public void SetSearchMode(LibrarySearchMode mode)
    {
        Query = Query with { SearchMode = mode };
        Reload(reset: true);
    }

    public void SetFavorites(bool value)
    {
        Query = Query with { FavoritesOnly = value };
        Reload(reset: true);
    }

    public void TogglePinnedOnly()
    {
        if (Query.Scope.Kind == LibraryScopeKind.Favorites)
        {
            Query = Query with { Scope = LibraryScope.Saved, FavoritesOnly = false };
            Reload(reset: true);
        }
        else SetFavorites(!Query.FavoritesOnly);
    }

    public void SetDays(int? days)
    {
        Query = Query with { Days = days };
        Reload(reset: true);
    }

    public void SetDomain(string domain)
    {
        Query = Query with { Domain = domain.Trim() };
        Reload(reset: true);
    }

    public void ToggleTagFilter(string tag)
    {
        var tags = Query.Tags.ToHashSet(Folding.Comparer);
        if (!tags.Add(tag)) tags.Remove(tag);
        Query = Query with { Tags = tags };
        Reload(reset: true);
    }

    public void ClearFilters()
    {
        Search = "";
        Query = Query with { Kind = null, FavoritesOnly = false, Tags = new HashSet<string>(), Days = null, Domain = "", Search = "", SearchMode = LibrarySearchMode.All, Sort = Query.Sort == LibrarySort.Relevance ? LibrarySort.Newest : Query.Sort };
        Reload(reset: true);
    }

    /// <summary>Shows a record that may lie outside the current filters by switching to the scope that holds it.</summary>
    public void Reveal(Guid id)
    {
        if (_store.Find(id) is not { } item) return;
        if (!_indices.ContainsKey(id))
        {
            Search = "";
            Query = new LibraryQuery { Scope = item.IsTrashed ? LibraryScope.Trash : item.IsSaved ? LibraryScope.Saved : LibraryScope.Recent };
            Reload(reset: true);
        }
        Select(id);
    }

    // ----- Selection and movement -----

    public bool Select(Guid id, bool toggle = false, bool extending = false, bool preservingSelection = false)
    {
        if (!_indices.TryGetValue(id, out var target)) return false;
        if (extending && _anchor is { } anchor && _indices.TryGetValue(anchor, out var start))
        {
            _selected.Clear();
            for (var i = Math.Min(start, target); i <= Math.Max(start, target); i++) _selected.Add(_ids[i]);
        }
        else if (toggle)
        {
            if (!_selected.Add(id)) _selected.Remove(id);
            _anchor = id;
        }
        else if (!preservingSelection || !_selected.Contains(id))
        {
            _selected.Clear();
            _selected.Add(id);
            _anchor = id;
        }
        Focused = id;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void SelectAll()
    {
        _selected.UnionWith(_ids);
        if (Focused is null && _ids.Count > 0)
        {
            Focused = _ids[0];
            _anchor = Focused;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Move(int delta, bool extending = false)
    {
        if (_ids.Count == 0) return;
        var target = Focused is { } id && _indices.TryGetValue(id, out var index) ? Math.Clamp(index + delta, 0, _ids.Count - 1) : 0;
        Select(_ids[target], extending: extending);
    }

    /// <summary>Moves one grid row, keeping the column; a short last row clamps to its final item.</summary>
    public void MoveVertically(int direction, bool extending = false)
    {
        if (_ids.Count == 0) return;
        if (Focused is not { } id || !_indices.TryGetValue(id, out var index))
        {
            Select(_ids[0]);
            return;
        }
        var width = ListMode ? 1 : Math.Max(1, Columns);
        var row = index / width;
        var lastRow = (_ids.Count - 1) / width;
        var targetRow = Math.Clamp(row + direction, 0, lastRow);
        if (targetRow == row) return;
        Select(_ids[Math.Min(_ids.Count - 1, targetRow * width + index % width)], extending: extending);
    }

    public void MoveToBoundary(bool last)
    {
        if (_ids.Count == 0) return;
        Select(last ? _ids[^1] : _ids[0]);
    }

    public void MovePage(int direction)
    {
        if (_ids.Count == 0) return;
        var index = Focused is { } id && _indices.TryGetValue(id, out var i) ? i : 0;
        Select(_ids[Math.Clamp(index + direction * PageStep, 0, _ids.Count - 1)]);
    }

    // ----- Record actions -----

    public void TogglePin()
    {
        var ids = ActionIds;
        if (ids.Count == 0) return;
        _store.TogglePin(ids);
        Reload();
    }

    public void Retain()
    {
        _store.Retain(ActionIds);
        Reload();
    }

    /// <summary>Moves records to the recoverable Trash and focuses the next survivor. Never deletes from Trash.</summary>
    public void TrashSelection(IReadOnlyList<Guid>? explicitIds = null)
    {
        if (Query.Scope.Kind == LibraryScopeKind.Trash) return;
        var targets = explicitIds ?? ActionIds;
        if (targets.Count == 0) return;
        var removed = targets.ToHashSet();
        var pivot = Focused is { } id && _indices.TryGetValue(id, out var index) ? index : 0;
        Guid? next = _ids.Skip(pivot).Where(i => !removed.Contains(i)).Cast<Guid?>().FirstOrDefault()
            ?? _ids.Take(pivot).Reverse().Where(i => !removed.Contains(i)).Cast<Guid?>().FirstOrDefault();
        _store.MoveToTrash(targets);
        Reload();
        if (next is { } survivor) Select(survivor);
    }

    /// <summary>Restores the latest Trash batch and focuses its first record. Returns false when there is nothing to undo.</summary>
    public bool UndoTrash()
    {
        var restored = _store.UndoTrash();
        Reload();
        if (restored.Count == 0) return false;
        if (!Select(restored[0]) && _ids.Count > 0 && Focused is null) Select(_ids[0]);
        return true;
    }

    public void Restore()
    {
        var ids = ActionIds.Where(id => _store.Find(id)?.IsTrashed == true).ToList();
        if (ids.Count == 0) return;
        _store.Restore(ids);
        Reload();
    }

    public void DeletePermanently()
    {
        var ids = ActionIds.Where(id => _store.Find(id)?.IsTrashed == true).ToList();
        if (ids.Count == 0) return;
        _store.DeletePermanently(ids);
        Reload();
    }

    public ClipItem NewNote(string name)
    {
        var note = _store.CreateNote("", name);
        Search = "";
        Query = new LibraryQuery { Scope = LibraryScope.Saved, Kind = ItemKind.Text };
        Reload(reset: true);
        Select(note.Id);
        return note;
    }

    // ----- Keyboard -----

    /// <summary>
    /// Routes a key in the Library. While an editor has focus only the palette, search, pane, note and
    /// folder shortcuts with Ctrl (or function keys) act; every other key belongs to the editor.
    /// </summary>
    public bool HandleKey(string gesture, bool editing)
    {
        var modified = gesture.StartsWith("Ctrl+", StringComparison.Ordinal) || gesture.StartsWith("Alt+", StringComparison.Ordinal);
        var function = gesture.Split('+')[^1] is { Length: >= 2 } key && key[0] == 'F' && char.IsAsciiDigit(key[1]);
        foreach (var raw in Keymap.Resolve(gesture))
        {
            var action = LibraryAction(raw);
            if (editing)
            {
                var allowed = action switch
                {
                    "actions" or "librarySearch" or "newNote" or "newFolder" => modified,
                    "focusNextPane" or "focusPreviousPane" => modified || function,
                    _ => false,
                };
                if (!allowed) continue;
            }
            if (Perform(action)) return true;
        }
        return false;
    }

    /// <summary>The Library keeps its own Trash keys: take-down keys trash and undo restores from Trash.</summary>
    public static string LibraryAction(string action) => action switch
    {
        "remove" => "trash",
        "undo" => "undoTrash",
        _ => action,
    };

    /// <summary>Executes a Library action without a key event. Returns false when it does not apply here.</summary>
    public bool Perform(string action)
    {
        switch (action)
        {
            case "previous": Move(-1); break;
            case "next": Move(1); break;
            case "up": MoveVertically(-1); break;
            case "down": MoveVertically(1); break;
            case "first": MoveToBoundary(last: false); break;
            case "last": MoveToBoundary(last: true); break;
            case "previousPage": MovePage(-1); break;
            case "nextPage": MovePage(1); break;
            case "extendPrevious": Move(-1, extending: true); break;
            case "extendNext": Move(1, extending: true); break;
            case "extendUp": MoveVertically(-1, extending: true); break;
            case "extendDown": MoveVertically(1, extending: true); break;
            case "selectAll": SelectAll(); break;
            case "pin": TogglePin(); break;
            case "pinnedOnly":
                TogglePinnedOnly();
                FocusContent();
                break;
            case "allTypes" or "images" or "texts" or "links":
                SetKind(action switch { "images" => ItemKind.Image, "texts" => ItemKind.Text, "links" => ItemKind.Link, _ => null });
                FocusContent();
                break;
            case "trash": TrashSelection(); break;
            case "undoTrash": UndoTrash(); break;
            case "copy" or "paste" or "preview" or "open" or "folder" or "name" or "tag" or "export" or "copyOCR"
                or "actions" or "librarySearch" or "focusNextPane" or "focusPreviousPane" or "newNote" or "newFolder" or "close":
                Requested?.Invoke(this, (action, ActionIds));
                break;
            case "edit" or "markup" or "crop":
                if (ActionIds is [var single] && _store.Find(single) is { IsTrashed: false } item
                    && (action == "edit" ? item.Kind == ItemKind.Text : item.Kind == ItemKind.Image))
                    Requested?.Invoke(this, (action, [single]));
                break;
            default:
                return false;
        }
        return true;
    }
}
