using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Pegline.App.Localization;
using Pegline.App.Platform;
using Pegline.App.Shelves;
using Pegline.Core.Input;
using Pegline.Core.Markup;
using Pegline.Core.Models;
using Pegline.Core.Storage;

namespace Pegline.App.Markup;

/// <summary>
/// Image markup and crop (M / C). Marks are vectors over the untouched original; Ctrl+S saves and keeps editing,
/// Ctrl+Enter or Esc saves and closes. Opened with C it is a crop-only session: Enter applies and saves, Esc leaves.
/// Hold Space and drag (or drag with the middle button) to pan; the wheel zooms from 100% to 400%.
/// </summary>
internal sealed class MarkupWindow : HostedWindow
{
    private const double RequestedWidth = 1280;
    private const double RequestedHeight = 860;

    private static readonly (MarkupTool Tool, string Action, string Glyph, string Label)[] Tools =
    [
        (MarkupTool.Select, "select", "cursor", "Select annotation"),
        (MarkupTool.Pen, "pen", "pen", "Pen"),
        (MarkupTool.Marker, "marker", "highlighter", "Marker"),
        (MarkupTool.Rectangle, "rectangle", "rectangle", "Rectangle"),
        (MarkupTool.Ellipse, "ellipse", "ellipse", "Ellipse"),
        (MarkupTool.Arrow, "arrow", "arrow", "Arrow"),
        (MarkupTool.Line, "line", "lineTool", "Line"),
        (MarkupTool.Text, "annotationText", "font", "Text annotation"),
    ];

