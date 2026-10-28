#pragma warning disable WPF0001 // Fluent ThemeMode is experimental in WPF
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Pegline.App.Localization;
using Pegline.App.Platform;
using Pegline.App.Shelves;
using Pegline.Core.Actions;
using Pegline.Core.Input;
using Pegline.Core.Models;
using Pegline.Core.Storage;

namespace Pegline.App.Settings;

/// <summary>
/// General, History, Sources, Keyboard shortcuts, Updates and About. Every control writes through
/// <see cref="SettingsStore.Update"/>, the same path as the tray menu; opening the window never writes anything.
/// </summary>
internal sealed class SettingsWindow : Window
{

    private static readonly (string Id, string Title, string Glyph)[] Sections =
    [
        ("general", "General", ""),
        ("history", "History", ""),
        ("sources", "Sources", ""),
        ("shortcuts", "Keyboard shortcuts", ""),
        ("updates", "Updates", ""),
        ("about", "About", ""),
    ];

    private readonly SettingsStore _settings;
    private readonly LibraryStore _store;
    private readonly HotKeyService _hotKeys;
    private readonly Updates.UpdateService _updates;
    private readonly ShelfPalette _palette;
    private readonly ShelfTheme _launchTheme;
    private readonly Action _openLibrary;
    private readonly Action _reopen;
    private readonly StackPanel _nav = new();
    private readonly StackPanel _content = new() { Margin = new Thickness(28, 4, 28, 28), MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ScrollViewer _scroll;
    private readonly TextBlock _heading = new() { FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(28, 22, 28, 6) };
    private readonly HashSet<string> _expanded = ["Browsing"];
    private string _section = "general";
    private Recording? _recording;
    private string? _warning;
    private string? _notice;
    private ItemKind? _clearKind;
    private bool _rebuildQueued;
    private bool _quitting;

    private sealed record Recording(bool Global, string Action, IDisposable? Lease);

    public SettingsWindow(SettingsStore settings, LibraryStore store, HotKeyService hotKeys, Updates.UpdateService updates, ShelfTheme launchTheme, Action openLibrary, Action reopen)
    {
        _settings = settings;
        _store = store;
        _hotKeys = hotKeys;
        _updates = updates;
        _launchTheme = launchTheme;
        _palette = ShelfPalette.For(launchTheme);
        _openLibrary = openLibrary;
        _reopen = reopen;

        ThemeMode = ThemeMode.Dark;
        Title = Loc.T("Settings") + " — Pegline";
        Width = 860;
        Height = 680;
        MinWidth = 700;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = _palette.Surface;
        Foreground = _palette.Foreground;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        UseLayoutRounding = true;
        _heading.Foreground = _palette.Foreground;
        _scroll = new ScrollViewer { Content = _content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Content = Build();

        _settings.Changed += (_, _) => QueueRebuild();
        _updates.Changed += (_, _) =>
        {
            if (_section is "updates" or "about") QueueRebuild();
        };
        _store.Changed += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (_section == "history") QueueRebuild();
        });
        PreviewKeyDown += OnKey;
        Deactivated += (_, _) => StopRecording(rebuild: true);
        Closing += (_, e) =>
        {
            StopRecording(rebuild: false);
            if (_quitting) return;
            e.Cancel = true;
            Hide();
        };
        SourceInitialized += (_, _) =>
        {
            var dark = 1;
            DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
        };
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public void Present(string section)
    {
        if (Sections.Any(s => s.Id == section)) _section = section;
        _notice = null;
        Rebuild(keepScroll: false);
        Show();
        if (_section == "updates") RefreshUpdates();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Win32.ForceForeground(new WindowInteropHelper(this).Handle);
    }

#if DEBUG
    internal void ShowForSnapshot(string section)
    {
        _section = section;
        Width = 900;
        Height = 1500;
        Show();
        Rebuild(keepScroll: false);
    }
#endif

    public void CloseForQuit()
    {
        _quitting = true;
        Close();
    }

    // ----- Frame -----

    private Grid Build()
    {
        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(212) });
        root.ColumnDefinitions.Add(new ColumnDefinition());

        var side = new StackPanel { Margin = new Thickness(12, 20, 12, 12) };
        var brand = new StackPanel { Margin = new Thickness(10, 0, 0, 18) };
        brand.Children.Add(Mark(36));
        brand.Children.Add(new TextBlock { Text = "Pegline", FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = _palette.Foreground, Margin = new Thickness(0, 6, 0, 0) });
        brand.Children.Add(new TextBlock { Text = VersionText(), FontSize = 11.5, Foreground = _palette.Muted, TextWrapping = TextWrapping.Wrap });
        side.Children.Add(brand);
        side.Children.Add(_nav);
        var sideHost = new Border { Background = _palette.Panel, BorderBrush = _palette.CardBorder, BorderThickness = new Thickness(0, 0, 1, 0), Child = side };
        root.Children.Add(sideHost);

