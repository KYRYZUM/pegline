using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Pegline.App.Localization;
using Pegline.App.Shelves;
using Pegline.Core.Library;
using Pegline.Core.Models;
using Pegline.Core.Storage;

namespace Pegline.App.Library;

/// <summary>
/// Virtualized card grid (or list) of Library results. Only rows inside the viewport are realized,
/// so twenty thousand records scroll as smoothly as twenty.
/// </summary>
internal sealed class LibraryGrid : Grid
{
    public const double CardWidth = 176;
    public const double CardHeight = 158;
    private const double ThumbHeight = 110;
    private const double Gap = 14;
    private const double Pad = 18;
    private const double ListRowHeight = 46;
    private const int CacheLimit = 400;

    private readonly LibraryStore _store;
    private readonly ThumbnailCache _thumbnails;
    private readonly ScrollViewer _scroll = new();
    private readonly Canvas _canvas = new();
    private readonly TextBlock _empty = new();
    private readonly Dictionary<Guid, (DateTimeOffset Version, bool List, Border Element)> _cache = [];
    private readonly HashSet<Guid> _realized = [];
    private IReadOnlyList<Guid> _ids = [];
    private IReadOnlySet<Guid> _selected = new HashSet<Guid>();
    private Guid? _focused;
    private bool _listMode;
    private Guid? _pressed;
    private Guid? _deferredClick;
    private Point _pressPoint;

    public LibraryGrid(LibraryStore store, ThumbnailCache thumbnails, ShelfPalette palette)
    {
        _store = store;
        _thumbnails = thumbnails;
        Palette = palette;
        Focusable = true;
        FocusVisualStyle = null;
        Background = Brushes.Transparent;
        _canvas.HorizontalAlignment = HorizontalAlignment.Left;
        _canvas.VerticalAlignment = VerticalAlignment.Top;
        _scroll.Content = _canvas;
        _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _scroll.Focusable = false;
        _scroll.ScrollChanged += (_, _) => Realize();
        Children.Add(_scroll);
        _empty.FontSize = 14;
        _empty.Foreground = palette.Muted;
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.VerticalAlignment = VerticalAlignment.Center;
        _empty.TextAlignment = TextAlignment.Center;
        _empty.IsHitTestVisible = false;
        Children.Add(_empty);
        SizeChanged += (_, e) =>
        {
            if (e.WidthChanged) Layout();
            else Realize();
        };
        MouseDown += (_, _) => Focus();
    }

    public ShelfPalette Palette { get; }

    public int Columns { get; private set; } = 1;

    public event EventHandler<int>? ColumnsChanged;

    public event EventHandler<(Guid Id, ModifierKeys Modifiers)>? ItemClicked;

    public event EventHandler<Guid>? ItemDoubleClicked;

    public event EventHandler<Guid>? ItemContextRequested;

    /// <summary>The pointer dragged an item past the system threshold; the handler runs the drag.</summary>
    public event EventHandler<Guid>? ItemDragged;

    public void Show(IReadOnlyList<Guid> ids, IReadOnlySet<Guid> selected, Guid? focused, bool listMode, string emptyText)
    {
        var modeChanged = listMode != _listMode;
        _listMode = listMode;
        var idsChanged = modeChanged || !ReferenceEquals(ids, _ids) && !ids.SequenceEqual(_ids);
        _ids = ids;
        _selected = selected;
        _focused = focused;
        _empty.Text = ids.Count == 0 ? emptyText : "";
        if (idsChanged) Layout();
        else
        {
            Realize();
            foreach (var id in _realized) Decorate(id);
        }
    }

    /// <summary>Drops cached cards so changed records render again.</summary>
    public void Invalidate(IEnumerable<Guid>? ids = null)
    {
        if (ids is null) _cache.Clear();
        else foreach (var id in ids) _cache.Remove(id);
        _canvas.Children.Clear();
        _realized.Clear();
        Realize();
    }

