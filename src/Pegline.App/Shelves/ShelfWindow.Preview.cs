using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Pegline.App.Actions;
using Pegline.App.Localization;
using Pegline.App.Platform;
using Pegline.App.Preview;
using Pegline.Core.Actions;
using Pegline.Core.Input;
using Pegline.Core.Models;
using Pegline.Core.Shelves;

namespace Pegline.App.Shelves;

/// <summary>Space preview and the Actions palette as they open from the shelves.</summary>
public partial class ShelfWindow : IPreviewHost
{
    private PreviewWindow? _preview;
    private ActionPalette? _actions;
    private bool _closingChildren;

    private static readonly HashSet<string> ImplementedActions =
    [
        "previous", "next", "up", "down", "first", "last", "previousPage", "nextPage", "copy", "paste", "selectAll", "pin",
        "pinnedOnly", "preview", "close", "switchShelf", "toggleShelves", "trash", "remove", "undo", "toggleLinks", "toggleTexts",
        "toggleImages", "search", "open", "edit", "finishEditing", "extendPrevious", "extendNext", "extendUp", "extendDown",
        "select1", "select2", "select3", "select4", "select5", "select6", "select7", "select8", "select9",
        "menu.toggleLine", "menu.quit", "menu.retain", "menu.takeDownLeft", "menu.takeDownRight", "menu.resetZoom",
        "menu.copyLatestImage", "menu.openFile", "menu.revealFile", "menu.clearUnsavedHistory", "menu.openScreenshotsFolder",
    ];

    Keymap IPreviewHost.Keymap => _keymap;

    // ----- Preview -----

    private void OpenPreview()
    {
        if (_nav.FocusedItem is not { } item)
        {
            ShowToast(Loc.T("Select an item first"));
            return;
        }
        // A preview acts on its current item, independently of a batch selection.
        _nav.Select(item.Id);
        Render();
        var preview = _preview ??= CreatePreview();
        if (!preview.Present(item, PositionOf(item), PreviewPlacement()))
        {
            ShowToast(Loc.T("Preview unavailable"));
            if (!preview.IsOpen) preview.ForceClose();
        }
    }

    private PreviewWindow CreatePreview()
    {
        var preview = new PreviewWindow(this, _store, _palette) { Owner = this };
        return preview;
    }

    /// <summary>Reuses the open preview's center; a new preview centers on the shelves' monitor.</summary>
    private Placement PreviewPlacement()
    {
        if (_preview is { IsOpen: true } open && Win32.GetWindowRect(open.Handle, out var rect))
        {
            var (work, scale) = Win32.MonitorOf(open.Handle);
            return new Placement(work, scale, rect);
        }
        return new Placement(_work, _scale, _work);
    }

    private string PositionOf(ClipItem item)
    {
        var row = _nav.Row(item.Kind);
        var index = row.ToList().FindIndex(i => i.Id == item.Id);
        return $"{index + 1} / {row.Count}";
    }

    /// <summary>Follows the navigator's focus after a move, removal or store change.</summary>
    private void SyncPreview()
    {
        if (_preview is not { IsOpen: true } preview || _closingChildren) return;
        if (_nav.FocusedItem is not { } item)
        {
            preview.ForceClose();
            return;
        }
        if (preview.Item?.Id == item.Id) preview.Refresh(item, PositionOf(item));
        else if (!preview.Present(item, PositionOf(item), PreviewPlacement())) preview.ForceClose();
    }

