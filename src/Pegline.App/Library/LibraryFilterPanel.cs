using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Pegline.App.Localization;
using Pegline.App.Shelves;
using Pegline.Core.Library;
using Pegline.Core.Storage;

namespace Pegline.App.Library;

/// <summary>
/// The Library's filter popover: where search looks (text and OCR, visual content or both), when items were
/// added, a link domain, tags that must all match and pinned items only. Every change applies at once.
/// </summary>
internal sealed class LibraryFilterPanel
{
    private readonly LibraryModel _model;
    private readonly LibraryStore _store;
    private readonly ShelfPalette _palette;
    private readonly Func<string> _visualNote;
    private readonly Popup _popup;
    private readonly StackPanel _content = new() { Width = 300 };
    private TextBox? _domain;

    public LibraryFilterPanel(LibraryModel model, LibraryStore store, ShelfPalette palette, Func<string> visualNote)
    {
        _model = model;
        _store = store;
        _palette = palette;
        _visualNote = visualNote;
        _popup = new Popup
        {
            StaysOpen = false,
            AllowsTransparency = true,
            Placement = PlacementMode.Bottom,
            PopupAnimation = PopupAnimation.Fade,
            Child = new Border
            {
                Background = palette.Panel,
                BorderBrush = palette.PanelBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(16, 14, 16, 14),
                Margin = new Thickness(0, 6, 0, 0),
                Child = _content,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Opacity = 0.35 },
            },
        };
        _popup.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            _popup.IsOpen = false;
            e.Handled = true;
        };
        _model.Changed += (_, _) =>
        {
            if (_popup.IsOpen && !(_domain?.IsKeyboardFocusWithin ?? false)) Render();
        };
    }

    public bool IsOpen => _popup.IsOpen;

    public event EventHandler? Closed
    {
        add => _popup.Closed += value;
        remove => _popup.Closed -= value;
    }

    public void Toggle(UIElement anchor)
    {
        if (_popup.IsOpen)
        {
            _popup.IsOpen = false;
            return;
        }
        _popup.PlacementTarget = anchor;
        Render();
        _popup.IsOpen = true;
        _content.Focus();
    }

#if DEBUG
    internal FrameworkElement DetachForSnapshot()
    {
        Render();
        var child = (FrameworkElement)_popup.Child;
        _popup.Child = null;
        return child;
    }
