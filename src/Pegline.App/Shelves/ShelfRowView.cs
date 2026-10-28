using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Pegline.App.Localization;
using Pegline.Core.Models;
using Pegline.Core.Storage;

namespace Pegline.App.Shelves;

internal sealed record RowState(
    IReadOnlyList<ClipItem> Page,
    int Offset,
    int Total,
    int PageSize,
    bool Active,
    Guid? Focused,
    IReadOnlySet<Guid> Selected,
    bool PinnedOnly,
    double Width);

/// <summary>One shelf: a header chip and a sagging rope with cards pegged to it.</summary>
internal sealed class ShelfRowView : Grid
{
    private const double SidePadding = 28;
    private const double RopeTop = 16;
    private const double Sag = 12;

    private readonly ItemKind _kind;
    private readonly LibraryStore _store;
    private readonly ThumbnailCache _thumbnails;
    private readonly Canvas _canvas = new() { ClipToBounds = false };
    private readonly StackPanel _header = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(SidePadding, 8, SidePadding, 0) };
    private readonly Dictionary<Guid, FrameworkElement> _cards = [];

    public ShelfRowView(ItemKind kind, LibraryStore store, ThumbnailCache thumbnails)
    {
        _kind = kind;
        _store = store;
        _thumbnails = thumbnails;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Children.Add(_header);
        SetRow(_canvas, 1);
        Children.Add(_canvas);
        Background = Brushes.Transparent;
        MouseWheel += (_, e) =>
        {
            PageRequested?.Invoke(this, e.Delta < 0 ? 1 : -1);
            e.Handled = true;
        };
    }

    public ItemKind Kind => _kind;

    public ShelfPalette Palette { get; set; } = ShelfPalette.For(ShelfTheme.Soft);

    public CardMetrics Metrics { get; set; } = CardMetrics.For(CardSize.Medium);

    public event EventHandler<(ClipItem Item, MouseButtonEventArgs Args)>? CardClicked;

    public event EventHandler<(ClipItem Item, MouseButtonEventArgs Args)>? CardDoubleClicked;

    public event EventHandler? HeaderClicked;

    /// <summary>The pointer dragged a card past the system threshold; the handler runs the drag.</summary>
    public event EventHandler<ClipItem>? CardDragged;

    public event EventHandler<int>? PageRequested;

    private Guid? _pressed;
    private Point _pressPoint;

    /// <summary>A recording's camera mark and (trimmed) length over its poster frame.</summary>
    public static Border DurationBadge(ClipItem item)
    {
        var text = new TextBlock { FontSize = 10, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
        text.Inlines.Add(new System.Windows.Documents.Run("") { FontFamily = ShelfPalette.Icons });
        text.Inlines.Add(new System.Windows.Documents.Run(" " + TimeSpan.FromSeconds(item.ShownDuration).ToString(@"m\:ss")) { FontFamily = new FontFamily("Segoe UI") });
        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xC0, 0, 0, 0)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6, 2, 6, 2),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 6, 6),
            Child = text,
        };
    }

    public static string Title(ItemKind kind) => Loc.T(kind switch
    {
        ItemKind.Image => "Images",
        ItemKind.Text => "Texts",
        _ => "Links",
    });

    public static string Glyph(ItemKind kind) => kind switch
    {
        ItemKind.Image => "",
        ItemKind.Text => "",
        _ => "",
    };

    /// <summary>Card positions in this row, used by the window to compute how many fit.</summary>
    public static int Fit(double width, CardMetrics metrics) =>
        Math.Max(1, (int)Math.Floor((width - SidePadding * 2 + metrics.Gap) / (metrics.Width + metrics.Gap)));

    public void Render(RowState state)
    {
        RenderHeader(state);
        _canvas.Children.Clear();
        _cards.Clear();
        _canvas.Width = state.Width;
        _canvas.Height = Metrics.RowHeight;

        var width = state.Width;
        var rope = new Path
        {
            Stroke = Palette.Rope,
            StrokeThickness = 1.6,
            Data = new PathGeometry([new PathFigure(new Point(0, RopeTop), [new QuadraticBezierSegment(new Point(width / 2, RopeTop + Sag * 2), new Point(width, RopeTop), true)], false)]),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 4, ShadowDepth = 1.5, Opacity = 0.45, Color = Colors.Black },
        };
        _canvas.Children.Add(rope);

        if (state.Page.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = state.Total == 0 && state.PinnedOnly ? Loc.T("No matching items") : "",
                Foreground = Palette.Muted,
                FontSize = 12,
            };
            Canvas.SetLeft(empty, SidePadding);
            Canvas.SetTop(empty, RopeTop + Sag + 18);
            _canvas.Children.Add(empty);
            return;
        }

        for (var i = 0; i < state.Page.Count; i++)
        {
            var item = state.Page[i];
            var left = SidePadding + i * (Metrics.Width + Metrics.Gap);
            var center = left + Metrics.Width / 2;
            var t = Math.Clamp(center / width, 0, 1);
            var ropeY = RopeTop + 4 * Sag * t * (1 - t);
            var top = ropeY + 9;

            var focused = state.Focused == item.Id;
            var selected = state.Selected.Contains(item.Id);
            var card = BuildCard(item, focused, selected, state.Active ? i + 1 : 0);
            Canvas.SetLeft(card, left);
            Canvas.SetTop(card, top);
            _canvas.Children.Add(card);
            _cards[item.Id] = card;

            var caption = Caption(item);
            if (caption.Length > 0)
            {
                var label = new TextBlock
                {
                    Text = caption,
                    Width = Metrics.Width,
                    TextAlignment = TextAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = focused ? Palette.Foreground : Palette.Muted,
                    FontSize = 11.5,
                    FontWeight = focused ? FontWeights.SemiBold : FontWeights.Normal,
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(label, left);
                Canvas.SetTop(label, top + Metrics.Height + 7);
                _canvas.Children.Add(label);
            }

            var peg = BuildPeg();
            Canvas.SetLeft(peg, center - 5);
            Canvas.SetTop(peg, ropeY - 9);
            _canvas.Children.Add(peg);
        }
    }

    private void RenderHeader(RowState state)
    {
        _header.Children.Clear();
        var chip = new Border
        {
            CornerRadius = new CornerRadius(11),
            Background = state.Active ? Palette.ChipActive : Palette.Chip,
            Padding = new Thickness(10, 3, 11, 4),
            Cursor = Cursors.Hand,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new TextBlock { Text = Glyph(_kind), FontFamily = ShelfPalette.Icons, FontSize = 12, Foreground = Palette.Foreground, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 6, 0) },
                    new TextBlock { Text = Title(_kind), FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Palette.Foreground, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = state.Total > 0 ? "  " + state.Total : "", FontSize = 11.5, Foreground = Palette.Muted, VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        chip.MouseLeftButtonUp += (_, e) =>
        {
            HeaderClicked?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        };
        _header.Children.Add(chip);

        if (state.PinnedOnly)
            _header.Children.Add(Badge("", Loc.T("Show pinned items")));

        if (state.Total > state.PageSize)
        {
            var pages = (state.Total + state.PageSize - 1) / state.PageSize;
            var page = Math.Min(pages, state.Offset / Math.Max(1, state.PageSize) + 1);
            if (state.Offset + state.PageSize >= state.Total) page = pages;
            _header.Children.Add(PageButton("", -1));
            _header.Children.Add(new TextBlock { Text = $"{page}/{pages}", Foreground = Palette.Muted, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 2, 0) });
            _header.Children.Add(PageButton("", 1));
        }
    }

    private Border Badge(string glyph, string tooltip) => new()
    {
        Margin = new Thickness(8, 0, 0, 0),
        CornerRadius = new CornerRadius(10),
        Background = Palette.Chip,
        Padding = new Thickness(7, 3, 7, 3),
        ToolTip = tooltip,
        Child = new TextBlock { Text = glyph, FontFamily = ShelfPalette.Icons, FontSize = 11, Foreground = Palette.Accent },
    };

    private Border PageButton(string glyph, int direction)
    {
        var button = new Border
        {
            Margin = new Thickness(direction < 0 ? 10 : 0, 0, 0, 0),
            Padding = new Thickness(6, 4, 6, 4),
            CornerRadius = new CornerRadius(8),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = new TextBlock { Text = glyph, FontFamily = ShelfPalette.Icons, FontSize = 10, Foreground = Palette.Muted },
        };
        button.MouseLeftButtonUp += (_, e) =>
        {
            PageRequested?.Invoke(this, direction);
            e.Handled = true;
        };
        return button;
    }

    private FrameworkElement BuildPeg()
    {
        // A small clothespin: two jaws with a spring line, drawn over the rope.
        var peg = new Grid { Width = 10, Height = 26, IsHitTestVisible = false };
        peg.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(2.5),
            Background = Palette.Peg,
            BorderBrush = Palette.PegShade,
            BorderThickness = new Thickness(0.8),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 3, ShadowDepth = 1, Opacity = 0.5, Color = Colors.Black },
        });
        peg.Children.Add(new Rectangle { Width = 0.9, Fill = Palette.PegShade, Margin = new Thickness(0, 3, 0, 3), HorizontalAlignment = HorizontalAlignment.Center });
        peg.Children.Add(new Rectangle { Height = 1.4, Fill = Palette.PegShade, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(1, 9, 1, 0) });
        return peg;
    }

    private FrameworkElement BuildCard(ClipItem item, bool focused, bool selected, int number)
    {
        var metrics = Metrics;
        var content = new Grid();
        var card = new Border
        {
            Width = metrics.Width,
            Height = metrics.Height,
            CornerRadius = new CornerRadius(10),
            Background = Palette.CardFor(item.Kind),
            BorderBrush = focused ? Palette.Accent : selected ? Palette.AccentSoft : Palette.CardBorder,
            BorderThickness = new Thickness(focused ? 2.5 : selected ? 2 : 1),
            Cursor = Cursors.Hand,
            Child = content,
            SnapsToDevicePixels = true,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = focused ? 18 : 10,
                ShadowDepth = 3,
                Opacity = focused ? 0.55 : 0.4,
                Color = focused ? Color.FromRgb(0x1D, 0x4E, 0xD8) : Colors.Black,
            },
            RenderTransformOrigin = new Point(0.5, 0),
        };

        switch (item.Kind)
        {
            case ItemKind.Image: BuildImage(content, item); break;
            case ItemKind.Text: BuildText(content, item); break;
            default: BuildLink(content, item); break;
        }

        if (item.Pinned)
            content.Children.Add(new Border
            {
                Width = 20,
                Height = 20,
                CornerRadius = new CornerRadius(10),
                Background = Palette.Accent,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 5, 5, 0),
                Child = new TextBlock { Text = "", FontFamily = ShelfPalette.Icons, FontSize = 10, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            });

        if (number is > 0 and <= 9)
            content.Children.Add(new Border
            {
                MinWidth = 18,
                Height = 18,
                CornerRadius = new CornerRadius(5),
                Background = new SolidColorBrush(Color.FromArgb(0xD0, 0x10, 0x14, 0x1C)),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(6, 0, 0, 6),
                Child = new TextBlock { Text = number.ToString(), FontSize = 10.5, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 1) },
            });

        // Cards hang slightly askew, like real laundry; hovering straightens them.
        var angle = ((item.Id.GetHashCode() & 0x7fffffff) % 7 - 3) * 0.55;
        var rotate = new RotateTransform(focused ? 0 : angle);
        card.RenderTransform = rotate;
        card.MouseEnter += (_, _) => rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(160)) { EasingFunction = new QuadraticEase() });
        card.MouseLeave += (_, _) => rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(focused ? 0 : angle, TimeSpan.FromMilliseconds(260)) { EasingFunction = new ElasticEase { Oscillations = 1, Springiness = 4 } });
        card.MouseLeftButtonUp += (_, e) =>
        {
            if (e.ClickCount == 1 && _pressed == item.Id) CardClicked?.Invoke(this, (item, e));
            _pressed = null;
            e.Handled = true;
        };
        card.MouseLeftButtonDown += (_, e) =>
        {
            _pressed = item.Id;
            _pressPoint = e.GetPosition(this);
            if (e.ClickCount == 2) CardDoubleClicked?.Invoke(this, (item, e));
        };
        card.MouseMove += (_, e) =>
        {
            if (_pressed != item.Id || e.LeftButton != MouseButtonState.Pressed || !Platform.DragOut.Exceeded(_pressPoint, e.GetPosition(this))) return;
            // A drag is not a click: the release that ends it must not copy.
            _pressed = null;
            CardDragged?.Invoke(this, item);
        };
        card.Clip = null;
        return card;
    }

    private void BuildImage(Grid content, ClipItem item)
    {
        var decode = (int)Math.Ceiling(Metrics.Width * 2);
        var source = _thumbnails.Get(_store.Media.PathOf(item.Recording && item.RenderedFile.Length == 0 ? item.ImageFile : item.DisplayFile), decode);
        var image = new Image
        {
            Source = source,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(4),
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        content.Children.Add(new Border { CornerRadius = new CornerRadius(7), ClipToBounds = true, Child = image });
        if (source is null)
            content.Children.Add(new TextBlock { Text = "", FontFamily = ShelfPalette.Icons, FontSize = 26, Foreground = Palette.Muted, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        if (item.Recording) content.Children.Add(DurationBadge(item));
    }

    private void BuildText(Grid content, ClipItem item)
    {
        var panel = new StackPanel { Margin = new Thickness(10, 9, 10, 8) };
        if (item.Name.Length > 0)
            panel.Children.Add(new TextBlock { Text = item.Name, FontWeight = FontWeights.SemiBold, FontSize = 12.5, Foreground = Palette.Foreground, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 3) });
        var preview = item.Text.Replace("\r\n", "\n").Trim('\n');
        if (preview.Length > 600) preview = preview[..600];
        panel.Children.Add(new TextBlock
        {
            Text = preview,
            FontSize = 12,
            LineHeight = 16,
            Foreground = Palette.Foreground,
            Opacity = item.Name.Length > 0 ? 0.8 : 1,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = Metrics.Height - 20 - (item.Name.Length > 0 ? 19 : 0),
        });
        content.Children.Add(panel);
    }

    private void BuildLink(Grid content, ClipItem item)
    {
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var thumbHost = new Border { Margin = new Thickness(6, 6, 6, 0), CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)), ClipToBounds = true };
        var thumb = item.LinkThumbnailFile.Length > 0 ? _thumbnails.Get(_store.Media.PathOf(item.LinkThumbnailFile), (int)Metrics.Width * 2) : null;
        thumbHost.Child = thumb is not null
            ? new Image { Source = thumb, Stretch = Stretch.UniformToFill }
            : new TextBlock { Text = "", FontFamily = ShelfPalette.Icons, FontSize = 22, Foreground = Palette.Muted, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(thumbHost);

        var host = Uri.TryCreate(item.Url, UriKind.Absolute, out var uri) ? uri.Host.Replace("www.", "") : item.Url;
        var title = item.DisplayTitle.Length > 0 ? item.DisplayTitle : item.Url;
        var text = new StackPanel { Margin = new Thickness(9, 5, 9, 7) };
        text.Children.Add(new TextBlock { Text = title, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = Palette.Foreground, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = host, FontSize = 10.5, Foreground = Palette.Muted, TextTrimming = TextTrimming.CharacterEllipsis });
        SetRow(text, 1);
        content.Children.Add(text);
    }

    private static string Caption(ClipItem item) => item.Kind switch
    {
        ItemKind.Image => item.Name,
        _ => "",
    };
}