        var main = new DockPanel();
        DockPanel.SetDock(_heading, Dock.Top);
        main.Children.Add(_heading);
        main.Children.Add(_scroll);
        Grid.SetColumn(main, 1);
        root.Children.Add(main);
        return root;
    }

    private void RenderNav()
    {
        _nav.Children.Clear();
        foreach (var (id, title, glyph) in Sections)
        {
            var selected = id == _section;
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock { Text = glyph, FontFamily = ShelfPalette.Icons, FontSize = 15, Width = 26, VerticalAlignment = VerticalAlignment.Center, Foreground = _palette.Foreground });
            row.Children.Add(new TextBlock { Text = Loc.T(title), FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center, Foreground = _palette.Foreground });
            var idle = selected ? _palette.ChipActive : Brushes.Transparent;
            var button = new Border
            {
                Background = idle,
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 3),
                Cursor = Cursors.Hand,
                Child = row,
                Focusable = true,
                FocusVisualStyle = null,
                Tag = "nav:" + id,
            };
            AutomationName(button, Loc.T(title));
            button.MouseEnter += (_, _) => { if (!selected) button.Background = _palette.Chip; };
            button.MouseLeave += (_, _) => button.Background = idle;
            button.MouseLeftButtonUp += (_, _) => Choose(id);
            button.KeyDown += (_, e) =>
            {
                if (e.Key is not (Key.Enter or Key.Space)) return;
                e.Handled = true;
                Choose(id);
            };
            _nav.Children.Add(button);
        }
    }

    private void Choose(string section)
    {
        if (section == _section) return;
        StopRecording(rebuild: false);
        _section = section;
        _notice = null;
        Rebuild(keepScroll: false);
        if (section == "updates") RefreshUpdates();
    }

    private void RefreshUpdates()
    {
        if (_updates.IsInstalled && !_updates.Busy && (_updates.LastChecked is not { } last || DateTimeOffset.Now - last > TimeSpan.FromMinutes(1)))
            _ = _updates.CheckAsync(manual: true);
    }

    private void QueueRebuild()
    {
        if (!IsVisible || _rebuildQueued) return;
        _rebuildQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _rebuildQueued = false;
            Rebuild(keepScroll: true);
        }, DispatcherPriority.Background);
    }

    /// <summary>Redraws the section from the stored settings, keeping the scroll position and the focused control.</summary>
    private void Rebuild(bool keepScroll)
    {
        var focusKey = (Keyboard.FocusedElement as FrameworkElement)?.Tag as string;
        var offset = _scroll.VerticalOffset;
        RenderNav();
        _heading.Text = Loc.T(Sections.First(s => s.Id == _section).Title);
        _content.Children.Clear();
        switch (_section)
        {
            case "general": General(); break;
            case "history": History(); break;
            case "sources": Sources(); break;
            case "shortcuts": Shortcuts(); break;
            case "updates": Updates(); break;
            default: About(); break;
        }
        Dispatcher.BeginInvoke(() =>
        {
            _scroll.ScrollToVerticalOffset(keepScroll ? offset : 0);
            if (focusKey is not null && Find(this, focusKey) is { } control) control.Focus();
        }, DispatcherPriority.Loaded);
    }

    private static FrameworkElement? Find(DependencyObject parent, string tag)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (child is FrameworkElement { Tag: string key } element && key == tag) return element;
            if (Find(child, tag) is { } found) return found;
        }
        return null;
    }

    // ----- General -----

    private void General()
    {
        var current = _settings.Current;
        var languages = new List<(string, string)> { ("system", Loc.T("System default")) };
        languages.AddRange(AppLanguages.Available.Select(code => (code, AppLanguages.NativeName(code))));
        var languageChanged = AppLanguages.Resolve(current.Language, System.Globalization.CultureInfo.CurrentUICulture) != Loc.Language;
        var themeChanged = current.Theme != _launchTheme;

        var shelves = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var kind in new[] { ItemKind.Image, ItemKind.Text, ItemKind.Link })
        {
            var visible = current.VisibleKinds.Contains(kind);
            var box = Check(Loc.T(kind switch { ItemKind.Image => "Images", ItemKind.Text => "Texts", _ => "Links" }), visible,
                value => _settings.Update(s =>
                {
                    s.VisibleKinds.Remove(kind);
                    if (value) s.VisibleKinds.Add(kind);
                }),
                "shelf:" + kind, enabled: !(visible && current.VisibleKinds.Count == 1));
            shelves.Children.Add(WithMargin(box, new Thickness(16, 8, 0, 8)));
        }

        var appearance = new List<UIElement?>
        {
            Choice(Loc.T("Language"), languages, current.Language, value => _settings.Update(s => s.Language = value), "language"),
            Labeled(Loc.T("Shelves"), Loc.T("At least one shelf stays visible."), shelves),
            Choice(Loc.T("Colors"), [(ShelfTheme.Soft, Loc.T("Soft colors")), (ShelfTheme.Neutral, Loc.T("Neutral glass"))], current.Theme,
                value => _settings.Update(s => s.Theme = value), "theme"),
            Choice(Loc.T("Card size"), [(CardSize.Small, Loc.T("Small")), (CardSize.Medium, Loc.T("Medium")), (CardSize.Large, Loc.T("Large"))], current.CardSize,
                value => _settings.Update(s => s.CardSize = value), "size"),
        };
        if (languageChanged || themeChanged)
        {
            var note = languageChanged ? Loc.T("The new language appears after Pegline reopens. Open edits are saved first.")
                : Loc.T("The shelves use the new colors now; Library and previews follow when Pegline reopens.");
            appearance.Add(Stack(Caption(note, new Thickness(0, 8, 0, 6)), Buttons(("Reopen Pegline", _reopen, true, "reopen"))));
        }
        Card(Loc.T("Appearance"), null, [.. appearance]);

        Card(Loc.T("Library"), Loc.T("Favorites, folders and tags keep items permanently. Other copies follow your history setting."),
            Buttons(("Open Library", _openLibrary, false, "openLibrary")));

        Card(Loc.T("Bring the shelves down"), Loc.T("Full-screen apps are games, videos and presentations. Your shortcut always works, even there."),
            Check(Loc.T("When the pointer rests at the top edge of the screen"), current.PointerReveal, value => _settings.Update(s => s.PointerReveal = value), "pointerReveal"),
            Check(Loc.T("When something new hangs"), current.OpenAfterScreenshot, value => _settings.Update(s => s.OpenAfterScreenshot = value), "peek"),
            Check(Loc.T("Over full-screen apps"), current.ShowOverFullScreen, value => _settings.Update(s => s.ShowOverFullScreen = value), "fullScreen"));

        Card(null, LoginItem.IsAvailable ? null : Loc.T("Starting at sign-in is available in the installed app."),
            Check(Loc.T("Sounds"), current.Sounds, value =>
            {
                _settings.Update(s => s.Sounds = value);
                if (value) Sounds.Play(Sound.Hang);
            }, "sounds"),
            Check(Loc.T("Start Pegline when I sign in"), LoginItem.IsAvailable && LoginItem.IsEnabled, value =>
            {
                if (!LoginItem.Set(value)) _notice = Loc.T("Windows did not allow the change.");
                _settings.Update(s => s.LaunchAtLogin = LoginItem.IsEnabled);
            }, "login", enabled: LoginItem.IsAvailable));
        if (_notice is not null) _content.Children.Add(Caption(_notice, new Thickness(4, 8, 4, 0)));
    }

    // ----- History -----

    private void History()
    {
        var current = _settings.Current;
        var choices = AppSettings.RetentionChoices.Select(days => (days, days switch
        {
            0 => Loc.T("Keep forever"),
            1 => Loc.T("Keep for 1 day"),
            _ => Loc.F("Keep for {0} days", days),
        })).ToList();
        Card(Loc.T("Unsaved copies"), Loc.T("Unsaved copies expire to recoverable Trash. Saved items and original files are kept."),
            [.. new[] { ItemKind.Image, ItemKind.Text, ItemKind.Link }.Select(kind => Choice(Loc.T(kind switch { ItemKind.Image => "Images", ItemKind.Text => "Texts", _ => "Links" }),
                choices, current.RetentionDays.GetValueOrDefault(kind, 30), days => _settings.Update(s => s.RetentionDays[kind] = days), "retention:" + kind))]);

        var count = _store.SnapshotUnsavedHistory(_clearKind).Entries.Count;
        var clear = Buttons(("Clear unsaved copies…", ClearHistory, false, "clear"));
        clear.IsEnabled = count > 0;
        Card(Loc.T("Clear history"), null,
            Choice(Loc.T("Copies to clear"), [((ItemKind?)null, Loc.T("All types")), (ItemKind.Image, Loc.T("Images")), (ItemKind.Text, Loc.T("Texts")), (ItemKind.Link, Loc.T("Links"))],
                _clearKind, kind =>
                {
                    _clearKind = kind;
                    _notice = null;
                    Rebuild(keepScroll: true);
                }, "clearKind"),
            Stack(Caption(count == 1 ? Loc.T("1 unsaved copy") : Loc.F("{0} unsaved copies", count), new Thickness(0, 6, 0, 6)),
                clear));
        if (_notice is not null) _content.Children.Add(Caption(_notice, new Thickness(4, 8, 4, 0)));
    }

    private void ClearHistory()
    {
        var snapshot = _store.SnapshotUnsavedHistory(_clearKind);
        if (snapshot.Entries.Count == 0) return;
        var answer = MessageBox.Show(this, Loc.F("Move {0} unsaved copies to Trash? Pinned, foldered and tagged items stay.", snapshot.Entries.Count),
            "Pegline", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK) return;
        var cleared = _store.ClearUnsavedHistory(snapshot);
        _notice = Loc.F("Moved {0} items to Trash", cleared.Count);
        Rebuild(keepScroll: true);
    }

    // ----- Sources -----

    private void Sources()
    {
        var current = _settings.Current;
        Card(Loc.T("Clipboard"), Loc.T("Copies are saved on this PC only. A single web address goes to Links; paragraphs stay as text. Copies that password managers mark as private are skipped."),
            Check(Loc.T("Capture copied images"), current.CaptureImages, value => _settings.Update(s => s.CaptureImages = value), "images"),
            Check(Loc.T("Capture copied text"), current.CaptureText, value => _settings.Update(s => s.CaptureText = value), "texts"),
            Check(Loc.T("Capture copied links"), current.CaptureLinks, value => _settings.Update(s => s.CaptureLinks = value), "links"));
        Card(Loc.T("Link previews"), Loc.T("Contacts the linked website or YouTube in the background. Local addresses and links carrying sign-in or secret parameters are skipped."),
            Check(Loc.T("Fetch link titles and thumbnails"), current.FetchLinkMetadata, value => _settings.Update(s => s.FetchLinkMetadata = value), "metadata"));
        Card(Loc.T("Screenshots and recordings"), Loc.T("Print Screen, Win+Shift+S and Snipping Tool recordings hang as soon as Windows saves them. The files stay where Windows put them."),
            Check(Loc.T("Collect the Windows Screenshots and Screen Recordings folders"), current.CaptureScreenshots, value => _settings.Update(s => s.CaptureScreenshots = value), "screenshots"));
        Card(Loc.T("Search"), Loc.T("Runs on this PC at low priority and waits while a full-screen app or game is in front."),
            Check(Loc.T("Read text in images and index them for visual search"), current.IndexImages, value => _settings.Update(s => s.IndexImages = value), "index"));

        var rows = new List<UIElement?>();
        foreach (var folder in current.WatchFolders)
        {
            var label = new StackPanel();
            label.Children.Add(new TextBlock { Text = Path.GetFileName(folder.TrimEnd('\\')) is { Length: > 0 } name ? name : folder, FontSize = 13.5, Foreground = _palette.Foreground });
            label.Children.Add(new TextBlock { Text = folder, FontSize = 11.5, Foreground = _palette.Muted, TextWrapping = TextWrapping.Wrap });
            var path = folder;
            rows.Add(Row(label, Buttons(
                ("Open folder", () => _notice = ShellActions.Open(path) is null ? null : Loc.T("Folder not found"), false, "openWatched:" + path),
                ("Stop watching", () => _settings.Update(s => s.WatchFolders.Remove(path)), false, "unwatch:" + path))));
        }
        var add = Buttons(("Add folder…", AddFolder, false, "addFolder"));
        rows.Add(current.WatchFolders.Count >= AppSettings.MaxWatchFolders ? Disabled(add) : add);
        Card(Loc.T("Also watch folders"), Loc.T("Watch up to three folders for new images and videos. Removing a folder leaves its files untouched."), [.. rows]);
        if (_notice is not null) _content.Children.Add(Caption(_notice, new Thickness(4, 8, 4, 0)));
    }

    private void AddFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = Loc.T("Also watch folders") };
        if (dialog.ShowDialog(this) != true) return;
        var chosen = dialog.FolderName;
        if (_settings.Current.WatchFolders.Contains(chosen, StringComparer.OrdinalIgnoreCase)) return;
        try
        {
            _settings.Update(s => s.WatchFolders.Add(chosen));
        }
        catch (ArgumentException)
        {
            _notice = Loc.T("Up to three folders can be watched.");
            Rebuild(keepScroll: true);
        }
    }

    // ----- Keyboard shortcuts -----

    private void Shortcuts()
    {
        var current = _settings.Current;
        if (_warning is not null)
            _content.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x33, 0xF5, 0x9E, 0x0B)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x99, 0xF5, 0x9E, 0x0B)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 10, 14, 10),
                Margin = new Thickness(0, 12, 0, 0),
                Child = new TextBlock { Text = "⚠  " + _warning, TextWrapping = TextWrapping.Wrap, Foreground = _palette.Foreground },
            });

        var defaults = new AppSettings().GlobalShortcuts;
        var rows = new List<UIElement?>();
        foreach (var action in ShortcutCatalog.GlobalActions)
        {
            var gesture = current.GlobalShortcuts.GetValueOrDefault(action, "");
            var recording = _recording is { Global: true } r && r.Action == action;
            var buttons = new List<(string, Action, bool, string)>
            {
                (recording ? "Press keys…" : "Record shortcut…", () => StartRecording(global: true, action), recording, "record:" + action),
            };
            if (action == "library" && Keymap.Canonical(gesture) != ShortcutCatalog.LibraryPreset)
                buttons.Add((Loc.F("Use {0}", ShortcutCatalog.LibraryPreset), () => SetGlobal(action, ShortcutCatalog.LibraryPreset), false, "preset:" + action));
            if (gesture.Length > 0) buttons.Add(("Off", () => SetGlobal(action, ""), false, "off:" + action));
            if (Keymap.Canonical(gesture) != Keymap.Canonical(defaults[action]) && defaults[action].Length > 0)
                buttons.Add(("Reset to default", () => SetGlobal(action, defaults[action]), false, "reset:" + action));
            var label = Label(Loc.T(ShortcutCatalog.GlobalTitle(action)),
                recording ? Loc.T("Press the new shortcut. Esc cancels.") : Loc.T(ShortcutCatalog.GlobalDescription(action)));
            var line = new DockPanel { LastChildFill = true };
            var cap = Keycap(gesture);
            DockPanel.SetDock(cap, Dock.Right);
            line.Children.Add(cap);
            line.Children.Add(label);
            rows.Add(WithMargin(Stack(line, WithMargin(Buttons([.. buttons]), new Thickness(0, 6, 0, 2))), new Thickness(0, 8, 0, 2)));
        }
        Card(Loc.T("System-wide shortcuts"), Loc.T("These work from any app. Use Ctrl, Alt or Win together with a key."), [.. rows]);

        Card(Loc.T("Paste into previous app"), null,
            Caption(Loc.F("{0} pastes the selected copy into the app you were using. Windows needs no extra permission, and Pegline never pastes into a password field.",
                KeyGestures.Display(new Keymap(current.LocalShortcuts).Primary("paste")) is { Length: > 0 } paste ? paste : Loc.T("Off")), new Thickness(0, 8, 0, 8)));

        _content.Children.Add(new TextBlock { Text = Loc.T("Browsing shortcuts"), FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = _palette.Foreground, Margin = new Thickness(2, 22, 0, 4) });
        _content.Children.Add(Caption(Loc.T("Single keys work while browsing the shelves, Library or a preview. Typing and standard editing shortcuts stay with the text field."), new Thickness(2, 0, 2, 8)));
        var keymap = new Keymap(current.LocalShortcuts);
        foreach (var (title, actions) in ShortcutCatalog.Groups)
        {
            var list = new StackPanel();
            var first = true;
            foreach (var action in actions)
            {
                if (!first) list.Children.Add(Divider());
                first = false;
                list.Children.Add(LocalRow(keymap, action));
            }
            var expander = new Expander
            {
                Header = new TextBlock { Text = Loc.T(title), FontSize = 13.5, Foreground = _palette.Foreground },
                IsExpanded = _expanded.Contains(title),
                Content = new Border { Padding = new Thickness(16, 2, 16, 6), Child = list },
                Margin = new Thickness(0, 0, 0, 8),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Tag = "group:" + title,
            };
            var group = title;
            expander.Expanded += (_, _) => _expanded.Add(group);
            expander.Collapsed += (_, _) => _expanded.Remove(group);
            _content.Children.Add(expander);
        }
        _content.Children.Add(Buttons(("Reset all browsing shortcuts", () =>
        {
            _warning = null;
            _settings.Update(s => s.LocalShortcuts.Clear());
        }, false, "resetAll")));
    }

    private Grid LocalRow(Keymap keymap, string action)
    {
        var recording = _recording is { Global: false } r && r.Action == action;
        var gesture = keymap.Primary(action);
        var places = string.Join(" · ", ShortcutCatalog.ScopeTitles(action).Select(Loc.T));
        var label = Label(Loc.T(ActionCatalog.KeyboardTitle(action)), recording ? Loc.T("Press the new shortcut. Esc cancels.") : places);

        var record = new Button
        {
            Content = recording ? Loc.T("Press keys…") : gesture.Length > 0 ? KeyGestures.Display(gesture) : Loc.T("Off"),
            MinWidth = 120,
            Padding = new Thickness(12, 4, 12, 4),
            Tag = "local:" + action,
            ToolTip = Loc.T("Record shortcut…"),
        };
        if (recording && TryFindResource("AccentButtonStyle") is Style accent) record.Style = accent;
        AutomationName(record, Loc.T(ActionCatalog.KeyboardTitle(action)));
        record.Click += (_, _) => StartRecording(global: false, action);

        var more = new Button { Content = new TextBlock { Text = "", FontFamily = ShelfPalette.Icons, FontSize = 13 }, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(6, 0, 0, 0), Tag = "more:" + action };
        AutomationName(more, Loc.T(ActionCatalog.KeyboardTitle(action)) + " · " + Loc.T("Settings"));
        var menu = new ContextMenu();
        var off = new MenuItem { Header = Loc.T("Off"), IsEnabled = gesture.Length > 0 };
        off.Click += (_, _) => SetLocal(action, "");
        var reset = new MenuItem { Header = Loc.T("Reset to default"), IsEnabled = keymap.Overrides.ContainsKey(action) };
        reset.Click += (_, _) => SetLocal(action, null);
        menu.Items.Add(off);
        menu.Items.Add(reset);
        more.ContextMenu = menu;
        more.Click += (_, _) =>
        {
            menu.PlacementTarget = more;
            menu.IsOpen = true;
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(record);
        buttons.Children.Add(more);
        return Row(label, buttons);
    }

    private void StartRecording(bool global, string action)
    {
        var again = _recording is { } r && r.Global == global && r.Action == action;
        StopRecording(rebuild: false);
        if (again)
        {
            Rebuild(keepScroll: true);
            return;
        }
        // Global shortcuts are released while recording, so pressing the current one records it instead of firing it.
        _recording = new Recording(global, action, global ? _hotKeys.Suspend() : null);
        _warning = null;
        Rebuild(keepScroll: true);
    }

    private void StopRecording(bool rebuild)
    {
        if (_recording is not { } recording) return;
        _recording = null;
        recording.Lease?.Dispose();
        if (rebuild && IsVisible) Rebuild(keepScroll: true);
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        var key = e.Key switch { Key.System => e.SystemKey, Key.ImeProcessed => e.ImeProcessedKey, _ => e.Key };
        if (_recording is not { } recording)
        {
            if (key == Key.Escape || (key == Key.W && Keyboard.Modifiers == ModifierKeys.Control))
            {
                if (Keyboard.FocusedElement is ComboBox { IsDropDownOpen: true }) return;
                e.Handled = true;
                Hide();
            }
            return;
        }
        e.Handled = true;
        if (KeyGestures.IsModifier(key)) return;
        var gesture = KeyGestures.FromEvent(e);
        if (gesture.Length == 0) return;
        StopRecording(rebuild: false);
        if (gesture == "Escape")
        {
            Rebuild(keepScroll: true);
            return;
        }
        if (recording.Global) SetGlobal(recording.Action, gesture);
        else SetLocal(recording.Action, gesture);
        Rebuild(keepScroll: true);
    }

    private void SetGlobal(string action, string gesture)
    {
        gesture = Keymap.Canonical(gesture);
        if (gesture.Length > 0)
        {
            if (!ShortcutCatalog.IsValidGlobal(gesture) || !KeyGestures.TryParseHotKey(gesture, out _, out _))
            {
                _warning = Loc.F("{0} cannot be a system-wide shortcut. Use Ctrl, Alt or Win together with a key.", KeyGestures.Display(gesture));
                QueueRebuild();
                return;
            }
            if (ShortcutCatalog.GlobalConflict(_settings.Current.GlobalShortcuts, action, gesture) is { } other)
            {
                _warning = Loc.F("{0} is already used for “{1}”.", KeyGestures.Display(gesture), Loc.T(ShortcutCatalog.GlobalTitle(other)));
                QueueRebuild();
                return;
            }
        }
        _warning = null;
        _settings.Update(s => s.GlobalShortcuts[action] = gesture);
        if (_hotKeys.Unavailable.Contains(action))
            _warning = Loc.F("Windows or another app already uses {0}. Choose another shortcut.", KeyGestures.Display(gesture));
    }

    /// <summary>Sets a browsing shortcut; <c>null</c> restores the default, an empty string turns it off.</summary>
    private void SetLocal(string action, string? gesture)
    {
        var keymap = new Keymap(_settings.Current.LocalShortcuts);
        if (gesture is { Length: > 0 })
        {
            gesture = Keymap.Canonical(gesture);
            var conflicts = ShortcutCatalog.Conflicts(keymap, action, gesture);
            if (conflicts.Count > 0)
            {
                _warning = Loc.F("{0} is already used for “{1}” in the same place. Change that shortcut first.",
                    KeyGestures.Display(gesture), string.Join("”, “", conflicts.Select(c => Loc.T(ActionCatalog.KeyboardTitle(c)))));
                QueueRebuild();
                return;
            }
        }
        _warning = null;
        _settings.Update(s =>
        {
            if (gesture is null || gesture == Keymap.Defaults[action]) s.LocalShortcuts.Remove(action);
            else s.LocalShortcuts[action] = gesture;
        });
    }

    // ----- Updates and About -----

    private void Updates()
    {
        var current = _settings.Current;
        var identity = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 10) };
        identity.Children.Add(Mark(44));
        var name = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        name.Children.Add(new TextBlock { Text = "Pegline", FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = _palette.Foreground });
        name.Children.Add(new TextBlock { Text = VersionText(), FontSize = 12, Foreground = _palette.Muted });
        if (_updates.AvailableVersion is { } available)
            name.Children.Add(new TextBlock { Text = Loc.F("Pegline {0} is available.", available), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = _palette.Accent, Margin = new Thickness(0, 3, 0, 0) });
        identity.Children.Add(name);
        Card(null, null, identity);

        var rows = new List<UIElement?>
        {
            Check(Loc.T("Automatically check for updates"), current.AutoCheckUpdates, value => _settings.Update(s => s.AutoCheckUpdates = value), "autoUpdates", enabled: _updates.IsInstalled),
            Caption(Loc.T("Checks at launch and every hour. You choose when to download and install."), new Thickness(0, 6, 0, 6)),
        };
        if (!_updates.IsInstalled)
        {
            rows.Add(Caption(_updates.Status, new Thickness(0, 6, 0, 8)));
            rows.Add(Buttons(("Open releases page", OpenReleases, true, "releases")));
            Card(Loc.T("App updates"), Loc.T("Your history and settings stay on this PC."), [.. rows]);
            if (_notice is not null) _content.Children.Add(Caption(_notice, new Thickness(4, 8, 4, 0)));
            return;
        }

        var state = new StackPanel { Margin = new Thickness(0, 8, 0, 6) };
        state.Children.Add(new TextBlock { Text = _updates.Status, FontSize = 13.5, Foreground = _palette.Foreground, TextWrapping = TextWrapping.Wrap });
        if (_updates.LastChecked is { } checkedAt)
            state.Children.Add(Caption(Loc.F("Last checked {0}", checkedAt.LocalDateTime.ToString("g", System.Globalization.CultureInfo.CurrentCulture)), new Thickness(0, 3, 0, 0)));
        if (_updates.Error is { } error)
            state.Children.Add(new TextBlock { Text = error, FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        if (_updates.Busy)
            state.Children.Add(new ProgressBar { Height = 4, Maximum = 100, Value = _updates.Progress ?? 0, IsIndeterminate = _updates.Progress is null, Margin = new Thickness(0, 8, 0, 0) });
        rows.Add(state);

        var actions = _updates.AvailableVersion is null
            ? Buttons(("Check for updates…", () => _ = _updates.CheckAsync(manual: true), true, "check"))
            : Buttons(("Install and reopen", () => _ = _updates.InstallAsync(), true, "install"),
                ("Release notes", () => _notice = ShellActions.OpenUrl(_updates.ReleaseNotesUrl!), false, "notes"));
        actions.IsEnabled = !_updates.Busy;
        rows.Add(actions);
        Card(Loc.T("App updates"), Loc.T("Updates are downloaded only when you choose to install. Your history and settings stay on this PC."), [.. rows]);

        var token = new PasswordBox { MinWidth = 280, Tag = "token", VerticalAlignment = VerticalAlignment.Center };
        AutomationName(token, Loc.T("GitHub access token"));
        var tokenButtons = Buttons(
            ("Save token", () =>
            {
                _updates.SaveToken(token.Password);
                token.Clear();
                _ = _updates.CheckAsync(manual: true);
            }, false, "saveToken"),
            ("Disconnect GitHub", _updates.ForgetToken, false, "forgetToken"));
        tokenButtons.IsEnabled = !_updates.Busy;
        Card(Loc.T("GitHub access"), Loc.T("Needed only while the releases are private: create a fine-grained token for this repository with Contents: Read-only. It is encrypted for your Windows account and never leaves this PC except to GitHub."),
            Labeled(Loc.T("GitHub access token"), _updates.HasToken ? Loc.T("Token saved") : null, token),
            tokenButtons);
        if (_notice is not null) _content.Children.Add(Caption(_notice, new Thickness(4, 8, 4, 0)));
    }

    private void About()
    {
        Card(null, null,
            Row(Label(Loc.T("Application"), null), Value("Pegline")),
            Row(Label(Loc.T("Version"), null), Value(VersionText())),
            Caption(Loc.T("Image, text and link shelves, with a private local library."), new Thickness(0, 8, 0, 8)));
        Card(null, null, Buttons(
            ("Check for updates…", () => Choose("updates"), false, "checkUpdates"),
            ("Open releases page", OpenReleases, false, "releases"),
            ("Open data folder", () => _notice = ShellActions.Open(App.DataDirectory), false, "dataFolder"),
            ("Third-party notices", OpenNotices, false, "notices")));
        if (_notice is not null) _content.Children.Add(Caption(_notice, new Thickness(4, 8, 4, 0)));
    }

    private void OpenReleases()
    {
        _notice = ShellActions.OpenUrl(Pegline.App.Updates.UpdateService.ReleasesUrl);
        if (_notice is not null) Rebuild(keepScroll: true);
    }

    private void OpenNotices()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "THIRD_PARTY_NOTICES.md");
        if (!File.Exists(path))
        {
            _notice = Loc.T("File not found.");
            Rebuild(keepScroll: true);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _notice = ex.Message;
            Rebuild(keepScroll: true);
        }
    }

    internal static string VersionText()
    {
        var info = typeof(SettingsWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        if (Pegline.App.Updates.UpdateService.IsInstalledBuild) info = info.Split('+')[0];
        var plus = info.IndexOf('+');
        var version = plus < 0 ? info : info[..plus];
        var build = plus < 0 ? "" : info[(plus + 1)..];
        if (build.Length > 7) build = build[..7];
        var text = build.Length > 0 ? $"{version} ({build})" : version;
#if DEBUG
        text += " · " + Loc.T("Development build");
#endif
        return text;
    }

    // ----- Parts -----

    private void Card(string? header, string? footer, params UIElement?[] rows)
    {
        if (header is not null)
            _content.Children.Add(new TextBlock { Text = header, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = _palette.Foreground, Margin = new Thickness(2, 20, 0, 8) });
        var stack = new StackPanel();
        var first = true;
        foreach (var row in rows.OfType<UIElement>())
        {
            if (!first) stack.Children.Add(Divider());
            first = false;
            stack.Children.Add(row);
        }
        _content.Children.Add(new Border
        {
            Background = _palette.Chip,
            BorderBrush = _palette.CardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 4, 16, 4),
            Margin = new Thickness(0, header is null ? 16 : 0, 0, 0),
            Child = stack,
        });
        if (footer is not null) _content.Children.Add(Caption(footer, new Thickness(4, 6, 4, 0)));
    }

    private Border Divider() => new() { Height = 1, Background = _palette.CardBorder };

    private TextBlock Caption(string text, Thickness margin) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = _palette.Muted,
        TextWrapping = TextWrapping.Wrap,
        Margin = margin,
    };

    private TextBlock Value(string text) => new() { Text = text, FontSize = 13.5, Foreground = _palette.Foreground, VerticalAlignment = VerticalAlignment.Center };

    private StackPanel Label(string title, string? detail)
    {
        var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        label.Children.Add(new TextBlock { Text = title, FontSize = 13.5, Foreground = _palette.Foreground, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(detail))
            label.Children.Add(new TextBlock { Text = detail, FontSize = 11.5, Foreground = _palette.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        return label;
    }

    private static Grid Row(UIElement left, UIElement? right)
    {
        var grid = new Grid { Margin = new Thickness(0, 8, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(left);
        if (right is not null)
        {
            Grid.SetColumn(right, 1);
            grid.Children.Add(right);
        }
        return grid;
    }

    private Grid Labeled(string title, string? detail, UIElement control) => Row(Label(title, detail), control);

    private static StackPanel Stack(params UIElement?[] children)
    {
        var stack = new StackPanel();
        foreach (var child in children.OfType<UIElement>()) stack.Children.Add(child);
        return stack;
    }

    private static T WithMargin<T>(T element, Thickness margin) where T : FrameworkElement
    {
        element.Margin = margin;
        return element;
    }

    private static T Disabled<T>(T element) where T : UIElement
    {
        element.IsEnabled = false;
        return element;
    }

    private CheckBox Check(string text, bool value, Action<bool> change, string key, bool enabled = true)
    {
        var box = new CheckBox
        {
            Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13.5 },
            IsChecked = value,
            IsEnabled = enabled,
            Margin = new Thickness(0, 8, 0, 8),
            Tag = "check:" + key,
        };
        box.Click += (_, _) => change(box.IsChecked == true);
        return box;
    }

    private Grid Choice<T>(string title, IReadOnlyList<(T Value, string Text)> options, T current, Action<T> change, string key)
    {
        var combo = new ComboBox { MinWidth = 220, Tag = "choice:" + key, VerticalAlignment = VerticalAlignment.Center };
        AutomationName(combo, title);
        foreach (var (value, text) in options) combo.Items.Add(new ComboBoxItem { Content = text, Tag = value });
        combo.SelectedIndex = Math.Max(0, options.ToList().FindIndex(o => EqualityComparer<T>.Default.Equals(o.Value, current)));
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem { Tag: var tag } && !EqualityComparer<T>.Default.Equals((T)tag!, current)) change((T)tag!);
        };
        return Row(Label(title, null), combo);
    }

    private WrapPanel Buttons(params (string Text, Action Click, bool Primary, string Key)[] buttons)
    {
        var panel = new WrapPanel { Margin = new Thickness(0, 6, 0, 6) };
        foreach (var (text, click, primary, key) in buttons)
        {
            var button = new Button { Content = Loc.T(text), Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(0, 2, 8, 2), Tag = "button:" + key };
            if (primary && TryFindResource("AccentButtonStyle") is Style accent) button.Style = accent;
            button.Click += (_, _) =>
            {
                click();
                QueueRebuild();
            };
            panel.Children.Add(button);
        }
        return panel;
    }

    private Border Keycap(string gesture) => new()
    {
        CornerRadius = new CornerRadius(5),
        BorderBrush = _palette.PanelBorder,
        BorderThickness = new Thickness(1),
        Background = _palette.Surface,
        Padding = new Thickness(9, 3, 9, 4),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = gesture.Length > 0 ? KeyGestures.Display(gesture) : Loc.T("Off"),
            FontSize = 12.5,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            Foreground = gesture.Length > 0 ? _palette.Foreground : _palette.Muted,
        },
    };

    private static readonly Lazy<ImageSource?> MarkImage = new(() =>
    {
        using var stream = typeof(SettingsWindow).Assembly.GetManifestResourceStream("Pegline.Brand.mark.png");
        if (stream is null) return null;
        var image = new System.Windows.Media.Imaging.BitmapImage();
        image.BeginInit();
        image.StreamSource = stream;
        image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        return image;
    });

    private static Image Mark(double size) => new() { Source = MarkImage.Value, Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Left };

    private static void AutomationName(DependencyObject element, string name) =>
        System.Windows.Automation.AutomationProperties.SetName(element, name);
}
