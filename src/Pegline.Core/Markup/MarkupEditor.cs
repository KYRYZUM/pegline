using Pegline.Core.Models;

namespace Pegline.Core.Markup;

/// <summary>A point in the editor's view space (device-independent pixels, origin top left).</summary>
public readonly record struct Vec(double X, double Y)
{
    public double DistanceTo(Vec other) => Math.Sqrt((X - other.X) * (X - other.X) + (Y - other.Y) * (Y - other.Y));
}

/// <summary>An axis-aligned rectangle in view space.</summary>
public readonly record struct Box(double X, double Y, double W, double H)
{
    public double Right => X + W;
    public double Bottom => Y + H;
    public double CenterX => X + W / 2;
    public double CenterY => Y + H / 2;

    public bool Contains(Vec p) => p.X >= X && p.X <= Right && p.Y >= Y && p.Y <= Bottom;

    public Box Inflate(double d) => new(X - d, Y - d, W + 2 * d, H + 2 * d);

    public static Box FromPoints(Vec a, Vec b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));

    public IEnumerable<Vec> Corners => [new(X, Y), new(Right, Y), new(X, Bottom), new(Right, Bottom)];
}

/// <summary>The text tool or a double-click on a text mark asks the window for an inline editor.</summary>
public sealed record TextRequest(MarkupPoint At, MarkupElement? Existing);

/// <summary>
/// The editing state of one image: tools and style, selection, move/resize, the crop tool and a 60-step
/// undo history. Pointer input arrives in view space; <see cref="Bounds"/> is the area the image occupies.
/// A gesture changes the live document only until it ends; losing focus cancels it without a trace.
/// </summary>
public sealed class MarkupEditor
{
    private const int HistoryLimit = 60;
    private static readonly MarkupPoint[] NoPoints = [];
    private readonly Func<string, double, (double Width, double Height)> _measureText;
    private readonly List<MarkupDocument> _undo = [];
    private readonly List<MarkupDocument> _redo = [];
    private MarkupDocument _saved;
    private MarkupDocument? _gestureStart;
    private Vec? _dragStart;
    private bool _resizing;
    private CropDrag? _cropDrag;
    private CropRect? _cropBeforeDrag;

    private abstract record CropDrag;
    private sealed record CreateCrop(Vec Anchor) : CropDrag;
    private sealed record MoveCrop(Box Original) : CropDrag;
    private sealed record ResizeCrop(Box Original, bool Left, bool Right, bool Top, bool Bottom) : CropDrag;

    /// <param name="measureText">Size of a text mark at a font size in view pixels.</param>
    public MarkupEditor(MarkupDocument document, Func<string, double, (double Width, double Height)> measureText)
    {
        Document = document.Validated();
        _saved = Document;
        _measureText = measureText;
    }

    public MarkupDocument Document { get; private set; }
    public MarkupTool Tool { get; private set; } = MarkupTool.Pen;
    public MarkupColor Color { get; private set; } = MarkupColor.Red;
    public double LineWidth { get; private set; } = MarkupStyle.Widths[1];
    public bool Filled { get; private set; }
    public double FontSize { get; private set; } = MarkupStyle.FontSizes[1];
    public Guid? SelectedId { get; private set; }
    public MarkupElement? Draft { get; private set; }
    public bool IsCropping { get; private set; }
    public CropRect? CropSelection { get; private set; }

    /// <summary>The view area the (cropped) image is shown in.</summary>
    public Box Bounds { get; set; } = new(0, 0, 1, 1);

    public MarkupElement? Selected => Document.Elements.FirstOrDefault(e => e.Id == SelectedId);
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public bool IsDirty => !Same(Document, _saved);
    public bool IsGesture => _dragStart is not null;

    /// <summary>Raised after anything visible changes, so the window can repaint and refresh its toolbar.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// The whole original image in view space. With a crop applied the view shows only the crop, so the
    /// original extends beyond <see cref="Bounds"/>; while cropping the full original fills the bounds.
    /// </summary>
    public Box ImageFrame
    {
        get
        {
            if (IsCropping || Document.Crop is not { } crop) return Bounds;
            var width = Bounds.W / crop.Width;
            var height = Bounds.H / crop.Height;
            return new Box(Bounds.X - crop.X * width, Bounds.Y - crop.Y * height, width, height);
        }
    }

