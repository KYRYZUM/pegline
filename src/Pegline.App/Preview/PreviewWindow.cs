using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Pegline.App.Localization;
using Pegline.App.Platform;
using Pegline.App.Shelves;
using Pegline.Core.Input;
using Pegline.Core.Models;
using Pegline.Core.Shelves;
using Pegline.Core.Storage;

namespace Pegline.App.Preview;

/// <summary>The surface that owns a preview: the shelves today, the Library later.</summary>
internal interface IPreviewHost
{
    Keymap Keymap { get; }

    /// <summary>Runs a browsing action against the previewed item. Returns false when it does not apply.</summary>
    bool Forward(string action, string gesture);

    bool SaveText(Guid id, string text);

    bool CopyText(string text);

    void PreviewDeactivated();

    void PreviewClosed();
}

/// <summary>Where a preview opens: the monitor's work area and DPI scale, and the rectangle to center on (pixels).</summary>
internal readonly record struct Placement(Win32.RECT Work, double Scale, Win32.RECT Anchor);

/// <summary>
/// Borderless Space preview. Images fit the screen and zoom from 100% to 400%; text can be edited
/// in place with automatic saving; links open in the browser. Navigation keys go back to the host.
/// </summary>
internal sealed class PreviewWindow : FloatingWindow
{
    private const double MinImageWidth = 360;
    private const double MinImageHeight = 220;

    private readonly IPreviewHost _host;
    private readonly LibraryStore _store;
    private readonly ShelfPalette _palette;
    private readonly Border _surface;
    private readonly Grid _layout = new();
    private readonly Border _controls;
    private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _position = new();
    private readonly TextBlock _status = new();
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _statusTimer;
    private TextBlock? _pinGlyph;
    private TextBlock? _editGlyph;
    private TextBlock? _editLabel;

    // Image
    private readonly ScrollViewer _imageScroll = new();
    private readonly Image _image = new();
    private Size _fitted;
    private PreviewZoom _zoom = new();
    private Point? _panStart;
    private Point _panOrigin;

    // Text
    private readonly TextBox _text = new();
    private bool _dirty;
    private bool _discardEdits;

    // Link
    private readonly Border _thumbnail = new();
    private readonly TextBlock _linkTitle = new();
    private readonly TextBlock _linkDetail = new();
    private readonly TextBlock _linkAddress = new();

    private ClipItem? _item;
    private ItemKind? _built;
    private string _positionText = "";

