using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Pegline.App.Localization;
using Pegline.App.Organize;
using Pegline.Core.Actions;
using Pegline.App.Platform;
using Pegline.Core.Input;
using Pegline.Core.Library;
using Pegline.Core.Models;
using Pegline.Core.Shelves;
using Pegline.Core.Storage;

namespace Pegline.App.Shelves;

/// <summary>The hanging shelves: a panel that slides down from the top of the active monitor.</summary>
public partial class ShelfWindow : Window
{
    private readonly LibraryStore _store;
    private readonly SettingsStore _settings;
    private readonly ClipboardWriter _writer;
    private readonly ForegroundTracker _foreground;
    private readonly Action _quit;
    private readonly Action<LibraryScope?, ItemKind?, bool> _openLibrary;
    private readonly Organizer _organizer;
    private readonly Capture.OcrService _ocr;
    private readonly Links.LinkPreviewService _links;
    private readonly ShelfNavigator _nav = new();
    private readonly ThumbnailCache _thumbnails = new();
    private readonly Dictionary<ItemKind, ShelfRowView> _rows = [];
    private Keymap _keymap;
    private ShelfPalette _palette;
    private CardMetrics _metrics;
    private Win32.RECT _work;
    private double _scale = 1;
    private bool _selectMode;
    private bool _holdOpen;
    private bool _hiding;
    private DateTime _shownAt;
    private bool _snapshot = false; // set only by the debug snapshot mode

    /// <summary>Development aid: PEGLINE_PIN_SHELVES=1 keeps the shelves open when they lose focus.</summary>
    private static readonly bool PinnedForDebugging = Environment.GetEnvironmentVariable("PEGLINE_PIN_SHELVES") == "1";

