#pragma warning disable WPF0001 // Fluent ThemeMode is experimental in WPF
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Pegline.App.Actions;
using Pegline.App.Localization;
using Pegline.App.Markup;
using Pegline.App.Organize;
using Pegline.App.Platform;
using Pegline.App.Preview;
using Pegline.App.Shelves;
using Pegline.Core.Actions;
using Pegline.Core.Input;
using Pegline.Core.Library;
using Pegline.Core.Models;
using Pegline.Core.Storage;

namespace Pegline.App.Library;

/// <summary>
/// The Library: every saved record and recent copy, with views, folders, tags, search, filters,
/// a details panel and a recoverable Trash. Closing hides the window and keeps its state.
/// </summary>
internal sealed class LibraryWindow : Window, IPreviewHost
{
    private readonly LibraryStore _store;
    private readonly SettingsStore _settings;
    private readonly ClipboardWriter _writer;
    private readonly ForegroundTracker _foreground;
    private readonly Action _showShelves;
    private readonly Action _quit;
    private readonly ShelfPalette _palette;
    private readonly LibraryModel _model;
    private readonly LibraryGrid _grid;
    private readonly LibrarySidebar _sidebar;
    private readonly LibraryInspector _inspector;
    private readonly ColumnDefinition _sidebarColumn = new() { Width = new GridLength(232) };
    private readonly ColumnDefinition _inspectorColumn = new() { Width = new GridLength(320) };
    private readonly TextBlock _title = new();
    private readonly TextBlock _count = new();
    private readonly StackPanel _kinds = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _tools = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _search = new();
    private readonly TextBlock _searchPlaceholder = new();
    private readonly Border _toast = new();
    private readonly TextBlock _toastText = new();
    private readonly DispatcherTimer _searchTimer;
    private bool _renderQueued;
    private PreviewWindow? _preview;
    private ActionPalette? _actions;
    private HostedWindow? _picker;
    private readonly Organizer _organizer;
    private readonly Capture.OcrService _ocr;
    private readonly Search.VisualSearchService _visual;
    private readonly Links.LinkPreviewService _links;
    private readonly LibraryFilterPanel _filters;
    private Border? _filterButton;
    private bool _quitting;

    public LibraryWindow(LibraryStore store, SettingsStore settings, ClipboardWriter writer, ForegroundTracker foreground, Action showShelves, Action quit, Organizer organizer, Capture.OcrService ocr, Search.VisualSearchService visual, Links.LinkPreviewService links)
    {
        _links = links;
        _organizer = organizer;
        _ocr = ocr;
        _visual = visual;
        _store = store;
        _settings = settings;
        _writer = writer;
        _foreground = foreground;
        _showShelves = showShelves;
        _quit = quit;
        _palette = ShelfPalette.For(settings.Current.Theme);
        _model = new LibraryModel(store)
        {
            ListMode = settings.Current.LibraryListMode,
            Keymap = new Keymap(settings.Current.LocalShortcuts),
            Visual = visual.IsBundled ? visual : null,
        };
        _filters = new LibraryFilterPanel(_model, store, _palette, VisualNote);
        _filters.ClearRequested += (_, _) => _search.Text = "";
        _filters.Closed += (_, _) => FocusGrid();
        _visual.StatusChanged += (_, _) =>
        {
            if (IsVisible) QueueRender();
        };
        _visual.Indexed += (_, _) =>
        {
            if (IsVisible && LibraryModel.WantsVisual(_model.Query)) _model.RefreshVisual();
        };
        _grid = new LibraryGrid(store, new ThumbnailCache(), _palette);
        _sidebar = new LibrarySidebar(store, _palette);
        _inspector = new LibraryInspector(store, new ThumbnailCache(), _palette);

        ThemeMode = ThemeMode.Dark;
        Title = Loc.T("Library") + " — Pegline";
        Width = 1240;
        Height = 780;
        MinWidth = 780;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = _palette.Surface;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        UseLayoutRounding = true;
        Content = Build();

        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            if (!_inspector.Flush()) return;
            if (_model.ApplyPendingSearch()) _model.Reload(reset: true);
        };