    public PreviewWindow(IPreviewHost host, LibraryStore store, ShelfPalette palette)
    {
        _host = host;
        _store = store;
        _palette = palette;
        Title = Loc.T("Preview");
        _surface = Surface(palette, 20);
        _surface.Child = _layout;
        Content = _surface;
        _controls = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = palette.Chip,
            BorderBrush = palette.PanelBorder,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 4, 6, 4),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Child = _buttons,
        };
        _position.FontSize = 11;
        _position.FontWeight = FontWeights.Medium;
        _position.Foreground = palette.Foreground;
        _position.MinWidth = 46;
        _position.TextAlignment = TextAlignment.Center;
        _position.VerticalAlignment = VerticalAlignment.Center;
        _status.FontSize = 11;
        _status.Foreground = palette.Muted;
        _status.TextTrimming = TextTrimming.CharacterEllipsis;

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _saveTimer.Tick += (_, _) => FlushEdits();
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1250) };
        _statusTimer.Tick += (_, _) =>
        {
            _statusTimer.Stop();
            UpdateStatus();
        };

        ConfigureImage();
        ConfigureText();
        ConfigureLink();

        PreviewKeyDown += OnKey;
        Deactivated += (_, _) => _host.PreviewDeactivated();
        MouseEnter += (_, _) => UpdateControlsVisibility(hovering: true);
        MouseLeave += (_, _) => UpdateControlsVisibility(hovering: false);
    }

    public ClipItem? Item => _item;

    /// <summary>True when the image changed on disk (markup or crop saved) and must be presented again.</summary>
    public bool NeedsReload(ClipItem item) => item.Kind == ItemKind.Image && _built == ItemKind.Image && item.DisplayFile != _imageFile;

    public bool IsTextEditing { get; private set; }

    private string _imageFile = "";

    public int ZoomPercent => _zoom.Percent;

    /// <summary>Shows an item, reusing the window's position when one is already open.</summary>
    public bool Present(ClipItem item, string position, Placement placement)
    {
        var changed = _item?.Id != item.Id || _built != item.Kind || !IsOpen || NeedsReload(item);
        if (changed && !FlushEdits()) return false;
        if (_built != item.Kind) Build(item.Kind);
        _item = item;
        _positionText = position;
        if (!changed)
        {
            Refresh(item, position);
            return true;
        }

        Size size;
        var work = placement.Work;
        var workWidth = (work.Right - work.Left) / placement.Scale;
        var workHeight = (work.Bottom - work.Top) / placement.Scale;
        switch (item.Kind)
        {
            case ItemKind.Image:
                var source = Decode(_store.MediaPath(item));
                if (source is null) return false;
                _image.Source = source;
                _imageFile = item.DisplayFile;
                var width = (item.PixelWidth > 0 ? item.PixelWidth : source.PixelWidth) / placement.Scale;
                var height = (item.PixelHeight > 0 ? item.PixelHeight : source.PixelHeight) / placement.Scale;
                var fit = Math.Min(1, Math.Min(workWidth * 0.88 / width, workHeight * 0.82 / height));
                _fitted = new Size(width * fit, height * fit);
                _zoom.Reset();
                ApplyZoom(1, new Point(0, 0));
                size = new Size(Math.Max(MinImageWidth, _fitted.Width), Math.Max(MinImageHeight, _fitted.Height));
                break;
            case ItemKind.Text:
                _discardEdits = false;
                _text.Text = item.Text;
                _text.IsReadOnly = true;
                _text.ScrollToHome();
                _text.Select(0, 0);
                IsTextEditing = false;
                var lines = Math.Clamp(item.Text.Length > 10000 ? 18 : item.Text.Split('\n').Length, 4, 18);
                size = new Size(Math.Min(680, workWidth * 0.8), Math.Min(workHeight * 0.72, Math.Max(260, lines * 24 + 100)));
                break;
            default:
                ShowLink(item);
                size = new Size(Math.Min(660, workWidth * 0.8), Math.Min(650, workHeight * 0.85));
                break;
        }
        UpdateControls();
        UpdateStatus();
        ShowAt(work, placement.Scale, placement.Anchor, size.Width, size.Height);
        Keyboard.Focus(this);
        UpdateControlsVisibility(IsMouseOver);
        return true;
    }

    /// <summary>Updates pin state, position and link details without touching an edit in progress.</summary>
    public void Refresh(ClipItem item, string position)
    {
        if (_item?.Id != item.Id) return;
        _item = item;
        _positionText = position;
        if (item.Kind == ItemKind.Text && !IsTextEditing && !_dirty && _text.Text != item.Text) _text.Text = item.Text;
        if (item.Kind == ItemKind.Link) ShowLink(item);
        UpdateControls();
        UpdateStatus();
    }

    public void ResetZoom()
    {
        if (_built != ItemKind.Image) return;
        EndPan();
        var old = _zoom.Scale;
        _zoom.Reset();
        ApplyZoom(old, new Point(_imageScroll.ViewportWidth / 2, _imageScroll.ViewportHeight / 2));
    }

    public void FlashCopied()
    {
        _status.Text = Loc.T(_item?.Kind == ItemKind.Link ? "Link copied" : _item?.Kind == ItemKind.Text ? "Text copied" : "Copied");
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    /// <summary>Closes even when the pending edit cannot be saved (for example, when the shelves hide).</summary>
    public void ForceClose()
    {
        _discardEdits = true;
        Close();
    }

    public void OpenLink()
    {
        if (_item is not { Kind: ItemKind.Link } link) return;
        if (!Uri.TryCreate(link.Url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return;
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            _status.Text = Loc.T("The browser could not be opened.");
        }
    }

    public void BeginEditing()
    {
        if (_built != ItemKind.Text || IsTextEditing) return;
        IsTextEditing = true;
        _text.IsReadOnly = false;
        Keyboard.Focus(_text);
        UpdateControls();
        UpdateStatus();
    }

    public bool FinishEditing()
    {
        if (!IsTextEditing) return true;
        if (!FlushEdits()) return false;
        IsTextEditing = false;
        _text.IsReadOnly = true;
        Keyboard.Focus(this);
        UpdateControls();
        UpdateStatus();
        return true;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_discardEdits && !FlushEdits())
        {
            e.Cancel = true;
            return;
        }
        _saveTimer.Stop();
        _statusTimer.Stop();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _host.PreviewClosed();
    }

    // ----- Layout -----

    private void Build(ItemKind kind)
    {
        _built = kind;
        _layout.Children.Clear();
        _layout.RowDefinitions.Clear();
        _surface.Padding = new Thickness(0);
        _surface.CornerRadius = new CornerRadius(kind == ItemKind.Image ? 14 : 20);
        _controls.Margin = kind == ItemKind.Image ? new Thickness(10) : new Thickness(0, 12, 16, 0);
        BuildControls(kind);
        switch (kind)
        {
            case ItemKind.Image:
                _layout.Children.Add(_imageScroll);
                _layout.Children.Add(_controls);
                break;
            case ItemKind.Text:
                _layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                _layout.RowDefinitions.Add(new RowDefinition());
                _layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                _layout.Children.Add(_controls);
                Grid.SetRow(_text, 1);
                _layout.Children.Add(_text);
                _status.Margin = new Thickness(24, 0, 20, 14);
                Grid.SetRow(_status, 2);
                _layout.Children.Add(_status);
                break;
            default:
                _layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                _layout.RowDefinitions.Add(new RowDefinition());
                _layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                _controls.Margin = new Thickness(0, 16, 20, 0);
                _layout.Children.Add(_controls);
                var body = new StackPanel { Margin = new Thickness(24, 14, 24, 0) };
                body.Children.Add(_thumbnail);
                body.Children.Add(_linkTitle);
                body.Children.Add(_linkDetail);
                body.Children.Add(_linkAddress);
                var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
                Grid.SetRow(scroll, 1);
                _layout.Children.Add(scroll);
                var footer = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(24, 14, 24, 20) };
                footer.Children.Add(TextButton(Glyphs.OpenExternal, Loc.T("Open in browser"), OpenLink));
                _status.Margin = new Thickness(12, 0, 0, 0);
                _status.VerticalAlignment = VerticalAlignment.Center;
                footer.Children.Add(_status);
                Grid.SetRow(footer, 2);
                _layout.Children.Add(footer);
                break;
        }
    }

    private void BuildControls(ItemKind kind)
    {
        _buttons.Children.Clear();
        _buttons.Children.Add(IconButton(Glyphs.ChevronLeft, Loc.T("Previous item"), () => _host.Forward("previous", "")));
        _buttons.Children.Add(_position);
        _buttons.Children.Add(IconButton(Glyphs.ChevronRight, Loc.T("Next item"), () => _host.Forward("next", "")));
        _buttons.Children.Add(IconButton(Glyphs.Copy, Loc.T("Copy"), () => _host.Forward("copy", "")));
        var pin = IconButton(Glyphs.Pin, Loc.T("Pin favorite"), () => _host.Forward("pin", ""));
        _pinGlyph = (TextBlock)pin.Child;
        _buttons.Children.Add(pin);
        _editGlyph = null;
        _editLabel = null;
        if (kind == ItemKind.Text)
        {
            var edit = TextButton(Glyphs.Edit, Loc.T("Edit text"), () =>
            {
                if (IsTextEditing) FinishEditing();
                else BeginEditing();
            });
            var content = (StackPanel)edit.Child;
            _editGlyph = (TextBlock)content.Children[0];
            _editLabel = (TextBlock)content.Children[1];
            _buttons.Children.Add(edit);
        }
        if (kind == ItemKind.Image)
            _buttons.Children.Add(IconButton(Glyphs.Markup, Loc.T("Markup image"), () => _host.Forward("markup", "")));
        _buttons.Children.Add(IconButton(Glyphs.Command, Loc.T("Actions") + Hint("actions"), () => _host.Forward("actions", "")));
        _buttons.Children.Add(IconButton(Glyphs.Close, Loc.T("Close preview"), () => Close()));
    }

    private string Hint(string action)
    {
        var key = _host.Keymap.Primary(action);
        return key.Length == 0 ? "" : " · " + KeyGestures.Display(key);
    }

    private Border IconButton(string glyph, string tooltip, Action click)
    {
        var button = new Border
        {
            Width = 26,
            Height = 26,
            Margin = new Thickness(1, 0, 1, 0),
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = tooltip,
            Child = new TextBlock { Text = glyph, FontFamily = ShelfPalette.Icons, FontSize = 12, Foreground = _palette.Foreground, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        Hover(button, click);
        return button;
    }

    private Border TextButton(string glyph, string label, Action click)
    {
        var button = new Border
        {
            Height = 26,
            MinWidth = 72,
            Margin = new Thickness(2, 0, 2, 0),
            Padding = new Thickness(8, 0, 10, 0),
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = glyph, FontFamily = ShelfPalette.Icons, FontSize = 12, Foreground = _palette.Foreground, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = label, FontSize = 12, Foreground = _palette.Foreground, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        Hover(button, click);
        return button;
    }

    private void Hover(Border button, Action click)
    {
        button.MouseEnter += (_, _) => button.Background = _palette.ChipActive;
        button.MouseLeave += (_, _) => button.Background = Brushes.Transparent;
        button.PreviewMouseLeftButtonDown += (_, e) => e.Handled = true; // keep window dragging and text focus out of it
        button.PreviewMouseLeftButtonUp += (_, e) =>
        {
            click();
            e.Handled = true;
        };
    }

    private void UpdateControls()
    {
        var position = _positionText;
        if (_built == ItemKind.Image && _zoom.Percent != 100) position += $" · {_zoom.Percent}%";
        _position.Text = position;
        if (_pinGlyph is not null && _item is not null)
        {
            _pinGlyph.Text = _item.Pinned ? Glyphs.Pinned : Glyphs.Pin;
            _pinGlyph.Foreground = _item.Pinned ? _palette.Accent : _palette.Foreground;
            ((Border)_pinGlyph.Parent).ToolTip = Loc.T(_item.Pinned ? "Unpin" : "Pin favorite") + Hint("pin");
        }
        if (_editGlyph is not null && _editLabel is not null)
        {
            _editGlyph.Text = IsTextEditing ? Glyphs.Accept : Glyphs.Edit;
            _editLabel.Text = Loc.T(IsTextEditing ? "Done" : "Edit text");
            ((Border)((StackPanel)_editGlyph.Parent).Parent).ToolTip = Loc.T(IsTextEditing ? "Finish editing" : "Edit text") + Hint(IsTextEditing ? "finishEditing" : "edit");
        }
    }

    private void UpdateControlsVisibility(bool hovering) =>
        _controls.Visibility = _built != ItemKind.Image || hovering ? Visibility.Visible : Visibility.Hidden;

    private void UpdateStatus()
    {
        if (_statusTimer.IsEnabled) return;
        var keys = _host.Keymap;
        string Key(string action) => KeyGestures.Display(keys.Primary(action));
        _status.Text = _built switch
        {
            ItemKind.Text when IsTextEditing => Loc.T("Editing · changes are saved automatically") + " · " + Key("finishEditing") + " " + Loc.T("Finish editing"),
            ItemKind.Text => Loc.T("Click the text to edit") + " · " + Key("previous") + "/" + Key("next") + " " + Loc.T("Preview") + " · " + Key("pin") + " " + Loc.T("Pin favorite"),
            ItemKind.Link => Key("open") + " · " + Loc.T("Open in browser"),
            _ => "",
        };
    }

    // ----- Image -----

    private void ConfigureImage()
    {
        _imageScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
        _imageScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        _imageScroll.Focusable = false;
        _imageScroll.Content = _image;
        _image.Stretch = Stretch.Fill;
        _image.HorizontalAlignment = HorizontalAlignment.Center;
        _image.VerticalAlignment = VerticalAlignment.Center;
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        _imageScroll.SizeChanged += (_, e) => _imageScroll.Clip = new RectangleGeometry(new Rect(e.NewSize), 13, 13);
        _imageScroll.PreviewMouseWheel += (_, e) =>
        {
            e.Handled = true;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                _imageScroll.ScrollToHorizontalOffset(_imageScroll.HorizontalOffset - e.Delta);
                return;
            }
            var old = _zoom.Scale;
            _zoom.Wheel(e.Delta / 120.0);
            ApplyZoom(old, e.GetPosition(_imageScroll));
        };
        _imageScroll.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                ResetZoom();
                e.Handled = true;
                return;
            }
            if (_zoom.CanPan)
            {
                _panStart = e.GetPosition(_imageScroll);
                _panOrigin = new Point(_imageScroll.HorizontalOffset, _imageScroll.VerticalOffset);
                _imageScroll.CaptureMouse();
                _imageScroll.Cursor = Cursors.SizeAll;
            }
            else
            {
                DragMove();
            }
            e.Handled = true;
        };
        _imageScroll.PreviewMouseMove += (_, e) =>
        {
            if (_panStart is not { } start) return;
            var point = e.GetPosition(_imageScroll);
            _imageScroll.ScrollToHorizontalOffset(_panOrigin.X - (point.X - start.X));
            _imageScroll.ScrollToVerticalOffset(_panOrigin.Y - (point.Y - start.Y));
        };
        _imageScroll.PreviewMouseLeftButtonUp += (_, _) => EndPan();
        _imageScroll.LostMouseCapture += (_, _) => EndPan();
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem(Loc.T("Markup image"), () => _host.Forward("markup", "")));
        menu.Items.Add(MenuItem(Loc.T("Crop image"), () => _host.Forward("crop", "")));
        menu.Items.Add(MenuItem(Loc.T("Reset zoom"), ResetZoom));
        menu.Items.Add(MenuItem(Loc.T("Close preview"), () => Close()));
        _imageScroll.ContextMenu = menu;
    }

    private static MenuItem MenuItem(string header, Action click)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => click();
        return item;
    }

    private void EndPan()
    {
        if (_panStart is null) return;
        _panStart = null;
        if (_imageScroll.IsMouseCaptured) _imageScroll.ReleaseMouseCapture();
        _imageScroll.Cursor = _zoom.CanPan ? Cursors.Hand : null;
    }

    private void ApplyZoom(double oldScale, Point anchor)
    {
        var scale = _zoom.Scale;
        _image.Width = _fitted.Width * scale;
        _image.Height = _fitted.Height * scale;
        _imageScroll.UpdateLayout();
        _imageScroll.ScrollToHorizontalOffset(PreviewZoom.AnchoredOffset(_imageScroll.HorizontalOffset, anchor.X, oldScale, scale, _fitted.Width, _imageScroll.ViewportWidth));
        _imageScroll.ScrollToVerticalOffset(PreviewZoom.AnchoredOffset(_imageScroll.VerticalOffset, anchor.Y, oldScale, scale, _fitted.Height, _imageScroll.ViewportHeight));
        _imageScroll.Cursor = _zoom.CanPan ? Cursors.Hand : null;
        UpdateControls();
    }

    private static BitmapSource? Decode(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.UriSource = new Uri(path);
            image.EndInit();
            BitmapSource result = image;
            if (Math.Max(image.PixelWidth, image.PixelHeight) > 4000)
            {
                var factor = 4000.0 / Math.Max(image.PixelWidth, image.PixelHeight);
                result = new TransformedBitmap(image, new ScaleTransform(factor, factor));
            }
            result.Freeze();
            return result;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    // ----- Text -----

    private void ConfigureText()
    {
        _text.IsReadOnly = true;
        _text.TextWrapping = TextWrapping.Wrap;
        _text.AcceptsReturn = true;
        _text.AcceptsTab = true;
        _text.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _text.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _text.BorderThickness = new Thickness(0);
        _text.Background = Brushes.Transparent;
        _text.Foreground = _palette.Foreground;
        _text.CaretBrush = _palette.Foreground;
        _text.SelectionBrush = _palette.Accent;
        _text.FontSize = 16;
        _text.Padding = new Thickness(4, 8, 4, 8);
        _text.Margin = new Thickness(20, 8, 20, 8);
        _text.PreviewMouseLeftButtonDown += (_, _) => BeginEditing();
        _text.TextChanged += (_, _) =>
        {
            if (!IsTextEditing) return;
            _dirty = true;
            _saveTimer.Stop();
            _saveTimer.Start();
        };
    }

    private bool FlushEdits()
    {
        _saveTimer.Stop();
        if (!_dirty || _item is null) return true;
        if (!_host.SaveText(_item.Id, _text.Text))
        {
            _status.Text = Loc.T("Could not save text history");
            return false;
        }
        _dirty = false;
        _status.Text = Loc.T("Changes saved");
        return true;
    }

    // ----- Link -----

    private void ConfigureLink()
    {
        _thumbnail.CornerRadius = new CornerRadius(12);
        _thumbnail.Background = _palette.Chip;
        _thumbnail.ClipToBounds = true;
        _thumbnail.SizeChanged += (_, e) =>
        {
            if (Math.Abs(e.NewSize.Height - e.NewSize.Width * 9 / 16) > 0.5) _thumbnail.Height = e.NewSize.Width * 9 / 16;
        };
        _linkTitle.FontSize = 21;
        _linkTitle.FontWeight = FontWeights.SemiBold;
        _linkTitle.Foreground = _palette.Foreground;
        _linkTitle.TextWrapping = TextWrapping.Wrap;
        _linkTitle.TextTrimming = TextTrimming.CharacterEllipsis;
        _linkTitle.MaxHeight = 3 * 28;
        _linkTitle.Margin = new Thickness(0, 12, 0, 0);
        _linkDetail.FontSize = 12;
        _linkDetail.Foreground = _palette.Muted;
        _linkDetail.TextWrapping = TextWrapping.Wrap;
        _linkDetail.Margin = new Thickness(0, 12, 0, 0);
        _linkAddress.FontSize = 11;
        _linkAddress.Foreground = _palette.Muted;
        _linkAddress.TextWrapping = TextWrapping.Wrap;
        _linkAddress.TextTrimming = TextTrimming.CharacterEllipsis;
        _linkAddress.MaxHeight = 2 * 16;
        _linkAddress.Margin = new Thickness(0, 12, 0, 0);
    }

    private void ShowLink(ClipItem item)
    {
        var host = Uri.TryCreate(item.Url, UriKind.Absolute, out var uri) ? uri.Host : "";
        _linkTitle.Text = item.DisplayTitle.Length > 0 ? item.DisplayTitle : host.Length > 0 ? host : item.Url;
        _linkDetail.Text = string.Join(" · ", new[] { item.LinkAuthor, item.LinkSiteName.Length > 0 ? item.LinkSiteName : host }.Where(s => s.Length > 0));
        _linkAddress.Text = item.Url;
        var path = item.LinkThumbnailFile.Length > 0 ? _store.Media.PathOf(item.LinkThumbnailFile) : "";
        var thumbnail = path.Length > 0 ? Decode(path) : null;
        _thumbnail.Child = thumbnail is not null
            ? new Image { Source = thumbnail, Stretch = Stretch.UniformToFill }
            : new TextBlock { Text = Glyphs.Link, FontFamily = ShelfPalette.Icons, FontSize = 48, Foreground = _palette.Muted, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    }

    // ----- Keys -----

    private void OnKey(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (KeyGestures.IsModifier(key)) return;
        var gesture = KeyGestures.FromEvent(e);
        if (gesture.Length == 0) return;
        var actions = _host.Keymap.Resolve(gesture);

        if (IsTextEditing)
        {
            if (key == Key.Escape || actions.Contains("finishEditing"))
            {
                FinishEditing();
                e.Handled = true;
                return;
            }
            // While typing, only Ctrl shortcuts reach pinning and the palette.
            if (gesture.StartsWith("Ctrl+", StringComparison.Ordinal))
                foreach (var action in actions)
                    if (action is "pin" or "pinnedOnly" or "actions" && _host.Forward(action, gesture))
                    {
                        e.Handled = true;
                        return;
                    }
            return;
        }

        // A selection inside the text copies just that fragment.
        if (_built == ItemKind.Text && actions.Contains("copy") && gesture == "Ctrl+C" && _text.SelectionLength > 0)
        {
            if (_host.CopyText(_text.SelectedText)) FlashCopied();
            e.Handled = true;
            return;
        }

        // In an image preview, Ctrl+Z / Ctrl+Shift+Z step through this session's markup and crop results.
        if (_built == ItemKind.Image && gesture is "Ctrl+Z" or "Ctrl+Shift+Z" or "Ctrl+Y"
            && _host.Forward(gesture == "Ctrl+Z" ? "markupUndo" : "markupRedo", gesture))
        {
            e.Handled = true;
            return;
        }

        foreach (var action in actions)
        {
            var handled = action switch
            {
                "close" or "preview" => CloseFromKey(),
                "edit" when _built == ItemKind.Text => Run(BeginEditing),
                "open" when _built == ItemKind.Link => Run(OpenLink),
                "finishEditing" => false, // shares Ctrl+Enter with paste outside editing
                _ => _host.Forward(action, gesture),
            };
            if (handled)
            {
                e.Handled = true;
                return;
            }
        }
    }

    private bool CloseFromKey()
    {
        Close();
        return true;
    }

    private static bool Run(Action action)
    {
        action();
        return true;
    }
}
