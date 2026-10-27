using System.Windows.Media;
using Pegline.Core.Models;

namespace Pegline.App.Shelves;

/// <summary>Colors and card metrics for the hanging shelves.</summary>
internal sealed class ShelfPalette
{
    public static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public required Brush Panel { get; init; }
    public required Brush PanelBorder { get; init; }
    /// <summary>Preview and palette surfaces: the panel color, nearly opaque.</summary>
    public required Brush Surface { get; init; }
    public required Brush Highlight { get; init; }
    public required Brush Rope { get; init; }
    public required Brush Peg { get; init; }
    public required Brush PegShade { get; init; }
    public required Brush Foreground { get; init; }
    public required Brush Muted { get; init; }
    public required Brush Chip { get; init; }
    public required Brush ChipActive { get; init; }
    public required Brush Accent { get; init; }
    public required Brush AccentSoft { get; init; }
    public required Brush ImageCard { get; init; }
    public required Brush TextCard { get; init; }
    public required Brush LinkCard { get; init; }
    public required Brush CardBorder { get; init; }

    public Brush CardFor(ItemKind kind) => kind switch
    {
        ItemKind.Image => ImageCard,
        ItemKind.Text => TextCard,
        _ => LinkCard,
    };

    public static ShelfPalette For(ShelfTheme theme) => theme == ShelfTheme.Neutral ? Neutral : Soft;

    private static readonly ShelfPalette Soft = new()
    {
        Panel = Frozen(new LinearGradientBrush(Color.FromArgb(0xF2, 0x13, 0x17, 0x20), Color.FromArgb(0xEC, 0x18, 0x1D, 0x28), 90)),
        PanelBorder = Solid(0x30, 0xFF, 0xFF, 0xFF),
        Surface = Solid(0xF8, 0x16, 0x1B, 0x25),
        Highlight = Solid(0x2E, 0x3B, 0x8B, 0xF6),
        Rope = Solid(0xB0, 0xC9, 0xC2, 0xB4),
        Peg = Solid(0xFF, 0xE9, 0xDF, 0xCC),
        PegShade = Solid(0xFF, 0xB8, 0xA8, 0x8C),
        Foreground = Solid(0xFF, 0xEE, 0xF1, 0xF6),
        Muted = Solid(0xFF, 0x96, 0x9F, 0xAE),
        Chip = Solid(0xFF, 0x25, 0x2B, 0x37),
        ChipActive = Solid(0xFF, 0x2F, 0x6F, 0xDB),
        Accent = Solid(0xFF, 0x3B, 0x8B, 0xF6),
        AccentSoft = Solid(0x80, 0x3B, 0x8B, 0xF6),
        ImageCard = Solid(0xFF, 0x22, 0x2B, 0x3A),
        TextCard = Solid(0xFF, 0x2A, 0x2B, 0x33),
        LinkCard = Solid(0xFF, 0x22, 0x30, 0x2F),
        CardBorder = Solid(0x26, 0xFF, 0xFF, 0xFF),
    };

    private static readonly ShelfPalette Neutral = new()
    {
        Panel = Solid(0xE0, 0x26, 0x28, 0x2C),
        PanelBorder = Solid(0x38, 0xFF, 0xFF, 0xFF),
        Surface = Solid(0xF5, 0x2B, 0x2D, 0x32),
        Highlight = Solid(0x2E, 0x60, 0xA5, 0xFA),
        Rope = Solid(0x90, 0xFF, 0xFF, 0xFF),
        Peg = Solid(0xFF, 0xE4, 0xE6, 0xEA),
        PegShade = Solid(0xFF, 0xA9, 0xAE, 0xB6),
        Foreground = Solid(0xFF, 0xF2, 0xF3, 0xF5),
        Muted = Solid(0xFF, 0xA0, 0xA4, 0xAB),
        Chip = Solid(0xFF, 0x34, 0x37, 0x3D),
        ChipActive = Solid(0xFF, 0x55, 0x5B, 0x66),
        Accent = Solid(0xFF, 0x60, 0xA5, 0xFA),
        AccentSoft = Solid(0x80, 0x60, 0xA5, 0xFA),
        ImageCard = Solid(0xFF, 0x30, 0x33, 0x39),
        TextCard = Solid(0xFF, 0x30, 0x33, 0x39),
        LinkCard = Solid(0xFF, 0x30, 0x33, 0x39),
        CardBorder = Solid(0x26, 0xFF, 0xFF, 0xFF),
    };

    private static SolidColorBrush Solid(byte a, byte r, byte g, byte b) => Frozen(new SolidColorBrush(Color.FromArgb(a, r, g, b)));

    private static T Frozen<T>(T brush) where T : Brush
    {
        brush.Freeze();
        return brush;
    }
}

internal readonly record struct CardMetrics(double Width, double Height, double Gap)
{
    public static CardMetrics For(CardSize size) => size switch
    {
        CardSize.Small => new(128, 92, 20),
        CardSize.Large => new(200, 142, 28),
        _ => new(158, 112, 24),
    };

    /// <summary>Rope, peg and caption space around a card.</summary>
    public double RowHeight => Height + 62;
}
