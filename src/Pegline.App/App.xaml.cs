using System.IO;
using System.Windows;
using System.Windows.Threading;
using Pegline.App.Capture;
using Pegline.App.Library;
using Pegline.App.Localization;
using Pegline.App.Organize;
using Pegline.App.Platform;
using Pegline.App.Shelves;
using Pegline.Core.Library;
using Pegline.Core.Capture;
using Pegline.Core.Models;
using Pegline.Core.Storage;
using Pegline.Core.Transfer;

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
    private LibraryWindow? _library;
    private DispatcherTimer? _expiry;
    private OcrService? _ocr;
    private Search.VisualSearchService? _visual;
    private Links.LinkPreviewService? _links;
    private FolderWatcher? _folders;
    private Settings.SettingsWindow? _settingsWindow;
    private EdgeReveal? _edge;
    private ShelfTheme _launchTheme;
    private readonly ScreenshotPairing _pairing = new();
    private string _watchedKey = "";

    public static string DataDirectory { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductFolder);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _instance = new Mutex(initiallyOwned: true, InstanceName, out var createdNew);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + "-show");
        if (!createdNew && e.Args.Contains("--reopen"))
        {
            // Reopening (to apply a language): wait for the previous process to finish quitting.
            try
            {
                createdNew = _instance.WaitOne(TimeSpan.FromSeconds(15));
            }
            catch (AbandonedMutexException)
            {
                createdNew = true;
            }
        }
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
        // The language is chosen once per run; Settings offers to reopen when it changes.
        Loc.Use(_settings.Current.Language);
        _launchTheme = _settings.Current.Theme;
        _store = new LibraryStore(DataDirectory) { RetentionDays = _settings.Current.RetentionDays };
        _store.Expire();

        var foreground = new ForegroundTracker();
        var files = new ItemFiles(_store, Path.Combine(DataDirectory, "transfer"));
        var writer = new ClipboardWriter(_store, files);
        files.CleanDragCopies();
        var organizer = new Organizer(_store, _settings, files);
        _ocr = new OcrService(_store, Dispatcher);
        _visual = new Search.VisualSearchService(_store, Dispatcher, Path.Combine(AppContext.BaseDirectory, "CLIP"), Path.Combine(DataDirectory, "search"), () => _settings.Current.IndexImages);
        _links = new Links.LinkPreviewService(_store, Dispatcher, () => _settings.Current.FetchLinkMetadata);
        _shelves = new ShelfWindow(_store, _settings, writer, foreground, Quit, OpenLibrary, organizer, _ocr, _links);
        _library = new LibraryWindow(_store, _settings, writer, new ForegroundTracker(), () => _shelves.ShowShelves(), Quit, organizer, _ocr, _visual, _links);

        _messages = new MessageWindow();
        _clipboard = new ClipboardMonitor(_messages);
        _clipboard.Captured += (_, content) => Capture(content);
        _library.FilesDropped += (_, paths) => CollectCopiedFiles(paths, intoLibrary: true);
        _hotKeys = new HotKeyService(_messages);
        _hotKeys.Pressed += (_, action) => OnGlobalShortcut(action);
        _tray = new TrayIcon(_settings, () => _shelves.ShowShelves(), () => OpenLibrary(null, null, false), CaptureScreen, () => OpenSettings("general"), Quit);
        _shelves.OpenSettings = OpenSettings;
        _library.OpenSettings = OpenSettings;
        ApplyGlobalShortcuts();
        _folders = new FolderWatcher(Dispatcher, CollectFile, path => _ = CollectRecording(path));
        ConfigureFolders();
        _edge = new EdgeReveal(Dispatcher, PointerRevealAllowed, () => _shelves.ShowPassive(null, TimeSpan.Zero))
        {
            Enabled = _settings.Current.PointerReveal,
        };

        var retention = RetentionKey(_settings.Current);
        _settings.Changed += (_, _) =>
        {
            var current = _settings.Current;
            _store.RetentionDays = current.RetentionDays;
            if (RetentionKey(current) is var now && now != retention)
            {
                // A shorter history applies at once instead of at the next half-hourly sweep.
                retention = now;
                _store.Expire();
            }
            ApplyGlobalShortcuts();
            ConfigureFolders();
            _edge.Enabled = current.PointerReveal;
            if (current.FetchLinkMetadata) _links.RequestAll();
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

        // Recognize text in images that have not been read yet, once the app has settled.
        Dispatcher.BeginInvoke(() =>
        {
            if (_settings.Current.IndexImages) _ocr.Enqueue(_store.Items);
            _visual.Start();
            _links.RequestAll();
        }, DispatcherPriority.ApplicationIdle);

        new Thread(() =>
        {
            while (_showSignal.WaitOne())
                Dispatcher.BeginInvoke(() => _shelves?.ShowShelves());
        }) { IsBackground = true, Name = "Pegline single instance" }.Start();

#if DEBUG
        // Development aid: --snapshot <library|shelves> <png> renders a window off-screen without taking focus, then exits.
        if (e.Args is ["--links", var linkOutput, .. var urls])
        {
            // Development aid: looks up links into the dev library and reports what each page gave, then exits.
            var ids = urls.Select(u => _store.InsertLink(u).Id).ToList();
            _store.ResetLinkLookup(ids);
            _links.Request(ids);
            var begun = DateTime.UtcNow;
            var wait = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            wait.Tick += (_, _) =>
            {
                var items = ids.Select(_store.Find).OfType<ClipItem>().ToList();
                if (items.Any(i => i.LinkMetadataState == "") && DateTime.UtcNow - begun < TimeSpan.FromSeconds(40)) return;
                wait.Stop();
                File.WriteAllLines(linkOutput, items.Select(i =>
                    $"{i.Url}{Environment.NewLine}  state={i.LinkMetadataState} title={i.Title} | site={i.LinkSiteName} | author={i.LinkAuthor} | thumb={(i.LinkThumbnailFile.Length > 0 ? new FileInfo(_store.Media.PathOf(i.LinkThumbnailFile)).Length + " bytes" : "none")}"));
                Shutdown();
            };
            wait.Start();
            return;
        }
        if (e.Args is ["--recording", var videoInput, var recordingOutput])
        {
            // Development aid: imports a video into the dev library, trims 1s-3s and reports the result, then exits.
            _ = Dispatcher.InvokeAsync(async () =>
            {
                var lines = new List<string>();
                try
                {
                    var item = await Recordings.ImportAsync(_store, Dispatcher, videoInput);
                    if (item is null) lines.Add("import failed");
                    else
                    {
                        lines.Add($"imported {item.PixelWidth}x{item.PixelHeight} {item.DurationSeconds:0.00}s poster={item.ImageFile}");
                        var temp = Path.Combine(DataDirectory, "transfer", "check-trim.mp4");
                        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
                        await Recordings.RenderTrimAsync(_store.Media.PathOf(item.VideoFile), 1, 3, temp, null, CancellationToken.None);
                        var video = await _store.Media.ImportAsync(temp, ".mp4", WatchedFiles.MaxVideoBytes);
                        File.Delete(temp);
                        var probe = await Recordings.ProbeAsync(_store.Media.PathOf(video), TimeSpan.FromSeconds(0.2));
                        _store.SetRecordingTrim(item.Id, video, probe.PosterPng, 1, 3);
                        var trimmed = _store.Find(item.Id)!;
                        lines.Add($"trimmed file length {probe.DurationSeconds:0.00}s shown {trimmed.ShownDuration:0.00}s original kept={File.Exists(_store.Media.PathOf(trimmed.VideoFile))}");
                        var file = new ItemFiles(_store, Path.Combine(DataDirectory, "transfer")).Materialize(trimmed);
                        lines.Add($"copy name {file.Name}");
                    }
                }
                catch (Exception ex)
                {
                    lines.Add(ex.ToString());
                }
                File.WriteAllLines(recordingOutput, lines);
                Shutdown();
            });
            return;
        }
        if (e.Args is ["--clip-check", var clipOutput])
        {
            // Development aid: checks the bundled MobileCLIP export (ranking and speed), then exits.
            File.WriteAllText(clipOutput, Search.VisualSearchService.SelfCheck(_store, Path.Combine(AppContext.BaseDirectory, "CLIP")));
            Shutdown();
            return;
        }
        if (e.Args is ["--visual-search", var phrase, var visualOutput])
        {
            // Development aid: indexes the dev library, ranks every image against a description, then exits.
            _visual.PauseForFullscreen = false;
            foreach (var sample in Directory.Exists(Path.Combine(DataDirectory, "visual-samples")) ? Directory.GetFiles(Path.Combine(DataDirectory, "visual-samples"), "*.png") : [])
            {
                if (FolderWatcher.ReadAsPng(sample) is { } png) _store.Retain([_store.InsertImage(png, sample).Id]);
                File.Delete(sample); // imported once
            }
            _visual.Start();
            var started = DateTime.UtcNow;
            var poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            poll.Tick += async (_, _) =>
            {
                if (!_visual.Settled && DateTime.UtcNow - started < TimeSpan.FromMinutes(5)) return;
                poll.Stop();
                var images = _store.Items.Where(i => i.Kind == ItemKind.Image && !i.IsTrashed).ToList();
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var ids = await _visual.SearchAsync(phrase, [.. images.Select(i => i.Id)], CancellationToken.None);
                var lines = new List<string> { $"available={_visual.IsAvailable} images={images.Count} indexed in {(DateTime.UtcNow - started).TotalSeconds:0.0}s search {watch.ElapsedMilliseconds}ms matches={ids.Count}" };
                lines.AddRange(ids.Select(id => _store.Find(id) is { } item ? $"{item.PixelWidth}x{item.PixelHeight} {item.DisplayTitle} | {item.OcrText.Split('\n')[0]}" : id.ToString()));
                foreach (var (id, score) in await _visual.ScoresAsync(phrase))
                    lines.Add($"  score {score:0.000} {_store.Find(id)?.PixelWidth}x{_store.Find(id)?.PixelHeight} {_store.Find(id)?.DisplayTitle}");
                File.WriteAllLines(visualOutput, lines);
                Shutdown();
            };
            poll.Start();
            return;
        }
        if (e.Args is ["--ocr", var ocrImage, var ocrOutput])
        {
            // Development aid: recognizes one file with the same engine the app uses.
            _ = Task.Run(async () =>
            {
                var text = await _ocr.RecognizeFileAsync(ocrImage);
                File.WriteAllText(ocrOutput, _ocr.Language + Environment.NewLine + (text ?? "<null>"));
                await Dispatcher.InvokeAsync(Shutdown);
            });
            return;
        }
        if (e.Args is ["--snapshot", var markup, var shot] && markup.StartsWith("markup", StringComparison.Ordinal))
        {
            var source = _store.Items.FirstOrDefault(i => i is { Kind: ItemKind.Image, Recording: false, IsTrashed: false });
            var editor = source is null ? null : Markup.MarkupWindow.Create(_store, source, ShelfPalette.For(_settings.Current.Theme), new Pegline.Core.Input.Keymap(), markup == "markup-crop", out _);
            if (editor is not null)
            {
                editor.DiscardOnClose = true;
                editor.Width = 1316;
                editor.Height = 896;
                ((FrameworkElement)editor.Content).Measure(new Size(1316, 896));
                ((FrameworkElement)editor.Content).Arrange(new Rect(0, 0, 1316, 896));
                editor.UpdateLayout();
                if (markup == "markup-crop") editor.CropForSnapshot();
                else editor.SeedForSnapshot(Path.ChangeExtension(shot, ".export.png"));
            }
            DevSnapshot.CaptureDetached(editor, new Size(1316, 896), shot, () => Shutdown());
            return;
        }
        if (e.Args is ["--snapshot", var picker, var image] && picker.StartsWith("picker-", StringComparison.Ordinal))
        {
            var palette = ShelfPalette.For(_settings.Current.Theme);
            var ids = _store.Items.Where(i => !i.IsTrashed).Take(3).Select(i => i.Id).ToList();
            Window? window = picker switch
            {
                "picker-folder" => organizer.PinToFolder(palette, ids, _ => { }),
                "picker-tag" => organizer.AddTag(palette, ids, _ => { }),
                _ => organizer.NameAndPin(palette, ids, _ => { }),
            };
            DevSnapshot.CaptureDetached(window, picker == "picker-name" ? new Size(556, 228) : new Size(556, 456), image, () => Shutdown());
            return;
        }
        if (e.Args is ["--snapshot", "library-filters", var filtersShot])
        {
            DevSnapshot.CaptureDetached(_library.FiltersForSnapshot(), new Size(336, 600), filtersShot, () => Shutdown());
            return;
        }
        if (e.Args is ["--snapshot", var page, var pageShot] && page.StartsWith("settings-", StringComparison.Ordinal))
        {
            _settingsWindow = new Settings.SettingsWindow(_settings, _store, _hotKeys, _launchTheme, () => { }, () => { });
            DevSnapshot.Capture(_settingsWindow, page, pageShot, () => Shutdown());
            return;
        }
        if (e.Args is ["--snapshot", var target, var output])
        {
            DevSnapshot.Capture(target.StartsWith("library", StringComparison.Ordinal) ? _library : _shelves, target, output, () => Shutdown());
            return;
        }
#endif
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
                    _links?.Request([_store.InsertLink(content.Text).Id]);
                    break;
                case ClipKind.Link when settings.CaptureText:
                case ClipKind.Text when settings.CaptureText:
                    _store.InsertText(content.Text);
                    break;
                case ClipKind.Image when settings.CaptureImages && content.Png is not null:
                    var image = _store.InsertImage(content.Png);
                    _pairing.ClipboardImage(image.Id, image.PixelWidth, image.PixelHeight, DateTimeOffset.Now);
                    Collected(image, crop: false);
                    break;
                case ClipKind.Files when settings.CaptureImages && content.Files is { Count: > 0 } files:
                    CollectCopiedFiles(files);
                    break;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or InvalidDataException or IOException)
        {
            Log(ex);
        }
    }

    /// <summary>Image files copied in Explorer or a chat app; they are read off the UI thread, the originals stay untouched.</summary>
    /// <param name="intoLibrary">Files dropped on the Library are saved there and do not pull up the shelves.</param>
    private async void CollectCopiedFiles(IReadOnlyList<string> files, bool intoLibrary = false)
    {
        ClipItem? last = null;
        var added = new List<Guid>();
        foreach (var path in files)
        {
            if (WatchedFiles.IsVideo(path))
            {
                if (_store is not null && await Recordings.ImportAsync(_store, Dispatcher, path) is { } recording)
                {
                    last = recording;
                    added.Add(recording.Id);
                }
                continue;
            }
            byte[]? png;
            try
            {
                png = await Task.Run(() => FolderWatcher.ReadAsPng(path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException or InvalidOperationException or ArgumentException)
            {
                Log(ex);
                continue;
            }
            if (png is null || _store is null) continue;
            try
            {
                last = _store.InsertImage(png, path);
                added.Add(last.Id);
                if (_settings?.Current.IndexImages == true) _ocr?.Enqueue(last);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or InvalidDataException or IOException)
            {
                Log(ex);
            }
        }
        if (intoLibrary)
        {
            if (added.Count > 0) _store?.Retain(added);
            _library?.FilesAdded(added.Count, files.Count);
        }
        else if (last is not null) Collected(last, crop: false);
    }

    /// <summary>A finished recording in a watched folder (or the Snipping Tool's Screen Recordings folder).</summary>
    private async Task CollectRecording(string path)
    {
        if (_store is null) return;
        if (await Recordings.ImportAsync(_store, Dispatcher, path) is { } recording) Collected(recording, crop: false);
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
            case "library":
                if (_library is { IsVisible: true, IsActive: true }) _library.HideLibrary();
                else OpenLibrary(null, null, false);
                break;
            case "screenshot":
                CaptureScreen();
                break;
            case "copyLatest":
                var latest = _store?.Items.FirstOrDefault(i => i.Kind == ItemKind.Image && i.IsOnShelf);
                if (latest is not null && _store is not null) new ClipboardWriter(_store).Copy([latest]);
                break;
        }
    }

    /// <summary>A new image arrived (clipboard, screenshot folder, watched folder or screen capture).</summary>
    private void Collected(ClipItem image, bool crop)
    {
        if (_settings is null || _shelves is null) return;
        var settings = _settings.Current;
        if (settings.IndexImages) _ocr?.Enqueue(image);
        if (settings.Sounds) Sounds.Play(Sound.Hang);
        if (crop)
        {
            _shelves.ShowCaptured(image.Id, crop);
            return;
        }
        // The new image shows itself for a moment without taking the keyboard. Over a full-screen app (a game or a
        // video) it waits quietly on its shelf unless the user allowed the shelves there.
        if (settings.OpenAfterScreenshot && (settings.ShowOverFullScreen || !Win32.IsFullscreenForeground()))
            _shelves.ShowPassive(image.Id, TimeSpan.FromSeconds(2.5));
    }

    private static string RetentionKey(AppSettings settings) =>
        string.Join(",", settings.RetentionDays.OrderBy(p => p.Key).Select(p => p.Value));

    /// <summary>The top edge brings the shelves down only when nothing else of Pegline's is in use.</summary>
    private bool PointerRevealAllowed()
    {
        if (_shelves is null || _settings is null || _shelves.IsShown) return false;
        if (_library is { IsActive: true } || _settingsWindow is { IsActive: true }) return false;
        return _settings.Current.ShowOverFullScreen || !Win32.IsFullscreenForeground();
    }

    private void OpenSettings(string section)
    {
        if (_settings is null || _store is null || _hotKeys is null) return;
        _settingsWindow ??= new Settings.SettingsWindow(_settings, _store, _hotKeys, _launchTheme, () => OpenLibrary(null, null, false), Reopen);
        _settingsWindow.Present(section);
    }

    /// <summary>Quits and starts again, so a new language reaches every window. Open edits are saved by quitting.</summary>
    private void Reopen()
    {
        if (Environment.ProcessPath is not { } path) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path, "--reopen") { UseShellExecute = false });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log(ex);
            return;
        }
        Quit();
    }

    /// <summary>An image saved to a watched folder. A screenshot already taken from the clipboard only gains its file path.</summary>
    private void CollectFile(byte[] png, string path)
    {
        if (_store is null) return;
        try
        {
            var (width, height) = MediaStore.ReadPngSize(png);
            if (_pairing.MatchFile(width, height, DateTimeOffset.Now) is { } existing)
            {
                _store.AttachSource(existing, path);
                return;
            }
            Collected(_store.InsertImage(png, path), crop: false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or InvalidDataException or IOException)
        {
            Log(ex);
        }
    }

    /// <summary>Ctrl+Alt+A: hides Pegline, captures the display under the pointer and opens the shot in crop.</summary>
    private async void CaptureScreen()
    {
        if (_store is null || _shelves is null) return;
        if (_shelves.IsShown) _shelves.HideShelves(restoreFocus: true);
        // Let the shelves leave the screen before the capture.
        await Task.Delay(180);
        try
        {
            Collected(_store.InsertImage(ScreenCapture.MonitorUnderPointer()), crop: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.ExternalException)
        {
            Log(ex);
            _tray?.Notify("Pegline", Loc.T("Screen capture is unavailable"));
        }
    }

    private void ConfigureFolders()
    {
        if (_settings is null || _folders is null) return;
        var folders = new List<string>();
        if (_settings.Current.CaptureScreenshots)
        {
            folders.Add(FolderWatcher.ScreenshotsFolder);
            folders.Add(FolderWatcher.ScreenRecordingsFolder);
        }
        folders.AddRange(_settings.Current.WatchFolders);
        var key = string.Join("|", folders);
        if (key == _watchedKey) return;
        _watchedKey = key;
        _folders.Configure(folders);
    }

    private void ApplyGlobalShortcuts()
    {
        if (_hotKeys is null || _settings is null) return;
        var failed = _hotKeys.Apply(_settings.Current.GlobalShortcuts);
        // Settings explains a refused shortcut in place.
        if (failed.Count > 0 && _settingsWindow is not { IsVisible: true })
            _tray?.Notify("Pegline", Loc.F("Shortcut unavailable: {0}", string.Join(", ", failed.Select(a => _settings.Current.GlobalShortcuts[a]))));
    }

    private void OpenLibrary(LibraryScope? scope, ItemKind? kind, bool search) => _library?.ShowLibrary(scope, kind, search);

    internal void Quit()
    {
        _settingsWindow?.CloseForQuit();
        _library?.CloseForQuit();
        _shelves?.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _expiry?.Stop();
        _hotKeys?.Dispose();
        _clipboard?.Dispose();
        _folders?.Dispose();
        _visual?.Dispose();
        _links?.Dispose();
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
