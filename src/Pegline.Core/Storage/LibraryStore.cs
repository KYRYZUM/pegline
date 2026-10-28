using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Pegline.Core.Capture;
using Pegline.Core.Models;

namespace Pegline.Core.Storage;

/// <summary>
/// The local Library: every record, folder and the session undo journals. Records stay in memory and each
/// operation is written to SQLite in one transaction; a failed write restores the previous in-memory state.
/// </summary>
public sealed class LibraryStore : IDisposable
{
    public const int MaxItems = 20000;
    public const int MaxTakeDownUndo = 32;
    public const int MaxTrashUndo = 80;

    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly Func<DateTimeOffset> _clock;
    private readonly SqliteConnection _db;
    private readonly List<ClipItem> _items = [];
    private readonly List<Folder> _folders = [];
    private readonly Dictionary<Guid, ClipItem> _byId = [];
    private readonly List<List<TakeDownEntry>> _takeDownUndo = [];
    private readonly List<List<TrashEntry>> _trashUndo = [];
    private readonly Lock _gate = new();

    public LibraryStore(string directory, Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.Now);
        Directory.CreateDirectory(directory);
        Media = new MediaStore(Path.Combine(directory, "media"));
        _db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, "library.db"),
            Pooling = false,
        }.ToString());
        _db.Open();
        Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA secure_delete=ON;");
        Execute("CREATE TABLE IF NOT EXISTS items(id TEXT PRIMARY KEY, data TEXT NOT NULL);");
        Execute("CREATE TABLE IF NOT EXISTS folders(id TEXT PRIMARY KEY, data TEXT NOT NULL);");
        Load();
    }

    public MediaStore Media { get; }

    /// <summary>Retention per kind in days; 0 keeps forever. Applied on capture and by <see cref="Expire"/>.</summary>
    public IReadOnlyDictionary<ItemKind, int> RetentionDays { get; set; } = new AppSettings().RetentionDays;

    /// <summary>Raised after every committed change.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<ClipItem> Items
    {
        get { lock (_gate) return [.. _items]; }
    }

    public IReadOnlyList<Folder> Folders
    {
        get { lock (_gate) return [.. _folders]; }
    }

    public bool CanUndoTakeDown
    {
        get { lock (_gate) return _takeDownUndo.Count > 0; }
    }

    public bool CanUndoTrash
    {
        get { lock (_gate) return _trashUndo.Any(batch => batch.Any(TrashEntryStillApplies)); }
    }

    public ClipItem? Find(Guid id)
    {
        lock (_gate) return _byId.GetValueOrDefault(id);
    }

    public IReadOnlyList<string> AllTags
    {
        get
        {
            lock (_gate)
                return [.. _items.Where(i => !i.IsTrashed).SelectMany(i => i.Tags).Distinct().Order(StringComparer.CurrentCultureIgnoreCase)];
        }
    }

    // ----- Capture -----

    public ClipItem InsertText(string text) => Insert(ItemKind.Text, text: text);

    public ClipItem InsertLink(string url, string title = "") =>
        Insert(ItemKind.Link, url: ClipboardAdmission.PlainUrl(url) ?? throw new ArgumentException("Invalid URL."), title: title);

    public ClipItem InsertImage(byte[] png, string sourcePath = "")
    {
        var (width, height) = MediaStore.ReadPngSize(png);
        var file = Media.PutPng(png);
        return Insert(ItemKind.Image, imageFile: file, width: width, height: height, sourcePath: sourcePath);
    }

    /// <summary>A note created in the Library: a saved text record that does not deduplicate against history.</summary>
    public ClipItem CreateNote(string text, string name)
    {
        lock (_gate)
        {
            EnsureCapacity();
            var now = _clock();
            var item = new ClipItem
            {
                Kind = ItemKind.Text,
                Text = text,
                Name = name.Trim(),
                CreatedAt = now,
                UpdatedAt = now,
                RetainedAt = now,
                CaptureRevision = Guid.NewGuid(),
                HiddenFromShelf = true, // notes live in the Library, not on the shelves
            };
            Commit(tx => tx.Add(item));
            return item;
        }
    }

    private ClipItem Insert(ItemKind kind, string text = "", string url = "", string title = "", string imageFile = "", int width = 0, int height = 0, string sourcePath = "")
    {
        if (kind == ItemKind.Text && !ClipboardAdmission.AdmitsText(text))
            throw new ArgumentException("Text is empty or too large.");
        lock (_gate)
        {
            var identity = kind == ItemKind.Text ? TextIdentity.Of(text) : null;
            var matching = _items.Where(i => i.Kind == kind && !i.IsTrashed && kind switch
            {
                ItemKind.Text => TextIdentity.Of(i.Text) == identity,
                ItemKind.Link => i.Url == url,
                _ => !i.Recording && i.ImageFile == imageFile,
            }).OrderByDescending(i => i.UpdatedAt).ToList();
            var prior = matching.FirstOrDefault();
            if (prior is null)
                EnsureCapacity();

            var now = _clock();
            ClipItem result = null!;
            Commit(tx =>
            {
                ClipItem item;
                if (prior is null)
                {
                    item = new ClipItem { Kind = kind, Url = url, ImageFile = imageFile, PixelWidth = width, PixelHeight = height, SourcePath = sourcePath };
                    tx.Add(item);
                }
                else
                {
                    item = tx.Touch(prior);
                    // Merge organization from older duplicates into the surviving identity.
                    foreach (var duplicate in matching.Skip(1))
                    {
                        item.Pinned |= duplicate.Pinned;
                        item.FolderIds = [.. item.FolderIds.Union(duplicate.FolderIds)];
                        item.Tags = [.. item.Tags.Union(duplicate.Tags)];
                        if (item.Name.Length == 0) item.Name = duplicate.Name;
                        if (item.Note.Length == 0) item.Note = duplicate.Note;
                        if (duplicate.RetainedAt is { } retained && (item.RetainedAt is null || retained < item.RetainedAt))
                            item.RetainedAt = retained;
                        tx.Remove(duplicate);
                    }
                }
                item.CreatedAt = now;
                item.UpdatedAt = now;
                item.HiddenFromShelf = false;
                item.CaptureRevision = Guid.NewGuid();
                if (kind == ItemKind.Text)
                    item.Text = text;
                if (title.Length > 0)
                    item.Title = title.Length > 500 ? title[..500] : title;
                ExpireInto(tx, now);
                result = item;
            });
            return result;
        }
    }

    // ----- Shelf operations -----

    /// <summary>Takes items off the shelf. Saved records stay in the Library; temporary ones go to Trash.</summary>
    public void TakeDown(IEnumerable<Guid> ids)
    {
        lock (_gate)
        {
            var items = Select(ids);
            if (items.Count == 0) return;
            var undo = items.Select(i => new TakeDownEntry(i.Id, i.TrashedAt, i.HiddenFromShelf)).ToList();
            var now = _clock();
            Commit(tx =>
            {
                foreach (var original in items)
                {
                    var item = tx.Touch(original);
                    if (item.IsSaved)
                        item.HiddenFromShelf = true;
                    else
                    {
                        item.TrashedAt = now;
                        item.TrashRevision = Guid.NewGuid();
                    }
                    item.UpdatedAt = now;
                }
            });
            _takeDownUndo.Add(undo);
            if (_takeDownUndo.Count > MaxTakeDownUndo)
                _takeDownUndo.RemoveAt(0);
            ForgetTrashUndo(items.Select(i => i.Id));
        }
    }

    /// <summary>Restores the latest take-down batch of this session. Returns the restored ids.</summary>
    public IReadOnlyList<Guid> UndoTakeDown()
    {
        lock (_gate)
        {
            while (_takeDownUndo.Count > 0)
            {
                var batch = _takeDownUndo[^1];
                _takeDownUndo.RemoveAt(_takeDownUndo.Count - 1);
                var live = batch.Where(e => _byId.ContainsKey(e.Id)).ToList();
                if (live.Count == 0) continue;
                Commit(tx =>
                {
                    foreach (var entry in live)
                    {
                        var item = tx.Touch(_byId[entry.Id]);
                        item.TrashedAt = entry.TrashedAt;
                        item.TrashRevision = entry.TrashedAt is null ? null : Guid.NewGuid();
                        item.HiddenFromShelf = entry.HiddenFromShelf;
                    }
                });
                return [.. live.Select(e => e.Id)];
            }
            return [];
        }
    }

    /// <summary>Toggles pinning for a batch: pins all unless every item is already pinned.</summary>
    public void TogglePin(IEnumerable<Guid> ids, bool? value = null)
    {
        lock (_gate)
        {
            var items = Select(ids);
            if (items.Count == 0) return;
            var target = value ?? items.Any(i => !i.Pinned);
            var now = _clock();
            Mutate(items, item =>
            {
                item.Pinned = target;
                if (target) item.RetainedAt ??= now;
                item.UpdatedAt = now;
            });
        }
    }

    public void MarkCopied(IEnumerable<Guid> ids)
    {
        lock (_gate)
        {
            var now = _clock();
            Mutate(Select(ids), item =>
            {
                item.LastCopiedAt = now;
                item.CaptureRevision = Guid.NewGuid();
            });
        }
    }

    /// <summary>Keeps items in the Library so retention never expires them.</summary>
    public void Retain(IEnumerable<Guid> ids)
    {
        lock (_gate)
        {
            var now = _clock();
            Mutate(Select(ids).Where(i => i.RetainedAt is null).ToList(), item =>
            {
                item.RetainedAt = now;
                item.UpdatedAt = now;
            });
        }
    }

    /// <summary>Replaces a text item's content. Returns false when the text is not admissible as a clip.</summary>
    public bool EditText(Guid id, string text)
    {
        if (!ClipboardAdmission.AdmitsText(text)) return false;
        lock (_gate)
        {
            var item = Find(id);
            if (item is null || item.Kind != ItemKind.Text || item.IsTrashed) return false;
            if (item.Text == text) return true;
            SetDetails(id, text: text);
            return true;
        }
    }

    // ----- Organization -----

    public void NameAndPin(Guid id, string name)
    {
        lock (_gate)
        {
            var now = _clock();
            Mutate(Select([id]), item =>
            {
                item.Name = Limit(name.Trim(), 200);
                item.Pinned = true;
                item.RetainedAt ??= now;
                item.UpdatedAt = now;
            });
        }
    }

    public void SetDetails(Guid id, string? name = null, string? note = null, string? text = null)
    {
        lock (_gate)
        {
            var now = _clock();
            Mutate(Select([id]), item =>
            {
                if (name is not null) item.Name = Limit(name.Trim(), 200);
                if (note is not null) item.Note = Limit(note, ClipboardAdmission.MaxTextChars);
                if (text is not null)
                {
                    if (item.Kind != ItemKind.Text) throw new InvalidOperationException("Only text can be edited.");
                    item.Text = Limit(text, ClipboardAdmission.MaxTextChars);
                }
                item.UpdatedAt = now;
            });
        }
    }

    public void AddToFolder(IEnumerable<Guid> ids, Guid folderId, bool pin = true, bool rememberRecent = true)
    {
        lock (_gate)
        {
            if (!_folders.Any(f => f.Id == folderId)) throw new InvalidOperationException("Folder unavailable.");
            var now = _clock();
            Mutate(Select(ids), item =>
            {
                if (!item.FolderIds.Contains(folderId)) item.FolderIds.Add(folderId);
                if (pin) item.Pinned = true;
                item.RetainedAt ??= now;
                item.UpdatedAt = now;
            });
            if (rememberRecent)
                RecentFolderUsed?.Invoke(this, folderId);
        }
    }

    /// <summary>Raised when a folder is chosen in the pin-to-folder picker so settings can remember it.</summary>
    public event EventHandler<Guid>? RecentFolderUsed;

    public void RemoveFromFolder(IEnumerable<Guid> ids, Guid folderId)
    {
        lock (_gate)
        {
            var now = _clock();
            Mutate(Select(ids), item =>
            {
                item.FolderIds.Remove(folderId);
                item.UpdatedAt = now;
            });
        }
    }

    /// <summary>Adds tags to every selected item, preserving each item's existing tags.</summary>
    public void AddTags(IEnumerable<Guid> ids, IEnumerable<string> tags)
    {
        var clean = tags.Select(t => Limit(t.Trim(), 64)).Where(t => t.Length > 0).Distinct().ToList();
        if (clean.Count == 0) return;
        lock (_gate)
        {
            var now = _clock();
            Mutate(Select(ids), item =>
            {
                foreach (var tag in clean)
                    if (!item.Tags.Contains(tag)) item.Tags.Add(tag);
                item.RetainedAt ??= now;
                item.UpdatedAt = now;
            });
        }
    }

    public void RemoveTag(IEnumerable<Guid> ids, string tag)
    {
        lock (_gate)
        {
            var now = _clock();
            Mutate(Select(ids), item =>
            {
                item.Tags.Remove(tag);
                item.UpdatedAt = now;
            });
        }
    }

    public void RenameTag(string tag, string name)
    {
        name = name.Trim();
        if (name.Length == 0 || name.Length > 64 || name.Any(char.IsControl)) throw new ArgumentException("Invalid tag name.");
        lock (_gate)
        {
            if (_items.Any(i => i.Tags.Any(t => t != tag && Fold(t) == Fold(name))))
                throw new InvalidOperationException("A tag with this name already exists.");
            var now = _clock();
            Mutate([.. _items.Where(i => i.Tags.Contains(tag))], item =>
            {
                item.RetainedAt ??= now;
                item.Tags = [.. item.Tags.Select(t => t == tag ? name : t).Distinct()];
            });
        }
    }

    public void DeleteTag(string tag)
    {
        lock (_gate)
        {
            var now = _clock();
            Mutate([.. _items.Where(i => i.Tags.Contains(tag))], item =>
            {
                item.RetainedAt ??= now;
                item.Tags.Remove(tag);
            });
        }
    }

    public Folder CreateFolder(string name, Guid? parentId = null)
    {
        name = Limit(name.Trim(), 120);
        if (name.Length == 0) throw new ArgumentException("Folder name is empty.");
        lock (_gate)
        {
            if (parentId is { } parent)
            {
                var parentFolder = _folders.FirstOrDefault(f => f.Id == parent) ?? throw new InvalidOperationException("Parent folder is unavailable.");
                if (parentFolder.ParentId is not null) throw new InvalidOperationException("Only top-level folders can contain subfolders.");
            }
            var folder = new Folder { Name = name, ParentId = parentId };
            Commit(tx => tx.AddFolder(folder));
            return folder;
        }
    }

    public void RenameFolder(Guid id, string name)
    {
        name = Limit(name.Trim(), 120);
        if (name.Length == 0) throw new ArgumentException("Folder name is empty.");
        lock (_gate)
        {
            var folder = _folders.FirstOrDefault(f => f.Id == id) ?? throw new InvalidOperationException("Folder unavailable.");
            Commit(tx => tx.TouchFolder(folder).Name = name);
        }
    }

    /// <summary>Removes a folder and its subfolders. Members keep a saved state so they are not expired.</summary>
    public void RemoveFolder(Guid id)
    {
        lock (_gate)
        {
            var removed = new HashSet<Guid> { id };
            foreach (var child in _folders.Where(f => f.ParentId == id)) removed.Add(child.Id);
            var now = _clock();
            Commit(tx =>
            {
                foreach (var folder in _folders.Where(f => removed.Contains(f.Id)).ToList())
                    tx.RemoveFolder(folder);
                foreach (var original in _items.Where(i => i.FolderIds.Any(removed.Contains)).ToList())
                {
                    var item = tx.Touch(original);
                    item.RetainedAt ??= now;
                    item.FolderIds.RemoveAll(removed.Contains);
                }
            });
        }
    }

    // ----- Trash -----

    /// <summary>Moves items to Trash as one undoable batch (Ctrl+Z in the Library).</summary>
    public IReadOnlyList<Guid> MoveToTrash(IEnumerable<Guid> ids)
    {
        lock (_gate)
        {
            var items = Select(ids).Where(i => !i.IsTrashed).ToList();
            if (items.Count == 0) return [];
            var before = items.Select(i => (i.Id, i.HiddenFromShelf)).ToList();
            var now = _clock();
            Commit(tx =>
            {
                foreach (var original in items)
                {
                    var item = tx.Touch(original);
                    item.TrashedAt = now;
                    item.TrashRevision = Guid.NewGuid();
                    item.HiddenFromShelf = true;
                    item.UpdatedAt = now;
                }
            });
            var affected = items.Select(i => i.Id).ToHashSet();
            ForgetTakeDownUndo(affected);
            ForgetTrashUndo(affected);
            _trashUndo.Add([.. before.Select(b => new TrashEntry(b.Id, b.HiddenFromShelf, _byId[b.Id].TrashRevision))]);
            if (_trashUndo.Count > MaxTrashUndo) _trashUndo.RemoveAt(0);
            return [.. affected];
        }
    }

    public IReadOnlyList<Guid> UndoTrash()
    {
        lock (_gate)
        {
            for (var index = _trashUndo.Count - 1; index >= 0; index--)
            {
                var entries = _trashUndo[index].Where(TrashEntryStillApplies).ToList();
                if (entries.Count == 0) continue;
                Commit(tx =>
                {
                    foreach (var entry in entries)
                    {
                        var item = tx.Touch(_byId[entry.Id]);
                        item.TrashedAt = null;
                        item.TrashRevision = null;
                        item.HiddenFromShelf = entry.HiddenFromShelf;
                    }
                });
                _trashUndo.RemoveRange(index, _trashUndo.Count - index);
                return [.. entries.Select(e => e.Id)];
            }
            _trashUndo.Clear();
            return [];
        }
    }

    public void Restore(IEnumerable<Guid> ids)
    {
        lock (_gate)
        {
            var items = Select(ids);
            Mutate(items, item =>
            {
                item.TrashedAt = null;
                item.TrashRevision = null;
                item.HiddenFromShelf = false;
            });
            var affected = items.Select(i => i.Id).ToHashSet();
            ForgetTakeDownUndo(affected);
            ForgetTrashUndo(affected);
        }
    }

    /// <summary>Permanently deletes trashed records and any managed media nothing else references.</summary>
    public void DeletePermanently(IEnumerable<Guid> ids)
    {
        lock (_gate)
        {
            var items = Select(ids);
            if (items.Any(i => !i.IsTrashed)) throw new InvalidOperationException("Move items to Trash before permanent deletion.");
            Commit(tx =>
            {
                foreach (var item in items) tx.Remove(item);
            });
            var affected = items.Select(i => i.Id).ToHashSet();
            ForgetTakeDownUndo(affected);
            ForgetTrashUndo(affected);
            Media.DeleteUnreferenced(ReferencedMedia());
        }
    }

    public void EmptyTrash()
    {
        lock (_gate)
            DeletePermanently([.. _items.Where(i => i.IsTrashed).Select(i => i.Id)]);
    }

    /// <summary>A bounded identity snapshot of unsaved history; the confirmation applies to exactly these capture revisions.</summary>
    public HistorySnapshot SnapshotUnsavedHistory(ItemKind? kind = null)
    {
        lock (_gate)
            return new HistorySnapshot([.. _items.Where(i => !i.IsTrashed && !i.IsSaved && (kind is null || i.Kind == kind)).Select(i => new HistorySnapshot.Entry(i.Id, i.CaptureRevision, i.CreatedAt, i.UpdatedAt, i.LastCopiedAt))]);
    }

    /// <summary>
    /// Moves the snapshotted history to Trash in one transaction. Items saved, re-copied or created after the
    /// snapshot are excluded.
    /// </summary>
    public IReadOnlyList<Guid> ClearUnsavedHistory(HistorySnapshot snapshot)
    {
        lock (_gate)
        {
            var affected = snapshot.Entries
                .Select(e => (Entry: e, Item: _byId.GetValueOrDefault(e.Id)))
                .Where(p => p.Item is { IsTrashed: false, IsSaved: false } item
                    && item.CaptureRevision == p.Entry.CaptureRevision && item.CreatedAt == p.Entry.CreatedAt
                    && item.UpdatedAt == p.Entry.UpdatedAt && item.LastCopiedAt == p.Entry.LastCopiedAt)
                .Select(p => p.Item!)
                .ToList();
            if (affected.Count == 0) return [];
            var now = _clock();
            Mutate(affected, item =>
            {
                item.TrashedAt = now;
                item.TrashRevision = Guid.NewGuid();
                item.HiddenFromShelf = true;
                item.UpdatedAt = now;
            });
            var ids = affected.Select(i => i.Id).ToHashSet();
            ForgetTakeDownUndo(ids);
            ForgetTrashUndo(ids);
            return [.. ids];
        }
    }

    /// <summary>Moves unsaved records older than their kind's retention into Trash.</summary>
    public void Expire()
    {
        lock (_gate)
        {
            var now = _clock();
            if (!_items.Any(i => IsExpired(i, now))) return;
            Commit(tx => ExpireInto(tx, now));
        }
    }

    // ----- Images -----

    /// <summary>Stores an edited rendering while the original file and editable metadata remain available.</summary>
    public void SaveImageEdit(Guid id, byte[] renderedPng, string annotationsJson, CropRect? crop)
    {
        MediaStore.ReadPngSize(renderedPng);
        lock (_gate)
        {
            var item = Select([id]).Single();
            if (item.Kind != ItemKind.Image || item.Recording) throw new InvalidOperationException("Only images can be edited.");
            var rendered = Media.PutPng(renderedPng);
            var now = _clock();
            Mutate([item], i =>
            {
                i.RenderedFile = rendered;
                i.AnnotationsJson = annotationsJson;
                i.Crop = crop;
                i.OcrText = "";
                i.UpdatedAt = now;
            });
        }
    }

    public void SetOcrText(Guid id, string text)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue(id, out var item)) return;
            Mutate([item], i => i.OcrText = Limit(text, ClipboardAdmission.MaxTextChars));
        }
    }

    public void SetLinkMetadata(Guid id, string title, string siteName, string author, string thumbnailFile)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue(id, out var item) || item.Kind != ItemKind.Link) return;
            Mutate([item], i =>
            {
                i.Title = Limit(title, 500);
                i.LinkSiteName = Limit(siteName, 200);
                i.LinkAuthor = Limit(author, 200);
                i.LinkThumbnailFile = thumbnailFile;
            });
        }
    }

    public string MediaPath(ClipItem item, bool original = false) =>
        Media.PathOf(original ? item.ImageFile : item.DisplayFile);

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
    }

    // ----- Internals -----

    private bool IsExpired(ClipItem item, DateTimeOffset now) =>
        !item.IsTrashed && !item.IsSaved && RetentionDays.TryGetValue(item.Kind, out var days) && days > 0 && item.CreatedAt < now.AddDays(-days);

    private void ExpireInto(Transaction tx, DateTimeOffset now)
    {
        foreach (var original in _items.Where(i => IsExpired(i, now)).ToList())
        {
            var item = tx.Touch(original);
            item.TrashedAt = now;
            item.TrashRevision = Guid.NewGuid();
        }
    }

    private HashSet<string> ReferencedMedia() =>
        [.. _items.SelectMany(i => new[] { i.ImageFile, i.RenderedFile, i.ThumbnailFile, i.LinkThumbnailFile, i.VideoFile, i.RenderedVideoFile }).Where(n => n.Length > 0)];

    private bool TrashEntryStillApplies(TrashEntry entry) =>
        _byId.TryGetValue(entry.Id, out var item) && item.IsTrashed && item.TrashRevision == entry.TrashRevision;

    private void ForgetTakeDownUndo(IEnumerable<Guid> ids)
    {
        var set = ids.ToHashSet();
        foreach (var batch in _takeDownUndo) batch.RemoveAll(e => set.Contains(e.Id));
        _takeDownUndo.RemoveAll(b => b.Count == 0);
    }

    private void ForgetTrashUndo(IEnumerable<Guid> ids)
    {
        var set = ids.ToHashSet();
        foreach (var batch in _trashUndo) batch.RemoveAll(e => set.Contains(e.Id));
        _trashUndo.RemoveAll(b => b.Count == 0);
    }

    private List<ClipItem> Select(IEnumerable<Guid> ids)
    {
        var result = new List<ClipItem>();
        foreach (var id in ids.Distinct())
            result.Add(_byId.GetValueOrDefault(id) ?? throw new InvalidOperationException("An item no longer exists."));
        return result;
    }

    private void EnsureCapacity()
    {
        if (_items.Count >= MaxItems) throw new InvalidOperationException("Library item limit reached.");
    }

    private void Mutate(IReadOnlyList<ClipItem> items, Action<ClipItem> change)
    {
        if (items.Count == 0) return;
        Commit(tx =>
        {
            foreach (var item in items) change(tx.Touch(item));
        });
    }

    private void Commit(Action<Transaction> change)
    {
        var tx = new Transaction(this);
        try
        {
            change(tx);
            tx.Write();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Load()
    {
        using (var command = _db.CreateCommand())
        {
            command.CommandText = "SELECT data FROM items";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var item = JsonSerializer.Deserialize<ClipItem>(reader.GetString(0), Json);
                if (item is null || _byId.ContainsKey(item.Id)) continue;
                _items.Add(item);
                _byId[item.Id] = item;
            }
        }
        using (var command = _db.CreateCommand())
        {
            command.CommandText = "SELECT data FROM folders";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                if (JsonSerializer.Deserialize<Folder>(reader.GetString(0), Json) is { } folder)
                    _folders.Add(folder);
        }
        SortItems();
    }

    private void SortItems() => _items.Sort((a, b) => b.CreatedAt.CompareTo(a.CreatedAt));

    private void Execute(string sql)
    {
        using var command = _db.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string Limit(string value, int max) => value.Length > max ? value[..max] : value;

    private static string Fold(string value) =>
        new string([.. value.Normalize(System.Text.NormalizationForm.FormD).Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)]).ToLowerInvariant();

    private sealed record TakeDownEntry(Guid Id, DateTimeOffset? TrashedAt, bool HiddenFromShelf);

    private sealed record TrashEntry(Guid Id, bool HiddenFromShelf, Guid? TrashRevision);

    /// <summary>Tracks touched records so a failed SQLite write can restore the exact previous state.</summary>
    private sealed class Transaction(LibraryStore store)
    {
        private readonly Dictionary<Guid, ClipItem?> _originalItems = [];
        private readonly Dictionary<Guid, Folder?> _originalFolders = [];
        private readonly HashSet<Guid> _removedItems = [];
        private readonly HashSet<Guid> _removedFolders = [];
        private readonly List<ClipItem> _orderBefore = [.. store._items];
        private readonly List<Folder> _foldersBefore = [.. store._folders];

        public ClipItem Touch(ClipItem item)
        {
            if (!_originalItems.ContainsKey(item.Id))
                _originalItems[item.Id] = item.Clone();
            return item;
        }

        public void Add(ClipItem item)
        {
            _originalItems.TryAdd(item.Id, null);
            store._items.Insert(0, item);
            store._byId[item.Id] = item;
        }

        public void Remove(ClipItem item)
        {
            Touch(item);
            _removedItems.Add(item.Id);
            store._items.Remove(item);
            store._byId.Remove(item.Id);
        }

        public Folder TouchFolder(Folder folder)
        {
            if (!_originalFolders.ContainsKey(folder.Id))
                _originalFolders[folder.Id] = folder.Clone();
            return folder;
        }

        public void AddFolder(Folder folder)
        {
            _originalFolders.TryAdd(folder.Id, null);
            store._folders.Add(folder);
        }

        public void RemoveFolder(Folder folder)
        {
            TouchFolder(folder);
            _removedFolders.Add(folder.Id);
            store._folders.Remove(folder);
        }

        public void Write()
        {
            using var transaction = store._db.BeginTransaction();
            foreach (var id in _originalItems.Keys)
            {
                if (_removedItems.Contains(id))
                    Run(transaction, "DELETE FROM items WHERE id=$id", id, null);
                else if (store._byId.TryGetValue(id, out var item))
                    Run(transaction, "INSERT INTO items(id,data) VALUES($id,$data) ON CONFLICT(id) DO UPDATE SET data=excluded.data", id, JsonSerializer.Serialize(item, Json));
            }
            foreach (var id in _originalFolders.Keys)
            {
                var folder = store._folders.FirstOrDefault(f => f.Id == id);
                if (_removedFolders.Contains(id) || folder is null)
                    Run(transaction, "DELETE FROM folders WHERE id=$id", id, null);
                else
                    Run(transaction, "INSERT INTO folders(id,data) VALUES($id,$data) ON CONFLICT(id) DO UPDATE SET data=excluded.data", id, JsonSerializer.Serialize(folder, Json));
            }
            transaction.Commit();
            store.SortItems();
        }

        public void Rollback()
        {
            var restored = new Dictionary<Guid, ClipItem>();
            foreach (var (id, original) in _originalItems)
                if (original is not null) restored[id] = original;
            store._items.Clear();
            store._byId.Clear();
            foreach (var item in _orderBefore)
            {
                var value = restored.GetValueOrDefault(item.Id, item);
                store._items.Add(value);
                store._byId[value.Id] = value;
            }
            store._folders.Clear();
            foreach (var folder in _foldersBefore)
                store._folders.Add(_originalFolders.GetValueOrDefault(folder.Id) ?? folder);
        }

        private static void Run(SqliteTransaction transaction, string sql, Guid id, string? data)
        {
            using var command = transaction.Connection!.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.Parameters.AddWithValue("$id", id.ToString());
            if (data is not null) command.Parameters.AddWithValue("$data", data);
            command.ExecuteNonQuery();
        }
    }
}

public sealed record HistorySnapshot(IReadOnlyList<HistorySnapshot.Entry> Entries)
{
    public sealed record Entry(Guid Id, Guid? CaptureRevision, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? LastCopiedAt);
}