    bool IPreviewHost.Forward(string action, string gesture)
    {
        if (_preview?.Item is not { } current) return false;
        var item = _store.Find(current.Id) ?? current;
        switch (action)
        {
            case "previous": _nav.Previous(); break;
            case "next": _nav.Next(); break;
            case "up": _nav.Up(); break;
            case "down": _nav.Down(); break;
            case "first": _nav.First(); break;
            case "last": _nav.Last(); break;
            case "previousPage": _nav.PreviousPage(); break;
            case "nextPage": _nav.NextPage(); break;
            case "pinnedOnly": _nav.TogglePinnedOnly(); break;
            case "copy":
                if (!_writer.Copy([item]))
                    ShowToast(Loc.T("Clipboard could not be written."));
                else if (gesture == "Enter") HideShelves(restoreFocus: true);
                else _preview.FlashCopied();
                return true;
            case "paste":
                _nav.Select(item.Id);
                _ = PasteSelectionAsync();
                return true;
            case "pin":
                _store.TogglePin([item.Id]);
                return true;
            case "remove":
                _store.TakeDown([item.Id]);
                ShowToast(Loc.T("Taken down"));
                return true;
            case "undo":
                var restored = _store.UndoTakeDown();
                if (restored.Count > 0) _nav.Select(restored[0]);
                ShowToast(Loc.T(restored.Count > 0 ? "Restored" : "Nothing to undo"));
                Render();
                SyncPreview();
                return true;
            case "trash":
                ConfirmTrash([item]);
                return true;
            case "actions":
                ShowActions(ActionOrigin.ShelfPreview);
                return true;
            case "search":
                _preview.Close();
                return Run("search", gesture);
            case "library" or "folder" or "name" or "tag" or "export" or "copyOCR" or "markup" or "crop":
                ShowToast(Loc.T("Coming soon"));
                return true;
            default:
                return false;
        }
        if (_nav.Focused is { } focused) _nav.Select(focused);
        Render();
        SyncPreview();
        return true;
    }

    bool IPreviewHost.SaveText(Guid id, string text) => _store.EditText(id, text);

    bool IPreviewHost.CopyText(string text) => _writer.CopyText(text);

    void IPreviewHost.PreviewDeactivated() => ScheduleFocusCheck();

    void IPreviewHost.PreviewClosed()
    {
        _preview = null;
        if (_closingChildren || _hiding || !IsShown) return;
        Activate();
        Keyboard.Focus(this);
        Render();
    }

