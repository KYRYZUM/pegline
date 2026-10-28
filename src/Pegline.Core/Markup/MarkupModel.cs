using System.Text.Json;
using System.Text.Json.Serialization;
using Pegline.Core.Models;

namespace Pegline.Core.Markup;

/// <summary>
/// Image markup is a vector document over the untouched original. Every coordinate is a fraction of the
/// full original image (origin top left), so zoom, screen DPI and crop never move a mark, and the
/// exported PNG is always rendered at the original's resolution.
/// </summary>
public enum MarkupTool
{
    Select,
    Pen,
    Marker,
    Rectangle,
    Ellipse,
    Arrow,
    Line,
    Text,
}

public readonly record struct MarkupPoint(double X, double Y);

public readonly record struct MarkupColor(double R, double G, double B, double A = 1)
{
    public static readonly MarkupColor Red = new(1, 0.23, 0.19);
    public static readonly MarkupColor Orange = new(1, 0.58, 0);
    public static readonly MarkupColor Yellow = new(1, 0.84, 0.04);
    public static readonly MarkupColor Green = new(0.19, 0.82, 0.35);
    public static readonly MarkupColor Blue = new(0.04, 0.48, 1);
    public static readonly MarkupColor Purple = new(0.69, 0.32, 0.87);
    public static readonly MarkupColor White = new(1, 1, 1);
    public static readonly MarkupColor Black = new(0, 0, 0);
    public static readonly IReadOnlyList<MarkupColor> Palette = [Red, Orange, Yellow, Green, Blue, Purple, White, Black];

    internal bool IsValid => new[] { R, G, B, A }.All(v => double.IsFinite(v) && v is >= 0 and <= 1);
}

public sealed record MarkupElement
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public MarkupTool Tool { get; init; }
    public IReadOnlyList<MarkupPoint> Points { get; init; } = [];
    public MarkupColor Color { get; init; } = MarkupColor.Red;

    /// <summary>Fraction of the shorter displayed image side, independent of zoom.</summary>
    public double LineWidth { get; init; } = MarkupStyle.Widths[1];

    public bool Fill { get; init; }
    public string? Text { get; init; }

    /// <summary>Fraction of the shorter displayed image side.</summary>
    public double FontSize { get; init; } = MarkupStyle.FontSizes[1];

    /// <summary>The marker's translucency multiplies the chosen color's alpha.</summary>
    public double Opacity => Color.A * (Tool == MarkupTool.Marker ? 0.32 : 1);
}

public static class MarkupStyle
{
    public static readonly double[] Widths = [0.002, 0.004, 0.008, 0.016];
    public static readonly double[] MarkerWidths = [0.02, 0.03, 0.04, 0.06];
    public static readonly double[] FontSizes = [0.025, 0.05, 0.08, 0.12];
}

public sealed class MarkupDocumentException(string message) : Exception(message);

/// <summary>The marks and the optional non-destructive crop of one image.</summary>
public sealed record MarkupDocument
{
    public const int MaxElements = 2_000;
    public const int MaxPoints = 200_000;
    public const int MaxPointsPerElement = 20_000;
    public const int MaxTextLength = 4_000;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public IReadOnlyList<MarkupElement> Elements { get; init; } = [];
    public CropRect? Crop { get; init; }

    public static MarkupDocument Empty { get; } = new();

    /// <summary>No marks and no (or a full-image) crop: the original is shown as is.</summary>
    public bool IsEmpty => Elements.Count == 0 && (Crop is null || IsFull(Crop));

    public int PointCount => Elements.Sum(e => e.Points.Count);

    public static bool IsFull(CropRect crop) => crop.X <= 0 && crop.Y <= 0 && crop.Width >= 1 && crop.Height >= 1;

    public static bool IsValidCrop(CropRect crop) =>
        new[] { crop.X, crop.Y, crop.Width, crop.Height }.All(double.IsFinite)
        && crop.X >= 0 && crop.Y >= 0 && crop.Width > 0 && crop.Height > 0
        && crop.X + crop.Width <= 1 + 1e-9 && crop.Y + crop.Height <= 1 + 1e-9;

    /// <summary>Throws <see cref="MarkupDocumentException"/> unless every limit and every element's shape holds.</summary>
    public MarkupDocument Validated()
    {
        if (Crop is { } crop && !IsValidCrop(crop)) throw new MarkupDocumentException("The crop is outside the image.");
        if (Elements.Count > MaxElements || Elements.Select(e => e.Id).Distinct().Count() != Elements.Count || PointCount > MaxPoints)
            throw new MarkupDocumentException("The image markup document is invalid.");
        foreach (var e in Elements)
        {
            var ok = e.Points.Count <= MaxPointsPerElement && e.Color.IsValid
                && double.IsFinite(e.LineWidth) && e.LineWidth is >= 0.0001 and <= 0.25
                && double.IsFinite(e.FontSize) && e.FontSize is >= 0.005 and <= 0.5
                && e.Points.All(p => double.IsFinite(p.X) && double.IsFinite(p.Y) && p.X is >= 0 and <= 1 && p.Y is >= 0 and <= 1)
                && (e.Text?.Length ?? 0) <= MaxTextLength
                && e.Tool switch
                {
                    MarkupTool.Pen or MarkupTool.Marker => e.Points.Count > 0,
                    MarkupTool.Rectangle or MarkupTool.Ellipse or MarkupTool.Arrow or MarkupTool.Line => e.Points.Count == 2,
                    MarkupTool.Text => e.Points.Count == 1 && !string.IsNullOrWhiteSpace(e.Text),
                    _ => false,
                };
            if (!ok) throw new MarkupDocumentException("The image markup document is invalid.");
        }
        return this;
    }

    /// <summary>The marks as stored on the record (<see cref="ClipItem.AnnotationsJson"/>); the crop is stored beside them.</summary>
    public string ElementsJson() => JsonSerializer.Serialize(Validated().Elements, Json);

    public static MarkupDocument Parse(string elementsJson, CropRect? crop)
    {
        IReadOnlyList<MarkupElement> elements;
        try
        {
            elements = string.IsNullOrWhiteSpace(elementsJson) ? [] : JsonSerializer.Deserialize<List<MarkupElement>>(elementsJson, Json) ?? [];
        }
        catch (JsonException)
        {
            throw new MarkupDocumentException("The image markup document is invalid.");
        }
        return new MarkupDocument { Elements = elements, Crop = crop is { } c && IsFull(c) ? null : crop }.Validated();
    }
}