    public void EnsureVisible(Guid id)
    {
        var index = IndexOf(id);
        if (index < 0) return;
        var (top, height) = RowBounds(index / Columns);
        if (top < _scroll.VerticalOffset + Pad / 2) _scroll.ScrollToVerticalOffset(Math.Max(0, top - Pad));
        else if (top + height > _scroll.VerticalOffset + _scroll.ViewportHeight - Pad / 2)
            _scroll.ScrollToVerticalOffset(top + height - _scroll.ViewportHeight + Pad);
    }

    private int IndexOf(Guid id)
    {
        for (var i = 0; i < _ids.Count; i++)
            if (_ids[i] == id) return i;
        return -1;
    }

    private double RowHeight => _listMode ? ListRowHeight : CardHeight + Gap;

    private (double Top, double Height) RowBounds(int row) => (Pad + row * RowHeight, _listMode ? ListRowHeight : CardHeight);

    private void Layout()
    {
        var width = Math.Max(0, ActualWidth - 14); // leave room for the scroll bar
        var columns = _listMode ? 1 : Math.Max(1, (int)((width - 2 * Pad + Gap) / (CardWidth + Gap)));
        if (columns != Columns)
        {
            Columns = columns;
            ColumnsChanged?.Invoke(this, columns);
        }
        var rows = (_ids.Count + Columns - 1) / Columns;
        _canvas.Height = 2 * Pad + rows * RowHeight;
        _canvas.Width = width;
        _canvas.Children.Clear();
        _realized.Clear();
        Realize();
    }

    private void Realize()
    {
        if (_ids.Count == 0)
        {
            _canvas.Children.Clear();
            _realized.Clear();
            return;
        }
        var first = Math.Max(0, (int)((_scroll.VerticalOffset - Pad) / RowHeight) - 1);
        var last = (int)((_scroll.VerticalOffset + Math.Max(_scroll.ViewportHeight, ActualHeight)) / RowHeight) + 1;
        var start = first * Columns;
        var end = Math.Min(_ids.Count, (last + 1) * Columns);
        var wanted = new HashSet<Guid>();
        var listWidth = Math.Max(200, _canvas.Width - 2 * Pad);
        for (var index = start; index < end; index++)
        {
            var id = _ids[index];
            wanted.Add(id);
            var element = ElementFor(id);
            if (element is null) continue;
            var row = index / Columns;
            var column = index % Columns;
            Canvas.SetTop(element, Pad + row * RowHeight);
            Canvas.SetLeft(element, Pad + column * (CardWidth + Gap));
            if (_listMode) element.Width = listWidth;
            if (_realized.Add(id)) _canvas.Children.Add(element);
            Decorate(id);
        }
        foreach (var id in _realized.Where(id => !wanted.Contains(id)).ToList())
        {
            _realized.Remove(id);
            if (_cache.TryGetValue(id, out var entry)) _canvas.Children.Remove(entry.Element);
        }
        if (_cache.Count > CacheLimit)
            foreach (var stale in _cache.Keys.Where(id => !_realized.Contains(id)).Take(_cache.Count - CacheLimit).ToList())
                _cache.Remove(stale);
    }

