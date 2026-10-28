using Pegline.Core.Markup;
using Pegline.Core.Models;
using Pegline.Core.Storage;

namespace Pegline.Core.Tests;

public sealed class MarkupTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pegline-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    // Text is measured as 0.6 em per character, one line high.
    private static MarkupEditor Editor(MarkupDocument? document = null) =>
        new(document ?? MarkupDocument.Empty, (text, size) => (text.Length * size * 0.6, size * 1.3)) { Bounds = new Box(0, 0, 400, 200) };

    private static void Drag(MarkupEditor editor, Vec from, Vec to, bool shift = false)
    {
        editor.PointerDown(from);
        editor.PointerMove(new Vec((from.X + to.X) / 2, (from.Y + to.Y) / 2), shift);
        editor.PointerUp(to, shift);
    }

    [Fact]
    public void ShapesAreStoredInUnitCoordinatesOfTheOriginal()
    {
        var editor = Editor();
        editor.SetTool(MarkupTool.Rectangle);
        Drag(editor, new Vec(40, 20), new Vec(200, 100));

        var mark = Assert.Single(editor.Document.Elements);
        Assert.Equal(MarkupTool.Rectangle, mark.Tool);
        Assert.Equal([new MarkupPoint(0.1, 0.1), new MarkupPoint(0.5, 0.5)], mark.Points);
        Assert.Equal(mark.Id, editor.SelectedId);
        Assert.True(editor.IsDirty);
        Assert.True(editor.CanUndo);
    }

    [Fact]
    public void ShiftMakesSquaresAndFortyFiveDegreeLines()
    {
        var editor = Editor();
        editor.SetTool(MarkupTool.Ellipse);
        Drag(editor, new Vec(0, 0), new Vec(100, 40), shift: true);
        var circle = editor.Document.Elements[^1];
        Assert.Equal(new MarkupPoint(0.1, 0.2), circle.Points[1]); // 40 × 40 view pixels

        editor.SetTool(MarkupTool.Line);
        Drag(editor, new Vec(0, 0), new Vec(100, 10), shift: true);
        var line = editor.Document.Elements[^1];
        Assert.Equal(0, line.Points[1].Y, 6);
    }

    [Fact]
    public void AClickWithoutMovementLeavesNoEmptyShape()
    {
        var editor = Editor();
        editor.SetTool(MarkupTool.Arrow);
        editor.PointerDown(new Vec(50, 50));
        editor.PointerUp(new Vec(50, 50));
        Assert.Empty(editor.Document.Elements);
        Assert.False(editor.CanUndo);

        editor.SetTool(MarkupTool.Pen);
        editor.PointerDown(new Vec(50, 50));
        editor.PointerUp(new Vec(50, 50));
        Assert.Single(editor.Document.Elements); // a pen dot is a mark
    }

    [Fact]
    public void SelectMovesWithinTheImageAndUndoRestores()
    {
        var editor = Editor();
        editor.SetTool(MarkupTool.Line);
        Drag(editor, new Vec(40, 100), new Vec(120, 100));
        editor.SetTool(MarkupTool.Select);

        Drag(editor, new Vec(80, 100), new Vec(780, 100)); // far past the right edge
        var moved = editor.Document.Elements[0];
        Assert.Equal(1, moved.Points.Max(p => p.X), 6);
        Assert.Equal(0.8, moved.Points.Min(p => p.X), 6);

        editor.Undo();
        Assert.Equal(0.1, editor.Document.Elements[0].Points[0].X, 6);
        editor.Redo();
        Assert.Equal(0.8, editor.Document.Elements[0].Points[0].X, 6);
    }

    [Fact]
    public void LosingFocusMidTransformDiscardsIt()
    {
        var editor = Editor();
        editor.SetTool(MarkupTool.Line);
        Drag(editor, new Vec(40, 100), new Vec(120, 100));
        editor.SetTool(MarkupTool.Select);
        var before = editor.Document;

        editor.PointerDown(new Vec(80, 100));
        editor.PointerMove(new Vec(200, 150));
        editor.CancelGesture();

        Assert.True(MarkupEditor.Same(before, editor.Document));
    }

    [Fact]
    public void StyleChangesApplyToTheSelectionAsOneUndoStep()
    {
        var editor = Editor();
        editor.SetTool(MarkupTool.Rectangle);
        Drag(editor, new Vec(40, 20), new Vec(200, 100));
        editor.SetTool(MarkupTool.Select);
        editor.PointerDown(new Vec(40, 60));
        editor.PointerUp(new Vec(40, 60));
        Assert.NotNull(editor.SelectedId);

        editor.ApplyStyle(color: MarkupColor.Blue, fill: true);
        Assert.Equal(MarkupColor.Blue, editor.Document.Elements[0].Color);
        Assert.True(editor.Document.Elements[0].Fill);
        editor.Undo();
        Assert.Equal(MarkupColor.Red, editor.Document.Elements[0].Color);
    }

    [Fact]
    public void MarkerStartsBroadAndYellowAndPenReturnsToANormalWidth()
    {
        var editor = Editor();
        editor.SetTool(MarkupTool.Marker);
        Assert.Equal(MarkupColor.Yellow, editor.Color);
        Assert.True(editor.LineWidth >= MarkupStyle.MarkerWidths[0]);
        editor.SetTool(MarkupTool.Pen);
        Assert.True(editor.LineWidth < MarkupStyle.MarkerWidths[0]);
    }

    [Fact]
    public void TextIsAddedEditedAndRemovedWhenBlank()
    {
        var editor = Editor();
        editor.SetTool(MarkupTool.Text);
        var request = editor.PointerDown(new Vec(100, 50));
        Assert.NotNull(request);
        Assert.Null(request.Existing);
        editor.CommitText(request, "Note");
        var mark = Assert.Single(editor.Document.Elements);
        Assert.Equal("Note", mark.Text);

        editor.SetTool(MarkupTool.Select);
        editor.PointerDown(new Vec(105, 55));
        editor.PointerUp(new Vec(105, 55));
        var edit = editor.PointerDown(new Vec(105, 55), clickCount: 2);
        Assert.Equal(mark.Id, edit?.Existing?.Id);
        editor.CommitText(edit!, "Changed");
        Assert.Equal("Changed", Assert.Single(editor.Document.Elements).Text);

        editor.CommitText(new TextRequest(mark.Points[0], editor.Document.Elements[0]), "   ");
        Assert.Empty(editor.Document.Elements);
    }

    [Fact]
    public void DeleteAndClearAreUndoable()
    {
        var editor = Editor();
        editor.SetTool(MarkupTool.Pen);
        Drag(editor, new Vec(10, 10), new Vec(60, 60));
        Drag(editor, new Vec(100, 10), new Vec(160, 60));
        editor.DeleteSelected();
        Assert.Single(editor.Document.Elements);
        editor.Clear();
        Assert.Empty(editor.Document.Elements);
        editor.Undo();
        editor.Undo();
        Assert.Equal(2, editor.Document.Elements.Count);
    }

    [Fact]
    public void CropSelectsAppliesAndKeepsMarksInOriginalCoordinates()
    {
        var editor = Editor();
        editor.SetTool(MarkupTool.Line);
        Drag(editor, new Vec(200, 100), new Vec(300, 100));
        var line = editor.Document.Elements[0];

        editor.BeginCrop();
        Assert.True(editor.IsCropping);
        // Starting on the image edge would grab that edge; start inside to draw a new area.
        Drag(editor, new Vec(200, 20), new Vec(400, 200));
        Assert.Equal(new CropRect(0.5, 0.1, 0.5, 0.9), editor.CropSelection);
        Assert.True(editor.ApplyCrop());

        Assert.Equal(new CropRect(0.5, 0.1, 0.5, 0.9), editor.Document.Crop);
        Assert.Equal(line, editor.Document.Elements[0]);
        // The view now shows only the crop, so the original frame is larger and starts above and left of it.
        var frame = editor.ImageFrame;
        Assert.Equal(-400, frame.X, 6);
        Assert.Equal(800, frame.W, 6);
        Assert.Equal(-200 / 9.0, frame.Y, 6);
        Assert.Equal(400 * 0.5 / (200 * 0.9), editor.ViewAspect(400, 200), 6);

        editor.BeginCrop();
        editor.ResetCropSelection();
        editor.ApplyCrop();
        Assert.Null(editor.Document.Crop);
        Assert.True(editor.Document.IsEmpty is false);
    }

    [Fact]
    public void CancellingACropChangesNothing()
    {
        var editor = Editor();
        editor.BeginCrop();
        Drag(editor, new Vec(10, 10), new Vec(100, 100));
        editor.CancelCrop();
        Assert.Null(editor.Document.Crop);
        Assert.False(editor.IsDirty);
    }

    [Fact]
    public void CropHandlesResizeAndTheSelectionStaysInsideTheImage()
    {
        var editor = Editor();
        editor.BeginCrop();
        Drag(editor, new Vec(100, 50), new Vec(300, 150));
        // Grab the right edge and pull it far outside.
        Drag(editor, new Vec(300, 100), new Vec(900, 100));
        var crop = editor.CropSelection!;
        Assert.Equal(0.25, crop.X, 6);
        Assert.Equal(1, crop.X + crop.Width, 6);
        // Drag inside to move it; it cannot leave the image.
        Drag(editor, new Vec(250, 100), new Vec(-500, 100));
        Assert.Equal(0, editor.CropSelection!.X, 6);
    }

    [Fact]
    public void DocumentsRoundTripAndInvalidOnesAreRejected()
    {
        var editor = Editor();
        editor.SetTool(MarkupTool.Arrow);
        Drag(editor, new Vec(10, 10), new Vec(200, 150));
        var json = editor.Document.ElementsJson();
        var parsed = MarkupDocument.Parse(json, new CropRect(0.1, 0.1, 0.5, 0.5));
        Assert.True(MarkupEditor.Same(editor.Document with { Crop = new CropRect(0.1, 0.1, 0.5, 0.5) }, parsed));
        Assert.Contains("\"arrow\"", json);

        Assert.Throws<MarkupDocumentException>(() => MarkupDocument.Parse("""[{"tool":"rectangle","points":[{"x":0.1,"y":0.1}]}]""", null));
        Assert.Throws<MarkupDocumentException>(() => MarkupDocument.Parse("""[{"tool":"pen","points":[{"x":1.5,"y":0.1}]}]""", null));
        Assert.Throws<MarkupDocumentException>(() => MarkupDocument.Parse("""[{"tool":"text","points":[{"x":0.1,"y":0.1}],"text":" "}]""", null));
        Assert.Throws<MarkupDocumentException>(() => MarkupDocument.Parse("not json", null));
        Assert.Throws<MarkupDocumentException>(() => MarkupDocument.Parse("[]", new CropRect(0.6, 0, 0.6, 1)));
        Assert.Null(MarkupDocument.Parse("[]", new CropRect(0, 0, 1, 1)).Crop);
    }

    [Fact]
    public void StoreKeepsTheOriginalAndUndoesEditsWithinTheSession()
    {
        using var store = new LibraryStore(_directory);
        var image = store.InsertImage(TestImages.Png(8, 6));
        var original = store.MediaPath(image, original: true);
        var originalBytes = File.ReadAllBytes(original);

        store.SaveImageEdit(image.Id, TestImages.Png(4, 6, shade: 10), "[]", new CropRect(0, 0, 0.5, 1));
        var edited = store.Find(image.Id)!;
        Assert.NotEqual("", edited.RenderedFile);
        Assert.Equal(new CropRect(0, 0, 0.5, 1), edited.Crop);
        Assert.Equal(originalBytes, File.ReadAllBytes(original));
        Assert.True(store.CanUndoImageEdit(image.Id));

        store.SaveImageEdit(image.Id, null, "[]", null);
        Assert.Equal("", store.Find(image.Id)!.RenderedFile);

        Assert.True(store.UndoImageEdit(image.Id));
        Assert.Equal(edited.RenderedFile, store.Find(image.Id)!.RenderedFile);
        Assert.True(File.Exists(store.MediaPath(store.Find(image.Id)!)));
        Assert.True(store.UndoImageEdit(image.Id));
        Assert.Equal("", store.Find(image.Id)!.RenderedFile);
        Assert.False(store.UndoImageEdit(image.Id));
        Assert.True(store.RedoImageEdit(image.Id));
        Assert.Equal(edited.RenderedFile, store.Find(image.Id)!.RenderedFile);
    }

    [Fact]
    public void TextAndRecordingsCannotBeImageEdited()
    {
        using var store = new LibraryStore(_directory);
        var text = store.InsertText("hello");
        Assert.Throws<InvalidOperationException>(() => store.SaveImageEdit(text.Id, TestImages.Png(2, 2), "[]", null));
    }
}
