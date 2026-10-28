using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Pegline.App.Localization;
using Pegline.App.Platform;
using Pegline.App.Shelves;
using Pegline.Core.Actions;

namespace Pegline.App.Actions;

/// <summary>
/// Keyboard-first command list (Ctrl+K). It keeps a steady size while typing; ↑/↓ move between
/// enabled rows, Enter runs one, Esc or clicking elsewhere closes it.
/// </summary>
internal sealed class ActionPalette : FloatingWindow
{
    private const double SurfaceWidth = 600;
    private const double MaxSurfaceHeight = 465;
    private const double Chrome = 137;

    private readonly IReadOnlyList<ActionCommand> _commands;
    private readonly ShelfPalette _palette;
    private readonly Action<string> _execute;
    private readonly Action? _searchContents;
    private readonly TextBox _query = new();
    private readonly TextBlock _placeholder = new();
    private readonly TextBlock _count = new();
    private readonly TextBlock _empty = new();
    private readonly StackPanel _list = new();
    private readonly ScrollViewer _scroll = new();
    private IReadOnlyList<ActionCommand> _visible = [];
    private readonly List<Border> _rows = [];
    private int _selected = -1;
    private bool _finished;

    public ActionPalette(IReadOnlyList<ActionCommand> commands, ShelfPalette palette, Action<string> execute, Action? searchContents)
    {
        _commands = commands;
        _palette = palette;
        _execute = execute;
        _searchContents = searchContents;
        Title = Loc.T("Actions");
        Content = Build();
        PreviewKeyDown += OnKey;
        Deactivated += (_, _) =>
        {
            // Clicking another surface ends this captured context; do not reclaim focus from it.
            if (_finished || !IsOpen) return;
            RestoreParentFocus = false;
            Finish();
        };
        _query.TextChanged += (_, _) =>
        {
            Filter(_query.Text);
        };
        Filter("");
    }

    public void ShowOver(Win32.RECT work, double scale, Win32.RECT anchor)
    {
        var height = Math.Min(MaxSurfaceHeight, (work.Bottom - work.Top) / scale - 32);
        _scroll.Height = Math.Max(44, height - Chrome);
        ShowAt(work, scale, anchor, SurfaceWidth, _scroll.Height + Chrome);
        Dispatcher.BeginInvoke(() => Keyboard.Focus(_query), System.Windows.Threading.DispatcherPriority.Input);
    }