    internal ShelfWindow(LibraryStore store, SettingsStore settings, ClipboardWriter writer, ForegroundTracker foreground, Action quit, Action<LibraryScope?, ItemKind?, bool> openLibrary, Organizer organizer, Capture.OcrService ocr, Links.LinkPreviewService links)
    {
        _organizer = organizer;
        _ocr = ocr;
        _links = links;
        _store = store;
        _settings = settings;
        _writer = writer;
        _foreground = foreground;
        _quit = quit;
        _openLibrary = openLibrary;
        InitializeComponent();
        _keymap = new Keymap(settings.Current.LocalShortcuts);
        _palette = ShelfPalette.For(settings.Current.Theme);
        _metrics = CardMetrics.For(settings.Current.CardSize);

        foreach (var kind in ShelfNavigator.Order)
        {
            var row = new ShelfRowView(kind, store, _thumbnails);
            row.CardClicked += (_, e) => OnCardClicked(e.Item, e.Args);
            row.CardDragged += (_, item) => DragCards(row, item);
            row.CardDoubleClicked += (_, e) =>
            {
                _nav.Select(e.Item.Id);
                OpenPreview();
            };
            row.HeaderClicked += (_, _) => ActivateRow(kind);
            row.PageRequested += (_, direction) => PageRow(kind, direction);
            _rows[kind] = row;
            Rows.Children.Add(row);
        }

        _nav.SetVisibleKinds(settings.Current.VisibleKinds);
        _nav.CloseRequested += (_, _) => HideShelves(restoreFocus: true);
        _store.Changed += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (!IsVisible) return;
            _nav.SetItems(_store.Items);
            Render();
            SyncPreview();
        });
        _settings.Changed += (_, _) => ApplySettings();

        SearchBox.TextChanged += (_, _) =>
        {
            SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (SearchBox.Text == _nav.Query) return;
            _nav.SetQuery(SearchBox.Text);
            Render();
        };
        SearchBox.PreviewKeyDown += OnSearchKeyDown;
        PreviewKeyDown += OnKeyDown;
        // A tray click or a slow focus hand-off can deactivate the panel right after it opens.
        Deactivated += (_, _) =>
        {
            if (DateTime.UtcNow - _shownAt > TimeSpan.FromMilliseconds(400)) ScheduleFocusCheck();
        };
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            Win32.SetWindowLong(handle, Win32.GWL_EXSTYLE, Win32.GetWindowLong(handle, Win32.GWL_EXSTYLE) | Win32.WS_EX_TOOLWINDOW);
        };
        ApplySettings();
    }

    public bool IsShown => IsVisible && !_hiding;

    // ----- Show / hide -----

    public void ToggleShelves()
    {
        if (IsShown) HideShelves(restoreFocus: true);
        else ShowShelves();
    }

    public void ShowShelves(bool search = false)
    {
        if (!IsShown) _foreground.Remember();
        _hiding = false;
        _shownAt = DateTime.UtcNow;
        (_work, _scale) = Win32.MonitorUnderCursor();
        var width = (_work.Right - _work.Left) / _scale;
        foreach (var kind in ShelfNavigator.Order) _nav.SetPageSize(kind, ShelfRowView.Fit(width, _metrics));
        _nav.SetItems(_store.Items);
        _nav.Open();
        if (search) _nav.OpenSearch();
        SearchBox.Text = "";
        Render();

        var wasVisible = IsVisible;
        if (!wasVisible)
        {
            Opacity = 0;
            Show();
        }
        Place();
        Activate();
        Win32.ForceForeground(new WindowInteropHelper(this).Handle);
        if (search) FocusSearch();
        else Keyboard.Focus(this);

        if (!wasVisible)
        {
            Opacity = 1;
            Slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-Panel.ActualHeight - 20, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
    }

#if DEBUG
    internal void ShowForSnapshot()
    {
        _snapshot = true;
        (_work, _scale) = Win32.MonitorUnderCursor();
        var width = (_work.Right - _work.Left) / _scale;
        foreach (var kind in ShelfNavigator.Order) _nav.SetPageSize(kind, ShelfRowView.Fit(width, _metrics));
        _nav.SetItems(_store.Items);
        _nav.Open();
        Width = width;
        Show();
        Render();
        Panel.Measure(new Size(width, double.PositiveInfinity));
        Height = Panel.DesiredSize.Height + 2;
    }
#endif

    public void HideShelves(bool restoreFocus)
    {
        if (!IsVisible || _hiding) return;
        _hiding = true;
        CloseChildren();
        _nav.Close();
        var animation = new DoubleAnimation(0, -Panel.ActualHeight - 20, TimeSpan.FromMilliseconds(150)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        animation.Completed += (_, _) =>
        {
            if (!_hiding) return;
            Hide();
            _hiding = false;
            Slide.BeginAnimation(TranslateTransform.YProperty, null);
        };
        Slide.BeginAnimation(TranslateTransform.YProperty, animation);
        if (restoreFocus) _foreground.Restore();
    }

    /// <summary>Sizes the window to the monitor width and the panel's height, in device pixels.</summary>
    private void Place()
    {
        if (_snapshot) return;
        var width = (_work.Right - _work.Left) / _scale;
        Panel.Measure(new Size(width, double.PositiveInfinity));
        var height = Math.Ceiling(Panel.DesiredSize.Height + 2);
        var handle = new WindowInteropHelper(this).Handle;
        for (var pass = 0; pass < 2; pass++) // a second pass settles a DPI change between monitors
            Win32.SetWindowPos(handle, Win32.HWND_TOPMOST, _work.Left, _work.Top, _work.Right - _work.Left, (int)Math.Ceiling(height * _scale), Win32.SWP_SHOWWINDOW | Win32.SWP_NOACTIVATE);
    }

    // ----- Rendering -----

    private void ApplySettings()
    {
        var current = _settings.Current;
        _keymap = new Keymap(current.LocalShortcuts);
        _palette = ShelfPalette.For(current.Theme);
        _metrics = CardMetrics.For(current.CardSize);
        Panel.Background = _palette.Panel;
        Panel.BorderBrush = _palette.PanelBorder;
        Brand.Foreground = _palette.Foreground;
        Status.Foreground = _palette.Foreground;
        SearchHost.Background = _palette.Chip;
        SearchHost.BorderBrush = _palette.Accent;
        SearchBox.Foreground = _palette.Foreground;
        SearchGlyph.Foreground = _palette.Foreground;
        SearchPlaceholder.Foreground = _palette.Foreground;
        SearchPlaceholder.Text = Loc.T("Search visible shelves…");
        SearchCount.Foreground = _palette.Foreground;
        EmptyTitle.Foreground = _palette.Foreground;
        EmptyHint.Foreground = _palette.Foreground;
        foreach (var row in _rows.Values)
        {
            row.Palette = _palette;
            row.Metrics = _metrics;
        }
        if (IsShown) Render();
    }

    private void Render()
    {
        var width = (_work.Right - _work.Left) / Math.Max(_scale, 0.1);
        if (width <= 0) width = SystemParameters.WorkArea.Width;
        var selected = _nav.Selection.ToHashSet();
        foreach (var kind in ShelfNavigator.Order)
        {
            var view = _rows[kind];
            if (!_nav.IsVisible(kind))
            {
                view.Visibility = Visibility.Collapsed;
                continue;
            }
            view.Visibility = Visibility.Visible;
            var row = _nav.Row(kind);
            var offset = _nav.Offset(kind);
            var size = _nav.PageSize(kind);
            var page = row.Skip(offset).Take(size).ToList();
            view.Render(new RowState(page, offset, row.Count, size, kind == _nav.ActiveKind && _nav.Focused is not null, _nav.Focused, selected, _nav.PinnedOnlyKinds.Contains(kind), width));
        }

        var anything = ShelfNavigator.Order.Any(k => _nav.IsVisible(k) && _nav.Row(k).Count > 0);
        EmptyState.Visibility = anything ? Visibility.Collapsed : Visibility.Visible;
        EmptyTitle.Text = Loc.T(_nav.Query.Length > 0 ? "No matching items" : "No items yet");
        EmptyHint.Text = _nav.Query.Length > 0 ? "" : Loc.T("Copy something to get started.");

        SearchHost.Visibility = _nav.SearchActive ? Visibility.Visible : Visibility.Collapsed;
        SearchHost.BorderThickness = new Thickness(_nav.SearchEditing ? 1.5 : 0);
        var results = ShelfNavigator.Order.Where(_nav.IsVisible).Sum(k => _nav.Row(k).Count);
        SearchCount.Text = _nav.Query.Length > 0 ? Loc.F("{0} results", results) : "";
        Status.Text = _nav.Selection.Count > 1 ? $"{_nav.Selection.Count} ✓" : "";
        RenderToolbar();
        if (IsVisible) Place();
    }

    private void RenderToolbar()
    {
        Toolbar.Children.Clear();
        Toolbar.Children.Add(ToolButton("", Loc.T("Search shelves") + "  " + _keymap.Primary("search"), _nav.SearchActive, () =>
        {
            _nav.OpenSearch();
            Render();
            FocusSearch();
        }));
        Toolbar.Children.Add(Separator());
        foreach (var kind in ShelfNavigator.Order)
        {
            var action = kind switch { ItemKind.Image => "toggleImages", ItemKind.Text => "toggleTexts", _ => "toggleLinks" };
            Toolbar.Children.Add(ToolButton(ShelfRowView.Glyph(kind), ShelfRowView.Title(kind) + "  " + _keymap.Primary(action), _nav.IsVisible(kind), () => ToggleKind(kind)));
        }
        Toolbar.Children.Add(Separator());
        Toolbar.Children.Add(ToolButton("", Loc.T("Show pinned items") + "  " + _keymap.Primary("pinnedOnly"), _nav.PinnedOnlyKinds.Contains(_nav.ActiveKind), () => Run("pinnedOnly", "")));
        Toolbar.Children.Add(ToolButton("", Loc.T("Select items"), _selectMode, () =>
        {
            _selectMode = !_selectMode;
            Render();
        }));
        Toolbar.Children.Add(ToolButton("", Loc.T("Hide shelves") + "  Esc", false, () => HideShelves(restoreFocus: true)));
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
            ToolTip = tooltip.Trim(),
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

    private Border Separator() => new() { Width = 1, Height = 18, Margin = new Thickness(6, 0, 6, 0), Background = _palette.PanelBorder };

    private void ShowToast(string text)
    {
        ToastText.Text = text;
        var fade = new DoubleAnimationUsingKeyFrames();
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1300))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1600))));
        Toast.BeginAnimation(OpacityProperty, fade);
    }

    // ----- Input -----

    private void FocusSearch()
    {
        SearchHost.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(() =>
        {
            SearchBox.Focus();
            Keyboard.Focus(SearchBox);
            SearchBox.CaretIndex = SearchBox.Text.Length;
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                _nav.CancelSearch();
                SearchBox.Text = "";
                Keyboard.Focus(this);
                Render();
                e.Handled = true;
                break;
            case Key.Enter or Key.Up or Key.Down:
                _nav.FinishSearch();
                Keyboard.Focus(this);
                Render();
                e.Handled = true;
                break;
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource == SearchBox || KeyGestures.IsModifier(e.Key == Key.System ? e.SystemKey : e.Key)) return;
        var gesture = KeyGestures.FromEvent(e);
        if (gesture.Length == 0) return;
        foreach (var action in _keymap.Resolve(gesture))
        {
            if (Run(action, gesture))
            {
                e.Handled = true;
                return;
            }
        }
    }

    /// <summary>Runs a shelf-context action. Returns false when the action does not apply to the shelves.</summary>
    private bool Run(string action, string gesture)
    {
        switch (action)
        {
            case "previous": _nav.Previous(); break;
            case "next": _nav.Next(); break;
            case "up": _nav.Up(); break;
            case "down": _nav.Down(); break;
            case "first": _nav.First(); break;
            case "last": _nav.Last(); break;
            case "previousPage": _nav.PreviousPage(); break;
            case "nextPage": _nav.NextPage(); break;
            case "extendPrevious": _nav.ExtendPrevious(); break;
            case "extendNext": _nav.ExtendNext(); break;
            case "extendUp": _nav.ExtendUp(); break;
            case "extendDown": _nav.ExtendDown(); break;
            case "selectAll": _nav.SelectAll(); break;
            case "switchShelf": _nav.SwitchShelf(); break;
            case "toggleImages": ToggleKind(ItemKind.Image); return true;
            case "toggleTexts": ToggleKind(ItemKind.Text); return true;
            case "toggleLinks": ToggleKind(ItemKind.Link); return true;
            case "toggleShelves":
                _nav.ToggleAllShelves();
                PersistVisibleKinds();
                break;
            case "pinnedOnly": _nav.TogglePinnedOnly(); break;
            case "search":
                _nav.OpenSearch();
                Render();
                FocusSearch();
                return true;
            case "close":
                if (_nav.SearchActive)
                {
                    _nav.CancelSearch();
                    SearchBox.Text = "";
                    break;
                }
                HideShelves(restoreFocus: true);
                return true;
            case "copy":
                CopySelection(closeAfter: gesture == "Enter");
                return true;
            case "paste":
                _ = PasteSelectionAsync();
                return true;
            case "pin":
                var targets = _nav.SelectedItems();
                if (targets.Count == 0) return true;
                var pinning = targets.Any(i => !i.Pinned);
                _store.TogglePin(targets.Select(i => i.Id), pinning);
                ShowToast(Loc.T(pinning ? "Pinned" : "Unpinned"));
                return true;
            case "remove":
                var removed = _nav.SelectedItems();
                if (removed.Count == 0) return true;
                _store.TakeDown(removed.Select(i => i.Id));
                ShowToast(Loc.T("Taken down"));
                return true;
            case "undo":
                var restored = _store.UndoTakeDown();
                ShowToast(Loc.T(restored.Count > 0 ? "Restored" : "Nothing to undo"));
                if (restored.Count > 0) _nav.Select(restored[0]);
                Render();
                return true;
            case "trash":
                ConfirmTrash(_nav.SelectedItems());
                return true;
            case var numbered when numbered.Length == 7 && numbered.StartsWith("select", StringComparison.Ordinal) && char.IsAsciiDigit(numbered[6]):
                _nav.SelectNumber(numbered[6] - '0');
                break;
            case "preview":
                OpenPreview();
                return true;
            case "actions":
                ShowActions(ActionOrigin.Line);
                return true;
            case "open":
                if (_nav.FocusedItem is { Kind: ItemKind.Link } link) OpenInBrowser(link);
                return true;
            case "edit":
                if (_nav.FocusedItem is not { Kind: ItemKind.Text }) return false;
                OpenPreview();
                _preview?.BeginEditing();
                return true;
            case "library":
                OpenLibrary(null, null, search: false);
                return true;
            case "folder" or "name" or "tag" or "export":
                Organize(action, ActionOrigin.Line);
                return true;
            case "markup" or "crop":
                OpenMarkup(action == "crop");
                return true;
            case "copyOCR":
                CopyRecognizedText();
                return true;
            case "refreshLink":
                RefreshLinks();
                return true;
            default:
                return false;
        }
        Render();
        return true;
    }

    private void ToggleKind(ItemKind kind)
    {
        _nav.ToggleKind(kind);
        if (IsShown)
        {
            PersistVisibleKinds();
            Render();
        }
    }

    private void PersistVisibleKinds()
    {
        var kinds = _nav.VisibleKinds.ToList();
        _settings.Update(s => s.VisibleKinds = kinds);
    }

    private void ActivateRow(ItemKind kind)
    {
        var row = _nav.Row(kind);
        if (row.Count == 0) return;
        _nav.Select(row[Math.Min(_nav.Offset(kind), row.Count - 1)].Id);
        Render();
    }

    private void PageRow(ItemKind kind, int direction)
    {
        if (_nav.ActiveKind != kind || _nav.Focused is null) ActivateRow(kind);
        if (direction < 0) _nav.PreviousPage();
        else _nav.NextPage();
        Render();
    }

    private void OnCardClicked(ClipItem item, MouseButtonEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        if (modifiers.HasFlag(ModifierKeys.Control)) _nav.ToggleSelection(item.Id);
        else if (modifiers.HasFlag(ModifierKeys.Shift)) _nav.SelectRange(item.Id);
        else
        {
            _nav.Select(item.Id);
            if (!_selectMode) CopySelection(closeAfter: false);
        }
        Render();
    }

    private void CopySelection(bool closeAfter)
    {
        var items = _nav.SelectedItems();
        if (items.Count == 0) return;
        if (!_writer.Copy(items))
        {
            ShowToast(Loc.T("Clipboard could not be written."));
            return;
        }
        if (closeAfter) HideShelves(restoreFocus: true);
        else ShowToast(items.Count > 1 ? Loc.F("Copied {0} items", items.Count) : Loc.T("Copied"));
    }

    private async Task PasteSelectionAsync()
    {
        var items = _nav.SelectedItems();
        if (items.Count == 0 || !_writer.Copy(items)) return;
        HideShelves(restoreFocus: false);
        var verdict = await _foreground.PasteIntoPreviousAsync();
        if (verdict != Pegline.Core.Transfer.PasteVerdict.Allowed)
            System.Windows.MessageBox.Show(Loc.T(Pegline.Core.Transfer.PastePolicy.Message(verdict)), "Pegline");
    }

    private void ConfirmTrash(IReadOnlyList<ClipItem> items)
    {
        if (items.Count == 0) return;
        _holdOpen = true;
        try
        {
            var answer = MessageBox.Show(_preview is { IsOpen: true } owner ? owner : this, Loc.T("Move to Trash?") + $"  ({items.Count})", "Pegline", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
            if (answer != MessageBoxResult.OK) return;
            _store.MoveToTrash(items.Select(i => i.Id));
            ShowToast(Loc.T("Moved to Trash"));
        }
        finally
        {
            _holdOpen = false;
            if (_preview is { IsOpen: true } preview) preview.Activate();
            else Activate();
        }
    }
}