    /// <summary>The aspect ratio (width / height) the view should give the image for the current state.</summary>
    public double ViewAspect(double imageWidth, double imageHeight) =>
        !IsCropping && Document.Crop is { } crop ? imageWidth * crop.Width / (imageHeight * crop.Height) : imageWidth / imageHeight;

    public void MarkSaved() => _saved = Document;

    // ----- Tools and style -----

    public void SetTool(MarkupTool tool)
    {
        CancelCrop();
        CancelGesture();
        Tool = tool;
        SelectedId = null;
        // The marker is a broad translucent stroke; switching back restores an ordinary pen width.
        if (tool == MarkupTool.Marker && LineWidth < MarkupStyle.MarkerWidths[0])
        {
            LineWidth = MarkupStyle.MarkerWidths[2];
            Color = MarkupColor.Yellow;
        }
        else if (tool != MarkupTool.Marker && LineWidth >= MarkupStyle.MarkerWidths[0]) LineWidth = MarkupStyle.Widths[2];
        Raise();
    }

    /// <summary>Sets the style for new marks and applies it to the selected mark.</summary>
    public void ApplyStyle(MarkupColor? color = null, double? width = null, bool? fill = null, double? fontSize = null)
    {
        if (color is { } c) Color = c;
        if (width is { } w) LineWidth = w;
        if (fill is { } f) Filled = f;
        if (fontSize is { } s) FontSize = s;
        if (Selected is not { } selected)
        {
            Raise();
            return;
        }
        var updated = selected with
        {
            Color = color ?? selected.Color,
            LineWidth = width ?? selected.LineWidth,
            Fill = fill is { } fillValue && selected.Tool is MarkupTool.Rectangle or MarkupTool.Ellipse ? fillValue : selected.Fill,
            FontSize = fontSize ?? selected.FontSize,
        };
        Commit(Document with { Elements = Replace(Document.Elements, updated) });
    }

    // ----- Pointer -----

    /// <summary>Starts a gesture. Returns a request for an inline text editor when one should open.</summary>
    public TextRequest? PointerDown(Vec p, int clickCount = 1)
    {
        if (IsCropping)
        {
            BeginCropDrag(p);
            return null;
        }
        _dragStart = p;
        _gestureStart = Document;
        var frame = ImageFrame;
        if (Tool == MarkupTool.Select)
        {
            if (Selected is { } current && ElementBounds(current, frame).Inflate(12).Contains(p))
            {
                var box = ElementBounds(current, frame);
                _resizing = box.Corners.Any(c => c.DistanceTo(p) < 15);
            }
            else
            {
                SelectedId = HitTest(p)?.Id;
                _resizing = false;
            }
            if (Selected is { } chosen)
            {
                Color = chosen.Color;
                LineWidth = chosen.LineWidth;
                Filled = chosen.Fill;
                FontSize = chosen.FontSize;
                if (clickCount == 2 && chosen.Tool == MarkupTool.Text)
                {
                    EndGesture();
                    Raise();
                    return new TextRequest(chosen.Points[0], chosen);
                }
            }
            Raise();
            return null;
        }
        SelectedId = null;
        var minimumPoints = Tool is MarkupTool.Text or MarkupTool.Pen or MarkupTool.Marker ? 1 : 2;
        if (Document.Elements.Count >= MarkupDocument.MaxElements || MarkupDocument.MaxPoints - Document.PointCount < minimumPoints)
        {
            EndGesture();
            return null;
        }
        var unit = Unit(p, frame);
        if (Tool == MarkupTool.Text)
        {
            EndGesture();
            Raise();
            return new TextRequest(unit, null);
        }
        Draft = new MarkupElement
        {
            Tool = Tool,
            Points = Tool is MarkupTool.Pen or MarkupTool.Marker ? [unit] : [unit, unit],
            Color = Color,
            LineWidth = LineWidth,
            Fill = Filled && Tool is MarkupTool.Rectangle or MarkupTool.Ellipse,
            FontSize = FontSize,
        };
        Raise();
        return null;
    }