    private Border? ElementFor(Guid id)
    {
        if (_store.Find(id) is not { } item) return null;
        if (_cache.TryGetValue(id, out var entry) && entry.Version == item.UpdatedAt && entry.List == _listMode) return entry.Element;
        if (entry.Element is not null) _canvas.Children.Remove(entry.Element);
        _realized.Remove(id);
        var element = _listMode ? ListRow(item) : Card(item);
        element.Tag = id;
        element.MouseLeftButtonDown += (_, e) =>
        {
            Focus();
            _pressed = id;
            _pressPoint = e.GetPosition(this);
            _deferredClick = null;
            if (e.ClickCount == 2) ItemDoubleClicked?.Invoke(this, id);
            // A plain press on a multi-selection may start a drag of all of it; select just this one on release.
            else if (Keyboard.Modifiers == ModifierKeys.None && _selected.Count > 1 && _selected.Contains(id)) _deferredClick = id;
            else ItemClicked?.Invoke(this, (id, Keyboard.Modifiers));
            e.Handled = true;
        };
        element.MouseLeftButtonUp += (_, _) =>
        {
            if (_deferredClick == id) ItemClicked?.Invoke(this, (id, ModifierKeys.None));
            _deferredClick = null;
            _pressed = null;
        };
        element.MouseMove += (_, e) =>
        {
            if (_pressed != id || e.LeftButton != MouseButtonState.Pressed || !Platform.DragOut.Exceeded(_pressPoint, e.GetPosition(this))) return;
            _pressed = null;
            _deferredClick = null;
            ItemDragged?.Invoke(this, id);
        };
        element.MouseRightButtonUp += (_, e) =>
        {
            Focus();
            ItemContextRequested?.Invoke(this, id);
            e.Handled = true;
        };
        _cache[id] = (item.UpdatedAt, _listMode, element);
        return element;
    }

    private void Decorate(Guid id)
    {
        if (!_cache.TryGetValue(id, out var entry)) return;
        var selected = _selected.Contains(id);
        var focused = _focused == id;
        var element = entry.Element;
        if (_listMode)
        {
            element.Background = selected ? Palette.Highlight : Brushes.Transparent;
            element.BorderBrush = focused ? Palette.Accent : Brushes.Transparent;
        }
        else
        {
            element.BorderBrush = selected ? Palette.Accent : Palette.CardBorder;
            element.BorderThickness = new Thickness(selected ? 2 : 1);
            element.Padding = new Thickness(selected ? 0 : 1);
        }
    }

    // ----- Card content -----