    private UIElement Build()
    {
        var surface = Surface(_palette, 16);
        var grid = new Grid { Margin = new Thickness(0, 12, 0, 12) };
        for (var i = 0; i < 5; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Header: mode badge, "Search contents", result count, close.
        var header = new DockPanel { Margin = new Thickness(18, 0, 18, 0), Height = 24, LastChildFill = false };
        var badge = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 3, 8, 3),
            Background = _palette.Highlight,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new TextBlock { Text = Glyphs.Command, FontFamily = ShelfPalette.Icons, FontSize = 10, Foreground = _palette.Accent, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = Loc.T("Actions"), FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = _palette.Accent, Margin = new Thickness(5, 0, 0, 0) },
                },
            },
        };
        header.Children.Add(badge);
        var close = HeaderButton(Glyphs.Close, Loc.T("Close"), Finish);
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        _count.FontSize = 11;
        _count.Foreground = _palette.Muted;
        _count.VerticalAlignment = VerticalAlignment.Center;
        _count.Margin = new Thickness(10, 0, 8, 0);
        DockPanel.SetDock(_count, Dock.Right);
        header.Children.Add(_count);
        if (_searchContents is not null)
        {
            var search = HeaderButton(Glyphs.Search, Loc.T("Search contents"), () =>
            {
                if (_finished) return;
                _finished = true;
                base.Close();
                _searchContents();
            }, label: Loc.T("Search contents"));
            DockPanel.SetDock(search, Dock.Right);
            header.Children.Add(search);
        }
        grid.Children.Add(header);

        // Query field.
        var input = SurfaceParts.Input(_palette, _query, _placeholder, Glyphs.Search, Loc.T("Search actions…"));
        input.Margin = new Thickness(20, 10, 20, 0);
        Grid.SetRow(input, 1);
        grid.Children.Add(input);

        var separator = new Border { Height = 1, Background = _palette.PanelBorder, Margin = new Thickness(0, 14, 0, 7) };
        Grid.SetRow(separator, 2);
        grid.Children.Add(separator);

        // Results.
        var results = new Grid { Margin = new Thickness(8, 0, 8, 0) };
        _scroll.Content = _list;
        _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _scroll.Focusable = false;
        results.Children.Add(_scroll);
        _empty.Text = Loc.T("No matching actions");
        _empty.FontSize = 13;
        _empty.Foreground = _palette.Muted;
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.VerticalAlignment = VerticalAlignment.Center;
        results.Children.Add(_empty);
        Grid.SetRow(results, 3);
        grid.Children.Add(results);

        // Footer hints.
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(18, 9, 18, 0), Height = 18 };
        footer.Children.Add(Keycap("↑ ↓"));
        footer.Children.Add(Hint(Loc.T("Browse results")));
        footer.Children.Add(new Border { Width = 18 });
        footer.Children.Add(Keycap("Enter"));
        footer.Children.Add(Hint(Loc.T("Run action")));
        footer.Children.Add(Keycap("Esc"));
        footer.Children.Add(Hint(Loc.T("Close")));
        Grid.SetRow(footer, 4);
        grid.Children.Add(footer);

        surface.Child = grid;
        return surface;
    }

    private Border HeaderButton(string glyph, string tooltip, Action click, string? label = null) =>
        SurfaceParts.HeaderButton(_palette, glyph, tooltip, click, label);

    private Border Keycap(string text) => SurfaceParts.Keycap(_palette, text);

    private TextBlock Hint(string text) => SurfaceParts.Hint(_palette, text);

    private void Filter(string query)
    {
        _visible = CommandFilter.Filter(_commands, query);
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
        _scroll.ScrollToTop();
        Select(Enumerable.Range(0, _visible.Count).FirstOrDefault(i => _visible[i].IsEnabled, -1));
    }

    private Border Row(ActionCommand command, int index)
    {
        var grid = new Grid { Margin = new Thickness(12, 6, 10, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = Glyphs.For(command.Symbol), FontFamily = ShelfPalette.Icons, FontSize = 15, Foreground = _palette.Muted, VerticalAlignment = VerticalAlignment.Center });

        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(new TextBlock { Text = command.Title, FontSize = 13, FontWeight = FontWeights.Medium, Foreground = _palette.Foreground, TextTrimming = TextTrimming.CharacterEllipsis });
        var detail = !command.IsEnabled && command.DisabledReason.Length > 0 ? command.DisabledReason : command.Subtitle;
        var description = string.Join(" · ", new[] { command.Group, detail }.Where(s => s.Length > 0));
        if (description.Length > 0)
            labels.Children.Add(new TextBlock { Text = description, FontSize = 11, Foreground = _palette.Muted, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(labels, 1);
        grid.Children.Add(labels);

        if (command.IsActive)
        {
            var check = new TextBlock { Text = Glyphs.Accept, FontFamily = ShelfPalette.Icons, FontSize = 11, Foreground = _palette.Accent, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) };
            Grid.SetColumn(check, 2);
            grid.Children.Add(check);
        }
        if (command.KeyHint.Length > 0)
        {
            var keycap = Keycap(command.KeyHint);
            keycap.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(keycap, 3);
            grid.Children.Add(keycap);
        }

        var row = new Border
        {
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(4, 1, 4, 1),
            MinHeight = 50,
            Background = Brushes.Transparent,
            Opacity = command.IsEnabled ? 1 : 0.55,
            Child = grid,
            ToolTip = string.Join("\n", new[] { command.Title, description, command.KeyHint }.Where(s => s.Length > 0)),
        };
        if (command.IsEnabled)
        {
            row.Cursor = Cursors.Hand;
            row.MouseEnter += (_, _) => Select(index, scroll: false);
            row.MouseLeftButtonUp += (_, e) =>
            {
                Select(index, scroll: false);
                Run();
                e.Handled = true;
            };
        }
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

    private void Move(int delta)
    {
        var enabled = Enumerable.Range(0, _visible.Count).Where(i => _visible[i].IsEnabled).ToList();
        if (enabled.Count == 0) return;
        var current = enabled.IndexOf(_selected);
        if (current < 0) current = delta > 0 ? -1 : enabled.Count;
        Select(enabled[Math.Clamp(current + delta, 0, enabled.Count - 1)]);
    }

    private void Run()
    {
        if (_finished || _selected < 0 || _selected >= _visible.Count || !_visible[_selected].IsEnabled) return;
        var id = _visible[_selected].Id;
        _finished = true;
        // Close first so the action can open a preview or picker without this window reclaiming focus.
        base.Close();
        _execute(id);
    }

    private void Finish()
    {
        _finished = true;
        base.Close();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        switch (e.Key)
        {
            case Key.Down: Move(1); break;
            case Key.Up: Move(-1); break;
            case Key.PageDown: Move(8); break;
            case Key.PageUp: Move(-8); break;
            case Key.Enter: Run(); break;
            case Key.Escape: Finish(); break;
            default: return;
        }
        e.Handled = true;
    }
}
