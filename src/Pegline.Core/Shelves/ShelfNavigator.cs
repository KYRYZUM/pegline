using Pegline.Core.Models;

namespace Pegline.Core.Shelves;

/// <summary>
/// Selection and keyboard navigation across the three hanging shelves. Pure state: the view supplies items
/// and page sizes, then renders <see cref="Row"/>, <see cref="Focused"/> and <see cref="Selection"/>.
/// </summary>
public sealed class ShelfNavigator
{
    public static readonly ItemKind[] Order = [ItemKind.Image, ItemKind.Text, ItemKind.Link];
    public const int MaxPageSize = 9;

    private readonly Dictionary<ItemKind, List<ClipItem>> _rows = Order.ToDictionary(k => k, _ => new List<ClipItem>());
    private readonly Dictionary<ItemKind, int> _offsets = Order.ToDictionary(k => k, _ => 0);
    private readonly Dictionary<ItemKind, int> _pageSizes = Order.ToDictionary(k => k, _ => MaxPageSize);
    private readonly List<Guid> _selection = [];
    private readonly HashSet<ItemKind> _pinnedOnly = [];
    private List<ItemKind> _visible = [.. Order];
    private IReadOnlyList<ClipItem> _all = [];
    private Guid? _anchor;

    public bool IsOpen { get; private set; }
    public ItemKind ActiveKind { get; private set; } = ItemKind.Image;
    public Guid? Focused { get; private set; }
    public IReadOnlyList<Guid> Selection => _selection;
    public IReadOnlyList<ItemKind> VisibleKinds => _visible;
    public IReadOnlySet<ItemKind> PinnedOnlyKinds => _pinnedOnly;
    public string Query { get; private set; } = "";
    public bool SearchEditing { get; private set; }
    public bool SearchActive { get; private set; }

    /// <summary>Raised when hiding the final visible row should close the shelves instead.</summary>
    public event EventHandler? CloseRequested;

    public IReadOnlyList<ClipItem> Row(ItemKind kind) => _rows[kind];

    public int Offset(ItemKind kind) => _offsets[kind];

    public int PageSize(ItemKind kind) => _pageSizes[kind];

    public bool IsVisible(ItemKind kind) => _visible.Contains(kind);

    public ClipItem? FocusedItem => Focused is { } id ? _rows[ActiveKind].FirstOrDefault(i => i.Id == id) : null;

    public void SetVisibleKinds(IEnumerable<ItemKind> kinds)
    {
        _visible = [.. Order.Where(kinds.Contains)];
        Refilter(preserveFocus: true);
    }

    public void SetPinnedOnly(IEnumerable<ItemKind> kinds)
    {
        _pinnedOnly.Clear();
        _pinnedOnly.UnionWith(kinds);
        Refilter(preserveFocus: true);
    }

    /// <summary>The number of cards that fit in a row; numbers 1–9 and Alt+arrows follow it.</summary>
    public void SetPageSize(ItemKind kind, int size)
    {
        _pageSizes[kind] = Math.Clamp(size, 1, MaxPageSize);
        if (Focused is not null && kind == ActiveKind) Reveal();
    }

    /// <summary>Replaces the item source (all records, newest first). Removed focus moves to the next survivor.</summary>
    public void SetItems(IReadOnlyList<ClipItem> items)
    {
        _all = items;
        Refilter(preserveFocus: true);
    }

    // ----- Open / close -----

    public void Open()
    {
        IsOpen = true;
        ClearSearchState();
        Rebuild();
        foreach (var kind in Order) _offsets[kind] = 0;
        FocusFirstAvailable(fallbackKind: _visible.FirstOrDefault(ActiveKind));
    }

    public void Close()
    {
        IsOpen = false;
        ClearSearchState();
        Rebuild();
        Focused = null;
        _selection.Clear();
        _anchor = null;
    }

    // ----- Search -----

    public void OpenSearch()
    {
        SearchEditing = true;
        SearchActive = true;
    }

    public void SetQuery(string query)
    {
        Query = query;
        SearchActive = true;
        Rebuild();
        foreach (var kind in Order) _offsets[kind] = 0;
        FocusFirstAvailable(fallbackKind: ActiveKind, preferCurrentKind: false);
    }