        _model.Changed += (_, _) => QueueRender();
        _model.Requested += (_, request) => OnRequested(request.Action, request.Ids);
        _store.Changed += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (!IsVisible) return;
            _model.Reload();
            Render(sidebar: true);
            SyncPreview();
        });
        _settings.Changed += (_, _) => _model.Keymap = new Keymap(_settings.Current.LocalShortcuts);
        _grid.ColumnsChanged += (_, columns) => _model.Columns = columns;
        _grid.ItemClicked += (_, click) =>
        {
            if (!_inspector.Flush()) return;
            if (click.Modifiers.HasFlag(ModifierKeys.Control)) _model.Select(click.Id, toggle: true);
            else if (click.Modifiers.HasFlag(ModifierKeys.Shift)) _model.Select(click.Id, extending: true);
            else _model.Select(click.Id);
        };
        _grid.ItemDoubleClicked += (_, id) =>
        {
            _model.Select(id);
            OpenPreview();
        };
        _grid.ItemDragged += (_, id) => DragItems(id);
        _grid.ItemContextRequested += (_, id) =>
        {
            _model.Select(id, preservingSelection: true);
            ShowContextMenu();
        };
        _sidebar.ScopeChosen += (_, scope) => ChooseScope(scope);
        _sidebar.NewFolderRequested += (_, _) => NewFolder(null);
        _sidebar.NewSubfolderRequested += (_, folder) => NewFolder(folder);
        _sidebar.RenameFolderRequested += (_, folder) => RenameFolder(folder);
        _sidebar.RemoveFolderRequested += (_, folder) => RemoveFolder(folder);
        _sidebar.RenameTagRequested += (_, tag) => RenameTag(tag);
        _sidebar.RemoveTagRequested += (_, tag) => RemoveTag(tag);
        _sidebar.ItemsDroppedOnFolder += (_, drop) =>
        {
            try
            {
                _store.AddToFolder(drop.Ids, drop.Folder.Id);
                Notify(Loc.F("Added to “{0}”", drop.Folder.Name));
            }
            catch (InvalidOperationException ex)
            {
                Notify(Loc.T(ex.Message));
            }
        };
        _inspector.ActionRequested += (_, action) => OnInspectorAction(action);
        _inspector.Notice += (_, text) => Notify(text);

        PreviewKeyDown += OnKey;
        // Image and video files dropped from Explorer are added to the Library (saved).
        AllowDrop = true;
        DragOver += (_, e) =>
        {
            if (e.Handled || !IsFileDrop(e.Data)) return;
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        };
        Drop += (_, e) =>
        {
            if (e.Handled || !IsFileDrop(e.Data) || e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
            e.Handled = true;
            var media = Core.Capture.ClipboardAdmission.CopiedMedia(paths);
            if (media.Count == 0)
            {
                Notify(Loc.T("Only images and MP4 / MOV videos can be added"));
                return;
            }
            Notify(Loc.F("Adding {0} files…", media.Count));
            FilesDropped?.Invoke(this, media);
        };
        Closing += (_, e) =>
        {
            if (_quitting) return;
            e.Cancel = true;
            HideLibrary();
        };
        SourceInitialized += (_, _) =>
        {
            var dark = 1;
            DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
        };
        ApplyPanes();
    }

    /// <summary>Image or video files dropped on the Library (already filtered to media, at most 20).</summary>
    public event EventHandler<IReadOnlyList<string>>? FilesDropped;

    /// <summary>Files from Explorer, not records dragged within Pegline.</summary>
    private static bool IsFileDrop(IDataObject data) => data.GetDataPresent(DataFormats.FileDrop) && !data.GetDataPresent(Platform.DragOut.IdsFormat);

    public void FilesAdded(int added, int dropped)
    {
        Notify(added == dropped ? Loc.F("Added {0} files to the Library", added) : Loc.F("Added {0} of {1} files; the rest could not be read", added, dropped));
        if (_model.Query.Scope.Kind is not (LibraryScopeKind.Saved or LibraryScopeKind.Recent)) ChooseScope(LibraryScope.Saved);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    // ----- Show / hide -----

    public void ShowLibrary(LibraryScope? scope = null, ItemKind? kind = null, bool search = false)
    {
        if (!IsVisible) _foreground.Remember();
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Win32.ForceForeground(new WindowInteropHelper(this).Handle);
        _model.Open();
        if (scope is not null && scope != _model.Query.Scope) _model.ChangeScope(scope);
        if (kind is not null && kind != _model.Query.Kind) _model.SetKind(kind);
        _model.FocusContent();
        Render(sidebar: true);
        if (search) FocusSearch();
        else FocusGrid();
    }

#if DEBUG
    internal Window FiltersForSnapshot()
    {
        _model.Open();
        _model.SetDays(7);
        _model.SetDomain("youtube.com");
        return new Window { Content = _filters.DetachForSnapshot() };
    }

    internal void ShowForSnapshot(LibraryScope? scope, bool list)
    {
        _model.ListMode = list;
        Show();
        _model.Open();
        if (scope is not null) _model.ChangeScope(scope);
        _model.FocusContent();
        Render(sidebar: true);
    }
#endif

    /// <summary>Lets the window really close when the app quits.</summary>
    public void CloseForQuit()
    {
        _inspector.Flush();
        _quitting = true;
        Close();
    }

    public void HideLibrary()
    {
        _inspector.Flush();
        _actions?.Close();
        _picker?.Close();
        _preview?.ForceClose();
        Hide();
    }

    // ----- Layout -----

    private UIElement Build()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());

        // Toolbar
        var toolbar = new Grid { Margin = new Thickness(14, 10, 14, 10) };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 0 });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var heading = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        heading.Children.Add(ToolButton("", Loc.T("Toggle sidebar"), false, () => RunMenu("toggleSidebar")));
        _title.FontSize = 18;
        _title.FontWeight = FontWeights.SemiBold;
        _title.Foreground = _palette.Foreground;
        _title.Margin = new Thickness(8, 0, 0, 1);
        _title.VerticalAlignment = VerticalAlignment.Center;
        heading.Children.Add(_title);
        _count.FontSize = 12;
        _count.Foreground = _palette.Muted;
        _count.Margin = new Thickness(10, 3, 0, 0);
        _count.VerticalAlignment = VerticalAlignment.Center;
        heading.Children.Add(_count);
        toolbar.Children.Add(heading);
        Grid.SetColumn(_kinds, 1);
        toolbar.Children.Add(_kinds);

        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        var searchHost = new Border
        {
            Width = 260,
            Height = 32,
            CornerRadius = new CornerRadius(9),
            Background = _palette.Chip,
            BorderBrush = _palette.PanelBorder,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 8, 0),
        };
        var searchGrid = new Grid { Margin = new Thickness(10, 0, 6, 0) };
        searchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        searchGrid.ColumnDefinitions.Add(new ColumnDefinition());
        searchGrid.Children.Add(new TextBlock { Text = Glyphs.Search, FontFamily = ShelfPalette.Icons, FontSize = 12, Foreground = _palette.Muted, VerticalAlignment = VerticalAlignment.Center });
        _search.Background = Brushes.Transparent;
        _search.BorderThickness = new Thickness(0);
        _search.Foreground = _palette.Foreground;
        _search.CaretBrush = _palette.Foreground;
        _search.FontSize = 13;
        _search.VerticalContentAlignment = VerticalAlignment.Center;
        _search.Margin = new Thickness(4, 0, 0, 0);
        _search.Padding = new Thickness(2, 0, 2, 0);
        _search.MinHeight = 0;
        _search.Style = null;
        _search.TextChanged += (_, _) =>
        {
            _searchPlaceholder.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            _model.SetSearch(_search.Text);
            _searchTimer.Stop();
            _searchTimer.Start();
        };
        Grid.SetColumn(_search, 1);
        searchGrid.Children.Add(_search);
        _searchPlaceholder.Text = Loc.T("Search Library") + "  (" + KeyGestures.Display(_model.Keymap.Primary("librarySearch")) + ")";
        _searchPlaceholder.Foreground = _palette.Muted;
        _searchPlaceholder.FontSize = 13;
        _searchPlaceholder.IsHitTestVisible = false;
        _searchPlaceholder.VerticalAlignment = VerticalAlignment.Center;
        _searchPlaceholder.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(_searchPlaceholder, 1);
        searchGrid.Children.Add(_searchPlaceholder);
        searchHost.Child = searchGrid;
        right.Children.Add(searchHost);
        right.Children.Add(_tools);
        Grid.SetColumn(right, 2);
        toolbar.Children.Add(right);
        var toolbarHost = new Border { Child = toolbar, BorderBrush = _palette.PanelBorder, BorderThickness = new Thickness(0, 0, 0, 1), Background = _palette.Panel };
        root.Children.Add(toolbarHost);

        // Body: sidebar, results, details
        var body = new Grid();
        body.ColumnDefinitions.Add(_sidebarColumn);
        body.ColumnDefinitions.Add(new ColumnDefinition());
        body.ColumnDefinitions.Add(_inspectorColumn);
        body.Children.Add(_sidebar);
        Grid.SetColumn(_grid, 1);
        body.Children.Add(_grid);
        Grid.SetColumn(_inspector, 2);
        body.Children.Add(_inspector);
        _toastText.Foreground = Brushes.White;
        _toastText.FontSize = 12.5;
        _toast.Child = _toastText;
        _toast.CornerRadius = new CornerRadius(14);
        _toast.Padding = new Thickness(14, 6, 14, 7);
        _toast.Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x24, 0x2A, 0x35));
        _toast.BorderBrush = _palette.PanelBorder;
        _toast.BorderThickness = new Thickness(1);
        _toast.HorizontalAlignment = HorizontalAlignment.Center;
        _toast.VerticalAlignment = VerticalAlignment.Bottom;
        _toast.Margin = new Thickness(0, 0, 0, 18);
        _toast.Opacity = 0;
        _toast.IsHitTestVisible = false;
        Grid.SetColumn(_toast, 1);
        body.Children.Add(_toast);
        Grid.SetRow(body, 1);
        root.Children.Add(body);
        return root;
    }

    private void ApplyPanes()
    {
        var current = _settings.Current;
        _sidebarColumn.Width = current.LibrarySidebarVisible ? new GridLength(232) : new GridLength(0);
        _sidebar.Visibility = current.LibrarySidebarVisible ? Visibility.Visible : Visibility.Collapsed;
        _inspectorColumn.Width = current.LibraryInspectorVisible ? new GridLength(320) : new GridLength(0);
        _inspector.Visibility = current.LibraryInspectorVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    private Border ToolButton(string glyph, string tooltip, bool active, Action click)
    {
        var button = new Border
        {
            Width = 32,
            Height = 30,
            Margin = new Thickness(2, 0, 2, 0),
            CornerRadius = new CornerRadius(8),
            Background = active ? _palette.ChipActive : Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = tooltip,
            Child = new TextBlock { Text = glyph, FontFamily = ShelfPalette.Icons, FontSize = 14, Foreground = _palette.Foreground, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        button.MouseEnter += (_, _) => { if (!active) button.Background = _palette.Chip; };
        button.MouseLeave += (_, _) => { if (!active) button.Background = Brushes.Transparent; };
        button.MouseLeftButtonUp += (_, e) =>
        {
            click();
            e.Handled = true;
        };
        return button;
    }

    private Border KindChip(string label, ItemKind? kind)
    {
        var active = _model.Query.Kind == kind;
        var chip = new Border
        {
            Height = 30,
            Padding = new Thickness(14, 0, 14, 0),
            Margin = new Thickness(2, 0, 2, 0),
            CornerRadius = new CornerRadius(8),
            Background = active ? _palette.ChipActive : Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = new TextBlock { Text = label, FontSize = 12.5, Foreground = _palette.Foreground, VerticalAlignment = VerticalAlignment.Center },
        };
        var action = kind switch { ItemKind.Image => "images", ItemKind.Text => "texts", ItemKind.Link => "links", _ => "allTypes" };
        chip.ToolTip = label + "  " + KeyGestures.Display(_model.Keymap.Primary(action));
        chip.MouseEnter += (_, _) => { if (!active) chip.Background = _palette.Chip; };
        chip.MouseLeave += (_, _) => { if (!active) chip.Background = Brushes.Transparent; };
        chip.MouseLeftButtonUp += (_, e) =>
        {
            _model.Perform(action);
            FocusGrid();
            e.Handled = true;
        };
        return chip;
    }

    // ----- Rendering -----

    private void QueueRender()
    {
        if (_renderQueued) return;
        _renderQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _renderQueued = false;
            Render(sidebar: false);
        }, DispatcherPriority.Render);
    }

    private void Render(bool sidebar)
    {
        if (sidebar) _sidebar.Render(_model.Query.Scope);
        _title.Text = ScopeTitle(_model.Query.Scope);
        _count.Text = (_model.Selected.Count > 1
            ? Loc.F("{0} of {1} selected", _model.Selected.Count, _model.Ids.Count)
            : Loc.F("{0} items", _model.Ids.Count))
            + (_model.VisualPending ? " · " + Loc.T("Searching images…") : "")
            + (_visual.Progress is { Total: > 0 } progress ? " · " + Loc.F("Reading images… {0} of {1}", progress.Done, progress.Total) : "");
        _kinds.Children.Clear();
        _kinds.Children.Add(KindChip(Loc.T("All types"), null));
        _kinds.Children.Add(KindChip(Loc.T("Images"), ItemKind.Image));
        _kinds.Children.Add(KindChip(Loc.T("Texts"), ItemKind.Text));
        _kinds.Children.Add(KindChip(Loc.T("Links"), ItemKind.Link));
        RenderTools();
        var filtered = _model.Query.Search.Length > 0 || _model.Query.HasFilters;
        var empty = filtered ? Loc.T("No matching items")
            : _model.Query.Scope.Kind == LibraryScopeKind.Trash ? Loc.T("Trash is empty")
            : _model.Query.Scope.Kind == LibraryScopeKind.Saved ? Loc.T("Nothing saved yet. Pin an item, add it to a folder or tag it to keep it here; everything you copy is in Recent copies.")
            : Loc.T("No items yet");
        (string, Action)? way = filtered ? (Loc.T("Clear filters"), () => RunMenu("clearFilters"))
            : _model.Query.Scope.Kind == LibraryScopeKind.Saved ? (Loc.T("View recent copies"), () => ChooseScope(LibraryScope.Recent))
            : null;
        _grid.Show(_model.Ids, _model.Selected, _model.Focused, _model.ListMode, empty, way);
        if (_model.Focused is { } focused) _grid.EnsureVisible(focused);
        _inspector.Show(_model.Selected.Count > 1 ? _model.ActionIds : _model.Focused is { } id ? [id] : [], _model.Focused);
    }

    private void RenderTools()
    {
        _tools.Children.Clear();
        var keys = _model.Keymap;
        string Key(string action) => keys.Primary(action) is { Length: > 0 } key ? "  " + KeyGestures.Display(key) : "";
        _tools.Children.Add(ToolButton(Glyphs.Pinned, Loc.T("Show pinned items") + Key("pinnedOnly"), _model.ShowingPinnedOnly, () =>
        {
            _model.Perform("pinnedOnly");
            FocusGrid();
        }));
        _filterButton = ToolButton(Glyphs.Filter, Loc.T("Filters"), LibraryFilterPanel.HasPanelFilters(_model.Query), () => RunMenu("filters"));
        _tools.Children.Add(_filterButton);
        _tools.Children.Add(ToolButton("", Loc.T("Sort"), false, ShowSortMenu));
        _tools.Children.Add(ToolButton(_model.ListMode ? "" : "", Loc.T(_model.ListMode ? "Grid" : "List"), false, () => RunMenu(_model.ListMode ? "grid" : "list")));
        _tools.Children.Add(ToolButton("", Loc.T(_settings.Current.LibraryInspectorVisible ? "Hide details" : "Show details"), _settings.Current.LibraryInspectorVisible, () => RunMenu("toggleInspector")));
        if (_model.Query.Scope.Kind == LibraryScopeKind.Trash)
            _tools.Children.Add(ToolButton("", Loc.T("Empty Trash…"), false, EmptyTrash));
        else if (_model.Query.Scope.Kind == LibraryScopeKind.Recent)
            _tools.Children.Add(ToolButton("", Loc.T("Clear unsaved copies…"), false, () => RunMenu("clearUnsavedHistory")));
        _tools.Children.Add(ToolButton("", Loc.T("New note") + Key("newNote"), false, () => OnRequested("newNote", [])));
        _tools.Children.Add(ToolButton(Glyphs.Command, Loc.T("Actions") + Key("actions"), false, () => ShowActions(ActionOrigin.Library)));
    }

    private string ScopeTitle(LibraryScope scope) => scope.Kind switch
    {
        LibraryScopeKind.Favorites => Loc.T("Favorites"),
        LibraryScopeKind.Unorganized => Loc.T("Unorganized"),
        LibraryScopeKind.Recent => Loc.T("Recent copies"),
        LibraryScopeKind.Trash => Loc.T("Trash"),
        LibraryScopeKind.Folder => _store.Folders.FirstOrDefault(f => f.Id == scope.FolderId)?.Name ?? Loc.T("Folder"),
        LibraryScopeKind.Tag => "#" + scope.Tag,
        _ => Loc.T("My library"),
    };

    private void Notify(string text)
    {
        _toastText.Text = text;
        var fade = new DoubleAnimationUsingKeyFrames();
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1600))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1900))));
        _toast.BeginAnimation(OpacityProperty, fade);
    }

    // ----- Focus -----

    private void FocusGrid()
    {
        _grid.Focus();
        Keyboard.Focus(_grid);
    }

    private void FocusSearch()
    {
        _search.Focus();
        Keyboard.Focus(_search);
        _search.SelectAll();
    }

    /// <summary>F6 / Ctrl+Tab order: sidebar, results, details, search.</summary>
    private void CyclePane(int direction)
    {
        var panes = new List<(Func<bool> Has, Action Focus)>
        {
            (() => _sidebar.IsKeyboardFocusWithin, () => { if (_settings.Current.LibrarySidebarVisible) { _sidebar.Focus(); Keyboard.Focus(_sidebar); } else FocusGrid(); }),
            (() => _grid.IsKeyboardFocusWithin, FocusGrid),
            (() => _inspector.IsKeyboardFocusWithin, () => { if (_settings.Current.LibraryInspectorVisible) _inspector.FocusEditor(); else FocusGrid(); }),
            (() => _search.IsKeyboardFocusWithin, FocusSearch),
        };
        var current = panes.FindIndex(p => p.Has());
        if (current < 0) current = 1;
        panes[(current + direction + panes.Count) % panes.Count].Focus();
    }

    // ----- Keys -----

    private void OnKey(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (KeyGestures.IsModifier(key)) return;
        var gesture = KeyGestures.FromEvent(e);
        if (gesture.Length == 0) return;
        var editing = Keyboard.FocusedElement is TextBox;
        if (gesture == "Ctrl+,")
        {
            e.Handled = true;
            if (_inspector.Flush()) OpenSettings?.Invoke("general");
            return;
        }

        if (_search.IsKeyboardFocusWithin)
        {
            switch (gesture)
            {
                case "Enter" or "ArrowDown":
                    if (!_inspector.Flush()) break;
                    _searchTimer.Stop();
                    _model.FocusContent();
                    FocusGrid();
                    e.Handled = true;
                    return;
                case "Escape":
                    if (_search.Text.Length > 0) _search.Text = "";
                    else FocusGrid();
                    e.Handled = true;
                    return;
            }
        }
        else if (_inspector.IsEditing && gesture == "Escape")
        {
            _inspector.Flush();
            FocusGrid();
            e.Handled = true;
            return;
        }
        else if (_sidebar.IsKeyboardFocusWithin && !editing && HandleSidebarKey(gesture))
        {
            e.Handled = true;
            return;
        }

        if (!editing && gesture != "Escape" && !_inspector.Flush()) return;
        if (_model.HandleKey(gesture, editing)) e.Handled = true;
    }

    private bool HandleSidebarKey(string gesture)
    {
        var scopes = _sidebar.Scopes;
        var index = scopes.ToList().IndexOf(_model.Query.Scope);
        switch (gesture)
        {
            case "ArrowUp" or "ArrowDown":
                if (scopes.Count == 0) return true;
                var next = scopes[Math.Clamp(index + (gesture == "ArrowUp" ? -1 : 1), 0, scopes.Count - 1)];
                if (next != _model.Query.Scope)
                {
                    _model.ChangeScope(next);
                    Render(sidebar: true);
                }
                return true;
            case "Enter" or "ArrowRight":
                _model.FocusContent();
                FocusGrid();
                return true;
            default:
                return false;
        }
    }

    // ----- Requests from the model -----

    private void OnRequested(string action, IReadOnlyList<Guid> ids)
    {
        var items = ids.Select(_store.Find).OfType<ClipItem>().ToList();
        switch (action)
        {
            case "copy":
                if (items.Count == 0) return;
                if (_writer.Copy(items)) Notify(items.Count > 1 ? Loc.F("Copied {0} items", items.Count) : Loc.T("Copied"));
                else Notify(Loc.T("Clipboard could not be written."));
                break;
            case "paste":
                _ = PasteAsync(items);
                break;
            case "preview":
                OpenPreview();
                break;
            case "open":
                if (items is [var target]) OpenItem(target);
                break;
            case "edit":
                OpenPreview();
                _preview?.BeginEditing();
                break;
            case "actions":
                ShowActions(ActionOrigin.Library);
                break;
            case "librarySearch":
                FocusSearch();
                break;
            case "focusNextPane":
                CyclePane(1);
                break;
            case "focusPreviousPane":
                CyclePane(-1);
                break;
            case "newNote":
                if (!_inspector.Flush()) return;
                _model.NewNote(Loc.T("Untitled note"));
                if (!_settings.Current.LibraryInspectorVisible) RunMenu("toggleInspector");
                Render(sidebar: true);
                Dispatcher.BeginInvoke(_inspector.FocusEditor, DispatcherPriority.Input);
                break;
            case "newFolder":
                NewFolder(null);
                break;
            case "close":
                HideLibrary();
                break;
            case "folder" or "name" or "tag" or "export":
                Organize(action, ids);
                break;
            case "markup" or "crop":
                if (ids is [var image]) OpenMarkup(image, action == "crop");
                break;
            case "copyOCR":
                if (ids is [var recognized]) CopyRecognizedText(recognized);
                else Notify(Loc.T("Select one image first"));
                break;
            case "refreshLink":
                RefreshLinks(ids);
                break;
            case "trimVideo":
                if (ids is [var video]) OpenTrim(video);
                else Notify(Loc.T("Select a video first"));
                break;
            default:
                Notify(Loc.T("Coming soon"));
                break;
        }
    }

    private void RefreshLinks(IReadOnlyList<Guid> ids)
    {
        var links = ids.Where(id => _store.Find(id) is { Kind: ItemKind.Link }).ToList();
        if (links.Count == 0)
        {
            Notify(Loc.T("Select a link first"));
            return;
        }
        _links.Request(links, force: true);
        Notify(Loc.T("Refreshing link preview…"));
    }

    /// <summary>Pin to folder, name and pin, add tag or export for the captured ids.</summary>
    private void Organize(string action, IReadOnlyList<Guid> ids)
    {
        if (_picker is not null || !_inspector.Flush()) return;
        var items = ids.Select(_store.Find).OfType<ClipItem>().Where(i => !i.IsTrashed).ToList();
        if (items.Count == 0)
        {
            Notify(Loc.T("Select an item first"));
            return;
        }
        Window parent = _preview is { IsOpen: true, IsActive: true } preview ? preview : this;
        if (action == "export")
        {
            _organizer.Export(parent, items, Notify);
            return;
        }
        var captured = items.Select(i => i.Id).ToList();
        HostedWindow? picker;
        if (action == "name")
        {
            var naming = _organizer.NameAndPin(_palette, captured, Notify);
            if (naming is not null)
                naming.Advanced += (_, next) =>
                {
                    if (next is { } id) _model.Reveal(id);
                    else Notify(Loc.T("Pinned"));
                };
            picker = naming;
        }
        else picker = action == "folder" ? _organizer.PinToFolder(_palette, captured, Notify) : _organizer.AddTag(_palette, captured, Notify);
        if (picker is not null) PresentChild(picker, parent);
    }

    /// <summary>Shows a picker or the image editor over <paramref name="parent"/> and returns focus to it afterwards.</summary>
    private void PresentChild(HostedWindow child, Window parent)
    {
        child.Owner = parent;
        child.Closed += (_, _) =>
        {
            _picker = null;
            if (_quitting) return;
            if (child is MarkupWindow or Capture.TrimWindow) SyncPreview();
            if (!child.RestoreParentFocus || !parent.IsVisible) return;
            parent.Activate();
            if (parent == this) FocusGrid();
        };
        _picker = child;
        child.Present(parent);
    }

    private void OpenTrim(Guid id)
    {
        if (_picker is not null || !_inspector.Flush()) return;
        if (_store.Find(id) is not { } item || Capture.TrimWindow.Create(_store, item, _palette, Path.Combine(App.DataDirectory, "transfer"), out _) is not { } trim)
        {
            Notify(Loc.T("Select a video first"));
            return;
        }
        trim.Saved += (_, message) => Notify(message);
        _preview?.Close();
        PresentChild(trim, this);
    }

    /// <summary>Image markup (M) or crop (C).</summary>
    private void OpenMarkup(Guid id, bool crop)
    {
        if (_picker is not null || !_inspector.Flush()) return;
        if (_store.Find(id) is not { } item) return;
        var editor = MarkupWindow.Create(_store, item, _palette, _model.Keymap, crop, out var error);
        if (editor is null)
        {
            Notify(error ?? Loc.T("Select one image first"));
            return;
        }
        editor.Saved += (_, message) => Notify(message);
        PresentChild(editor, _preview is { IsOpen: true, IsActive: true } preview ? preview : this);
    }

    /// <summary>Ctrl+Shift+C: copies the text Windows OCR reads in the image.</summary>
    private async void CopyRecognizedText(Guid id)
    {
        if (_store.Find(id) is not { Kind: ItemKind.Image, Recording: false } image)
        {
            Notify(Loc.T("Select one image first"));
            return;
        }
        if (!_ocr.IsAvailable)
        {
            Notify(Loc.T("Text recognition is unavailable. Add a language with optical character recognition in Windows Settings."));
            return;
        }
        if (image.NeedsOcr) Notify(Loc.T("Recognizing image text locally…"));
        var text = await _ocr.TextOfAsync(id);
        if (text is null) Notify(Loc.T("Text could not be recognized"));
        else if (text.Trim().Length == 0) Notify(Loc.T("No text found"));
        else if (_writer.CopyText(text)) Notify(Loc.T("Image text copied"));
        else Notify(Loc.T("Clipboard could not be written."));
    }

    private bool StepImageEdit(Guid id, bool redo)
    {
        if (_store.Find(id) is not { Kind: ItemKind.Image }) return false;
        var done = redo ? _store.RedoImageEdit(id) : _store.UndoImageEdit(id);
        Notify(Loc.T(done ? (redo ? "Redone" : "Undone") : (redo ? "Nothing to redo" : "Nothing to undo")));
        return true;
    }

    /// <summary>Drags the item, or the whole selection when the item is part of it.</summary>
    private void DragItems(Guid id)
    {
        if (!_inspector.Flush()) return;
        var ids = _model.Selected.Contains(id) ? _model.ActionIds : [id];
        DragOut.Start(_grid, [.. ids.Select(_store.Find).OfType<ClipItem>().Where(i => !i.IsTrashed)], _organizer.Files);
    }

    private async Task PasteAsync(IReadOnlyList<ClipItem> items)
    {
        if (items.Count == 0 || !_writer.Copy(items)) return;
        HideLibrary();
        var verdict = await _foreground.PasteIntoPreviousAsync();
        if (verdict != Pegline.Core.Transfer.PasteVerdict.Allowed)
            System.Windows.MessageBox.Show(Loc.T(Pegline.Core.Transfer.PastePolicy.Message(verdict)), "Pegline");
    }

    private void OpenItem(ClipItem item)
    {
        var error = item.Kind switch
        {
            ItemKind.Link => ShellActions.OpenUrl(item.Url),
            ItemKind.Image => ShellActions.Open(_store.MediaPath(item)),
            _ => null,
        };
        if (error is not null) Notify(error);
    }

    private void OnInspectorAction(string action)
    {
        switch (action)
        {
            case "copy" or "pin" or "preview" or "open":
                _model.Perform(action);
                break;
            case "trash":
                ConfirmTrash();
                break;
            case "restore":
                _model.Restore();
                Notify(Loc.T("Restored"));
                break;
            case "deletePermanently":
                ConfirmDeletePermanently();
                break;
            case "refreshLink":
                RefreshLinks(_model.ActionIds);
                break;
            case "trimVideo":
                if (_model.ActionIds is [var video]) OpenTrim(video);
                break;
        }
    }

    private void ShowContextMenu()
    {
        var items = _model.ActionIds.Select(_store.Find).OfType<ClipItem>().ToList();
        if (items.Count == 0) return;
        var single = items.Count == 1 ? items[0] : null;
        var menu = new ContextMenu();
        void Add(string header, Action click, bool enabled = true)
        {
            var entry = new MenuItem { Header = header, IsEnabled = enabled };
            entry.Click += (_, _) => click();
            menu.Items.Add(entry);
        }
        Add(Loc.T("Copy"), () => _model.Perform("copy"));
        Add(Loc.T("Paste into previous app"), () => _model.Perform("paste"));
        Add(Loc.T("Preview"), () => _model.Perform("preview"), single is not null);
        if (single is { Kind: ItemKind.Link }) Add(Loc.T("Open in browser"), () => OpenItem(single));
        if (single is { Kind: ItemKind.Image })
        {
            Add(Loc.T("Open file"), () => OpenItem(single));
            Add(Loc.T("Show in File Explorer"), () => Notify(ShellActions.Reveal(_store.MediaPath(single)) ?? ""));
        }
        menu.Items.Add(new Separator());
        Add(Loc.T(items.All(i => i.Pinned) ? "Unpin" : "Pin"), () => _model.Perform("pin"));
        if (items.Any(i => !i.IsSaved)) Add(Loc.T("Save to library"), () => RunMenu("retain"));
        var live = items.All(i => !i.IsTrashed);
        Add(Loc.T("Pin to folder…"), () => _model.Perform("folder"), live);
        Add(Loc.T("Name and pin…"), () => _model.Perform("name"), live);
        Add(Loc.T("Add tag…"), () => _model.Perform("tag"), live);
        Add(Loc.T("Export copies…"), () => _model.Perform("export"), live);
        var folders = _store.Folders;
        if (folders.Count > 0)
        {
            var add = new MenuItem { Header = Loc.T("Add to folder") };
            foreach (var folder in folders.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var entry = new MenuItem { Header = folder.ParentId is { } parent ? (folders.FirstOrDefault(f => f.Id == parent)?.Name + " / " + folder.Name) : folder.Name };
                entry.Click += (_, _) => Execute(ActionCatalog.AssignFolderPrefix + folder.Id, ActionOrigin.Library);
                add.Items.Add(entry);
            }
            menu.Items.Add(add);
        }
        menu.Items.Add(new Separator());
        if (items.All(i => i.IsTrashed))
        {
            Add(Loc.T("Restore"), () => OnInspectorAction("restore"));
            Add(Loc.T("Delete permanently…"), ConfirmDeletePermanently);
        }
        else Add(Loc.T("Move to Trash…"), ConfirmTrash);
        menu.IsOpen = true;
    }

    // ----- Confirmations -----

    private void ConfirmTrash()
    {
        var ids = _model.ActionIds;
        if (ids.Count == 0) return;
        var answer = MessageBox.Show(this, Loc.F("Move {0} items to recoverable Trash?", ids.Count) + "\n\n" + Loc.T("You can restore them from Library → Trash. Original files stay in place."),
            "Pegline", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK) return;
        _model.TrashSelection(ids);
        Notify(Loc.T("Moved to Trash"));
    }

    private void ConfirmDeletePermanently()
    {
        var ids = _model.ActionIds.Where(id => _store.Find(id)?.IsTrashed == true).ToList();
        if (ids.Count == 0) return;
        var answer = MessageBox.Show(this, Loc.F("Delete {0} items permanently? This cannot be undone.", ids.Count),
            "Pegline", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK) return;
        _model.DeletePermanently();
        Notify(Loc.T("Deleted permanently"));
    }

    private void EmptyTrash()
    {
        var count = _store.Items.Count(i => i.IsTrashed);
        if (count == 0) return;
        var answer = MessageBox.Show(this, Loc.F("Delete {0} items permanently? This cannot be undone.", count),
            "Pegline", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK) return;
        _store.EmptyTrash();
        Notify(Loc.T("Trash emptied"));
    }

    private void ConfirmClearHistory()
    {
        var snapshot = _store.SnapshotUnsavedHistory();
        if (snapshot.Entries.Count == 0)
        {
            Notify(Loc.T("No unsaved copies"));
            return;
        }
        var answer = MessageBox.Show(this, Loc.F("Move {0} unsaved copies to Trash? Pinned, foldered and tagged items stay.", snapshot.Entries.Count),
            "Pegline", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK) return;
        var cleared = _store.ClearUnsavedHistory(snapshot);
        Notify(Loc.F("Moved {0} items to Trash", cleared.Count));
    }

    // ----- Folders and tags -----

    private void ChooseScope(LibraryScope scope)
    {
        if (!_inspector.Flush()) return;
        _model.ChangeScope(scope);
        _model.FocusContent();
        Render(sidebar: true);
        FocusGrid();
    }

    private void NewFolder(Folder? parent)
    {
        var name = PromptWindow.Ask(this, Loc.T(parent is null ? "New folder" : "New subfolder"), "", _palette);
        if (name is null) return;
        try
        {
            var folder = _store.CreateFolder(name, parent?.Id);
            ChooseScope(LibraryScope.ForFolder(folder.Id));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            Notify(ex.Message);
        }
    }

    private void RenameFolder(Folder folder)
    {
        var name = PromptWindow.Ask(this, Loc.T("Rename folder"), folder.Name, _palette);
        if (name is null) return;
        try { _store.RenameFolder(folder.Id, name); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { Notify(ex.Message); }
    }

    private void RemoveFolder(Folder folder)
    {
        var answer = MessageBox.Show(this, Loc.F("Remove the folder “{0}”? Its items stay in your Library.", folder.Name),
            "Pegline", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK) return;
        _store.RemoveFolder(folder.Id);
        ChooseScope(LibraryScope.Saved);
    }

    private void RenameTag(string tag)
    {
        var name = PromptWindow.Ask(this, Loc.T("Rename tag"), tag, _palette);
        if (name is null) return;
        try
        {
            _store.RenameTag(tag, name.TrimStart('#'));
            if (_model.Query.Scope.Kind == LibraryScopeKind.Tag) ChooseScope(LibraryScope.ForTag(name.TrimStart('#')));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { Notify(ex.Message); }
    }

    private void RemoveTag(string tag)
    {
        var answer = MessageBox.Show(this, Loc.F("Remove the tag “#{0}” from every item?", tag),
            "Pegline", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK) return;
        _store.DeleteTag(tag);
        ChooseScope(LibraryScope.Saved);
    }

    // ----- Actions palette -----

    private static readonly HashSet<string> LibraryActions =
    [
        "previous", "next", "up", "down", "first", "last", "previousPage", "nextPage", "extendPrevious", "extendNext", "extendUp",
        "extendDown", "selectAll", "pin", "pinnedOnly", "allTypes", "images", "texts", "links", "trash", "undoTrash", "copy", "paste",
        "preview", "open", "edit", "librarySearch", "search", "library", "focusNextPane", "focusPreviousPane", "close", "finishEditing",
        "menu.newNote", "menu.newFolder", "menu.newSubfolder", "menu.renameFolder", "menu.removeFolder", "menu.renameTag", "menu.removeTag",
        "menu.retain", "menu.restore", "menu.editDetails", "menu.scopeSaved", "menu.scopeFavorites", "menu.scopeUnorganized",
        "menu.scopeRecent", "menu.scopeTrash", "menu.grid", "menu.list", "menu.toggleSidebar", "menu.toggleInspector",
        "menu.toggleFullscreen", "menu.clearFilters", "menu.sortNewest", "menu.sortOldest", "menu.sortTitle", "menu.sortTitleDescending",
        "menu.sortCopied", "menu.sortRelevance", "menu.searchAll", "menu.searchText", "menu.searchVisual", "menu.filters", "menu.daysAny", "menu.days7", "menu.days30",
        "menu.openFile", "menu.revealFile", "menu.clearUnsavedHistory", "menu.toggleLine", "menu.copyLatestImage", "menu.quit",
        "folder", "name", "tag", "export", "markup", "crop", "menu.markupUndo", "menu.markupRedo", "copyOCR", "refreshLink", "trimVideo",
        "menu.openScreenshotsFolder", "menu.resetZoom", "menu.settings", "menu.settingsGeneral", "menu.settingsHistory",
        "menu.settingsSources", "menu.settingsShortcuts", "menu.settingsAbout", "menu.checkUpdates", "menu.installUpdate",
    ];

    /// <summary>Opens Settings at a section; the Library stays open behind it.</summary>
    public Action<string>? OpenSettings { get; set; }

    private static bool IsImplemented(string id) =>
        LibraryActions.Contains(id)
        || id.StartsWith(ActionCatalog.OpenFolderPrefix, StringComparison.Ordinal)
        || id.StartsWith(ActionCatalog.OpenTagPrefix, StringComparison.Ordinal)
        || id.StartsWith(ActionCatalog.AssignFolderPrefix, StringComparison.Ordinal)
        || id.StartsWith(ActionCatalog.MoveFolderPrefix, StringComparison.Ordinal)
        || id.StartsWith(ActionCatalog.UnassignFolderPrefix, StringComparison.Ordinal)
        || id.StartsWith(ActionCatalog.RemoveTagPrefix, StringComparison.Ordinal);

    private IReadOnlyList<ClipItem> Targets(ActionOrigin origin) =>
        (origin == ActionOrigin.LibraryPreview && _preview?.Item is { } current ? [current.Id] : _model.ActionIds)
            .Select(_store.Find).OfType<ClipItem>().ToList();

    private void ShowActions(ActionOrigin origin)
    {
        if (_actions is not null || !_inspector.Flush()) return;
        var items = Targets(origin);
        var first = items.FirstOrDefault();
        var single = items.Count == 1 ? first : null;
        var query = _model.Query;
        var shortcuts = _settings.Current.GlobalShortcuts;
        var context = new ActionContext
        {
            Origin = origin,
            SelectedCount = items.Count,
            Kind = items.Count > 0 && items.All(i => i.Kind == first!.Kind) ? first!.Kind : null,
            HasFile = first is { Kind: ItemKind.Image } && File.Exists(_store.MediaPath(first)),
            IsRecording = first?.Recording ?? false,
            IsDeleted = first?.IsTrashed ?? false,
            AllPinned = items.Count > 0 && items.All(i => i.Pinned),
            HasItems = _model.Ids.Count > 0,
            HasLatestImage = _store.Items.Any(i => i.Kind == ItemKind.Image && i.IsOnShelf),
            CanUndoLibraryTrash = _store.CanUndoTrash,
            CanUndoMarkup = single is { Kind: ItemKind.Image } && _store.CanUndoImageEdit(single.Id),
            CanRedoMarkup = single is { Kind: ItemKind.Image } && _store.CanRedoImageEdit(single.Id),
            IsTextEditing = _preview?.IsTextEditing ?? false,
            ActiveFavorites = _model.ShowingPinnedOnly,
            GlobalToggleHint = shortcuts.GetValueOrDefault("toggle", ""),
            CopyLatestHint = shortcuts.GetValueOrDefault("copyLatest", ""),
            CanCheckUpdates = App.UpdateChecks?.IsInstalled ?? false,
            CanInstallUpdate = App.UpdateChecks?.AvailableVersion is not null,
            LibraryScope = query.Scope,
            Folders = _store.Folders,
            Tags = _store.AllTags,
            ItemFolders = single is null ? [] : _store.Folders.Where(f => single.FolderIds.Contains(f.Id)).ToList(),
            ItemTags = single?.Tags ?? [],
            LibraryKind = query.Kind,
            LibraryListMode = _model.ListMode,
            LibraryInspectorVisible = _settings.Current.LibraryInspectorVisible,
            LibrarySidebarVisible = _settings.Current.LibrarySidebarVisible,
            LibrarySort = query.Sort,
            LibrarySearchMode = query.SearchMode,
            LibraryDays = query.Days,
            HasLibraryFilters = query.HasFilters,
        };
        var entries = ActionCatalog.Entries(context, _model.Keymap, Loc.T, KeyGestures.Display)
            .Select(command => command.IsEnabled && !IsImplemented(command.Id)
                ? command with { IsEnabled = false, DisabledReason = Loc.T("Coming soon") }
                : command)
            .ToList();
        var palette = new ActionPalette(entries, _palette, id => Execute(id, origin), () =>
        {
            _preview?.Close();
            FocusSearch();
        });
        Window parent = origin == ActionOrigin.LibraryPreview && _preview is { IsOpen: true } preview ? preview : this;
        palette.Owner = parent;
        palette.Closed += (_, _) =>
        {
            _actions = null;
            if (palette.RestoreParentFocus && parent.IsVisible) parent.Activate();
        };
        _actions = palette;
        var handle = new WindowInteropHelper(parent).Handle;
        Win32.GetWindowRect(handle, out var anchor);
        var (work, scale) = Win32.MonitorOf(handle);
        palette.ShowOver(work, scale, anchor);
    }

    private void Execute(string id, ActionOrigin origin)
    {
        var ids = Targets(origin).Select(i => i.Id).ToList();
        try
        {
            if (id.StartsWith(ActionCatalog.MenuPrefix, StringComparison.Ordinal))
                RunMenu(id[ActionCatalog.MenuPrefix.Length..]);
            else if (id.StartsWith(ActionCatalog.OpenFolderPrefix, StringComparison.Ordinal))
                ChooseScope(LibraryScope.ForFolder(Guid.Parse(id[ActionCatalog.OpenFolderPrefix.Length..])));
            else if (id.StartsWith(ActionCatalog.OpenTagPrefix, StringComparison.Ordinal))
                ChooseScope(LibraryScope.ForTag(id[ActionCatalog.OpenTagPrefix.Length..]));
            else if (id.StartsWith(ActionCatalog.AssignFolderPrefix, StringComparison.Ordinal))
            {
                _store.AddToFolder(ids, Guid.Parse(id[ActionCatalog.AssignFolderPrefix.Length..]));
                Notify(Loc.T("Added to folder"));
            }
            else if (id.StartsWith(ActionCatalog.MoveFolderPrefix, StringComparison.Ordinal))
            {
                var target = Guid.Parse(id[ActionCatalog.MoveFolderPrefix.Length..]);
                _store.AddToFolder(ids, target);
                if (_model.Query.Scope.FolderId is { } source && source != target) _store.RemoveFromFolder(ids, source);
                Notify(Loc.T("Moved to folder"));
            }
            else if (id.StartsWith(ActionCatalog.UnassignFolderPrefix, StringComparison.Ordinal))
                _store.RemoveFromFolder(ids, Guid.Parse(id[ActionCatalog.UnassignFolderPrefix.Length..]));
            else if (id.StartsWith(ActionCatalog.RemoveTagPrefix, StringComparison.Ordinal))
                _store.RemoveTag(ids, id[ActionCatalog.RemoveTagPrefix.Length..]);
            else if (id is "folder" or "name" or "tag" or "export")
                Organize(id, ids);
            else if (id is "markup" or "crop")
            {
                if (ids is [var image]) OpenMarkup(image, id == "crop");
            }
            else if (origin == ActionOrigin.LibraryPreview && _preview is { IsOpen: true } preview)
            {
                switch (id)
                {
                    case "close" or "preview": preview.Close(); break;
                    case "edit": preview.BeginEditing(); break;
                    case "finishEditing": preview.FinishEditing(); break;
                    case "open": preview.OpenLink(); break;
                    default:
                        if (!((IPreviewHost)this).Forward(id, "")) _model.Perform(LibraryModel.LibraryAction(id));
                        break;
                }
            }
            else
            {
                switch (id)
                {
                    case "search" or "librarySearch": FocusSearch(); break;
                    case "library": FocusGrid(); break;
                    default: _model.Perform(LibraryModel.LibraryAction(id)); break;
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException)
        {
            Notify(Loc.T(ex.Message));
        }
    }

    private void RunMenu(string action)
    {
        var scope = _model.Query.Scope;
        if (Shelves.ShelfWindow.SettingsSection(action) is { } section)
        {
            OpenSettings?.Invoke(section);
            return;
        }
        switch (action)
        {
            case "newNote": OnRequested("newNote", []); break;
            case "markupUndo" or "markupRedo":
                if (Targets(ActionOrigin.Library) is [{ Kind: ItemKind.Image } edited]) StepImageEdit(edited.Id, action == "markupRedo");
                break;
            case "newFolder": NewFolder(null); break;
            case "newSubfolder" or "renameFolder" or "removeFolder":
                if (_store.Folders.FirstOrDefault(f => f.Id == scope.FolderId) is not { } folder) break;
                if (action == "newSubfolder") NewFolder(folder);
                else if (action == "renameFolder") RenameFolder(folder);
                else RemoveFolder(folder);
                break;
            case "renameTag": if (scope.Tag is { } renamed) RenameTag(renamed); break;
            case "removeTag": if (scope.Tag is { } removed) RemoveTag(removed); break;
            case "retain":
                _model.Retain();
                Notify(Loc.T("Saved to Library"));
                break;
            case "restore": OnInspectorAction("restore"); break;
            case "editDetails":
                if (!_settings.Current.LibraryInspectorVisible) RunMenu("toggleInspector");
                Dispatcher.BeginInvoke(_inspector.FocusEditor, DispatcherPriority.Input);
                break;
            case "scopeSaved": ChooseScope(LibraryScope.Saved); break;
            case "scopeFavorites": ChooseScope(LibraryScope.Favorites); break;
            case "scopeUnorganized": ChooseScope(LibraryScope.Unorganized); break;
            case "scopeRecent": ChooseScope(LibraryScope.Recent); break;
            case "scopeTrash": ChooseScope(LibraryScope.Trash); break;
            case "grid" or "list":
                _model.ListMode = action == "list";
                _settings.Update(s => s.LibraryListMode = action == "list");
                Render(sidebar: false);
                break;
            case "toggleSidebar":
                _settings.Update(s => s.LibrarySidebarVisible = !s.LibrarySidebarVisible);
                ApplyPanes();
                break;
            case "toggleInspector":
                _inspector.Flush();
                _settings.Update(s => s.LibraryInspectorVisible = !s.LibraryInspectorVisible);
                ApplyPanes();
                Render(sidebar: false);
                break;
            case "toggleFullscreen":
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                break;
            case "clearFilters":
                _search.Text = "";
                _model.ClearFilters();
                break;
            case "sortNewest": _model.SetSort(LibrarySort.Newest); break;
            case "sortOldest": _model.SetSort(LibrarySort.Oldest); break;
            case "sortTitle": _model.SetSort(LibrarySort.Title); break;
            case "sortTitleDescending": _model.SetSort(LibrarySort.TitleDescending); break;
            case "sortCopied": _model.SetSort(LibrarySort.Copied); break;
            case "sortRelevance": _model.SetSort(LibrarySort.Relevance); break;
            case "searchAll": _model.SetSearchMode(LibrarySearchMode.All); break;
            case "searchText": _model.SetSearchMode(LibrarySearchMode.Text); break;
            case "searchVisual": _model.SetSearchMode(LibrarySearchMode.Visual); break;
            case "filters":
                Render(sidebar: false);
                if (_filterButton is not null) _filters.Toggle(_filterButton);
                break;
            case "daysAny": _model.SetDays(null); break;
            case "days7": _model.SetDays(7); break;
            case "days30": _model.SetDays(30); break;
            case "openFile" or "revealFile":
                if (Targets(ActionOrigin.Library) is [{ Kind: ItemKind.Image } image])
                {
                    var path = _store.MediaPath(image);
                    if ((action == "openFile" ? ShellActions.Open(path) : ShellActions.Reveal(path)) is { } error) Notify(error);
                }
                break;
            case "clearUnsavedHistory": ConfirmClearHistory(); break;
            case "toggleLine":
                HideLibrary();
                _showShelves();
                break;
            case "copyLatestImage":
                var latest = _store.Items.FirstOrDefault(i => i.Kind == ItemKind.Image && i.IsOnShelf);
                if (latest is not null && _writer.Copy([latest])) Notify(Loc.T("Copied"));
                break;
            case "openScreenshotsFolder":
                var screenshots = Capture.FolderWatcher.ScreenshotsFolder;
                Notify(ShellActions.Open(screenshots) is null ? "" : Loc.T("Folder not found"));
                break;
            case "resetZoom": _preview?.ResetZoom(); break;
            case "quit": _quit(); break;
            default: Notify(Loc.T("Coming soon")); break;
        }
    }

    /// <summary>What the filter panel says about visual search in this build.</summary>
    private string VisualNote()
    {
        if (!_visual.IsBundled) return Loc.T("Visual search is not included in this build; text and OCR search still work.");
        if (_visual.IsBroken) return Loc.T("Visual search is unavailable: its model files are damaged. Reinstall Pegline to restore them.");
        if (!_settings.Current.IndexImages) return Loc.T("Image indexing is turned off in Settings › Sources.");
        return _visual.Progress is { Total: > 0 } progress ? Loc.F("Reading images… {0} of {1}", progress.Done, progress.Total) : "";
    }

    private void ShowSortMenu()
    {
        var menu = new ContextMenu();
        void Add(string header, bool active, Action click)
        {
            var entry = new MenuItem { Header = header, IsCheckable = true, IsChecked = active };
            entry.Click += (_, _) => click();
            menu.Items.Add(entry);
        }
        var query = _model.Query;
        Add(Loc.T("Newest first"), query.Sort == LibrarySort.Newest, () => RunMenu("sortNewest"));
        Add(Loc.T("Oldest first"), query.Sort == LibrarySort.Oldest, () => RunMenu("sortOldest"));
        Add(Loc.T("Title"), query.Sort == LibrarySort.Title, () => RunMenu("sortTitle"));
        Add(Loc.T("Title, Z–A"), query.Sort == LibrarySort.TitleDescending, () => RunMenu("sortTitleDescending"));
        Add(Loc.T("Last copied"), query.Sort == LibrarySort.Copied, () => RunMenu("sortCopied"));
        if (query.Search.Length > 0) Add(Loc.T("Best match"), query.Sort == LibrarySort.Relevance, () => RunMenu("sortRelevance"));
        menu.Items.Add(new Separator());
        Add(Loc.T("Any time"), query.Days is null, () => RunMenu("daysAny"));
        Add(Loc.T("Last 7 days"), query.Days == 7, () => RunMenu("days7"));
        Add(Loc.T("Last 30 days"), query.Days == 30, () => RunMenu("days30"));
        if (query.HasFilters)
        {
            menu.Items.Add(new Separator());
            Add(Loc.T("Clear filters"), false, () => RunMenu("clearFilters"));
        }
        menu.IsOpen = true;
    }

    // ----- Preview -----

    Keymap IPreviewHost.Keymap => _model.Keymap;

    private void OpenPreview()
    {
        if (_model.FocusedItem is not { } item) return;
        if (!_inspector.Flush()) return;
        _preview ??= new PreviewWindow(this, _store, _palette) { Owner = this };
        if (!_preview.Present(item, PositionOf(item.Id), Placement()))
        {
            Notify(Loc.T("Preview unavailable"));
            if (!_preview.IsOpen) _preview.ForceClose();
        }
    }

    private Placement Placement()
    {
        var target = _preview is { IsOpen: true } open ? (Window)open : this;
        var handle = new WindowInteropHelper(target).Handle;
        Win32.GetWindowRect(handle, out var rect);
        var (work, scale) = Win32.MonitorOf(handle);
        return new Placement(work, scale, rect);
    }

    private string PositionOf(Guid id) => $"{(_model.IndexOf(id) ?? -1) + 1} / {_model.Ids.Count}";

    private void SyncPreview()
    {
        // The image editor owns the preview's item until it closes; the preview catches up afterwards.
        if (_picker is MarkupWindow) return;
        if (_preview is not { IsOpen: true } preview) return;
        if (_model.FocusedItem is not { } item)
        {
            preview.ForceClose();
            return;
        }
        if (preview.Item?.Id == item.Id && !preview.NeedsReload(item)) preview.Refresh(item, PositionOf(item.Id));
        else if (!preview.Present(item, PositionOf(item.Id), Placement())) preview.ForceClose();
    }

    bool IPreviewHost.Forward(string action, string gesture)
    {
        if (_preview?.Item is not { } current) return false;
        action = LibraryModel.LibraryAction(action);
        switch (action)
        {
            case "previous" or "next" or "up" or "down" or "first" or "last" or "previousPage" or "nextPage" or "pinnedOnly":
                _model.Perform(action);
                break;
            case "copy":
                if (_writer.Copy([_store.Find(current.Id) ?? current])) _preview.FlashCopied();
                return true;
            case "paste":
                _preview.ForceClose();
                _ = PasteAsync([_store.Find(current.Id) ?? current]);
                return true;
            case "pin":
                _store.TogglePin([current.Id]);
                return true;
            case "trash":
                _model.TrashSelection([current.Id]);
                break;
            case "undoTrash":
                _model.UndoTrash();
                break;
            case "actions":
                ShowActions(ActionOrigin.LibraryPreview);
                return true;
            case "folder" or "name" or "tag" or "export":
                Organize(action, [current.Id]);
                return true;
            case "markup" or "crop":
                OpenMarkup(current.Id, action == "crop");
                return true;
            case "markupUndo" or "markupRedo":
                return StepImageEdit(current.Id, action == "markupRedo");
            case "library":
                FocusGrid();
                return true;
            case "copyOCR":
                CopyRecognizedText(current.Id);
                return true;
            case "refreshLink":
                RefreshLinks([current.Id]);
                return true;
            case "trimVideo":
                OpenTrim(current.Id);
                return true;
            default:
                return false;
        }
        Render(sidebar: false);
        SyncPreview();
        return true;
    }

    bool IPreviewHost.SaveText(Guid id, string text) => _store.EditText(id, text);

    bool IPreviewHost.CopyText(string text) => _writer.CopyText(text);

    void IPreviewHost.PreviewDeactivated() { }

    void IPreviewHost.PreviewClosed()
    {
        _preview = null;
        if (!IsVisible) return;
        Activate();
        FocusGrid();
    }
}
