using System.IO;
using System.Windows;
using System.Windows.Threading;
using Pegline.App.Localization;
using Pegline.App.Platform;
using Pegline.App.Shelves;
using Pegline.Core.Models;
using Pegline.Core.Storage;

namespace Pegline.App;

public partial class App : Application
{
#if DEBUG
    private const string ProductFolder = "Pegline Dev";
#else
    private const string ProductFolder = "Pegline";
#endif
    private static readonly string InstanceName = @"Local\" + ProductFolder.Replace(' ', '-') + "-instance";

    private Mutex? _instance;
    private EventWaitHandle? _showSignal;
    private LibraryStore? _store;
    private SettingsStore? _settings;
    private MessageWindow? _messages;
    private ClipboardMonitor? _clipboard;
    private HotKeyService? _hotKeys;
    private TrayIcon? _tray;
    private ShelfWindow? _shelves;
    private DispatcherTimer? _expiry;

    public static string DataDirectory { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductFolder);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _instance = new Mutex(initiallyOwned: true, InstanceName, out var createdNew);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + "-show");
        if (!createdNew)
        {
            // Another Pegline is running: ask it to show its shelves.
            _showSignal.Set();
            Shutdown();
            return;
        }
        DispatcherUnhandledException += (_, args) =>
        {
            Log(args.Exception);
            args.Handled = true;
        };

        _settings = new SettingsStore(DataDirectory);
        Loc.Use(_settings.Current.Language);
        _store = new LibraryStore(DataDirectory) { RetentionDays = _settings.Current.RetentionDays };
        _store.Expire();

        var writer = new ClipboardWriter(_store);
        var foreground = new ForegroundTracker();
        _shelves = new ShelfWindow(_store, _settings, writer, foreground, Quit);

        _messages = new MessageWindow();
        _clipboard = new ClipboardMonitor(_messages);
        _clipboard.Captured += (_, content) => Capture(content);
        _hotKeys = new HotKeyService(_messages);
        _hotKeys.Pressed += (_, action) => OnGlobalShortcut(action);
        _tray = new TrayIcon(_settings, () => _shelves.ShowShelves(), Quit);
        ApplyGlobalShortcuts();

        _settings.Changed += (_, _) =>
        {
            Loc.Use(_settings.Current.Language);
            _store.RetentionDays = _settings.Current.RetentionDays;
            ApplyGlobalShortcuts();
        };
        _store.RecentFolderUsed += (_, folder) => _settings.Update(s =>
        {
            s.RecentPinFolderIds.Remove(folder);
            s.RecentPinFolderIds.Insert(0, folder);
            if (s.RecentPinFolderIds.Count > 8) s.RecentPinFolderIds.RemoveAt(8);
        });

        _expiry = new DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
        _expiry.Tick += (_, _) => _store.Expire();
        _expiry.Start();

        new Thread(() =>
        {
            while (_showSignal.WaitOne())
                Dispatcher.BeginInvoke(() => _shelves?.ShowShelves());
        }) { IsBackground = true, Name = "Pegline single instance" }.Start();

        if (!File.Exists(Path.Combine(DataDirectory, ".welcomed")))
        {
            File.WriteAllText(Path.Combine(DataDirectory, ".welcomed"), "");
            _tray.Notify(Loc.T("Pegline is running"), Loc.F("Press {0} to show your shelves.", _settings.Current.GlobalShortcuts["toggle"]));
        }
    }

    private void Capture(ClipboardContent content)
    {
        if (_store is null || _settings is null) return;
        var settings = _settings.Current;
        try
        {
            switch (content.Kind)
            {
                case ClipKind.Link when settings.CaptureLinks:
                    _store.InsertLink(content.Text);
                    break;
                case ClipKind.Link when settings.CaptureText:
                case ClipKind.Text when settings.CaptureText:
                    _store.InsertText(content.Text);
                    break;
                case ClipKind.Image when settings.CaptureImages && content.Png is not null:
                    _store.InsertImage(content.Png);
                    break;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or InvalidDataException or IOException)
        {
            Log(ex);
        }
    }

    private void OnGlobalShortcut(string action)
    {
        switch (action)
        {
            case "toggle":
                _shelves?.ToggleShelves();
                break;
            case "search":
                _shelves?.ShowShelves(search: true);
                break;
            case "copyLatest":
                var latest = _store?.Items.FirstOrDefault(i => i.Kind == ItemKind.Image && i.IsOnShelf);
                if (latest is not null && _store is not null) new ClipboardWriter(_store).Copy([latest]);
                break;
        }
    }

    private void ApplyGlobalShortcuts()
    {
        if (_hotKeys is null || _settings is null) return;
        var failed = _hotKeys.Apply(_settings.Current.GlobalShortcuts);
        if (failed.Count > 0)
            _tray?.Notify("Pegline", Loc.F("Shortcut unavailable: {0}", string.Join(", ", failed.Select(a => _settings.Current.GlobalShortcuts[a]))));
    }

    internal void Quit()
    {
        _shelves?.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _expiry?.Stop();
        _hotKeys?.Dispose();
        _clipboard?.Dispose();
        _messages?.Dispose();
        _tray?.Dispose();
        _store?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }

    internal static void Log(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.AppendAllText(Path.Combine(DataDirectory, "pegline.log"), $"{DateTimeOffset.Now:O} {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
    }
}