    /// <summary>Leaves the search editor for result browsing; the query stays applied.</summary>
    public void FinishSearch() => SearchEditing = false;

    /// <summary>Clears the query and closes shelf search.</summary>
    public void CancelSearch()
    {
        var hadQuery = Query.Length > 0;
        ClearSearchState();
        if (!hadQuery) return;
        var focus = Focused;
        Rebuild();
        if (focus is { } id && _rows[ActiveKind].Any(i => i.Id == id)) Reveal();
        else FocusFirstAvailable(fallbackKind: ActiveKind);
    }

    // ----- Horizontal -----

    public void Previous() => MoveBy(-1, extend: false);

    public void Next() => MoveBy(1, extend: false);

    public void ExtendPrevious() => MoveBy(-1, extend: true);

    public void ExtendNext() => MoveBy(1, extend: true);

    public void First() => MoveTo(0);

    public void Last() => MoveTo(_rows[ActiveKind].Count - 1);

    public void PreviousPage() => Page(-1);

    public void NextPage() => Page(1);

    /// <summary>Selects the numbered card (1–9) on the active row's visible page.</summary>
    public void SelectNumber(int number)
    {
        var row = _rows[ActiveKind];
        var index = _offsets[ActiveKind] + number - 1;
        if (number < 1 || number > _pageSizes[ActiveKind] || index >= row.Count) return;
        Focus(row[index], resetSelection: true);
    }

    // ----- Vertical -----

    public void Up() => Vertical(-1, extend: false);

    public void Down() => Vertical(1, extend: false);

    public void ExtendUp() => Vertical(-1, extend: true);

    public void ExtendDown() => Vertical(1, extend: true);

    /// <summary>Cycles the active shelf through visible, nonempty rows (Tab and Shift+Tab both advance).</summary>
    public void SwitchShelf()
    {
        var rows = _visible.Where(k => _rows[k].Count > 0).ToList();
        if (rows.Count == 0) return;
        var current = rows.IndexOf(ActiveKind);
        var target = rows[(current + 1) % rows.Count];
        if (target == ActiveKind && Focused is not null) return;
        MoveToRow(target, extend: false);
    }

    // ----- Selection -----

    public void SelectAll()
    {
        var row = _rows[ActiveKind];
        if (row.Count == 0) return;
        _selection.Clear();
        _selection.AddRange(row.Select(i => i.Id));
        Focused ??= row[0].Id;
        _anchor ??= Focused;
    }

    /// <summary>Ctrl+Click: adds or removes one card from the batch.</summary>
    public void ToggleSelection(Guid id)
    {
        var (kind, _) = Locate(id);
        if (kind is null) return;
        if (kind != ActiveKind)
        {
            ActiveKind = kind.Value;
            _selection.Clear();
        }
        if (!_selection.Remove(id)) _selection.Add(id);
        Focused = id;
        _anchor = id;
        Reveal();
    }

    /// <summary>Shift+Click: selects the range from the anchor to the card within one row.</summary>
    public void SelectRange(Guid id)
    {
        var (kind, index) = Locate(id);
        if (kind is null) return;
        var row = _rows[kind.Value];
        var anchorIndex = kind == ActiveKind && _anchor is { } anchor ? row.FindIndex(i => i.Id == anchor) : -1;
        ActiveKind = kind.Value;
        if (anchorIndex < 0)
        {
            Focus(row[index], resetSelection: true);
            return;
        }
        _selection.Clear();
        var step = index >= anchorIndex ? 1 : -1;
        for (var i = anchorIndex; i != index + step; i += step) _selection.Add(row[i].Id);
        Focused = id;
        Reveal();
    }

    /// <summary>Plain selection of one card (mouse hover/click focus without copying).</summary>
    public void Select(Guid id)
    {
        var (kind, index) = Locate(id);
        if (kind is null) return;
        ActiveKind = kind.Value;
        Focus(_rows[kind.Value][index], resetSelection: true);
    }

    /// <summary>The selected records in shelf order.</summary>
    public IReadOnlyList<ClipItem> SelectedItems()
    {
        var row = _rows[ActiveKind];
        var chosen = row.Where(i => _selection.Contains(i.Id)).ToList();
        if (chosen.Count == 0 && FocusedItem is { } focused) chosen.Add(focused);
        return chosen;
    }

