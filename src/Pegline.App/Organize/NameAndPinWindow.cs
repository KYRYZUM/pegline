using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Pegline.App.Localization;
using Pegline.App.Platform;
using Pegline.App.Shelves;
using Pegline.Core.Models;
using Pegline.Core.Organize;

namespace Pegline.App.Organize;

/// <summary>
/// "Name and pin…" (Alt+P). Enter saves the name, pins the item and moves to the next one in the frozen batch;
/// Esc leaves without touching the current item. The name is search metadata, never a replacement for content.
/// </summary>
internal sealed class NameAndPinWindow : HostedWindow
{
    private const double SurfaceWidth = 520;
    private const double SurfaceHeight = 192;

    private readonly ShelfPalette _palette;
    private readonly NameAndPinSession _session;
    private readonly TextBox _name = new();
    private readonly TextBlock _placeholder = new();
    private readonly TextBlock _progress = new();
    private readonly TextBlock _context = new();
    private readonly TextBlock _message = new();
    private readonly TextBlock _confirmLabel;
    private bool _finished;

    public NameAndPinWindow(ShelfPalette palette, NameAndPinSession session)
    {
        _palette = palette;
        _session = session;
        Title = Loc.T("Name and pin…");
        var confirm = SurfaceParts.Button(palette, Loc.T("Pin with name"), primary: true, Confirm);
        _confirmLabel = (TextBlock)confirm.Child;
        Content = Build(confirm);
        PreviewKeyDown += OnKey;
        Deactivated += (_, _) =>
        {
            if (_finished || !IsOpen) return;
            RestoreParentFocus = false;
            Finish();
        };
        _name.TextChanged += (_, _) => _message.Visibility = Visibility.Collapsed;
        Load();
    }

    /// <summary>Raised after each item is named, with the id of the next item when the batch continues.</summary>
    public event EventHandler<Guid?>? Advanced;

    public override void Present(Window parent)
    {
        ShowOver(parent, SurfaceWidth, SurfaceHeight);
        Dispatcher.BeginInvoke(FocusName, System.Windows.Threading.DispatcherPriority.Input);
    }

    private UIElement Build(Border confirm)
    {
        var surface = Surface(_palette, 16);
        var panel = new StackPanel { Margin = new Thickness(0, 12, 0, 14) };

        var header = new DockPanel { Margin = new Thickness(18, 0, 14, 0), Height = 24, LastChildFill = false };
        header.Children.Add(new TextBlock { Text = Loc.T("Name and pin…"), FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = _palette.Muted, VerticalAlignment = VerticalAlignment.Center });
        var close = SurfaceParts.HeaderButton(_palette, Glyphs.Close, Loc.T("Close"), Finish);
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        _progress.FontSize = 11;
        _progress.Foreground = _palette.Muted;
        _progress.VerticalAlignment = VerticalAlignment.Center;
        _progress.Margin = new Thickness(10, 0, 8, 0);
        DockPanel.SetDock(_progress, Dock.Right);
        header.Children.Add(_progress);
        panel.Children.Add(header);

        var input = SurfaceParts.Input(_palette, _name, _placeholder, Glyphs.Pin, Loc.T("Add a name for search…"));
        input.Margin = new Thickness(20, 10, 20, 0);
        _name.MaxLength = 200;
        panel.Children.Add(input);

        _context.FontSize = 12;
        _context.Foreground = _palette.Muted;
        _context.Margin = new Thickness(20, 10, 20, 0);
        _context.TextTrimming = TextTrimming.CharacterEllipsis;
        panel.Children.Add(_context);

        _message.FontSize = 11;
        _message.Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
        _message.Margin = new Thickness(20, 6, 20, 0);
        _message.TextWrapping = TextWrapping.Wrap;
        _message.Visibility = Visibility.Collapsed;
        panel.Children.Add(_message);

        var footer = new DockPanel { Margin = new Thickness(18, 14, 18, 0), Height = 28, LastChildFill = false };
        var hints = new StackPanel { Orientation = Orientation.Horizontal };
        hints.Children.Add(SurfaceParts.Keycap(_palette, "Enter"));
        hints.Children.Add(SurfaceParts.Keycap(_palette, "Esc"));
        footer.Children.Add(hints);
        DockPanel.SetDock(confirm, Dock.Right);
        footer.Children.Add(confirm);
        var cancel = SurfaceParts.Button(_palette, Loc.T("Cancel"), primary: false, Finish);
        DockPanel.SetDock(cancel, Dock.Right);
        footer.Children.Add(cancel);
        panel.Children.Add(footer);

        surface.Child = panel;
        return surface;
    }

    private void Load()
    {
        var item = _session.CurrentItem;
        _name.Text = item?.Name ?? "";
        _progress.Text = _session.Count > 1 ? Loc.F("Item {0} of {1}", _session.Index + 1, _session.Count) : "";
        _confirmLabel.Text = Loc.T(_session.HasNext ? "Save and next" : "Pin with name");
        _context.Text = item is null ? "" : Describe(item);
    }

    private static string Describe(ClipItem item)
    {
        var kind = Loc.T(item.Kind switch { ItemKind.Image => "Image", ItemKind.Text => "Text", _ => "Link" });
        string detail;
        if (item.Name.Length > 0) detail = item.Name;
        else
        {
            detail = item.Kind switch
            {
                ItemKind.Text => item.Text.Trim(),
                ItemKind.Image => item.PixelWidth > 0 ? $"{item.PixelWidth} × {item.PixelHeight}" : Loc.T("Image"),
                _ => Uri.TryCreate(item.Url, UriKind.Absolute, out var uri)
                    ? (item.Title.Trim().Length > 0 ? item.Title.Trim() + " · " + uri.Host : uri.Host)
                    : item.Url,
            };
        }
        var line = string.Join(' ', detail.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
        return kind + " · " + (line.Length > 180 ? line[..180] : line);
    }

    private void FocusName()
    {
        Keyboard.Focus(_name);
        _name.SelectAll();
    }

    private void Confirm()
    {
        if (_finished) return;
        try
        {
            if (_session.Confirm(_name.Text))
            {
                Advanced?.Invoke(this, null);
                Finish();
                return;
            }
            Load();
            FocusName();
            Advanced?.Invoke(this, _session.Current);
        }
        catch (InvalidOperationException ex)
        {
            _message.Text = Loc.T(ex.Message);
            _message.Visibility = Visibility.Visible;
            Keyboard.Focus(_name);
        }
    }

    private void Finish()
    {
        _finished = true;
        Close();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        // An input method composing text owns Enter and Esc until it commits.
        if (e.Key == Key.ImeProcessed) return;
        switch (e.Key)
        {
            case Key.Enter:
                // A held Enter must never accept the next item's prefilled name.
                if (!e.IsRepeat) Confirm();
                break;
            case Key.Escape:
                Finish();
                break;
            default:
                return;
        }
        e.Handled = true;
    }
}
