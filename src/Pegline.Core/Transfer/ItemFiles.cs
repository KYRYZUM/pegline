using System.Globalization;
using System.Text;
using Pegline.Core.Models;
using Pegline.Core.Storage;

namespace Pegline.Core.Transfer;

/// <summary>A file that stands for one record, and the name it should carry when copied out.</summary>
public sealed record ItemFile(Guid Id, string Path, string Name);

public sealed record ExportResult(IReadOnlyList<string> Copies, IReadOnlyList<Guid> Failed);

/// <summary>
/// Turns records into real files for export and drag and drop. Images use their managed (edited) file;
/// text becomes a UTF-8 .txt and a link an Internet Shortcut (.url) in a private transfer folder.
/// Export copies never overwrite: a taken name gets " (2)", " (3)"… and originals stay where they are.
/// </summary>
public sealed class ItemFiles(LibraryStore store, string transferDirectory)
{
    public const int MaxNameLength = 80;

    public ItemFile Materialize(ClipItem item)
    {
        switch (item.Kind)
        {
            case ItemKind.Image when item.Recording:
                var video = store.Media.PathOf(item.CurrentVideoFile);
                if (item.CurrentVideoFile.Length == 0 || !File.Exists(video)) throw new InvalidOperationException("File could not be read or safely saved");
                return new ItemFile(item.Id, video, ExportName(item, Path.GetExtension(video)));
            case ItemKind.Image:
                var media = store.MediaPath(item);
                if (!File.Exists(media)) throw new InvalidOperationException("File could not be read or safely saved");
                return new ItemFile(item.Id, media, ExportName(item, Path.GetExtension(media)));
            case ItemKind.Text:
                return new ItemFile(item.Id, Write(item, "Texts", ".txt", Encoding.UTF8.GetBytes(item.Text)), ExportName(item, ".txt"));
            default:
                if (item.Url.Length == 0) throw new InvalidOperationException("File could not be read or safely saved");
                var shortcut = "[InternetShortcut]\r\nURL=" + item.Url.Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal) + "\r\n";
                return new ItemFile(item.Id, Write(item, "Links", ".url", Encoding.UTF8.GetBytes(shortcut)), ExportName(item, ".url"));
        }
    }

    /// <summary>
    /// A file for drag and drop that already carries its export name, so a drop into File Explorer or an app
    /// sees "Invoice.png" rather than a content hash. Each record gets its own folder; the copy is refreshed
    /// when the content changed.
    /// </summary>
    public string ForDrag(ClipItem item)
    {
        var file = Materialize(item);
        var directory = Path.Combine(transferDirectory, "Drag", item.Id.ToString("N"));
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, file.Name);
        foreach (var stale in Directory.EnumerateFiles(directory).Where(p => !string.Equals(p, target, StringComparison.OrdinalIgnoreCase)))
            TryDelete(stale);
        var source = new FileInfo(file.Path);
        var existing = new FileInfo(target);
        if (!existing.Exists || existing.Length != source.Length || existing.LastWriteTimeUtc < source.LastWriteTimeUtc)
            File.Copy(file.Path, target, overwrite: true);
        return target;
    }

    /// <summary>Removes drag copies left from earlier sessions. Drops have finished copying by then.</summary>
    public void CleanDragCopies()
    {
        var directory = Path.Combine(transferDirectory, "Drag");
        if (!Directory.Exists(directory)) return;
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Copies each record's file into <paramref name="folder"/>. Records that cannot be read are reported, not skipped silently.</summary>
    public ExportResult Export(IEnumerable<ClipItem> items, string folder)
    {
        if (!Directory.Exists(folder)) throw new InvalidOperationException("Choose a valid destination folder");
        var copies = new List<string>();
        var failed = new List<Guid>();
        foreach (var item in items)
        {
            ItemFile file;
            try
            {
                file = Materialize(item);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                failed.Add(item.Id);
                continue;
            }
            var target = CopyUnique(file.Path, folder, file.Name);
            if (target is null) failed.Add(item.Id);
            else copies.Add(target);
        }
        return new ExportResult(copies, failed);
    }

    /// <summary>
    /// The user's name for the record if it has one, otherwise the original file name (with the managed file's
    /// extension), a link's title or host, or the first words of the text; always a valid Windows file name.
    /// </summary>
    public static string ExportName(ClipItem item, string extension)
    {
        string stem;
        if (item.Name.Trim().Length > 0) stem = item.Name;
        else if (item.Kind == ItemKind.Image && item.SourcePath.Length > 0) stem = Path.GetFileNameWithoutExtension(item.SourcePath);
        else if (item.Kind == ItemKind.Link) stem = item.Title.Trim().Length > 0 ? item.Title : Uri.TryCreate(item.Url, UriKind.Absolute, out var uri) ? uri.Host : "Link";
        else if (item.Kind == ItemKind.Text) stem = FirstLine(item.Text);
        else stem = "";
        stem = Sanitize(stem);
        if (stem.Length == 0)
            stem = (item.Recording ? "Recording " : item.Kind == ItemKind.Image ? "Image " : "Text ") + item.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH.mm.ss", CultureInfo.InvariantCulture);
        return stem + extension.ToLowerInvariant();
    }

    public static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
            builder.Append(invalid.Contains(c) || char.IsControl(c) ? ' ' : c);
        var clean = string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim().TrimEnd('.');
        if (clean.Length > MaxNameLength) clean = clean[..MaxNameLength].TrimEnd(' ', '.');
        var reserved = Path.GetFileNameWithoutExtension(clean).ToUpperInvariant();
        if (reserved is "CON" or "PRN" or "AUX" or "NUL" || (reserved.Length == 4 && (reserved.StartsWith("COM", StringComparison.Ordinal) || reserved.StartsWith("LPT", StringComparison.Ordinal)) && char.IsAsciiDigit(reserved[3])))
            clean = "_" + clean;
        return clean;
    }

    /// <summary>Copies without ever replacing an existing file; returns the new path or null.</summary>
    public static string? CopyUnique(string source, string folder, string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var suffix = 1; suffix <= 10_000; suffix++)
        {
            var target = Path.Combine(folder, suffix == 1 ? name : $"{stem} ({suffix}){extension}");
            if (File.Exists(target) || Directory.Exists(target)) continue;
            try
            {
                File.Copy(source, target, overwrite: false);
                return target;
            }
            catch (IOException) when (File.Exists(target))
            {
                // Appeared between the check and the copy: try the next name.
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
        return null;
    }

    private static string FirstLine(string text)
    {
        var line = text.Trim().Split('\n', 2)[0].Trim();
        return line.Length > 48 ? line[..48] : line;
    }

    private string Write(ClipItem item, string folder, string extension, byte[] bytes)
    {
        var directory = Path.Combine(transferDirectory, folder);
        Directory.CreateDirectory(directory);
        // A stable path per record lets repeated drags reuse one file; refresh it when the content changed.
        var path = Path.Combine(directory, (item.Kind == ItemKind.Text ? "Text " : "Link ") + item.Id.ToString("D") + extension);
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) return path;
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