    // ----- Row visibility -----

    public void ToggleKind(ItemKind kind)
    {
        if (_visible.Contains(kind))
        {
            if (_visible.Count == 1)
            {
                CloseRequested?.Invoke(this, EventArgs.Empty);
                return;
            }
            _visible.Remove(kind);
            if (kind == ActiveKind)
            {
                Rebuild();
                FocusFirstAvailable(fallbackKind: _visible[0]);
                return;
            }
        }
        else
        {
            _visible = [.. Order.Where(k => k == kind || _visible.Contains(k))];
        }
        Refilter(preserveFocus: true);
    }

    /// <summary>Ctrl+Shift+B: shows all rows, or only the active row when all are already shown.</summary>
    public void ToggleAllShelves()
    {
        if (_visible.Count == Order.Length) _visible = [ActiveKind];
        else _visible = [.. Order];
        Refilter(preserveFocus: true);
    }

    public void TogglePinnedOnly()
    {
        if (!_pinnedOnly.Remove(ActiveKind)) _pinnedOnly.Add(ActiveKind);
        var kind = ActiveKind;
        Rebuild();
        if (Focused is { } id && _rows[kind].Any(i => i.Id == id)) return;
        _offsets[kind] = 0;
        if (_rows[kind].Count > 0) Focus(_rows[kind][0], resetSelection: true);
        else
        {
            Focused = null;
            _selection.Clear();
        }
    }

    // ----- Internals -----

    private void ClearSearchState()
    {
        Query = "";
        SearchEditing = false;
        SearchActive = false;
    }

    private void Rebuild()
    {
        foreach (var kind in Order)
        {
            var row = _rows[kind];
            row.Clear();
            if (!_visible.Contains(kind)) continue;
            foreach (var item in _all)
            {
                if (item.Kind != kind || !item.IsOnShelf) continue;
                if (_pinnedOnly.Contains(kind) && !item.Pinned) continue;
                if (Query.Length > 0 && !ShelfSearch.Matches(item, Query)) continue;
                row.Add(item);
            }
            _offsets[kind] = Math.Clamp(_offsets[kind], 0, Math.Max(0, row.Count - 1));
        }
    }

    private void Refilter(bool preserveFocus)
    {
        var previousRows = Order.ToDictionary(k => k, k => _rows[k].Select(i => i.Id).ToList());
        var focus = Focused;
        var kind = ActiveKind;
        Rebuild();
        if (!IsOpen || !preserveFocus) return;

        var selectionSurvivors = _selection.Where(id => _rows[kind].Any(i => i.Id == id)).ToList();
        if (focus is { } id && _rows[kind].Any(i => i.Id == id))
        {
            _selection.Clear();
            _selection.AddRange(selectionSurvivors.Count > 0 ? selectionSurvivors : [id]);
            Reveal();
            return;
        }

        if (focus is { } removed && _rows[kind].Count > 0)
        {
            // Choose the next surviving neighbor after the removed card (or batch), else the previous one.
            var before = previousRows[kind];
            var current = _rows[kind].Select(i => i.Id).ToHashSet();
            var start = before.IndexOf(removed);
            var lastRemoved = Math.Max(start, before.FindLastIndex(x => _selection.Contains(x) && !current.Contains(x)));
            var next = before.Skip(lastRemoved + 1).FirstOrDefault(current.Contains);
            var previous = before.Take(Math.Max(start, 0)).LastOrDefault(current.Contains);
            var target = next != Guid.Empty ? next : previous;
            if (target != Guid.Empty)
            {
                Focus(_rows[kind].First(i => i.Id == target), resetSelection: true);
                return;
            }
        }
        if (focus is null && Focused is null && _rows.Values.All(r => r.Count == 0)) return;
        FocusFirstAvailable(fallbackKind: _visible.FirstOrDefault(kind));
    }

