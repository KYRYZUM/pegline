using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Pegline.App.Localization;
using Pegline.App.Platform;
using Pegline.App.Shelves;

namespace Pegline.App.Organize;

/// <summary>One choice in a <see cref="PickerWindow"/>. <see cref="Key"/> identifies it across reloads.</summary>
internal sealed record PickerRow(object? Key, string Title, string Glyph, int Depth = 0);

/// <summary>
/// The keyboard picker behind "Pin to folder…" and "Add tag…": a search field over a short list.
/// Typing filters, ↑/↓ move, Enter confirms the selected row, Esc (or clicking elsewhere) changes nothing.
/// An optional create mode turns the field into a name input; the secondary button (Ctrl+N) usually starts it.
/// </summary>
internal sealed class PickerWindow : HostedWindow
{
    private const double SurfaceWidth = 520;
    private const double SurfaceHeight = 420;
    private const double RowHeight = 38;

    private readonly ShelfPalette _palette;
    private readonly Func<string, IReadOnlyList<PickerRow>> _source;
    private readonly Func<PickerRow, string?> _confirm;
    private readonly string _searchPlaceholder;
    private readonly TextBox _query = new();
    private readonly TextBlock _placeholder = new();
    private readonly TextBlock _count = new();
    private readonly TextBlock _empty = new();
    private readonly TextBlock _message = new();
    private readonly StackPanel _list = new();
    private readonly ScrollViewer _scroll = new();
    private readonly List<Border> _rows = [];
    private IReadOnlyList<PickerRow> _visible = [];
    private int _selected = -1;
    private bool _finished;
    private Func<string, (string? Error, object? Select)>? _create;
    private string _queryBeforeCreating = "";
    private readonly Action? _secondary;

    public PickerWindow(ShelfPalette palette, string heading, string placeholder, string emptyText, string confirmLabel,
        Func<string, IReadOnlyList<PickerRow>> source, Func<PickerRow, string?> confirm, (string Label, string Hint, Action Run)? secondary = null)
    {
        _palette = palette;
        _source = source;
        _confirm = confirm;
        _searchPlaceholder = placeholder;
        _secondary = secondary?.Run;
        Title = heading;
        Content = Build(heading, emptyText, confirmLabel, secondary);
        PreviewKeyDown += OnKey;
        Deactivated += (_, _) =>
        {
            if (_finished || !IsOpen) return;
            RestoreParentFocus = false;
            Finish();
        };
        _query.TextChanged += (_, _) =>
        {
            if (_create is not null) _message.Visibility = Visibility.Collapsed;
            else Reload(null);
        };
        Reload(null);
    }

    /// <summary>True when a choice was committed.</summary>
    public bool Confirmed { get; private set; }