    private readonly LibraryStore _store;
    private readonly ClipItem _item;
    private readonly ShelfPalette _palette;
    private readonly Keymap _keymap;
    private readonly bool _cropOnly;
    private readonly BitmapSource _original;
    private readonly MarkupEditor _editor;
    private readonly Grid _viewport = new() { ClipToBounds = true };
    private readonly Canvas _document = new() { ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private readonly MarkupOverlay _overlay;
    private readonly MatrixTransform _view = new();
    private readonly TextBlock _title = new();
    private readonly TextBlock _status = new();
    private readonly StackPanel _editTools = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _cropTools = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly Dictionary<MarkupTool, Border> _toolButtons = [];
    private readonly List<(MarkupColor Color, Border Swatch)> _swatches = [];
    private readonly Border _undoButton;
    private readonly Border _redoButton;
    private readonly Border _deleteButton;
    private TextBox? _textBox;
    private TextRequest? _textRequest;
    private bool _spaceHeld;
    private Point? _panFrom;
    private double _zoom = 1;
    private bool _wasCropping;
    private bool _closing;

    private MarkupWindow(LibraryStore store, ClipItem item, BitmapSource original, MarkupDocument document, ShelfPalette palette, Keymap keymap, bool cropOnly)
    {
        _store = store;
        _item = item;
        _original = original;
        _palette = palette;
        _keymap = keymap;
        _cropOnly = cropOnly;
        _editor = new MarkupEditor(document, MarkupRenderer.MeasureText);
        _overlay = new MarkupOverlay(_editor, palette);
        Title = Loc.T("Image markup");
        _undoButton = IconButton("undo", Loc.T("Undo") + " · Ctrl+Z", _editor.Undo);
        _redoButton = IconButton("redo", Loc.T("Redo") + " · Ctrl+Shift+Z", _editor.Redo);
        _deleteButton = IconButton("trash", Loc.T("Delete annotation") + " · Delete", _editor.DeleteSelected);
        Content = Build();
        _editor.Changed += (_, _) => OnEditorChanged();
        PreviewKeyDown += OnKeyDown;
        PreviewKeyUp += OnKeyUp;
        Deactivated += (_, _) =>
        {
            // Losing focus ends an unfinished stroke or transform without a trace; the document stays.
            EndPan();
            _spaceHeld = false;
            if (_editor.IsGesture) _editor.CancelGesture();
            UpdateCursor();
        };
        Closing += (_, _) =>
        {
            // Every way out keeps the work: closing saves what has not been saved yet.
            if (_closing || DiscardOnClose) return;
            _closing = true;
            CommitText();
            if (_editor.IsDirty) Save(report: false);
        };
        _viewport.SizeChanged += (_, _) => LayoutDocument();
        if (cropOnly) _editor.BeginCrop();
        else _editor.SetTool(MarkupTool.Pen);
        OnEditorChanged();
    }

    /// <summary>Raised after a successful save, with a short message for the host's toast.</summary>
    public event EventHandler<string>? Saved;

    public Guid ItemId => _item.Id;

    /// <summary>Opens the editor for an image, or returns null with the reason in <paramref name="error"/>.</summary>
    public static MarkupWindow? Create(LibraryStore store, ClipItem item, ShelfPalette palette, Keymap keymap, bool cropOnly, out string? error)
    {
        error = null;
        if (item.Kind != ItemKind.Image || item.Recording || item.IsTrashed)
        {
            error = Loc.T("Select one image first");
            return null;
        }
        try
        {
            var path = store.MediaPath(item, original: true);
            if (!File.Exists(path)) throw new InvalidOperationException("This image could not be opened for markup.");
            var original = MarkupRenderer.Load(path);
            MarkupDocument document;
            try
            {
                document = MarkupDocument.Parse(item.AnnotationsJson, item.Crop);
            }
            catch (MarkupDocumentException)
            {
                // A damaged document must not lock the image: start clean over the untouched original.
                document = MarkupDocument.Empty;
            }
            return new MarkupWindow(store, item, original, document, palette, keymap, cropOnly);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or NotSupportedException or FileFormatException or UnauthorizedAccessException)
        {
            error = Loc.T(ex is InvalidOperationException ? ex.Message : "This image could not be opened for markup.");
            return null;
        }
    }

    public override void Present(Window parent)
    {
        ShowOver(parent, RequestedWidth, RequestedHeight);
        Dispatcher.BeginInvoke(() =>
        {
            LayoutDocument();
            Keyboard.Focus(this);
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>Development snapshots render without saving anything.</summary>
    internal bool DiscardOnClose { get; set; }

#if DEBUG
    /// <summary>Development aid: adds sample marks and writes the exported PNG so the renderer can be checked.</summary>
    internal void SeedForSnapshot(string exportPath)
    {
        _editor.Bounds = new Box(0, 0, 400, 400 / _editor.ViewAspect(_original.PixelWidth, _original.PixelHeight));
        void Drag(MarkupTool tool, Vec a, Vec b)
        {
            _editor.SetTool(tool);
            _editor.PointerDown(a);
            _editor.PointerMove(new Vec((a.X + b.X) / 2, (a.Y + b.Y) / 2 + 6));
            _editor.PointerUp(b);
        }
        Drag(MarkupTool.Rectangle, new Vec(20, 10), new Vec(160, 70));
        Drag(MarkupTool.Ellipse, new Vec(200, 10), new Vec(300, 70));
        Drag(MarkupTool.Arrow, new Vec(40, 100), new Vec(240, 40));
        Drag(MarkupTool.Marker, new Vec(30, 85), new Vec(330, 90));
        Drag(MarkupTool.Pen, new Vec(320, 20), new Vec(380, 80));
        _editor.SetTool(MarkupTool.Text);
        if (_editor.PointerDown(new Vec(250, 80)) is { } request) _editor.CommitText(request, "Pegline");
        _editor.SetTool(MarkupTool.Select);
        _editor.PointerDown(new Vec(20, 40));
        _editor.PointerUp(new Vec(20, 40));
        File.WriteAllBytes(exportPath, MarkupRenderer.RenderPng(_original, _editor.Document) ?? []);
        LayoutDocument();
    }

    internal void CropForSnapshot()
    {
        _editor.BeginCrop();
        LayoutDocument();
        var b = _editor.Bounds;
        _editor.PointerDown(new Vec(b.W * 0.2, b.H * 0.25));
        _editor.PointerUp(new Vec(b.W * 0.7, b.H * 0.8));
    }
#endif

    // ----- Layout -----

    private UIElement Build()
    {
        var surface = Surface(_palette, 14);
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var toolbar = new DockPanel { Margin = new Thickness(14, 10, 10, 10), LastChildFill = false };
        _title.FontSize = 12;
        _title.FontWeight = FontWeights.SemiBold;
        _title.Foreground = _palette.Muted;
        _title.VerticalAlignment = VerticalAlignment.Center;
        _title.Margin = new Thickness(0, 0, 14, 0);
        toolbar.Children.Add(_title);

        var close = SurfaceParts.HeaderButton(_palette, Glyphs.Close, Loc.T("Done") + " · Esc", Done);
        close.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(close, Dock.Right);
        toolbar.Children.Add(close);
        DockPanel.SetDock(_actions, Dock.Right);
        toolbar.Children.Add(_actions);

        // Editing tools: crop, the eight tools, colors, style, undo/redo, delete.
        _editTools.Children.Add(IconButton("crop", Loc.T("Crop image") + " · " + Hint("crop"), _editor.BeginCrop));
        _editTools.Children.Add(Separator());
        foreach (var (tool, action, glyph, label) in Tools)
        {
            var button = IconButton(glyph, Loc.T(label) + " · " + Hint(action), () => SelectTool(tool));
            _toolButtons[tool] = button;
            _editTools.Children.Add(button);
        }
        _editTools.Children.Add(Separator());
        foreach (var color in MarkupColor.Palette)
        {
            var swatch = new Border
            {
                Width = 18,
                Height = 18,
                Margin = new Thickness(2, 0, 2, 0),
                CornerRadius = new CornerRadius(9),
                Background = MarkupRenderer.BrushFor(color),
                BorderThickness = new Thickness(2),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = Loc.T("Annotation color"),
            };
            swatch.MouseLeftButtonUp += (_, e) =>
            {
                CommitText();
                _editor.ApplyStyle(color: color);
                e.Handled = true;
            };
            _swatches.Add((color, swatch));
            _editTools.Children.Add(swatch);
        }
        _editTools.Children.Add(IconButton("sliders", Loc.T("Annotation style"), ShowStyleMenu));
        _editTools.Children.Add(Separator());
        _editTools.Children.Add(_undoButton);
        _editTools.Children.Add(_redoButton);
        _editTools.Children.Add(_deleteButton);
        _editTools.Children.Add(IconButton("eraser", Loc.T("Clear annotations"), () =>
        {
            CommitText();
            _editor.Clear();
        }));
        toolbar.Children.Add(_editTools);

        _cropTools.Children.Add(TextButton(Loc.T("Reset crop"), primary: false, _editor.ResetCropSelection));
        toolbar.Children.Add(_cropTools);
        root.Children.Add(toolbar);

        _viewport.Background = new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0));
        _viewport.Margin = new Thickness(10, 0, 10, 0);
        _image.Source = _original;
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        _document.RenderTransform = _view;
        _document.Children.Add(_image);
        _document.Children.Add(_overlay);
        _viewport.Children.Add(_document);
        _overlay.MouseLeftButtonDown += OnPointerDown;
        _overlay.MouseMove += OnPointerMove;
        _overlay.MouseLeftButtonUp += OnPointerUp;
        _overlay.MouseDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Middle) return;
            BeginPan(e);
            e.Handled = true;
        };
        _overlay.MouseUp += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Middle) EndPan();
        };
        _viewport.MouseWheel += OnWheel;
        Grid.SetRow(_viewport, 1);
        root.Children.Add(_viewport);

        var footer = new DockPanel { Margin = new Thickness(16, 8, 16, 10), LastChildFill = true };
        _status.FontSize = 11.5;
        _status.Foreground = _palette.Muted;
        _status.TextTrimming = TextTrimming.CharacterEllipsis;
        footer.Children.Add(_status);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        surface.Child = root;
        return surface;
    }

    /// <summary>Fits the document (the cropped image, or the whole original while cropping) into the viewport.</summary>
    private void LayoutDocument()
    {
        var available = new Size(Math.Max(40, _viewport.ActualWidth - 32), Math.Max(40, _viewport.ActualHeight - 32));
        var aspect = _editor.ViewAspect(_original.PixelWidth, _original.PixelHeight);
        var naturalWidth = _original.PixelWidth * (_editor.IsCropping || _editor.Document.Crop is null ? 1 : _editor.Document.Crop.Width) * 2;
        var width = Math.Min(Math.Min(available.Width, available.Height * aspect), Math.Max(80, naturalWidth));
        var height = width / aspect;
        _document.Width = width;
        _document.Height = height;
        _editor.Bounds = new Box(0, 0, width, height);
        _overlay.Width = width;
        _overlay.Height = height;
        PlaceImage();
        ResetZoom();
    }

    private void PlaceImage()
    {
        var frame = _editor.ImageFrame;
        Canvas.SetLeft(_image, frame.X);
        Canvas.SetTop(_image, frame.Y);
        _image.Width = frame.W;
        _image.Height = frame.H;
        if (_textBox is not null && _textRequest is not null) PlaceTextBox();
    }

    private void OnEditorChanged()
    {
        if (_editor.IsCropping != _wasCropping)
        {
            _wasCropping = _editor.IsCropping;
            LayoutDocument();
        }
        else PlaceImage();
        _overlay.InvalidateVisual();
        var cropping = _editor.IsCropping;
        _title.Text = Loc.T(cropping ? "Crop image" : "Image markup");
        _editTools.Visibility = cropping ? Visibility.Collapsed : Visibility.Visible;
        _cropTools.Visibility = cropping ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (tool, button) in _toolButtons)
            button.Background = tool == _editor.Tool ? _palette.Highlight : Brushes.Transparent;
        foreach (var (color, swatch) in _swatches)
            swatch.BorderBrush = color == _editor.Color ? _palette.Foreground : Brushes.Transparent;
        _undoButton.Opacity = _editor.CanUndo ? 1 : 0.4;
        _redoButton.Opacity = _editor.CanRedo ? 1 : 0.4;
        _deleteButton.Opacity = _editor.SelectedId is null ? 0.4 : 1;
        RebuildActions();
        UpdateCursor();
        if (_status.Tag is not "error") _status.Text = HintText();
    }

    private void RebuildActions()
    {
        _actions.Children.Clear();
        if (_editor.IsCropping)
        {
            _actions.Children.Add(TextButton(Loc.T("Cancel crop"), primary: false, CancelCrop));
            _actions.Children.Add(TextButton(Loc.T("Apply crop"), primary: true, ApplyCrop));
        }
        else
        {
            _actions.Children.Add(TextButton(Loc.T("Save"), primary: false, () => Save(report: true)));
            _actions.Children.Add(TextButton(Loc.T("Done"), primary: true, Done));
        }
    }

    private string HintText()
    {
        if (_editor.IsCropping) return Loc.T("Drag to choose the area · Enter applies · Esc cancels");
        var parts = new List<string> { Loc.T("Shift keeps lines straight and shapes even"), Loc.T("Space + drag pans · wheel zooms") };
        parts.Add(_editor.IsDirty ? Loc.T("Unsaved changes") : Loc.T("Saved"));
        return string.Join("   ·   ", parts);
    }

    private void ShowError(string text)
    {
        _status.Text = text;
        _status.Tag = "error";
        _status.Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
    }

    private void ClearError()
    {
        if (_status.Tag is not "error") return;
        _status.Tag = null;
        _status.Foreground = _palette.Muted;
        _status.Text = HintText();
    }

    // ----- Buttons and menus -----

    private string Hint(string action) => KeyGestures.Display(_keymap.Primary(action));

    private Border IconButton(string glyph, string tooltip, Action click)
    {
        var button = new Border
        {
            Width = 32,
            Height = 30,
            Margin = new Thickness(1, 0, 1, 0),
            CornerRadius = new CornerRadius(7),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = tooltip,
            Child = new TextBlock { Text = Glyphs.For(glyph), FontFamily = ShelfPalette.Icons, FontSize = 15, Foreground = _palette.Foreground, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        button.MouseLeftButtonUp += (_, e) =>
        {
            click();
            Keyboard.Focus(this);
            e.Handled = true;
        };
        return button;
    }

    private Border TextButton(string text, bool primary, Action click)
    {
        var button = SurfaceParts.Button(_palette, text, primary, () =>
        {
            click();
            if (IsOpen && !_closing) Keyboard.Focus(this);
        });
        button.VerticalAlignment = VerticalAlignment.Center;
        return button;
    }

    private Border Separator() => new() { Width = 1, Height = 20, Margin = new Thickness(8, 0, 8, 0), Background = _palette.PanelBorder, VerticalAlignment = VerticalAlignment.Center };

    private void ShowStyleMenu()
    {
        CommitText();
        var menu = new ContextMenu { PlacementTarget = _editTools, Placement = PlacementMode.Bottom };
        var marker = _editor.Tool == MarkupTool.Marker || _editor.Selected?.Tool == MarkupTool.Marker;
        var widths = marker ? MarkupStyle.MarkerWidths : MarkupStyle.Widths;
        string[] widthNames = ["Thin", "Medium", "Thick", "Heavy"];
        var width = new MenuItem { Header = Loc.T("Stroke width") };
        for (var i = 0; i < widths.Length; i++)
        {
            var value = widths[i];
            var entry = new MenuItem { Header = Loc.T(widthNames[i]), IsCheckable = true, IsChecked = Math.Abs(_editor.LineWidth - value) < 1e-9 };
            entry.Click += (_, _) => _editor.ApplyStyle(width: value);
            width.Items.Add(entry);
        }
        menu.Items.Add(width);
        var fill = new MenuItem { Header = Loc.T("Fill shape"), IsCheckable = true, IsChecked = _editor.Filled };
        fill.Click += (_, _) => _editor.ApplyStyle(fill: !_editor.Filled);
        menu.Items.Add(fill);
        string[] sizeNames = ["Small", "Medium", "Large", "Very large"];
        var font = new MenuItem { Header = Loc.T("Annotation text size") };
        for (var i = 0; i < MarkupStyle.FontSizes.Length; i++)
        {
            var value = MarkupStyle.FontSizes[i];
            var entry = new MenuItem { Header = Loc.T(sizeNames[i]), IsCheckable = true, IsChecked = Math.Abs(_editor.FontSize - value) < 1e-9 };
            entry.Click += (_, _) => _editor.ApplyStyle(fontSize: value);
            font.Items.Add(entry);
        }
        menu.Items.Add(font);
        menu.IsOpen = true;
    }

    // ----- Commands -----

    private void SelectTool(MarkupTool tool)
    {
        CommitText();
        _editor.SetTool(tool);
    }

    private void ApplyCrop()
    {
        if (!_editor.ApplyCrop()) return;
        if (_cropOnly && Save(report: true)) Finish();
    }

    private void CancelCrop()
    {
        _editor.CancelCrop();
        if (_cropOnly) Finish();
    }

    /// <summary>Renders and stores the result. Failures keep the draft open with the reason shown.</summary>
    private bool Save(bool report)
    {
        CommitText();
        if (!_editor.IsDirty) return true;
        try
        {
            var document = _editor.Document;
            var png = MarkupRenderer.RenderPng(_original, document);
            _store.SaveImageEdit(_item.Id, png, document.ElementsJson(), document.Crop);
            _editor.MarkSaved();
            ClearError();
            _status.Text = HintText();
            if (report) Saved?.Invoke(this, Loc.T("Markup saved"));
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or MarkupDocumentException or IOException or OutOfMemoryException or UnauthorizedAccessException)
        {
            App.Log(ex);
            ShowError(Loc.T("The marked image could not be saved.") + " " + Loc.T(ex.Message));
            return false;
        }
    }

    private void Done()
    {
        if (_editor.IsCropping)
        {
            CancelCrop();
            return;
        }
        if (!Save(report: true)) return;
        Finish();
    }

    private void Finish()
    {
        _closing = true;
        Close();
    }

    // ----- Keyboard -----

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (KeyGestures.IsModifier(key)) return;
        var gesture = KeyGestures.FromEvent(e);
        if (_textBox is not null)
        {
            if (gesture == "Ctrl+Enter")
            {
                CommitText();
                e.Handled = true;
            }
            else if (key == Key.Escape)
            {
                CancelText();
                e.Handled = true;
            }
            return; // typing belongs to the text field
        }
        if (key == Key.Space)
        {
            if (!e.IsRepeat)
            {
                _spaceHeld = true;
                UpdateCursor();
            }
            e.Handled = true;
            return;
        }
        e.Handled = true;
        if (_editor.IsCropping)
        {
            switch (gesture)
            {
                case "Enter": ApplyCrop(); break;
                case "Escape": CancelCrop(); break;
                default: e.Handled = false; break;
            }
            return;
        }
        switch (gesture)
        {
            case "Ctrl+S": Save(report: true); return;
            case "Ctrl+Z": _editor.Undo(); return;
            case "Ctrl+Shift+Z" or "Ctrl+Y": _editor.Redo(); return;
            case "Delete" or "Backspace": _editor.DeleteSelected(); return;
            case "Escape": Done(); return;
        }
        foreach (var action in _keymap.Resolve(gesture))
        {
            switch (action)
            {
                case "finishEditing" or "markup": Done(); return;
                case "crop": _editor.BeginCrop(); return;
            }
            var tool = Tools.FirstOrDefault(t => t.Action == action);
            if (tool.Action is not null)
            {
                SelectTool(tool.Tool);
                return;
            }
        }
        e.Handled = false;
    }

    private void OnKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || _textBox is not null) return;
        _spaceHeld = false;
        UpdateCursor();
        e.Handled = true;
    }

    // ----- Pointer -----

    private void OnPointerDown(object sender, MouseButtonEventArgs e)
    {
        Keyboard.Focus(this);
        if (_spaceHeld)
        {
            BeginPan(e);
            e.Handled = true;
            return;
        }
        if (_textBox is not null)
        {
            CommitText();
            e.Handled = true;
            return;
        }
        ClearError();
        var p = e.GetPosition(_overlay);
        var request = _editor.PointerDown(new Vec(p.X, p.Y), e.ClickCount);
        if (request is not null) BeginText(request);
        else _overlay.CaptureMouse();
        e.Handled = true;
    }

    private void OnPointerMove(object sender, MouseEventArgs e)
    {
        if (_panFrom is { } from)
        {
            var now = e.GetPosition(_viewport);
            Pan(now.X - from.X, now.Y - from.Y);
            _panFrom = now;
            return;
        }
        if (!_overlay.IsMouseCaptured) return;
        var p = e.GetPosition(_overlay);
        _editor.PointerMove(new Vec(p.X, p.Y), Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
    }

    private void OnPointerUp(object sender, MouseButtonEventArgs e)
    {
        if (_panFrom is not null)
        {
            EndPan();
            return;
        }
        if (!_overlay.IsMouseCaptured) return;
        var p = e.GetPosition(_overlay);
        _editor.PointerUp(new Vec(p.X, p.Y), Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        _overlay.ReleaseMouseCapture();
    }

    private void UpdateCursor() => _overlay.Cursor =
        _spaceHeld || _panFrom is not null ? Cursors.Hand
        : _editor.IsCropping ? Cursors.Cross
        : _editor.Tool switch { MarkupTool.Select => Cursors.Arrow, MarkupTool.Text => Cursors.IBeam, _ => Cursors.Cross };

    // ----- Zoom and pan -----

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            Pan(e.Delta / 3.0, 0);
            e.Handled = true;
            return;
        }
        var next = Math.Clamp(_zoom * (e.Delta > 0 ? 1.116 : 1 / 1.116), 1, 4);
        if (Math.Abs(next - _zoom) < 1e-6) return;
        // Keep the point under the pointer still while zooming.
        var q = e.GetPosition(_document);
        var m = _view.Matrix;
        var offsetX = m.OffsetX + q.X * (_zoom - next);
        var offsetY = m.OffsetY + q.Y * (_zoom - next);
        _zoom = next;
        _view.Matrix = next <= 1 ? Matrix.Identity : new Matrix(next, 0, 0, next, offsetX, offsetY);
        if (next <= 1) _zoom = 1;
        ClampPan();
        e.Handled = true;
    }

    private void Pan(double dx, double dy)
    {
        if (_zoom <= 1) return;
        var m = _view.Matrix;
        _view.Matrix = new Matrix(m.M11, 0, 0, m.M22, m.OffsetX + dx, m.OffsetY + dy);
        ClampPan();
    }

    /// <summary>Keeps the zoomed document covering its own original area so it cannot be lost off-screen.</summary>
    private void ClampPan()
    {
        var m = _view.Matrix;
        var w = _document.Width;
        var h = _document.Height;
        var x = Math.Clamp(m.OffsetX, w - w * _zoom, 0);
        var y = Math.Clamp(m.OffsetY, h - h * _zoom, 0);
        _view.Matrix = new Matrix(m.M11, 0, 0, m.M22, x, y);
    }

    private void ResetZoom()
    {
        _zoom = 1;
        _view.Matrix = Matrix.Identity;
    }

    private void BeginPan(MouseButtonEventArgs e)
    {
        if (_zoom <= 1) return;
        _panFrom = e.GetPosition(_viewport);
        _overlay.CaptureMouse();
        UpdateCursor();
    }

    private void EndPan()
    {
        if (_panFrom is null) return;
        _panFrom = null;
        _overlay.ReleaseMouseCapture();
        UpdateCursor();
    }

    // ----- Text marks -----

    private void BeginText(TextRequest request)
    {
        _textRequest = request;
        var frame = _editor.ImageFrame;
        var size = Math.Max(6, (request.Existing?.FontSize ?? _editor.FontSize) * Math.Min(frame.W, frame.H));
        _textBox = new TextBox
        {
            Text = request.Existing?.Text ?? "",
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontWeight = FontWeights.Medium,
            FontSize = size,
            Foreground = MarkupRenderer.BrushFor(request.Existing?.Color ?? _editor.Color),
            CaretBrush = _palette.Foreground,
            Background = new SolidColorBrush(Color.FromArgb(0x99, 0x10, 0x14, 0x1C)),
            BorderBrush = _palette.Accent,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(0),
            AcceptsReturn = true,
            MinWidth = 120,
            MaxLength = MarkupDocument.MaxTextLength,
        };
        _document.Children.Add(_textBox);
        _overlay.EditingTextId = request.Existing?.Id;
        PlaceTextBox();
        _status.Text = Loc.T("Ctrl+Enter finishes the text · Esc cancels");
        Dispatcher.BeginInvoke(() =>
        {
            if (_textBox is null) return;
            Keyboard.Focus(_textBox);
            _textBox.SelectAll();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void PlaceTextBox()
    {
        if (_textBox is null || _textRequest is null) return;
        var at = MarkupEditor.Position(_textRequest.At, _editor.ImageFrame);
        // The border sits just outside the text so the saved mark lands where the typed text was.
        Canvas.SetLeft(_textBox, at.X - 1);
        Canvas.SetTop(_textBox, at.Y - 1);
        _textBox.MaxWidth = Math.Max(120, _document.Width - at.X);
    }

    private void CommitText()
    {
        if (_textBox is null || _textRequest is null) return;
        var (box, request) = (_textBox, _textRequest);
        RemoveTextBox();
        _editor.CommitText(request, box.Text.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    private void CancelText()
    {
        RemoveTextBox();
        _overlay.InvalidateVisual();
        _status.Text = HintText();
    }

    private void RemoveTextBox()
    {
        if (_textBox is not null) _document.Children.Remove(_textBox);
        _textBox = null;
        _textRequest = null;
        _overlay.EditingTextId = null;
        Keyboard.Focus(this);
    }
}

/// <summary>Draws the marks, the stroke in progress, the selection handles and the crop frame over the image.</summary>
internal sealed class MarkupOverlay(MarkupEditor editor, ShelfPalette palette) : FrameworkElement
{
    public Guid? EditingTextId { get; set; }

    protected override void OnRender(DrawingContext context)
    {
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        // Transparent fill so the whole surface receives the pointer.
        context.DrawRectangle(Brushes.Transparent, null, bounds);
        var frame = editor.ImageFrame;
        var frameRect = new Rect(frame.X, frame.Y, frame.W, frame.H);
        context.PushClip(new RectangleGeometry(bounds));
        MarkupRenderer.Draw(context, editor.Document.Elements.Where(e => e.Id != EditingTextId), frameRect);
        if (editor.Draft is { } draft) MarkupRenderer.Draw(context, [draft], frameRect);
        if (editor.IsCropping) DrawCrop(context, bounds);
        else if (editor.Tool == MarkupTool.Select && EditingTextId is null && editor.Selected is { } selected) DrawSelection(context, editor.ElementBounds(selected, frame));
        context.Pop();
    }

    private void DrawSelection(DrawingContext context, Box box)
    {
        var rect = new Rect(box.X - 4, box.Y - 4, box.W + 8, box.H + 8);
        var dash = new Pen(palette.Accent, 1) { DashStyle = new DashStyle([4, 3], 0) };
        context.DrawRectangle(null, dash, rect);
        var handle = new Pen(palette.Accent, 1);
        foreach (var corner in new[] { rect.TopLeft, rect.TopRight, rect.BottomLeft, rect.BottomRight })
            context.DrawRoundedRectangle(Brushes.White, handle, new Rect(corner.X - 4, corner.Y - 4, 8, 8), 2, 2);
    }

    private void DrawCrop(DrawingContext context, Rect bounds)
    {
        var c = editor.CropBox;
        var box = new Rect(c.X, c.Y, c.W, c.H);
        var shade = new SolidColorBrush(Color.FromArgb(0x8C, 0, 0, 0));
        var outside = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(bounds), new RectangleGeometry(box));
        context.DrawGeometry(shade, null, outside);
        var grid = new Pen(new SolidColorBrush(Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF)), 0.75);
        foreach (var f in new[] { 1 / 3.0, 2 / 3.0 })
        {
            context.DrawLine(grid, new Point(box.X + box.Width * f, box.Y), new Point(box.X + box.Width * f, box.Bottom));
            context.DrawLine(grid, new Point(box.X, box.Y + box.Height * f), new Point(box.Right, box.Y + box.Height * f));
        }
        context.DrawRectangle(null, new Pen(Brushes.White, 1.5), box);
        foreach (var point in new[]
        {
            box.TopLeft, new Point(box.X + box.Width / 2, box.Y), box.TopRight, new Point(box.X, box.Y + box.Height / 2),
            new Point(box.Right, box.Y + box.Height / 2), box.BottomLeft, new Point(box.X + box.Width / 2, box.Bottom), box.BottomRight,
        })
            context.DrawRectangle(Brushes.White, null, new Rect(point.X - 3.5, point.Y - 3.5, 7, 7));
    }
}