    /// <param name="shift">Constrains shapes to squares/circles and lines to 45° steps.</param>
    public void PointerMove(Vec p, bool shift = false)
    {
        if (IsCropping)
        {
            UpdateCropDrag(p);
            return;
        }
        if (_dragStart is not { } start) return;
        var frame = ImageFrame;
        if (Tool == MarkupTool.Select)
        {
            if (_gestureStart?.Elements.FirstOrDefault(e => e.Id == SelectedId) is not { } original) return;
            var moved = _resizing ? Resize(original, start, p, frame) : Move(original, start, p, frame);
            Document = Document with { Elements = Replace(Document.Elements, moved) };
            Raise();
            return;
        }
        if (Draft is not { } draft) return;
        if (draft.Tool is MarkupTool.Pen or MarkupTool.Marker)
        {
            var last = Position(draft.Points[^1], frame);
            if (last.DistanceTo(p) < 1) return;
            var room = Math.Min(MarkupDocument.MaxPointsPerElement, MarkupDocument.MaxPoints - Document.PointCount);
            if (draft.Points.Count >= room) return;
            Draft = draft with { Points = [.. draft.Points, Unit(p, frame)] };
        }
        else
        {
            var end = p;
            if (shift)
            {
                var dx = p.X - start.X;
                var dy = p.Y - start.Y;
                if (draft.Tool is MarkupTool.Rectangle or MarkupTool.Ellipse)
                {
                    var side = Math.Min(Math.Abs(dx), Math.Abs(dy));
                    end = new Vec(start.X + Math.CopySign(side, dx), start.Y + Math.CopySign(side, dy));
                }
                else
                {
                    var angle = Math.Round(Math.Atan2(dy, dx) / (Math.PI / 4)) * (Math.PI / 4);
                    var length = Math.Sqrt(dx * dx + dy * dy);
                    end = new Vec(start.X + length * Math.Cos(angle), start.Y + length * Math.Sin(angle));
                }
            }
            Draft = draft with { Points = [draft.Points[0], Unit(end, frame)] };
        }
        Raise();
    }

    public void PointerUp(Vec p, bool shift = false)
    {
        if (IsCropping)
        {
            UpdateCropDrag(p);
            _cropDrag = null;
            _cropBeforeDrag = null;
            _dragStart = null;
            Raise();
            return;
        }
        if (_dragStart is null) return;
        PointerMove(p, shift);
        var previous = _gestureStart ?? Document;
        if (Draft is { } draft && Document.Elements.Count < MarkupDocument.MaxElements
            && (draft.Tool is MarkupTool.Pen or MarkupTool.Marker || draft.Points[0] != draft.Points[1]))
        {
            Document = Document with { Elements = [.. Document.Elements, draft] };
            SelectedId = draft.Id;
        }
        EndGesture();
        Record(previous);
    }

    /// <summary>Discards an unfinished stroke, shape or transform (for example when the window loses focus).</summary>
    public void CancelGesture()
    {
        if (_gestureStart is { } original && Tool == MarkupTool.Select) Document = original;
        if (_cropDrag is not null) CropSelection = _cropBeforeDrag;
        EndGesture();
        _cropDrag = null;
        _cropBeforeDrag = null;
        Raise();
    }

    // ----- Text -----

    /// <summary>Adds, replaces or (for blank text) removes a text mark.</summary>
    public void CommitText(TextRequest request, string text)
    {
        if (text.Length > MarkupDocument.MaxTextLength) text = text[..MarkupDocument.MaxTextLength];
        var elements = Document.Elements;
        if (string.IsNullOrWhiteSpace(text))
        {
            if (request.Existing is { } removed)
            {
                elements = [.. elements.Where(e => e.Id != removed.Id)];
                SelectedId = null;
            }
        }
        else
        {
            var value = new MarkupElement { Tool = MarkupTool.Text, Points = [request.At], Color = Color, LineWidth = LineWidth, Text = text, FontSize = FontSize };
            if (request.Existing is { } existing && elements.Any(e => e.Id == existing.Id))
            {
                value = value with { Id = existing.Id };
                elements = Replace(elements, value);
            }
            else if (elements.Count < MarkupDocument.MaxElements) elements = [.. elements, value];
            SelectedId = value.Id;
        }
        Commit(Document with { Elements = elements });
    }

