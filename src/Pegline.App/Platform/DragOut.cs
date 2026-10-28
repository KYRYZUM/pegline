using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Pegline.Core.Models;
using Pegline.Core.Transfer;

namespace Pegline.App.Platform;

/// <summary>
/// Drag and drop out of Pegline. Images travel as real files named after the record; text and links travel as
/// text (and a URL for links), or as .txt / .url files while Alt is held. The drop target always gets a copy.
/// The record ids ride along so the Library sidebar can accept a drop onto a folder.
/// </summary>
internal static class DragOut
{
    public const string IdsFormat = "Pegline.ItemIds";

    /// <summary>True once the pointer has moved far enough from <paramref name="origin"/> to start a drag.</summary>
    public static bool Exceeded(Point origin, Point current) =>
        Math.Abs(current.X - origin.X) >= SystemParameters.MinimumHorizontalDragDistance
        || Math.Abs(current.Y - origin.Y) >= SystemParameters.MinimumVerticalDragDistance;

    /// <summary>Runs the drag; returns false when nothing could be prepared.</summary>
    public static bool Start(DependencyObject source, IReadOnlyList<ClipItem> items, ItemFiles files)
    {
        if (items.Count == 0) return false;
        var asFiles = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
        var data = new DataObject();
        var paths = new StringCollection();
        foreach (var item in items)
        {
            if (item.Kind != ItemKind.Image && !asFiles) continue;
            try
            {
                paths.Add(files.ForDrag(item));
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                // A missing file is left out; the rest of the batch still drags.
            }
        }
        if (paths.Count > 0) data.SetFileDropList(paths);
        var texts = items.Where(i => i.Kind != ItemKind.Image).Select(i => i.Kind == ItemKind.Link ? i.Url : i.Text).Where(t => t.Length > 0).ToList();
        if (texts.Count > 0 && !asFiles) data.SetText(string.Join(Environment.NewLine + Environment.NewLine, texts), TextDataFormat.UnicodeText);
        if (!asFiles && items is [{ Kind: ItemKind.Link } link])
            data.SetData("UniformResourceLocatorW", new MemoryStream(System.Text.Encoding.Unicode.GetBytes(link.Url + "\0")));
        if (paths.Count == 0 && texts.Count == 0) return false;
        data.SetData(IdsFormat, string.Join(",", items.Select(i => i.Id.ToString("D"))));
        DragDrop.DoDragDrop(source, data, DragDropEffects.Copy);
        return true;
    }

    /// <summary>Record ids carried by a Pegline drag, or empty for anything else.</summary>
    public static IReadOnlyList<Guid> IdsOf(IDataObject data) =>
        data.GetDataPresent(IdsFormat) && data.GetData(IdsFormat) is string value
            ? [.. value.Split(',').Select(s => Guid.TryParse(s, out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty)]
            : [];
}
