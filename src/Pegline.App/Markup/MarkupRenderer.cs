using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Pegline.Core.Markup;
using Pegline.Core.Models;

namespace Pegline.App.Markup;

/// <summary>
/// Draws markup with WPF. The on-screen overlay and the exported PNG use the same code, scaled to the frame,
/// so what you see while editing is what the saved image contains.
/// </summary>
internal static class MarkupRenderer
{
    public const long MaxPixels = 80_000_000;
    private static readonly Typeface TextFace = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Medium, FontStretches.Normal);

    public static (double Width, double Height) MeasureText(string text, double size)
    {
        var formatted = Format(text, size, Brushes.Black);
        return (formatted.WidthIncludingTrailingWhitespace, formatted.Height);
    }

    public static Brush BrushFor(MarkupColor color, double opacity = 1)
    {
        var brush = new SolidColorBrush(Color.FromArgb(
            (byte)Math.Round(255 * Math.Clamp(opacity, 0, 1)),
            (byte)Math.Round(255 * color.R), (byte)Math.Round(255 * color.G), (byte)Math.Round(255 * color.B)));
        brush.Freeze();
        return brush;
    }

    /// <summary>Draws <paramref name="elements"/> over the original image placed at <paramref name="frame"/>, clipped to it.</summary>
    public static void Draw(DrawingContext context, IEnumerable<MarkupElement> elements, Rect frame)
    {
        if (frame.Width <= 0 || frame.Height <= 0) return;
        context.PushClip(new RectangleGeometry(frame));
        foreach (var element in elements) Draw(context, element, frame);
        context.Pop();
    }

    private static void Draw(DrawingContext context, MarkupElement element, Rect frame)
    {
        if (element.Points.Count == 0) return;
        var scale = Math.Min(frame.Width, frame.Height);
        var width = Math.Max(0.1, element.LineWidth * scale);
        var brush = BrushFor(element.Color, element.Opacity);
        var pen = new Pen(brush, width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        pen.Freeze();
        Point At(MarkupPoint p) => new(frame.X + p.X * frame.Width, frame.Y + p.Y * frame.Height);
        var start = At(element.Points[0]);
        switch (element.Tool)
        {
            case MarkupTool.Pen or MarkupTool.Marker:
                if (element.Points.Count == 1)
                {
                    context.DrawEllipse(brush, null, start, width / 2, width / 2);
                    return;
                }
                var stroke = new StreamGeometry();
                using (var g = stroke.Open())
                {
                    g.BeginFigure(start, isFilled: false, isClosed: false);
                    g.PolyLineTo([.. element.Points.Skip(1).Select(At)], isStroked: true, isSmoothJoin: true);
                }
                stroke.Freeze();
                context.DrawGeometry(null, pen, stroke);
                break;
            case MarkupTool.Rectangle or MarkupTool.Ellipse when element.Points.Count >= 2:
                var box = new Rect(start, At(element.Points[^1]));
                if (element.Tool == MarkupTool.Rectangle) context.DrawRectangle(element.Fill ? brush : null, element.Fill ? null : pen, box);
                else context.DrawEllipse(element.Fill ? brush : null, element.Fill ? null : pen, new Point(box.X + box.Width / 2, box.Y + box.Height / 2), box.Width / 2, box.Height / 2);
                break;
            case MarkupTool.Line or MarkupTool.Arrow when element.Points.Count >= 2:
                var end = At(element.Points[^1]);
                context.DrawLine(pen, start, end);
                if (element.Tool == MarkupTool.Arrow)
                {
                    var angle = Math.Atan2(end.Y - start.Y, end.X - start.X);
                    var distance = (end - start).Length;
                    var length = Math.Min(Math.Max(width * 4, scale * 0.018), distance * 0.4);
                    var head = new StreamGeometry();
                    using (var g = head.Open())
                    {
                        g.BeginFigure(new Point(end.X - length * Math.Cos(angle - Math.PI / 6), end.Y - length * Math.Sin(angle - Math.PI / 6)), false, false);
                        g.LineTo(end, true, true);
                        g.LineTo(new Point(end.X - length * Math.Cos(angle + Math.PI / 6), end.Y - length * Math.Sin(angle + Math.PI / 6)), true, true);
                    }
                    head.Freeze();
                    context.DrawGeometry(null, pen, head);
                }
                break;
            case MarkupTool.Text:
                context.DrawText(Format(element.Text ?? "", Math.Max(1, element.FontSize * scale), brush), start);
                break;
        }
    }

    private static FormattedText Format(string text, double size, Brush brush) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, TextFace, size, brush, 1.0);

    /// <summary>Decodes the original at full resolution, refusing images over 80 megapixels.</summary>
    public static BitmapSource Load(string path)
    {
        var decoder = BitmapDecoder.Create(new Uri(path), BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        if ((long)frame.PixelWidth * frame.PixelHeight > MaxPixels) throw new InvalidOperationException("Markup supports images up to 80 megapixels.");
        frame.Freeze();
        return frame;
    }

    /// <summary>
    /// A fresh PNG at the original's resolution with the marks drawn in, cropped when a crop is set; null when the
    /// document is empty, which means "show the original". The original file is only read.
    /// </summary>
    public static byte[]? RenderPng(BitmapSource original, MarkupDocument document)
    {
        document.Validated();
        if (document.IsEmpty) return null;
        int width = original.PixelWidth, height = original.PixelHeight;
        var rect = new Rect(0, 0, width, height);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawImage(original, rect);
            Draw(context, document.Elements, rect);
        }
        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        BitmapSource output = target;
        if (document.Crop is { } crop && !MarkupDocument.IsFull(crop))
        {
            // Round outwards so every selected source pixel survives and a tiny crop still has one pixel.
            var left = Math.Clamp((int)Math.Floor(crop.X * width), 0, width - 1);
            var top = Math.Clamp((int)Math.Floor(crop.Y * height), 0, height - 1);
            var right = Math.Clamp((int)Math.Ceiling((crop.X + crop.Width) * width), left + 1, width);
            var bottom = Math.Clamp((int)Math.Ceiling((crop.Y + crop.Height) * height), top + 1, height);
            output = new CroppedBitmap(target, new Int32Rect(left, top, right - left, bottom - top));
        }
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(output));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