    public override void Present(Window parent)
    {
        ShowOver(parent, SurfaceWidth, SurfaceHeight);
        var height = SurfaceSize.Height;
        _scroll.Height = Math.Max(RowHeight, height - 196);
        Dispatcher.BeginInvoke(() => Keyboard.Focus(_query), System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>
    /// Switches the field to a name input. Enter calls <paramref name="create"/>, which returns an error to
    /// show, or the key of the row to select after the list reloads with an empty query. Esc goes back.
    /// </summary>
    public void BeginCreate(string placeholder, Func<string, (string? Error, object? Select)> create)
    {
        if (_create is not null) return;
        _queryBeforeCreating = _query.Text;
        _create = create;
        _placeholder.Text = placeholder;
        _query.Text = "";
        ShowMessage(placeholder, error: false);
        Keyboard.Focus(_query);
    }

    private UIElement Build(string heading, string emptyText, string confirmLabel, (string Label, string Hint, Action Run)? secondary)
    {
        var surface = Surface(_palette, 16);
        var grid = new Grid { Margin = new Thickness(0, 12, 0, 14) };
        for (var i = 0; i < 6; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new DockPanel { Margin = new Thickness(18, 0, 14, 0), Height = 24, LastChildFill = false };
        header.Children.Add(new TextBlock { Text = heading, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = _palette.Muted, VerticalAlignment = VerticalAlignment.Center });
        var close = SurfaceParts.HeaderButton(_palette, Glyphs.Close, Loc.T("Close"), Finish);
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        _count.FontSize = 11;
        _count.Foreground = _palette.Muted;
        _count.VerticalAlignment = VerticalAlignment.Center;
        _count.Margin = new Thickness(10, 0, 8, 0);
        DockPanel.SetDock(_count, Dock.Right);
        header.Children.Add(_count);
        grid.Children.Add(header);

        var input = SurfaceParts.Input(_palette, _query, _placeholder, Glyphs.Search, _searchPlaceholder);
        input.Margin = new Thickness(20, 9, 20, 0);
        Grid.SetRow(input, 1);
        grid.Children.Add(input);

        var separator = new Border { Height = 1, Background = _palette.PanelBorder, Margin = new Thickness(0, 14, 0, 8) };
        Grid.SetRow(separator, 2);
        grid.Children.Add(separator);

        var results = new Grid { Margin = new Thickness(8, 0, 8, 0) };
        _scroll.Content = _list;
        _scroll.Height = SurfaceHeight - 196;
        _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _scroll.Focusable = false;
        results.Children.Add(_scroll);
        _empty.Text = emptyText;
        _empty.FontSize = 13;
        _empty.Foreground = _palette.Muted;
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.VerticalAlignment = VerticalAlignment.Center;
        results.Children.Add(_empty);
        Grid.SetRow(results, 3);
        grid.Children.Add(results);

        _message.FontSize = 11;
        _message.Margin = new Thickness(20, 8, 20, 0);
        _message.TextTrimming = TextTrimming.CharacterEllipsis;
        _message.Visibility = Visibility.Collapsed;
        Grid.SetRow(_message, 4);
        grid.Children.Add(_message);

        var footer = new DockPanel { Margin = new Thickness(18, 10, 18, 0), Height = 28, LastChildFill = false };
        var hints = new StackPanel { Orientation = Orientation.Horizontal };
        hints.Children.Add(SurfaceParts.Keycap(_palette, "↑ ↓"));
        hints.Children.Add(SurfaceParts.Keycap(_palette, "Enter"));
        hints.Children.Add(SurfaceParts.Keycap(_palette, "Esc"));
        footer.Children.Add(hints);
        var confirm = SurfaceParts.Button(_palette, confirmLabel, primary: true, Commit);
        DockPanel.SetDock(confirm, Dock.Right);
        footer.Children.Add(confirm);
        if (secondary is { } extra)
        {
            var button = SurfaceParts.Button(_palette, extra.Label, primary: false, extra.Run);
            button.ToolTip = extra.Hint;
            DockPanel.SetDock(button, Dock.Right);
            footer.Children.Add(button);
        }
        Grid.SetRow(footer, 5);
        grid.Children.Add(footer);

        surface.Child = grid;
        return surface;
    }

    private void Reload(object? select)
    {
        _visible = _source(_query.Text);
        _list.Children.Clear();
        _rows.Clear();
        for (var i = 0; i < _visible.Count; i++)
        {
            var row = Row(_visible[i], i);
            _rows.Add(row);
            _list.Children.Add(row);
        }
        _count.Text = _visible.Count.ToString(System.Globalization.CultureInfo.CurrentCulture);
        _empty.Visibility = _visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _message.Visibility = Visibility.Collapsed;
        _scroll.ScrollToTop();
        var index = select is null ? 0 : Math.Max(0, _visible.ToList().FindIndex(r => Equals(r.Key, select)));
        Select(_visible.Count == 0 ? -1 : index);
    }

    private Border Row(PickerRow choice, int index)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12 + choice.Depth * 22, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new TextBlock { Text = choice.Glyph, FontFamily = ShelfPalette.Icons, FontSize = 14, Foreground = _palette.Muted, Width = 26, VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(new TextBlock { Text = choice.Title, FontSize = 13, Foreground = _palette.Foreground, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, MaxWidth = SurfaceWidth - 110 - choice.Depth * 22 });
        var row = new Border
        {
            Height = RowHeight,
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(4, 1, 4, 1),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = panel,
            ToolTip = choice.Title,
        };
        row.MouseEnter += (_, _) => Select(index, scroll: false);
        row.MouseLeftButtonDown += (_, e) =>
        {
            Select(index, scroll: false);
            if (e.ClickCount == 2) Commit();
            Keyboard.Focus(_query);
            e.Handled = true;
        };
        return row;
    }

    private void Select(int index, bool scroll = true)
    {
        if (_selected >= 0 && _selected < _rows.Count) _rows[_selected].Background = Brushes.Transparent;
        _selected = index;
        if (index < 0 || index >= _rows.Count) return;
        _rows[index].Background = _palette.Highlight;
        if (scroll) _rows[index].BringIntoView();
    }

    private void ShowMessage(string text, bool error = true)
    {
        _message.Text = text;
        _message.Foreground = error ? new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71)) : _palette.Muted;
        _message.Visibility = Visibility.Visible;
    }

    private void Commit()
    {
        if (_finished) return;
        if (_create is { } create)
        {
            var (error, select) = create(_query.Text.Trim());
            if (error is not null)
            {
                ShowMessage(error);
                return;
            }
            EndCreate("");
            Reload(select);
            return;
        }
        if (_selected < 0 || _selected >= _visible.Count) return;
        var failure = _confirm(_visible[_selected]);
        if (failure is not null)
        {
            ShowMessage(failure);
            return;
        }
        Confirmed = true;
        Finish();
    }

    private void EndCreate(string query)
    {
        _create = null;
        _placeholder.Text = _searchPlaceholder;
        _query.Text = query;
        _query.CaretIndex = query.Length;
        Keyboard.Focus(_query);
    }

    private void Finish()
    {
        _finished = true;
        Close();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (Keyboard.Modifiers == ModifierKeys.Control && key is Key.F or Key.N)
        {
            if (key == Key.N) _secondary?.Invoke();
            Keyboard.Focus(_query);
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        switch (key)
        {
            case Key.Down when _create is null: Select(Math.Min(_visible.Count - 1, Math.Max(0, _selected) + 1)); break;
            case Key.Up when _create is null: Select(Math.Max(0, _selected - 1)); break;
            case Key.PageDown when _create is null: Select(Math.Min(_visible.Count - 1, Math.Max(0, _selected) + 8)); break;
            case Key.PageUp when _create is null: Select(Math.Max(0, _selected - 8)); break;
            case Key.Enter: Commit(); break;
            case Key.Escape:
                if (_create is not null) EndCreate(_queryBeforeCreating);
                else Finish();
                break;
            default: return;
        }
        e.Handled = true;
    }
}