    // ----- Editing commands -----

    public void DeleteSelected()
    {
        if (SelectedId is not { } id) return;
        SelectedId = null;
        Commit(Document with { Elements = [.. Document.Elements.Where(e => e.Id != id)] });
    }

    public void Clear()
    {
        SelectedId = null;
        Commit(Document with { Elements = [] });
    }

    public void Undo()
    {
        CancelCrop();
        if (_undo.Count == 0) return;
        _redo.Add(Document);
        Document = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        SelectedId = null;
        Raise();
    }

    public void Redo()
    {
        CancelCrop();
        if (_redo.Count == 0) return;
        _undo.Add(Document);
        Document = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        SelectedId = null;
        Raise();
    }

    // ----- Crop -----

    public void BeginCrop()
    {
        CancelGesture();
        SelectedId = null;
        CropSelection = Document.Crop ?? new CropRect(0, 0, 1, 1);
        IsCropping = true;
        Raise();
    }

    /// <summary>Commits the selection; a full-image selection removes the crop. Returns false when not cropping.</summary>
    public bool ApplyCrop()
    {
        if (!IsCropping || CropSelection is not { } selection || !MarkupDocument.IsValidCrop(selection)) return false;
        _cropDrag = null;
        _cropBeforeDrag = null;
        _dragStart = null;
        IsCropping = false;
        CropSelection = null;
        Commit(Document with { Crop = MarkupDocument.IsFull(selection) ? null : selection });
        return true;
    }

    public void CancelCrop()
    {
        if (!IsCropping) return;
        _cropDrag = null;
        _cropBeforeDrag = null;
        _dragStart = null;
        IsCropping = false;
        CropSelection = null;
        Raise();
    }

    public void ResetCropSelection()
    {
        if (!IsCropping) return;
        CropSelection = new CropRect(0, 0, 1, 1);
        Raise();
    }

    /// <summary>The crop selection in view space (cropping shows the whole original in <see cref="Bounds"/>).</summary>
    public Box CropBox
    {
        get
        {
            var c = CropSelection ?? new CropRect(0, 0, 1, 1);
            return new Box(Bounds.X + c.X * Bounds.W, Bounds.Y + c.Y * Bounds.H, c.Width * Bounds.W, c.Height * Bounds.H);
        }
    }

    private Vec Clamp(Vec p) => new(Math.Clamp(p.X, Bounds.X, Bounds.Right), Math.Clamp(p.Y, Bounds.Y, Bounds.Bottom));

    private (double W, double H) MinimumCrop => (
        Math.Min(Bounds.W, Math.Max(Bounds.W * 0.005, Math.Min(16, Bounds.W * 0.1))),
        Math.Min(Bounds.H, Math.Max(Bounds.H * 0.005, Math.Min(16, Bounds.H * 0.1))));

    private void BeginCropDrag(Vec value)
    {
        var start = Clamp(value);
        var box = CropBox;
        _dragStart = start;
        _cropBeforeDrag = CropSelection;
        const double tolerance = 9;
        bool left = false, right = false, top = false, bottom = false;
        if (box.Inflate(tolerance).Contains(start))
        {
            if (Math.Abs(start.X - box.X) <= tolerance) left = true;
            else if (Math.Abs(start.X - box.Right) <= tolerance) right = true;
            if (Math.Abs(start.Y - box.Y) <= tolerance) top = true;
            else if (Math.Abs(start.Y - box.Bottom) <= tolerance) bottom = true;
        }
        var full = CropSelection is null || MarkupDocument.IsFull(CropSelection);
        _cropDrag = left || right || top || bottom ? new ResizeCrop(box, left, right, top, bottom)
            : !full && box.Contains(start) ? new MoveCrop(box)
            : new CreateCrop(start);
        Raise();
    }

