using System.Drawing;
using System.Drawing.Drawing2D;
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

    public TrayIcon(SettingsStore settings, Action showShelves, Action openLibrary, Action captureScreen, Action quit)
    {
        _settings = settings;
        _image = DrawIcon();
        _icon = new NotifyIcon { Icon = _image, Text = "Pegline", Visible = true };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) showShelves();
        };
        _icon.ContextMenuStrip = new ContextMenuStrip();
        _icon.ContextMenuStrip.Opening += (_, _) => BuildMenu(showShelves, openLibrary, captureScreen, quit);
        BuildMenu(showShelves, openLibrary, captureScreen, quit);
    }

    public void Notify(string title, string text) => _icon.ShowBalloonTip(4000, title, text, ToolTipIcon.None);

    private void BuildMenu(Action showShelves, Action openLibrary, Action captureScreen, Action quit)
    {
        var menu = _icon.ContextMenuStrip!;
        menu.Items.Clear();
        var current = _settings.Current;
        menu.Items.Add(Loc.T("Show shelves") + "\t" + current.GlobalShortcuts.GetValueOrDefault("toggle", ""), null, (_, _) => showShelves());
        menu.Items.Add(Loc.T("Open Library") + "	" + current.GlobalShortcuts.GetValueOrDefault("library", ""), null, (_, _) => openLibrary());
        menu.Items.Add(Loc.T("Capture screen") + "	" + current.GlobalShortcuts.GetValueOrDefault("screenshot", ""), null, (_, _) => captureScreen());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Toggle(Loc.T("Capture copied text"), current.CaptureText, value => _settings.Update(s => s.CaptureText = value)));
        menu.Items.Add(Toggle(Loc.T("Capture copied images"), current.CaptureImages, value => _settings.Update(s => s.CaptureImages = value)));
        menu.Items.Add(Toggle(Loc.T("Capture copied links"), current.CaptureLinks, value => _settings.Update(s => s.CaptureLinks = value)));
        menu.Items.Add(Toggle(Loc.T("Collect the Windows Screenshots folder"), current.CaptureScreenshots, value => _settings.Update(s => s.CaptureScreenshots = value)));
        menu.Items.Add(Toggle(Loc.T("Show shelves after a screenshot"), current.OpenAfterScreenshot, value => _settings.Update(s => s.OpenAfterScreenshot = value)));
        menu.Items.Add(Toggle(Loc.T("Show link titles and previews"), current.FetchLinkMetadata, value => _settings.Update(s => s.FetchLinkMetadata = value)));
        menu.Items.Add(Toggle(Loc.T("Index images for search"), current.IndexImages, value => _settings.Update(s => s.IndexImages = value)));
        menu.Items.Add(WatchedFolders(current.WatchFolders));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Loc.T("Quit Pegline"), null, (_, _) => quit());
    }

    /// <summary>Up to three extra folders whose new images are collected. Interim home until the Settings window.</summary>
    private ToolStripMenuItem WatchedFolders(IReadOnlyList<string> folders)
    {
        var menu = new ToolStripMenuItem(Loc.T("Watched folders"));
        foreach (var folder in folders)
        {
            var entry = new ToolStripMenuItem(folder);
            entry.DropDownItems.Add(Loc.T("Stop watching"), null, (_, _) => _settings.Update(s => s.WatchFolders.Remove(folder)));
            menu.DropDownItems.Add(entry);
        }
        if (folders.Count > 0) menu.DropDownItems.Add(new ToolStripSeparator());
        var add = new ToolStripMenuItem(Loc.T("Add folder…")) { Enabled = folders.Count < Pegline.Core.Models.AppSettings.MaxWatchFolders };
        add.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = Loc.T("Watched folders") };
            if (dialog.ShowDialog() != true) return;
            var chosen = dialog.FolderName;
            if (_settings.Current.WatchFolders.Contains(chosen, StringComparer.OrdinalIgnoreCase)) return;
            try
            {
                _settings.Update(s => s.WatchFolders.Add(chosen));
            }
            catch (ArgumentException)
            {
                Notify("Pegline", Loc.T("Up to three folders can be watched."));
            }
        };
        menu.DropDownItems.Add(add);
        return menu;
    }

    private static ToolStripMenuItem Toggle(string text, bool value, Action<bool> change)
    {
        var item = new ToolStripMenuItem(text) { Checked = value, CheckOnClick = true };
        item.CheckedChanged += (_, _) => change(item.Checked);
        return item;
    }

    /// <summary>Placeholder mark until the final logo arrives: a clothespin on a rope.</summary>
    private static Icon DrawIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var rope = new Pen(Color.FromArgb(230, 200, 205, 214), 2.2f);
            g.DrawArc(rope, -6, -14, 44, 30, 20, 140);
            using var card = new SolidBrush(Color.FromArgb(255, 59, 139, 246));
            using var cardPath = Rounded(new RectangleF(7, 13, 18, 16), 3.5f);
            g.FillPath(card, cardPath);
            using var peg = new SolidBrush(Color.FromArgb(255, 238, 228, 210));
            using var pegPath = Rounded(new RectangleF(13.5f, 4, 5, 13), 1.6f);
            g.FillPath(peg, pegPath);
            using var spring = new Pen(Color.FromArgb(255, 150, 130, 100), 1.1f);
            g.DrawLine(spring, 16, 6, 16, 15);
        }
        return Icon.FromHandle(bitmap.GetHicon());
    }

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _image.Dispose();
    }
}
