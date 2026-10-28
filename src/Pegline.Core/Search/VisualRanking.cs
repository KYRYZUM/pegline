namespace Pegline.Core.Search;

/// <summary>A square part of an image, in source pixels.</summary>
public readonly record struct ImageRegion(double X, double Y, double Width, double Height);

/// <summary>
/// MobileCLIP-S2 places pictures and sentences in one 512-dimensional space, so "a pink dress" lands near a
/// screenshot showing one even when no text on it says so. Same input policy and match rules as the reference app.
/// </summary>
public static class VisualRanking
{
    public const int Dimensions = 512;
    public const int Side = 256;
    public const int MaxViews = 4;

    /// <summary>The longest side images are reduced to before they are seen.</summary>
    public const int MaxSource = 1024;

    /// <summary>A unit length copy, or null for a wrong size, NaN or zero vector.</summary>
    public static float[]? Normalized(ReadOnlySpan<float> values)
    {
        if (values.Length != Dimensions) return null;
        var sum = 0f;
        foreach (var value in values)
        {
            if (!float.IsFinite(value)) return null;
            sum += value * value;
        }
        var norm = MathF.Sqrt(sum);
        if (!float.IsFinite(norm) || norm <= 0) return null;
        var output = new float[Dimensions];
        for (var i = 0; i < Dimensions; i++) output[i] = values[i] / norm;
        return output;
    }

    /// <summary>The best cosine between a query and an image's views; null when nothing is comparable.</summary>
    public static double? Score(float[] query, IReadOnlyList<float[]> views)
    {
        if (query.Length != Dimensions || views.Count is 0 or > MaxViews) return null;
        var best = double.NegativeInfinity;
        foreach (var view in views)
        {
            if (view.Length != Dimensions) return null;
            var sum = 0f;
            for (var i = 0; i < Dimensions; i++) sum += query[i] * view[i];
            if (float.IsFinite(sum)) best = Math.Max(best, sum);
        }
        return double.IsFinite(best) ? best : null;
    }

    /// <summary>
    /// The images worth showing, best first: near the best score and above a floor; in larger sets also clearly
    /// above the rest, so a query that resembles nothing does not return everything.
    /// </summary>
    public static IReadOnlyList<Guid> Matches(IReadOnlyList<(Guid Id, double Score)> values)
    {
        var scores = values.Where(v => double.IsFinite(v.Score)).ToList();
        if (scores.Count == 0) return [];
        var best = Math.Max(0, scores.Max(v => v.Score));
        var mean = scores.Average(v => v.Score);
        var spread = Math.Max(0.01, Math.Sqrt(scores.Sum(v => Math.Pow(v.Score - mean, 2)) / scores.Count));
        return [.. scores
            .Where(v => v.Score >= Math.Max(0.22, best - 0.045) && (scores.Count < 8 || v.Score >= 0.30 || (v.Score - mean) / spread >= 1.25))
            .OrderByDescending(v => v.Score)
            .ThenBy(v => v.Id.ToString(), StringComparer.Ordinal)
            .Take(200)
            .Select(v => v.Id)];
    }

    /// <summary>
    /// What the model sees of an image: the whole picture (null) and, for a wide or tall one, up to three square
    /// tiles along its length so small things at either end are not lost. Slivers and long strips are skipped;
    /// they are mostly padding and end up resembling every query (their text is still searchable).
    /// </summary>
    public static IReadOnlyList<ImageRegion?> Regions(int width, int height)
    {
        if (width < 1 || height < 1 || width > MaxSource || height > MaxSource) throw new ArgumentOutOfRangeException(nameof(width), "Invalid image size.");
        double longSide = Math.Max(width, height), shortSide = Math.Min(width, height);
        if (shortSide < 120 || longSide / shortSide > 4) return [];
        var views = new List<ImageRegion?> { null };
        if (longSide / shortSide > 1.3)
        {
            var tiles = Math.Min(3, (int)Math.Ceiling(longSide / shortSide));
            for (var i = 0; i < tiles; i++)
            {
                var offset = (longSide - shortSide) * i / (tiles - 1);
                views.Add(width >= height ? new ImageRegion(offset, 0, shortSide, shortSide) : new ImageRegion(0, offset, shortSide, shortSide));
            }
        }
        return views;
    }

    /// <summary>
    /// RGB float tensors (NCHW, [0,1]) for each region of straight-alpha BGRA pixels. The whole picture is fitted on
    /// neutral gray; tiles fill the square. Bilinear sampling, transparent pixels blend into the gray.
    /// </summary>
    public static IReadOnlyList<float[]> Tensors(byte[] bgra, int width, int height)
    {
        if (bgra.Length != width * height * 4) throw new ArgumentException("Invalid image pixels.", nameof(bgra));
        const int plane = Side * Side;
        const float gray = 128f / 255f;
        var tensors = new List<float[]>();
        foreach (var crop in Regions(width, height))
        {
            var output = new float[3 * plane];
            var scale = (double)Side / Math.Max(width, height);
            var fit = crop is null
                ? new ImageRegion((Side - width * scale) / 2, (Side - height * scale) / 2, width * scale, height * scale)
                : new ImageRegion(0, 0, Side, Side);
            var region = crop ?? new ImageRegion(0, 0, width, height);
            for (var y = 0; y < Side; y++)
            {
                for (var x = 0; x < Side; x++)
                {
                    var at = y * Side + x;
                    if (x + .5 < fit.X || x + .5 >= fit.X + fit.Width || y + .5 < fit.Y || y + .5 >= fit.Y + fit.Height)
                    {
                        output[at] = output[plane + at] = output[2 * plane + at] = gray;
                        continue;
                    }
                    var fx = Math.Clamp(region.X + (x + .5 - fit.X) / fit.Width * region.Width - .5, 0, width - 1);
                    var fy = Math.Clamp(region.Y + (y + .5 - fit.Y) / fit.Height * region.Height - .5, 0, height - 1);
                    int x0 = (int)fx, y0 = (int)fy, x1 = Math.Min(width - 1, x0 + 1), y1 = Math.Min(height - 1, y0 + 1);
                    double dx = fx - x0, dy = fy - y0;
                    double Sample(int channel)
                    {
                        var a = bgra[(y0 * width + x0) * 4 + channel] * (1 - dx) + bgra[(y0 * width + x1) * 4 + channel] * dx;
                        var b = bgra[(y1 * width + x0) * 4 + channel] * (1 - dx) + bgra[(y1 * width + x1) * 4 + channel] * dx;
                        return a * (1 - dy) + b * dy;
                    }
                    var alpha = Sample(3) / 255;
                    for (var channel = 0; channel < 3; channel++)
                        output[channel * plane + at] = (float)((Sample(2 - channel) * alpha + 128 * (1 - alpha)) / 255);
                }
            }
            tensors.Add(output);
        }
        return tensors;
    }
}
