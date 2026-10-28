using System.Drawing;
using System.Windows.Forms;
using Pegline.App.Localization;
using Pegline.Core.Storage;

namespace Pegline.App;

/// <summary>Notification-area icon and menu.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly SettingsStore _settings;
    private readonly Icon _image;
    private readonly Updates.UpdateService _updates;

    public TrayIcon(SettingsStore settings, Updates.UpdateService updates, Action showShelves, Action openLibrary, Action captureScreen, Action<string> openSettings, Action quit)
    {
        _settings = settings;
        _updates = updates;
        _image = LoadIcon();
        _icon = new NotifyIcon { Icon = _image, Text = "Pegline", Visible = true };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) showShelves();
        };
        _icon.ContextMenuStrip = new ContextMenuStrip();
        _icon.ContextMenuStrip.Opening += (_, _) => BuildMenu(showShelves, openLibrary, captureScreen, openSettings, quit);
        BuildMenu(showShelves, openLibrary, captureScreen, openSettings, quit);
    }

    public void Notify(string title, string text) => _icon.ShowBalloonTip(4000, title, text, ToolTipIcon.None);

    private void BuildMenu(Action showShelves, Action openLibrary, Action captureScreen, Action<string> openSettings, Action quit)
    {
        var menu = _icon.ContextMenuStrip!;
        menu.Items.Clear();
        var current = _settings.Current;
        // A new version also appears here; installing still starts only from Settings.
        if (_updates.AvailableVersion is { } version)
        {
            menu.Items.Add(Loc.F("Install Pegline {0}…", version), null, (_, _) => openSettings("updates"));
            menu.Items.Add(new ToolStripSeparator());
        }
        menu.Items.Add(Loc.T("Show shelves") + "\t" + current.GlobalShortcuts.GetValueOrDefault("toggle", ""), null, (_, _) => showShelves());
        menu.Items.Add(Loc.T("Open Library") + "	" + current.GlobalShortcuts.GetValueOrDefault("library", ""), null, (_, _) => openLibrary());
        menu.Items.Add(Loc.T("Capture screen") + "	" + current.GlobalShortcuts.GetValueOrDefault("screenshot", ""), null, (_, _) => captureScreen());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Toggle(Loc.T("Capture copied text"), current.CaptureText, value => _settings.Update(s => s.CaptureText = value)));
        menu.Items.Add(Toggle(Loc.T("Capture copied images"), current.CaptureImages, value => _settings.Update(s => s.CaptureImages = value)));
        menu.Items.Add(Toggle(Loc.T("Capture copied links"), current.CaptureLinks, value => _settings.Update(s => s.CaptureLinks = value)));
        menu.Items.Add(Toggle(Loc.T("Collect the Windows Screenshots folder"), current.CaptureScreenshots, value => _settings.Update(s => s.CaptureScreenshots = value)));
        menu.Items.Add(Toggle(Loc.T("Show link titles and previews"), current.FetchLinkMetadata, value => _settings.Update(s => s.FetchLinkMetadata = value)));
        menu.Items.Add(Toggle(Loc.T("Index images for search"), current.IndexImages, value => _settings.Update(s => s.IndexImages = value)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Loc.T("Settings…"), null, (_, _) => openSettings("general"));
        menu.Items.Add(Loc.T("Quit Pegline"), null, (_, _) => quit());
    }

    private static ToolStripMenuItem Toggle(string text, bool value, Action<bool> change)
    {
        var item = new ToolStripMenuItem(text) { Checked = value, CheckOnClick = true };
        item.CheckedChanged += (_, _) => change(item.Checked);
        return item;
    }

    /// <summary>The Pegline mark at the notification area's size for the current display scale.</summary>
    private static Icon LoadIcon()
    {
        using var stream = typeof(TrayIcon).Assembly.GetManifestResourceStream("Pegline.Brand.tray.ico")!;
        return new Icon(stream, SystemInformation.SmallIconSize);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _image.Dispose();
    }
}