    private Border Card(ClipItem item)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(ThumbHeight) });
        grid.RowDefinitions.Add(new RowDefinition());
        var thumb = new Border { CornerRadius = new CornerRadius(9, 9, 0, 0), ClipToBounds = true, Background = Palette.Chip, Child = Thumbnail(item) };
        grid.Children.Add(thumb);
        if (item.Pinned)
        {
            var pin = new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(11),
                Background = Palette.Accent,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 6, 6, 0),
                Child = new TextBlock { Text = Glyphs.Pinned, FontFamily = ShelfPalette.Icons, FontSize = 10, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            grid.Children.Add(pin);
        }
        var labels = new StackPanel { Margin = new Thickness(10, 7, 10, 6) };
        labels.Children.Add(new TextBlock { Text = TitleFor(item), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Palette.Foreground, TextTrimming = TextTrimming.CharacterEllipsis });
        labels.Children.Add(new TextBlock { Text = Meta(item), FontSize = 10.5, Foreground = Palette.Muted, Margin = new Thickness(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetRow(labels, 1);
        grid.Children.Add(labels);
        return new Border
        {
            Width = CardWidth,
            Height = CardHeight,
            CornerRadius = new CornerRadius(10),
            Background = Palette.CardFor(item.Kind),
            BorderBrush = Palette.CardBorder,
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Child = grid,
            ToolTip = TitleFor(item),
        };
    }

    private Border ListRow(ClipItem item)
    {
        var grid = new Grid { Margin = new Thickness(8, 4, 12, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.Children.Add(new Border { Width = 38, Height = 32, CornerRadius = new CornerRadius(6), ClipToBounds = true, Background = Palette.CardFor(item.Kind), HorizontalAlignment = HorizontalAlignment.Left, Child = Thumbnail(item, small: true) });
        var title = new DockPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        if (item.Pinned) title.Children.Add(new TextBlock { Text = Glyphs.Pinned, FontFamily = ShelfPalette.Icons, FontSize = 11, Foreground = Palette.Accent, Margin = new Thickness(0, 1, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        title.Children.Add(new TextBlock { Text = TitleFor(item), FontSize = 12.5, Foreground = Palette.Foreground, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(title, 1);
        grid.Children.Add(title);
        var kind = new TextBlock { Text = ShelfRowView.Title(item.Kind), FontSize = 11.5, Foreground = Palette.Muted, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(kind, 2);
        grid.Children.Add(kind);
        var date = new TextBlock { Text = item.CreatedAt.LocalDateTime.ToString("g", CultureInfo.CurrentCulture), FontSize = 11.5, Foreground = Palette.Muted, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(date, 3);
        grid.Children.Add(date);
        return new Border
        {
            Height = ListRowHeight - 4,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = grid,
        };
    }

    private FrameworkElement Thumbnail(ClipItem item, bool small = false)
    {
        switch (item.Kind)
        {
            case ItemKind.Image:
                var image = _thumbnails.Get(_store.MediaPath(item), small ? 80 : 360);
                if (image is not null) return new Image { Source = image, Stretch = Stretch.UniformToFill };
                return Glyph(ShelfRowView.Glyph(ItemKind.Image), small);
            case ItemKind.Link:
                if (item.LinkThumbnailFile.Length > 0 && _thumbnails.Get(_store.Media.PathOf(item.LinkThumbnailFile), small ? 80 : 360) is { } preview)
                    return new Image { Source = preview, Stretch = Stretch.UniformToFill };
                if (small) return Glyph(Glyphs.Link, small);
                var link = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                link.Children.Add(new TextBlock { Text = Glyphs.Link, FontFamily = ShelfPalette.Icons, FontSize = 24, Foreground = Palette.Muted, HorizontalAlignment = HorizontalAlignment.Center });
                link.Children.Add(new TextBlock { Text = LibraryQueryEngine.HostOf(item), FontSize = 11, Foreground = Palette.Muted, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(8, 6, 8, 0), TextTrimming = TextTrimming.CharacterEllipsis });
                return link;
            default:
                if (small) return Glyph(ShelfRowView.Glyph(ItemKind.Text), small);
                var text = item.Text.Length > 400 ? item.Text[..400] : item.Text;
                return new TextBlock { Text = text, FontSize = 11, Foreground = Palette.Foreground, Opacity = 0.85, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(10, 9, 10, 4) };
        }
    }

    private TextBlock Glyph(string glyph, bool small) => new()
    {
        Text = glyph,
        FontFamily = ShelfPalette.Icons,
        FontSize = small ? 14 : 26,
        Foreground = Palette.Muted,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static string TitleFor(ClipItem item)
    {
        var title = LibraryQueryEngine.TitleOf(item);
        return title.Length > 0 ? title : Loc.T(item.Kind switch { ItemKind.Text => "Untitled note", ItemKind.Image => "Image", _ => "Untitled" });
    }

    private static string Meta(ClipItem item)
    {
        var parts = new List<string> { Relative(item.CreatedAt) };
        if (item.Kind == ItemKind.Image && item.PixelWidth > 0) parts.Add($"{item.PixelWidth}×{item.PixelHeight}");
        if (item.Tags.Count > 0) parts.Add(string.Join(" ", item.Tags.Take(2).Select(t => "#" + t)) + (item.Tags.Count > 2 ? $" +{item.Tags.Count - 2}" : ""));
        return string.Join(" · ", parts);
    }

    public static string Relative(DateTimeOffset time)
    {
        var age = DateTimeOffset.Now - time;
        if (age < TimeSpan.FromMinutes(1)) return Loc.T("Just now");
        if (age < TimeSpan.FromHours(1)) return Loc.F("{0} min ago", (int)age.TotalMinutes);
        if (age < TimeSpan.FromDays(1)) return Loc.F("{0} h ago", (int)age.TotalHours);
        if (age < TimeSpan.FromDays(7)) return Loc.F("{0} d ago", (int)age.TotalDays);
        return time.LocalDateTime.ToString("d", CultureInfo.CurrentCulture);
    }
}
