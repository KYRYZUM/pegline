using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using Pegline.Core.Capture;
using Pegline.Core.Models;
using Pegline.Core.Storage;

namespace Pegline.App.Platform;

/// <summary>Puts records on the clipboard in shelf order, marked as Pegline's own so they are not re-captured.</summary>
internal sealed class ClipboardWriter(LibraryStore store, Pegline.Core.Transfer.ItemFiles? files = null)
{
    public bool Copy(IReadOnlyList<ClipItem> items)
    {
        if (items.Count == 0) return false;
        var data = new DataObject();
        data.SetData(ClipboardAdmission.OwnFormat, new MemoryStream([1]));

        var texts = items.Where(i => i.Kind != ItemKind.Image).Select(i => i.Kind == ItemKind.Link ? i.Url : i.Text).ToList();
        var media = items.Where(i => i.Kind == ItemKind.Image).ToList();

        if (texts.Count > 0)
            data.SetText(string.Join(Environment.NewLine, texts), TextDataFormat.UnicodeText);

        if (media.Count > 0)
        {
            var paths = new StringCollection();
            foreach (var item in media)
            {
                if (Named(item) is { } file) paths.Add(file);
            }
            var firstRaster = media.FirstOrDefault(i => !i.Recording);
            if (firstRaster is not null && texts.Count == 0)
            {
                var path = store.MediaPath(firstRaster);
                var png = File.ReadAllBytes(path);
                data.SetData("PNG", new MemoryStream(png));
                data.SetImage(Decode(png));
            }
            // A single still image is offered as pixels only; batches and recordings also travel as files.
            if (media.Count > 1 || media[0].Recording || texts.Count > 0)
                data.SetFileDropList(paths);
        }

        if (!Write(data)) return false;
        store.MarkCopied(items.Select(i => i.Id));
        return true;
    }

    /// <summary>
    /// The file pasted into Explorer or a chat: a copy carrying the record's name ("Demo take.mp4"), not the
    /// content hash it is stored under. Falls back to the managed file when no named copy can be made.
    /// </summary>
    private string? Named(ClipItem item)
    {
        var managed = item.Recording ? store.Media.PathOf(item.CurrentVideoFile) : store.MediaPath(item);
        if (!File.Exists(managed)) return null;
        // A long recording is not duplicated just for its name; copying gigabytes would stall the copy.
        if (files is null || new FileInfo(managed).Length > 64L * 1024 * 1024) return managed;
        try
        {
            return files.ForDrag(item);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return managed;
        }
    }

    /// <summary>Copies a text fragment, such as a selection inside the text preview, without capturing it again.</summary>
    public bool CopyText(string text)
    {
        if (text.Length == 0) return false;
        var data = new DataObject();
        data.SetData(ClipboardAdmission.OwnFormat, new MemoryStream([1]));
        data.SetText(text, TextDataFormat.UnicodeText);
        return Write(data);
    }

    private static bool Write(DataObject data)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, copy: true);
                return true;
            }
            catch (COMException)
            {
                Thread.Sleep(40 * (attempt + 1));
            }
        }
        return false;
    }

    private static BitmapSource Decode(byte[] png)
    {
        using var stream = new MemoryStream(png);
        var frame = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        frame.Freeze();
        return frame;
    }
}
