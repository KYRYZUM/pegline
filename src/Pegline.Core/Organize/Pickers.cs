using Pegline.Core.Models;
using Pegline.Core.Storage;
using Pegline.Core.Text;

namespace Pegline.Core.Organize;

/// <summary>One row of the pin-to-folder picker. A null <see cref="Id"/> pins without a folder.</summary>
public sealed record FolderChoice(Guid? Id, string Title, int Depth);

public static class FolderChoices
{
    /// <summary>
    /// Top-level folders each followed by their subfolders, then reordered so recently used folders come first
    /// (most recent first, otherwise the tree order stays). "Pin without folder" is always last.
    /// </summary>
    public static IReadOnlyList<FolderChoice> All(IReadOnlyList<Folder> folders, IReadOnlyList<Guid> recent, string withoutFolder)
    {
        var names = folders.ToDictionary(f => f.Id, f => f.Name);
        string TitleOf(Folder f) => f.ParentId is { } parent && names.TryGetValue(parent, out var name) ? name + " / " + f.Name : f.Name;
        var tree = folders.Where(f => f.ParentId is null)
            .SelectMany(parent => new[] { new FolderChoice(parent.Id, TitleOf(parent), 0) }
                .Concat(folders.Where(f => f.ParentId == parent.Id).Select(f => new FolderChoice(f.Id, TitleOf(f), 1))))
            .ToList();
        var rank = new Dictionary<Guid, int>();
        for (var i = 0; i < recent.Count; i++) rank.TryAdd(recent[i], i);
        var ordered = tree.Select((choice, index) => (choice, index))
            .OrderBy(e => rank.GetValueOrDefault(e.choice.Id!.Value, int.MaxValue))
            .ThenBy(e => e.index)
            .Select(e => e.choice)
            .ToList();
        ordered.Add(new FolderChoice(null, withoutFolder, 0));
        return ordered;
    }

    /// <summary>Every typed term must appear in the folded "Parent / Folder" title.</summary>
    public static IReadOnlyList<FolderChoice> Filter(IReadOnlyList<FolderChoice> all, string query)
    {
        var terms = Folding.Terms(query);
        return [.. all.Where(c => terms.All(Folding.Fold(c.Title).Contains))];
    }
}

/// <summary>One row of the tag picker: an existing tag, or a new one built from the typed text.</summary>
public sealed record TagChoice(string Name, bool IsNew);

public static class TagChoices
{
    public const int MaxLength = 64;

    /// <summary>
    /// Existing tags matching every typed term, followed by "create" for the typed name when it is valid and
    /// not already a tag. Leading/trailing '#' and spaces are ignored.
    /// </summary>
    public static IReadOnlyList<TagChoice> Build(IReadOnlyList<string> tags, string query)
    {
        var name = query.Trim().Trim('#').Trim();
        var terms = Folding.Terms(name);
        var choices = tags.Where(t => terms.All(Folding.Fold(t).Contains)).Select(t => new TagChoice(t, false)).ToList();
        if (name.Length > 0 && name.Length <= MaxLength && !name.Any(char.IsControl)
            && !tags.Any(t => Folding.Fold(t) == Folding.Fold(name)))
            choices.Add(new TagChoice(name, true));
        return choices;
    }
}

/// <summary>
/// Name and pin, one item at a time. The ordered selection is frozen when the picker opens, so later shelf
/// changes cannot broaden the batch. A name is metadata only; the copied text, URL or pixels never change.
/// </summary>
public sealed class NameAndPinSession
{
    private readonly LibraryStore _store;
    private readonly IReadOnlyList<Guid> _ids;

    public NameAndPinSession(LibraryStore store, IEnumerable<Guid> ids)
    {
        _store = store;
        _ids = [.. ids.Distinct()];
        if (_ids.Count == 0) throw new InvalidOperationException("Nothing to name.");
        if (_ids.Any(id => store.Find(id) is not { IsTrashed: false })) throw new InvalidOperationException(Unavailable);
    }

    public const string Unavailable = "This item is no longer available. Press Esc to leave.";

    public int Index { get; private set; }

    public int Count => _ids.Count;

    public Guid Current => _ids[Index];

    public bool HasNext => Index + 1 < _ids.Count;

    public ClipItem? CurrentItem => _store.Find(Current) is { IsTrashed: false } item ? item : null;

    /// <summary>
    /// Saves the name, pins the current item and advances past items removed since the batch opened.
    /// Returns true when the batch is finished. Throws when the current item is no longer available.
    /// </summary>
    public bool Confirm(string name)
    {
        if (CurrentItem is null) throw new InvalidOperationException(Unavailable);
        _store.NameAndPin(Current, name);
        for (var next = Index + 1; next < _ids.Count; next++)
        {
            if (_store.Find(_ids[next]) is not { IsTrashed: false }) continue;
            Index = next;
            return false;
        }
        return true;
    }
}