    private void OpenInBrowser(ClipItem link)
    {
        if (!Uri.TryCreate(link.Url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return;
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            HideShelves(restoreFocus: false);
        }
        catch (Win32Exception)
        {
            ShowToast(Loc.T("The browser could not be opened."));
        }
    }

    // ----- Focus -----

    /// <summary>Hides everything once focus has left all of Pegline's surfaces.</summary>
    private void ScheduleFocusCheck() => Dispatcher.BeginInvoke(() =>
    {
        if (!IsShown || _holdOpen || PinnedForDebugging) return;
        if (IsActive || _preview is { IsActive: true } || _actions is { IsActive: true }) return;
        HideShelves(restoreFocus: false);
    }, DispatcherPriority.Background);

    private void CloseChildren()
    {
        _closingChildren = true;
        try
        {
            _actions?.Close();
            _preview?.ForceClose();
        }
        finally
        {
            _actions = null;
            _preview = null;
            _closingChildren = false;
        }
    }

    // ----- Actions palette -----

    private void ShowActions(ActionOrigin origin)
    {
        if (_actions is not null) return;
        var targets = Targets(origin);
        var entries = ActionCatalog.Entries(Context(origin, targets), _keymap, Loc.T, KeyGestures.Display)
            .Select(command => command.IsEnabled && !IsImplemented(command.Id)
                ? command with { IsEnabled = false, DisabledReason = Loc.T("Coming soon") }
                : command)
            .ToList();
        var palette = new ActionPalette(entries, _palette, id => Execute(id, origin), () =>
        {
            _preview?.Close();
            Run("search", "");
        });
        Window parent = origin == ActionOrigin.ShelfPreview && _preview is { IsOpen: true } preview ? preview : this;
        palette.Owner = parent;
        palette.Closed += (_, _) =>
        {
            _actions = null;
            if (_closingChildren) return;
            if (palette.RestoreParentFocus && parent.IsVisible)
            {
                parent.Activate();
                Keyboard.Focus(parent);
            }
            else ScheduleFocusCheck();
        };
        _actions = palette;
        var handle = new System.Windows.Interop.WindowInteropHelper(parent).Handle;
        Win32.GetWindowRect(handle, out var anchor);
        var (work, scale) = Win32.MonitorOf(handle);
        palette.ShowOver(work, scale, anchor);
    }

    private static bool IsImplemented(string id) =>
        ImplementedActions.Contains(id)
        || id.StartsWith(ActionCatalog.AssignFolderPrefix, StringComparison.Ordinal)
        || id.StartsWith(ActionCatalog.UnassignFolderPrefix, StringComparison.Ordinal)
        || id.StartsWith(ActionCatalog.RemoveTagPrefix, StringComparison.Ordinal);

    private IReadOnlyList<ClipItem> Targets(ActionOrigin origin) =>
        origin == ActionOrigin.ShelfPreview && _preview?.Item is { } current
            ? [_store.Find(current.Id) ?? current]
            : _nav.SelectedItems();

    private ActionContext Context(ActionOrigin origin, IReadOnlyList<ClipItem> items)
    {
        var first = items.FirstOrDefault();
        var single = items.Count == 1 ? first : null;
        var row = single is null ? [] : _nav.Row(single.Kind).ToList();
        var index = single is null ? -1 : row.FindIndex(i => i.Id == single.Id);
        var shortcuts = _settings.Current.GlobalShortcuts;
        return new ActionContext
        {
            Origin = origin,
            SelectedCount = items.Count,
            Kind = items.Count > 0 && items.All(i => i.Kind == first!.Kind) ? first!.Kind : null,
            HasFile = first is { Kind: ItemKind.Image } && File.Exists(_store.MediaPath(first)),
            IsRecording = first?.Recording ?? false,
            IsDeleted = first?.IsTrashed ?? false,
            AllPinned = items.Count > 0 && items.All(i => i.Pinned),
            HasItems = ShelfNavigatorHasItems(),
            HasLatestImage = _store.Items.Any(i => i.Kind == ItemKind.Image && i.IsOnShelf),
            CanUndoShelfRemoval = _store.CanUndoTakeDown,
            IsTextEditing = _preview?.IsTextEditing ?? false,
            ActiveFavorites = _nav.PinnedOnlyKinds.Contains(_nav.ActiveKind),
            LineRevealed = IsShown,
            GlobalToggleHint = shortcuts.GetValueOrDefault("toggle", ""),
            CopyLatestHint = shortcuts.GetValueOrDefault("copyLatest", ""),
            VisibleKinds = _nav.VisibleKinds.ToHashSet(),
            Folders = _store.Folders,
            Tags = _store.AllTags,
            ItemFolders = single is null ? [] : _store.Folders.Where(f => single.FolderIds.Contains(f.Id)).ToList(),
            ItemTags = single?.Tags ?? [],
            CanTakeDownLeft = index > 0,
            CanTakeDownRight = index >= 0 && index < row.Count - 1,
        };
    }

    private bool ShelfNavigatorHasItems() => ShelfNavigator.Order.Any(k => _nav.IsVisible(k) && _nav.Row(k).Count > 0);

    private void Execute(string id, ActionOrigin origin)
    {
        var targets = Targets(origin).Where(i => _store.Find(i.Id) is not null).ToList();
        var ids = targets.Select(i => i.Id).ToList();
        try
        {
            if (id.StartsWith(ActionCatalog.MenuPrefix, StringComparison.Ordinal))
                RunMenu(id[ActionCatalog.MenuPrefix.Length..], targets);
            else if (id.StartsWith(ActionCatalog.AssignFolderPrefix, StringComparison.Ordinal))
            {
                _store.AddToFolder(ids, Guid.Parse(id[ActionCatalog.AssignFolderPrefix.Length..]));
                ShowToast(Loc.T("Added to folder"));
            }
            else if (id.StartsWith(ActionCatalog.UnassignFolderPrefix, StringComparison.Ordinal))
                _store.RemoveFromFolder(ids, Guid.Parse(id[ActionCatalog.UnassignFolderPrefix.Length..]));
            else if (id.StartsWith(ActionCatalog.RemoveTagPrefix, StringComparison.Ordinal))
                _store.RemoveTag(ids, id[ActionCatalog.RemoveTagPrefix.Length..]);
            else if (origin == ActionOrigin.ShelfPreview && _preview is { IsOpen: true } preview)
                RunInPreview(preview, id);
            else
                Run(id, "");
        }
        catch (InvalidOperationException ex)
        {
            ShowToast(ex.Message);
        }
    }

    private void RunInPreview(PreviewWindow preview, string action)
    {
        switch (action)
        {
            case "close" or "preview": preview.Close(); break;
            case "edit": preview.BeginEditing(); break;
            case "finishEditing": preview.FinishEditing(); break;
            case "open": preview.OpenLink(); break;
            default:
                if (!((IPreviewHost)this).Forward(action, "")) Run(action, "");
                break;
        }
    }

    private void RunMenu(string action, IReadOnlyList<ClipItem> targets)
    {
        var single = targets.Count == 1 ? targets[0] : null;
        switch (action)
        {
            case "toggleLine":
                HideShelves(restoreFocus: true);
                break;
            case "quit":
                _quit();
                break;
            case "retain":
                _store.Retain(targets.Select(i => i.Id));
                ShowToast(Loc.T("Saved to Library"));
                break;
            case "takeDownLeft" or "takeDownRight" when single is not null:
                var row = _nav.Row(single.Kind).ToList();
                var index = row.FindIndex(i => i.Id == single.Id);
                var side = action == "takeDownLeft" ? row.Take(index) : row.Skip(index + 1);
                var removed = side.Select(i => i.Id).ToList();
                if (removed.Count == 0) break;
                _store.TakeDown(removed);
                ShowToast(Loc.F("Took down {0} items", removed.Count));
                break;
            case "resetZoom":
                _preview?.ResetZoom();
                break;
            case "copyLatestImage":
                var latest = _store.Items.FirstOrDefault(i => i.Kind == ItemKind.Image && i.IsOnShelf);
                if (latest is not null && _writer.Copy([latest])) ShowToast(Loc.T("Copied"));
                break;
            case "openFile" when single is not null:
                Shell(_store.MediaPath(single), "");
                break;
            case "revealFile" when single is not null:
                Shell("explorer.exe", $"/select,\"{_store.MediaPath(single)}\"");
                break;
            case "clearUnsavedHistory":
                ConfirmClearHistory();
                break;
            case "openScreenshotsFolder":
                var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
                if (Directory.Exists(folder)) Shell(folder, "");
                else ShowToast(Loc.T("Folder not found"));
                break;
            default:
                ShowToast(Loc.T("Coming soon"));
                break;
        }
    }

    private void Shell(string target, string arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target, arguments) { UseShellExecute = true });
            HideShelves(restoreFocus: false);
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException)
        {
            ShowToast(ex.Message);
        }
    }

    private void ConfirmClearHistory()
    {
        var snapshot = _store.SnapshotUnsavedHistory();
        if (snapshot.Entries.Count == 0)
        {
            ShowToast(Loc.T("No unsaved copies"));
            return;
        }
        _holdOpen = true;
        try
        {
            var answer = MessageBox.Show(this, Loc.F("Move {0} unsaved copies to Trash? Pinned, foldered and tagged items stay.", snapshot.Entries.Count),
                "Pegline", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
            if (answer != MessageBoxResult.OK) return;
            var cleared = _store.ClearUnsavedHistory(snapshot);
            ShowToast(Loc.F("Moved {0} items to Trash", cleared.Count));
        }
        finally
        {
            _holdOpen = false;
            Activate();
        }
    }
}