    private void UpdateCropDrag(Vec value)
    {
        if (_cropDrag is null || _dragStart is not { } start || Bounds.W <= 0 || Bounds.H <= 0) return;
        var p = Clamp(value);
        var (minW, minH) = MinimumCrop;
        Box box;
        switch (_cropDrag)
        {
            case CreateCrop(var anchor):
                if (p.DistanceTo(anchor) < 2) return;
                var w = Math.Max(minW, Math.Abs(p.X - anchor.X));
                var h = Math.Max(minH, Math.Abs(p.Y - anchor.Y));
                box = new Box(
                    Math.Clamp(p.X < anchor.X ? anchor.X - w : anchor.X, Bounds.X, Bounds.Right - w),
                    Math.Clamp(p.Y < anchor.Y ? anchor.Y - h : anchor.Y, Bounds.Y, Bounds.Bottom - h), w, h);
                break;
            case MoveCrop(var original):
                box = original with
                {
                    X = Math.Clamp(original.X + p.X - start.X, Bounds.X, Bounds.Right - original.W),
                    Y = Math.Clamp(original.Y + p.Y - start.Y, Bounds.Y, Bounds.Bottom - original.H),
                };
                break;
            case ResizeCrop(var original, var l, var r, var t, var b):
                double minX = original.X, maxX = original.Right, minY = original.Y, maxY = original.Bottom;
                if (l) minX = Math.Min(maxX - minW, p.X);
                if (r) maxX = Math.Max(minX + minW, p.X);
                if (t) minY = Math.Min(maxY - minH, p.Y);
                if (b) maxY = Math.Max(minY + minH, p.Y);
                box = new Box(minX, minY, maxX - minX, maxY - minY);
                break;
            default:
                return;
        }
        var x = Math.Clamp((box.X - Bounds.X) / Bounds.W, 0, 1);
        var y = Math.Clamp((box.Y - Bounds.Y) / Bounds.H, 0, 1);
        CropSelection = new CropRect(x, y, Math.Min(1 - x, box.W / Bounds.W), Math.Min(1 - y, box.H / Bounds.H));
        Raise();
    }

    // ----- Geometry -----

    public static Vec Position(MarkupPoint p, Box frame) => new(frame.X + p.X * frame.W, frame.Y + p.Y * frame.H);

    public static MarkupPoint Unit(Vec p, Box frame) =>
        new(Math.Clamp((p.X - frame.X) / Math.Max(1, frame.W), 0, 1), Math.Clamp((p.Y - frame.Y) / Math.Max(1, frame.H), 0, 1));

    /// <summary>Selection bounds, with room for the stroke and an arrow head; independent of zoom.</summary>
    public Box ElementBounds(MarkupElement element, Box frame)
    {
        if (element.Points.Count == 0) return default;
        var scale = Math.Min(frame.W, frame.H);
        var first = Position(element.Points[0], frame);
        if (element.Tool == MarkupTool.Text)
        {
            var (w, h) = _measureText(element.Text ?? "", Math.Max(1, element.FontSize * scale));
            return new Box(first.X, first.Y, w, h);
        }
        var positions = element.Points.Select(p => Position(p, frame)).ToList();
        var minX = positions.Min(p => p.X);
        var minY = positions.Min(p => p.Y);
        var box = new Box(minX, minY, positions.Max(p => p.X) - minX, positions.Max(p => p.Y) - minY);
        return box.Inflate(element.LineWidth * scale * (element.Tool == MarkupTool.Arrow ? 4 : 0.5));
    }