#endif

    /// <summary>Filters beyond the kind chips and the search text are active.</summary>
    public static bool HasPanelFilters(LibraryQuery query) =>
        query.SearchMode != LibrarySearchMode.All || query.Days is not null || query.Domain.Length > 0 || query.Tags.Count > 0 || query.FavoritesOnly;

    private void Render()
    {
        var query = _model.Query;
        _content.Children.Clear();
        _content.Children.Add(new TextBlock { Text = Loc.T("Filters"), FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = _palette.Foreground, Margin = new Thickness(0, 0, 0, 10) });

        Section(Loc.T("Search in"));
        Choice(Loc.T("Text and visual content"), query.SearchMode == LibrarySearchMode.All, () => _model.SetSearchMode(LibrarySearchMode.All));
        Choice(Loc.T("Text only (including OCR)"), query.SearchMode == LibrarySearchMode.Text, () => _model.SetSearchMode(LibrarySearchMode.Text));
        Choice(Loc.T("Visual content only"), query.SearchMode == LibrarySearchMode.Visual, () => _model.SetSearchMode(LibrarySearchMode.Visual));
        Note(Loc.T("Image text is read locally. Describe images in English for best visual matches."));
        if (_visualNote() is { Length: > 0 } note) Note(note);

        Section(Loc.T("Date added"));
        var days = new WrapPanel();
        days.Children.Add(Chip(Loc.T("Any time"), query.Days is null, () => _model.SetDays(null)));
        days.Children.Add(Chip(Loc.T("Last 7 days"), query.Days == 7, () => _model.SetDays(7)));
        days.Children.Add(Chip(Loc.T("Last 30 days"), query.Days == 30, () => _model.SetDays(30)));
        _content.Children.Add(days);

        Section(Loc.T("Domain"));
        var domainRow = new Grid();
        domainRow.ColumnDefinitions.Add(new ColumnDefinition());
        domainRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var field = new Grid();
        _domain = new TextBox
        {
            Text = query.Domain,
            Background = _palette.Chip,
            Foreground = _palette.Foreground,
            CaretBrush = _palette.Foreground,
            BorderBrush = _palette.PanelBorder,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 4, 6, 4),
            FontSize = 12.5,
            Style = null,
        };
        var placeholder = new TextBlock
        {
            Text = Loc.T("Domain, e.g. youtube.com"),
            Foreground = _palette.Muted,
            FontSize = 12.5,
            Margin = new Thickness(9, 5, 0, 0),
            IsHitTestVisible = false,
            Visibility = query.Domain.Length == 0 ? Visibility.Visible : Visibility.Collapsed,
        };
        var domain = _domain;
        domain.TextChanged += (_, _) => placeholder.Visibility = domain.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        domain.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            ApplyDomain(domain.Text);
            e.Handled = true;
        };
        field.Children.Add(domain);
        field.Children.Add(placeholder);
        domainRow.Children.Add(field);
        var apply = Chip(Loc.T("Apply"), false, () => ApplyDomain(domain.Text));
        apply.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(apply, 1);
        domainRow.Children.Add(apply);
        _content.Children.Add(domainRow);

        var tags = _store.AllTags;
        if (tags.Count > 0)
        {
            Section(Loc.T("Tags (match all)"));
            var list = new StackPanel();
            foreach (var tag in tags)
                list.Children.Add(Check("#" + tag, query.Tags.Contains(tag), () => _model.ToggleTagFilter(tag)));
            _content.Children.Add(new ScrollViewer { Content = list, MaxHeight = 150, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        }

        Section(Loc.T("Show"));
        _content.Children.Add(Check(Loc.T("Pinned items only"), query.FavoritesOnly, () => _model.SetFavorites(!query.FavoritesOnly)));

        var clear = Chip(Loc.T("Clear filters"), false, () =>
        {
            _model.ClearFilters();
            ClearRequested?.Invoke(this, EventArgs.Empty);
        });
        clear.Margin = new Thickness(0, 14, 0, 0);
        clear.HorizontalAlignment = HorizontalAlignment.Left;
        _content.Children.Add(clear);
    }

    /// <summary>Clear filters also empties the window's search box.</summary>
    public event EventHandler? ClearRequested;

    private void ApplyDomain(string value)
    {
        if (value.Trim() == _model.Query.Domain) return;
        _model.SetDomain(value);
        Render();
    }

    private void Section(string title) => _content.Children.Add(new TextBlock
    {
        Text = title,
        FontSize = 11.5,
        FontWeight = FontWeights.SemiBold,
        Foreground = _palette.Muted,
        Margin = new Thickness(0, 12, 0, 6),
    });

    private void Note(string text) => _content.Children.Add(new TextBlock
    {
        Text = text,
        FontSize = 11.5,
        Foreground = _palette.Muted,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 6, 0, 0),
    });

    private void Choice(string label, bool active, Action choose)
    {
        var row = Row(active ? "" : "", label, active, () =>
        {
            if (!active) choose();
        });
        _content.Children.Add(row);
    }

    private Border Check(string label, bool on, Action toggle) => Row(on ? "" : "", label, on, toggle);

    private Border Row(string glyph, string label, bool active, Action click)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new TextBlock { Text = glyph, FontFamily = ShelfPalette.Icons, FontSize = 12, Width = 22, Foreground = active ? _palette.Accent : _palette.Muted, VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(new TextBlock { Text = label, FontSize = 12.5, Foreground = _palette.Foreground, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        var row = new Border { Child = panel, Padding = new Thickness(6, 5, 6, 5), CornerRadius = new CornerRadius(6), Background = Brushes.Transparent, Cursor = Cursors.Hand };
        row.MouseEnter += (_, _) => row.Background = _palette.Chip;
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, e) =>
        {
            click();
            e.Handled = true;
        };
        return row;
    }

    private Border Chip(string label, bool active, Action click)
    {
        var chip = new Border
        {
            Padding = new Thickness(10, 4, 10, 5),
            Margin = new Thickness(0, 0, 6, 6),
            CornerRadius = new CornerRadius(7),
            Background = active ? _palette.ChipActive : _palette.Chip,
            Cursor = Cursors.Hand,
            Child = new TextBlock { Text = label, FontSize = 12.5, Foreground = _palette.Foreground },
        };
        chip.MouseEnter += (_, _) => { if (!active) chip.Background = _palette.ChipActive; };
        chip.MouseLeave += (_, _) => { if (!active) chip.Background = _palette.Chip; };
        chip.MouseLeftButtonUp += (_, e) =>
        {
            click();
            e.Handled = true;
        };
        return chip;
    }
}