    private void FocusFirstAvailable(ItemKind fallbackKind, bool preferCurrentKind = false)
    {
        var candidates = preferCurrentKind ? [ActiveKind, .. _visible] : _visible;
        foreach (var kind in candidates)
        {
            if (!_visible.Contains(kind) || _rows[kind].Count == 0) continue;
            ActiveKind = kind;
            Focus(_rows[kind][_offsets[kind] = 0], resetSelection: true);
            return;
        }
        ActiveKind = fallbackKind;
        Focused = null;
        _selection.Clear();
        _anchor = null;
    }

    private void Focus(ClipItem item, bool resetSelection)
    {
        Focused = item.Id;
        if (resetSelection)
        {
            _selection.Clear();
            _selection.Add(item.Id);
            _anchor = item.Id;
        }
        Reveal();
    }

    private void Reveal()
    {
        var row = _rows[ActiveKind];
        var index = Focused is { } id ? row.FindIndex(i => i.Id == id) : -1;
        if (index < 0) return;
        var size = _pageSizes[ActiveKind];
        if (index < _offsets[ActiveKind]) _offsets[ActiveKind] = index;
        else if (index >= _offsets[ActiveKind] + size) _offsets[ActiveKind] = index - size + 1;
    }

    private int FocusIndex() => Focused is { } id ? _rows[ActiveKind].FindIndex(i => i.Id == id) : -1;

    private void MoveBy(int delta, bool extend)
    {
        var row = _rows[ActiveKind];
        var index = FocusIndex();
        if (index < 0)
        {
            if (row.Count > 0) Focus(row[0], resetSelection: true);
            return;
        }
        var target = Math.Clamp(index + delta, 0, row.Count - 1);
        if (target == index) return;
        if (extend) ExtendTo(row[target]);
        else Focus(row[target], resetSelection: true);
    }

    private void MoveTo(int index)
    {
        var row = _rows[ActiveKind];
        if (row.Count == 0) return;
        Focus(row[Math.Clamp(index, 0, row.Count - 1)], resetSelection: true);
    }

    private void Page(int direction)
    {
        var row = _rows[ActiveKind];
        if (row.Count == 0) return;
        var size = _pageSizes[ActiveKind];
        var index = Math.Max(FocusIndex(), 0);
        var column = index - _offsets[ActiveKind];
        var maxOffset = Math.Max(0, row.Count - size);
        var offset = Math.Clamp(_offsets[ActiveKind] + direction * size, 0, maxOffset);
        if (offset == _offsets[ActiveKind])
        {
            // Already on the first/last page: move to its edge.
            Focus(row[direction < 0 ? 0 : row.Count - 1], resetSelection: true);
            return;
        }
        _offsets[ActiveKind] = offset;
        Focus(row[Math.Clamp(offset + column, 0, row.Count - 1)], resetSelection: true);
    }

    private void Vertical(int direction, bool extend)
    {
        if (Focused is null) return;
        var position = _visible.IndexOf(ActiveKind);
        for (var r = position + direction; r >= 0 && r < _visible.Count; r += direction)
        {
            var kind = _visible[r];
            if (_rows[kind].Count == 0) continue;
            MoveToRow(kind, extend);
            return;
        }
    }

    private void MoveToRow(ItemKind target, bool extend)
    {
        var column = Math.Max(0, FocusIndex() - _offsets[ActiveKind]);
        var row = _rows[target];
        var index = Math.Min(_offsets[target] + column, row.Count - 1);
        if (extend && ActiveKind == target)
        {
            ExtendTo(row[index]);
            return;
        }
        ActiveKind = target;
        Focus(row[index], resetSelection: true);
    }

    private void ExtendTo(ClipItem item)
    {
        var row = _rows[ActiveKind];
        var anchorIndex = _anchor is { } anchor ? row.FindIndex(i => i.Id == anchor) : -1;
        if (anchorIndex < 0) anchorIndex = FocusIndex();
        var target = row.IndexOf(item);
        _selection.Clear();
        var step = target >= anchorIndex ? 1 : -1;
        for (var i = anchorIndex; i != target + step; i += step) _selection.Add(row[i].Id);
        Focused = item.Id;
        Reveal();
    }

    private (ItemKind? Kind, int Index) Locate(Guid id)
    {
        foreach (var kind in Order)
        {
            var index = _rows[kind].FindIndex(i => i.Id == id);
            if (index >= 0) return (kind, index);
        }
        return (null, -1);
    }
}
