namespace Pegline.Core.Shelves;

/// <summary>Image preview magnification between 100% and 400%.</summary>
public struct PreviewZoom
{
    public const double Min = 1;
    public const double Max = 4;

    /// <summary>Exponential step per wheel notch; twelve notches go from fit to the maximum.</summary>
    public const double WheelStep = 0.116;

    public PreviewZoom() { }

    public double Scale { get; private set; } = Min;

    public readonly int Percent => (int)Math.Round(Scale * 100);

    public readonly bool CanPan => Scale > Min;

    /// <summary>Applies a wheel movement in notches (WPF delta / 120); touchpads send fractions.</summary>
    public void Wheel(double notches)
    {
        if (!double.IsFinite(notches)) return;
        Scale = Clamp(Scale * Math.Exp(Math.Clamp(notches, -12, 12) * WheelStep));
    }

    /// <summary>Applies a pinch: 0.1 grows by 10%.</summary>
    public void Magnify(double delta)
    {
        if (!double.IsFinite(delta)) return;
        Scale = Clamp(Scale * Math.Max(0.01, 1 + delta));
    }

    public void Reset() => Scale = Min;

    /// <summary>
    /// Scroll offset that keeps the content point under <paramref name="anchor"/> fixed when the
    /// scale changes from <paramref name="oldScale"/>, clamped to the scrollable range.
    /// </summary>
    public static double AnchoredOffset(double offset, double anchor, double oldScale, double newScale, double fitted, double viewport)
    {
        var content = (offset + anchor) / oldScale;
        return Math.Clamp(content * newScale - anchor, 0, Math.Max(0, fitted * newScale - viewport));
    }

    private static double Clamp(double value) => Math.Clamp(value, Min, Max);
}