    /// <summary>The topmost mark under the pointer: on its stroke, or anywhere inside a filled shape or text.</summary>
    public MarkupElement? HitTest(Vec p)
    {
        var frame = ImageFrame;
        foreach (var element in Document.Elements.Reverse())
        {
            var bounds = ElementBounds(element, frame);
            if (!bounds.Inflate(7).Contains(p)) continue;
            if (element.Tool == MarkupTool.Text || (element.Fill && element.Tool is MarkupTool.Rectangle or MarkupTool.Ellipse)) return element;
            if (element.Tool == MarkupTool.Ellipse)
            {
                var x = (p.X - bounds.CenterX) / Math.Max(1, bounds.W / 2);
                var y = (p.Y - bounds.CenterY) / Math.Max(1, bounds.H / 2);
                if (Math.Abs(Math.Sqrt(x * x + y * y) - 1) < 0.18) return element;
                continue;
            }
            if (element.Tool == MarkupTool.Rectangle)
            {
                if (!bounds.Inflate(-8).Contains(p)) return element;
                continue;
            }
            var points = element.Points.Select(q => Position(q, frame)).ToList();
            var tolerance = Math.Max(8, element.LineWidth * Math.Min(frame.W, frame.H) / 2 + 5);
            if (points.Count == 1)
            {
                if (points[0].DistanceTo(p) <= tolerance) return element;
                continue;
            }
            for (var i = 1; i < points.Count; i++)
                if (SegmentDistance(p, points[i - 1], points[i]) <= tolerance) return element;
        }
        return null;
    }

    private static double SegmentDistance(Vec p, Vec a, Vec b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var length = dx * dx + dy * dy;
        var t = length == 0 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / length, 0, 1);
        return p.DistanceTo(new Vec(a.X + t * dx, a.Y + t * dy));
    }

    private static MarkupElement Move(MarkupElement original, Vec start, Vec p, Box frame)
    {
        var dx = (p.X - start.X) / Math.Max(1, frame.W);
        var dy = (p.Y - start.Y) / Math.Max(1, frame.H);
        // Keep every point inside the image.
        dx = Math.Clamp(dx, -original.Points.Min(q => q.X), 1 - original.Points.Max(q => q.X));
        dy = Math.Clamp(dy, -original.Points.Min(q => q.Y), 1 - original.Points.Max(q => q.Y));
        return original with { Points = [.. original.Points.Select(q => new MarkupPoint(q.X + dx, q.Y + dy))] };
    }

    private MarkupElement Resize(MarkupElement original, Vec start, Vec p, Box frame)
    {
        var box = ElementBounds(original, frame);
        // The corner opposite the grabbed one stays put.
        var anchor = new Vec(start.X < box.CenterX ? box.Right : box.X, start.Y < box.CenterY ? box.Bottom : box.Y);
        var sx = Math.Max(0.05, (p.X - anchor.X) / (Math.Abs(start.X - anchor.X) < 1 ? 1 : start.X - anchor.X));
        var sy = Math.Max(0.05, (p.Y - anchor.Y) / (Math.Abs(start.Y - anchor.Y) < 1 ? 1 : start.Y - anchor.Y));
        var points = original.Points.Select(q =>
        {
            var v = Position(q, frame);
            return Unit(new Vec(anchor.X + (v.X - anchor.X) * sx, anchor.Y + (v.Y - anchor.Y) * sy), frame);
        }).ToList();
        return original with
        {
            Points = points,
            FontSize = original.Tool == MarkupTool.Text ? Math.Clamp(original.FontSize * Math.Max(sx, sy), 0.005, 0.5) : original.FontSize,
        };
    }

    // ----- History -----

    private void Commit(MarkupDocument next)
    {
        var previous = Document;
        Document = next;
        Record(previous);
    }

    private void Record(MarkupDocument previous)
    {
        if (!Same(previous, Document))
        {
            _undo.Add(previous);
            if (_undo.Count > HistoryLimit) _undo.RemoveAt(0);
            _redo.Clear();
        }
        Raise();
    }

    private void EndGesture()
    {
        Draft = null;
        _dragStart = null;
        _gestureStart = null;
        _resizing = false;
    }

    private void Raise() => Changed?.Invoke(this, EventArgs.Empty);

    private static IReadOnlyList<MarkupElement> Replace(IReadOnlyList<MarkupElement> elements, MarkupElement value) =>
        [.. elements.Select(e => e.Id == value.Id ? value : e)];

    /// <summary>Deep equality: element lists compare by content, not by reference.</summary>
    public static bool Same(MarkupDocument a, MarkupDocument b) =>
        a.Crop == b.Crop && a.Elements.Count == b.Elements.Count
        && a.Elements.Zip(b.Elements).All(pair => pair.First with { Points = NoPoints } == pair.Second with { Points = NoPoints }
            && pair.First.Points.SequenceEqual(pair.Second.Points));
}
