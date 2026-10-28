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

/// <summary>Library views, folders (one level of subfolders) and tags, each with its record count.</summary>
internal sealed class LibrarySidebar : Border
{
    private readonly LibraryStore _store;
    private readonly ShelfPalette _palette;
    private readonly StackPanel _panel = new() { Margin = new Thickness(10, 12, 10, 12) };
    private readonly List<(LibraryScope Scope, Border Row)> _rows = [];
    private LibraryScope _current = LibraryScope.Saved;

    public LibrarySidebar(LibraryStore store, ShelfPalette palette)
    {
        _store = store;
        _palette = palette;
        Background = palette.Panel;
        BorderBrush = palette.PanelBorder;
        BorderThickness = new Thickness(0, 0, 1, 0);
        Focusable = true;
        FocusVisualStyle = null;
        Child = new ScrollViewer { Content = _panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
    }

    public event EventHandler<LibraryScope>? ScopeChosen;

    public event EventHandler? NewFolderRequested;

    public event EventHandler<Folder>? NewSubfolderRequested;

    public event EventHandler<Folder>? RenameFolderRequested;

    public event EventHandler<Folder>? RemoveFolderRequested;

    public event EventHandler<string>? RenameTagRequested;

    public event EventHandler<string>? RemoveTagRequested;

    /// <summary>Rows in keyboard order, so arrow keys can walk the sidebar.</summary>
    public IReadOnlyList<LibraryScope> Scopes => _rows.Select(r => r.Scope).ToList();

    public void Render(LibraryScope current)
    {
        _current = current;
        _panel.Children.Clear();
        _rows.Clear();
        var items = _store.Items;
        var folders = _store.Folders;
        int Count(LibraryScope scope) => items.Count(i => LibraryQueryEngine.InScope(i, scope, ScopeFolders(scope, folders)));

        Section(Loc.T("Library"), null);
        Row(LibraryScope.Saved, "", Loc.T("My library"), Count(LibraryScope.Saved), 0);
        Row(LibraryScope.Favorites, Glyphs.Pinned, Loc.T("Favorites"), Count(LibraryScope.Favorites), 0);
        Row(LibraryScope.Unorganized, "", Loc.T("Unorganized"), Count(LibraryScope.Unorganized), 0);
        Row(LibraryScope.Recent, "", Loc.T("Recent copies"), Count(LibraryScope.Recent), 0);
        Row(LibraryScope.Trash, "", Loc.T("Trash"), Count(LibraryScope.Trash), 0);

        Section(Loc.T("Folders"), () => NewFolderRequested?.Invoke(this, EventArgs.Empty));
        var sorted = folders.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        foreach (var folder in sorted.Where(f => f.ParentId is null))
        {
            FolderRow(folder, Count(LibraryScope.ForFolder(folder.Id)), 0);
            foreach (var child in sorted.Where(f => f.ParentId == folder.Id))
                FolderRow(child, Count(LibraryScope.ForFolder(child.Id)), 1);
        }
        if (folders.Count == 0) Hint(Loc.T("No folders yet"));

        var tags = _store.AllTags;
        Section(Loc.T("Tags"), null);
        foreach (var tag in tags)
        {
            var scope = LibraryScope.ForTag(tag);
            var row = Row(scope, "", "#" + tag, Count(scope), 0);
            row.ContextMenu = Menu(
                (Loc.T("Rename tag"), () => RenameTagRequested?.Invoke(this, tag)),
                (Loc.T("Remove tag"), () => RemoveTagRequested?.Invoke(this, tag)));
        }
        if (tags.Count == 0) Hint(Loc.T("No tags yet"));
    }

    private static HashSet<Guid>? ScopeFolders(LibraryScope scope, IReadOnlyList<Folder> folders) =>
        scope.Kind == LibraryScopeKind.Folder
            ? folders.Where(f => f.Id == scope.FolderId || f.ParentId == scope.FolderId).Select(f => f.Id).ToHashSet()
            : null;

    private void FolderRow(Folder folder, int count, int depth)
    {
        var row = Row(LibraryScope.ForFolder(folder.Id), "", folder.Name, count, depth);
        var entries = new List<(string, Action)>();
        if (folder.ParentId is null) entries.Add((Loc.T("New subfolder"), () => NewSubfolderRequested?.Invoke(this, folder)));
        entries.Add((Loc.T("Rename folder"), () => RenameFolderRequested?.Invoke(this, folder)));
        entries.Add((Loc.T("Remove folder"), () => RemoveFolderRequested?.Invoke(this, folder)));
        row.ContextMenu = Menu([.. entries]);
    }

    private static ContextMenu Menu(params (string Header, Action Click)[] entries)
    {
        var menu = new ContextMenu();
        foreach (var (header, click) in entries)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => click();
            menu.Items.Add(item);
        }
        return menu;
    }

    private void Section(string title, Action? add)
    {
        var header = new DockPanel { Margin = new Thickness(8, _panel.Children.Count == 0 ? 0 : 16, 4, 4), LastChildFill = true };
        if (add is not null)
        {
            var button = new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(6),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                ToolTip = Loc.T("New folder"),
                Child = new TextBlock { Text = "", FontFamily = ShelfPalette.Icons, FontSize = 11, Foreground = _palette.Muted, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            button.MouseEnter += (_, _) => button.Background = _palette.Chip;
            button.MouseLeave += (_, _) => button.Background = Brushes.Transparent;
            button.MouseLeftButtonUp += (_, e) =>
            {
                add();
                e.Handled = true;
            };
            DockPanel.SetDock(button, Dock.Right);
            header.Children.Add(button);
        }
        header.Children.Add(new TextBlock { Text = title.ToUpper(CultureInfo.CurrentCulture), FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = _palette.Muted, VerticalAlignment = VerticalAlignment.Center });
        _panel.Children.Add(header);
    }

    private void Hint(string text) =>
        _panel.Children.Add(new TextBlock { Text = text, FontSize = 11.5, Foreground = _palette.Muted, Opacity = 0.8, Margin = new Thickness(10, 2, 8, 2) });

    private Border Row(LibraryScope scope, string glyph, string title, int count, int depth)
    {
        var active = scope == _current;
        var grid = new Grid { Margin = new Thickness(8 + depth * 16, 0, 8, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = glyph, FontFamily = ShelfPalette.Icons, FontSize = 13, Foreground = active ? _palette.Accent : _palette.Muted, VerticalAlignment = VerticalAlignment.Center });
        var label = new TextBlock { Text = title, FontSize = 13, Foreground = _palette.Foreground, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);
        var number = new TextBlock { Text = count.ToString(CultureInfo.CurrentCulture), FontSize = 11, Foreground = _palette.Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        Grid.SetColumn(number, 2);
        grid.Children.Add(number);
        var row = new Border
        {
            Height = 32,
            CornerRadius = new CornerRadius(8),
            Background = active ? _palette.Highlight : Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = grid,
        };
        row.MouseEnter += (_, _) => { if (scope != _current) row.Background = _palette.Chip; };
        row.MouseLeave += (_, _) => { if (scope != _current) row.Background = Brushes.Transparent; };
        row.MouseLeftButtonUp += (_, e) =>
        {
            ScopeChosen?.Invoke(this, scope);
            e.Handled = true;
        };
        _rows.Add((scope, row));
        _panel.Children.Add(row);
        return row;
    }
}
