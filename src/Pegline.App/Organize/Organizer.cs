using System.IO;
using System.Windows;
using Pegline.App.Localization;
using Pegline.App.Platform;
using Pegline.App.Shelves;
using Pegline.Core.Models;
using Pegline.Core.Organize;
using Pegline.Core.Storage;
using Pegline.Core.Transfer;

namespace Pegline.App.Organize;

/// <summary>
/// Opens the organizing pickers (folder, tag, name) and runs export for whichever window asked. Each picker
/// captures its ids when it opens, so later selection changes cannot broaden the batch.
/// </summary>
internal sealed class Organizer(LibraryStore store, SettingsStore settings, ItemFiles files)
{
    public ItemFiles Files => files;

    /// <summary>Pin to folder… (Shift+P): recent folders first, Ctrl+N creates one, the last row pins without a folder.</summary>
    public HostedWindow? PinToFolder(ShelfPalette palette, IReadOnlyList<Guid> ids, Action<string> notify)
    {
        var captured = Live(ids);
        if (captured.Count == 0) return null;
        PickerWindow? picker = null;
        picker = new PickerWindow(palette,
            Loc.F("Pin {0} items to a folder", captured.Count),
            Loc.T("Search folders…"),
            Loc.T("No matching folders"),
            Loc.T("Pin favorite"),
            query => [.. FolderChoices.Filter(FolderChoices.All(store.Folders, settings.Current.RecentPinFolderIds, Loc.T("Pin without folder")), query)
                .Select(c => new PickerRow(c.Id, c.Title, Glyphs.For(c.Id is null ? "pin" : "folder"), c.Depth))],
            row => Attempt(() =>
            {
                if (row.Key is Guid folder)
                {
                    store.AddToFolder(captured, folder);
                    notify(Loc.T("Added to folder"));
                }
                else
                {
                    store.TogglePin(captured, true);
                    notify(Loc.T("Pinned"));
                }
            }),
            (Loc.T("New folder"), "Ctrl+N", () => picker?.BeginCreate(Loc.T("New folder"), name =>
            {
                try
                {
                    return (null, store.CreateFolder(name).Id);
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
                {
                    return (Loc.T(ex.Message), null);
                }
            })));
        return picker;
    }

    /// <summary>Add tag… (Ctrl+Shift+T): pick an existing tag or create the typed one, for every captured item.</summary>
    public HostedWindow? AddTag(ShelfPalette palette, IReadOnlyList<Guid> ids, Action<string> notify)
    {
        var captured = Live(ids);
        if (captured.Count == 0) return null;
        var tags = store.AllTags;
        return new PickerWindow(palette,
            Loc.F("Add tag to {0} items", captured.Count),
            Loc.T("Search tags…"),
            Loc.T(tags.Count == 0 ? "Type a name to create a tag" : "No matching tags"),
            Loc.T("Add tag"),
            query => [.. TagChoices.Build(tags, query)
                .Select(c => new PickerRow(c.Name, c.IsNew ? Loc.F("Create tag “{0}”", c.Name) : c.Name, Glyphs.For(c.IsNew ? "add" : "tag")))],
            row => Attempt(() =>
            {
                store.AddTags(captured, [(string)row.Key!]);
                notify(Loc.F("Tagged “{0}”", (string)row.Key!));
            }));
    }

    /// <summary>Name and pin… (Alt+P), item by item in the given order.</summary>
    public NameAndPinWindow? NameAndPin(ShelfPalette palette, IReadOnlyList<Guid> ids, Action<string> notify)
    {
        try
        {
            return new NameAndPinWindow(palette, new NameAndPinSession(store, ids));
        }
        catch (InvalidOperationException ex)
        {
            notify(Loc.T(ex.Message));
            return null;
        }
    }

    /// <summary>Export copies… (Ctrl+Shift+E): asks for a folder and copies there without replacing anything.</summary>
    public void Export(Window owner, IReadOnlyList<ClipItem> items, Action<string> notify)
    {
        var live = items.Select(i => store.Find(i.Id)).OfType<ClipItem>().Where(i => !i.IsTrashed).ToList();
        if (live.Count == 0) return;
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = Loc.T("Export copies"),
            InitialDirectory = Directory.Exists(settings.Current.LastExportFolder) ? settings.Current.LastExportFolder : Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        };
        if (dialog.ShowDialog(owner) != true) return;
        try
        {
            var result = files.Export(live, dialog.FolderName);
            settings.Update(s => s.LastExportFolder = dialog.FolderName);
            notify(result.Failed.Count == 0
                ? Loc.F("Exported {0}", result.Copies.Count)
                : Loc.F("Exported {0} · failed {1}", result.Copies.Count, result.Failed.Count));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            notify(Loc.T("Could not export copies"));
        }
    }

    private List<Guid> Live(IReadOnlyList<Guid> ids) => [.. ids.Distinct().Where(id => store.Find(id) is { IsTrashed: false })];

    private static string? Attempt(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return Loc.T(ex.Message);
        }
    }
}
