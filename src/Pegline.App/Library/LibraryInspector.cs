using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Pegline.App.Localization;
using Pegline.App.Shelves;
using Pegline.Core.Library;
using Pegline.Core.Models;
using Pegline.Core.Storage;

namespace Pegline.App.Library;

/// <summary>Details of the focused record: name, content and note save automatically while typing.</summary>
internal sealed class LibraryInspector : Border
{
    private readonly LibraryStore _store;
    private readonly ThumbnailCache _thumbnails;
    private readonly ShelfPalette _palette;
    private readonly StackPanel _panel = new() { Margin = new Thickness(16, 14, 16, 16) };
    private readonly TextBox _name = new();
    private readonly TextBox _content = new();
    private readonly TextBox _note = new();
    private readonly DispatcherTimer _saveTimer;
    private Guid? _inspected;
    private bool _loading;
    private bool _dirty;

    public LibraryInspector(LibraryStore store, ThumbnailCache thumbnails, ShelfPalette palette)
    {
        _store = store;
        _thumbnails = thumbnails;
        _palette = palette;
        Background = palette.Panel;
        BorderBrush = palette.PanelBorder;
        BorderThickness = new Thickness(1, 0, 0, 0);
        Child = new ScrollViewer { Content = _panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _saveTimer.Tick += (_, _) => Flush();
        foreach (var box in new[] { _name, _content, _note })
        {
            box.FontSize = 13;
            box.TextChanged += (_, _) =>
            {
                if (_loading) return;
                _dirty = true;
                _saveTimer.Stop();
                _saveTimer.Start();
            };
            box.LostKeyboardFocus += (_, _) => Flush();
        }
        _name.MaxLength = 200;
        _content.AcceptsReturn = true;
        _content.AcceptsTab = true;
        _content.TextWrapping = TextWrapping.Wrap;
        _content.MinHeight = 120;
        _content.MaxHeight = 320;
        _content.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _note.AcceptsReturn = true;
        _note.TextWrapping = TextWrapping.Wrap;
        _note.MinHeight = 70;
        _note.MaxHeight = 220;
        _note.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
    }

    /// <summary>A record action chosen from the panel: copy, pin, preview, open, trash, restore, deletePermanently.</summary>
    public event EventHandler<string>? ActionRequested;

    public event EventHandler<string>? Notice;

    public bool IsEditing => _name.IsKeyboardFocusWithin || _content.IsKeyboardFocusWithin || _note.IsKeyboardFocusWithin;

    public void FocusEditor()
    {
        var target = _content.Parent is not null && _store.Find(_inspected ?? Guid.Empty)?.Kind == ItemKind.Text ? _content : _name;
        target.Focus();
        Keyboard.Focus(target);
        target.CaretIndex = target.Text.Length;
    }

    /// <summary>Saves pending edits. Returns false when they could not be saved.</summary>
    public bool Flush()
    {
        _saveTimer.Stop();
        if (!_dirty || _inspected is not { } id || _store.Find(id) is not { } item) return true;
        try
        {
            _dirty = false;
            _store.SetDetails(id,
                name: _name.Text != item.Name ? _name.Text : null,
                note: _note.Text != item.Note ? _note.Text : null,
                text: item.Kind == ItemKind.Text && _content.Text != item.Text ? _content.Text : null);
            return true;
        }
        catch (InvalidOperationException ex)
        {
            _dirty = true;
            Notice?.Invoke(this, Loc.T("Changes could not be saved; keep this draft open") + " " + ex.Message);
            return false;
        }
    }

    public void Show(IReadOnlyList<Guid> selection, Guid? focused)
    {
        Guid? id = selection.Count > 1 ? null : focused ?? (selection.Count == 1 ? selection[0] : null);
        if (id != _inspected) Flush();
        var item = id is { } value ? _store.Find(value) : null;
        if (selection.Count > 1)
        {
            _inspected = null;
            RenderBatch(selection.Count);
            return;
        }
        if (item is null)
        {
            _inspected = null;
            _panel.Children.Clear();
            _panel.Children.Add(new TextBlock { Text = Loc.T("Select an item to see its details"), Foreground = _palette.Muted, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
            return;
        }
        var same = _inspected == item.Id;
        _inspected = item.Id;
        Render(item, keepEditors: same);
    }

    private void RenderBatch(int count)
    {
        _panel.Children.Clear();
        _panel.Children.Add(new TextBlock { Text = Loc.F("{0} items selected", count), FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = _palette.Foreground });
        var buttons = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(Button("", Loc.T("Copy"), "copy"));
        buttons.Children.Add(Button(Glyphs.Pin, Loc.T("Pin"), "pin"));
        buttons.Children.Add(Button("", Loc.T("Move to Trash…"), "trash"));
        _panel.Children.Add(buttons);
    }

    private void Render(ClipItem item, bool keepEditors)
    {
        _loading = true;
        try
        {
            if (!keepEditors || !_name.IsKeyboardFocusWithin) _name.Text = item.Name;
            if (!keepEditors || !_content.IsKeyboardFocusWithin) _content.Text = item.Text;
            if (!keepEditors || !_note.IsKeyboardFocusWithin) _note.Text = item.Note;
            if (!keepEditors) _dirty = false;
        }
        finally
        {
            _loading = false;
        }
        _panel.Children.Clear();
        Detach(_name);
        Detach(_content);
        Detach(_note);

        // Visual summary
        if (item.Kind == ItemKind.Image && _thumbnails.Get(_store.MediaPath(item), 560) is { } image)
        {
            var frame = new Border { CornerRadius = new CornerRadius(10), ClipToBounds = true, Background = _palette.Chip, MaxHeight = 220, Margin = new Thickness(0, 0, 0, 12), Cursor = Cursors.Hand, ToolTip = Loc.T("Preview") };
            frame.Child = new Image { Source = image, Stretch = Stretch.Uniform, MaxHeight = 220 };
            frame.MouseLeftButtonUp += (_, _) => ActionRequested?.Invoke(this, "preview");
            _panel.Children.Add(frame);
        }

        _panel.Children.Add(new TextBlock { Text = LibraryGrid.TitleFor(item), FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = _palette.Foreground, TextWrapping = TextWrapping.Wrap, MaxHeight = 66, TextTrimming = TextTrimming.CharacterEllipsis });
        _panel.Children.Add(new TextBlock { Text = Facts(item), FontSize = 11.5, Foreground = _palette.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });

        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 4) };
        buttons.Children.Add(Button("", Loc.T("Copy"), "copy"));
        buttons.Children.Add(Button(item.Pinned ? Glyphs.Pinned : Glyphs.Pin, Loc.T(item.Pinned ? "Unpin" : "Pin"), "pin", item.Pinned));
        buttons.Children.Add(Button("", Loc.T("Preview"), "preview"));
        if (item.Kind == ItemKind.Link) buttons.Children.Add(Button(Glyphs.OpenExternal, Loc.T("Open in browser"), "open"));
        if (item.IsTrashed)
        {
            buttons.Children.Add(Button("", Loc.T("Restore"), "restore"));
            buttons.Children.Add(Button("", Loc.T("Delete permanently…"), "deletePermanently"));
        }
        else buttons.Children.Add(Button("", Loc.T("Move to Trash…"), "trash"));
        _panel.Children.Add(buttons);

        var editable = !item.IsTrashed;
        Field(Loc.T("Name"), _name, editable);
        if (item.Kind == ItemKind.Text) Field(Loc.T("Content"), _content, editable);
        if (item.Kind == ItemKind.Link)
        {
            Label(Loc.T("Address"));
            _panel.Children.Add(new TextBox { Text = item.Url, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontSize = 12 });
            var state = item.LinkMetadataState switch
            {
                "failed" => Loc.T("Preview unavailable; your link is saved"),
                "blocked" => Loc.T("No preview is fetched for local, non-HTTPS or token-bearing links"),
                _ => "",
            };
            if (state.Length > 0) _panel.Children.Add(new TextBlock { Text = state, FontSize = 11.5, Foreground = _palette.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
            if (editable && item.LinkMetadataState != "blocked")
            {
                var refresh = Button("", Loc.T("Refresh link preview"), "refreshLink");
                refresh.Margin = new Thickness(0, 6, 0, 0);
                refresh.HorizontalAlignment = HorizontalAlignment.Left;
                _panel.Children.Add(refresh);
            }
        }
        Field(Loc.T("Note"), _note, editable);

        var folders = _store.Folders.Where(f => item.FolderIds.Contains(f.Id)).ToList();
        if (folders.Count > 0)
        {
            Label(Loc.T("Folders"));
            var chips = new WrapPanel();
            foreach (var folder in folders)
                chips.Children.Add(Chip(folder.Name, Loc.T("Remove from folder") + ": " + folder.Name, () => _store.RemoveFromFolder([item.Id], folder.Id)));
            _panel.Children.Add(chips);
        }
        if (item.Tags.Count > 0)
        {
            Label(Loc.T("Tags"));
            var chips = new WrapPanel();
            foreach (var tag in item.Tags)
                chips.Children.Add(Chip("#" + tag, Loc.T("Remove tag") + ": " + tag, () => _store.RemoveTag([item.Id], tag)));
            _panel.Children.Add(chips);
        }
    }

    private static void Detach(TextBox box)
    {
        if (box.Parent is Panel panel) panel.Children.Remove(box);
    }

    private static string Facts(ClipItem item)
    {
        var parts = new List<string> { ShelfRowView.Title(item.Kind) };
        if (item.Kind == ItemKind.Image && item.PixelWidth > 0) parts.Add($"{item.PixelWidth}×{item.PixelHeight}");
        if (item.Kind == ItemKind.Text) parts.Add(Loc.F("{0} characters", item.Text.Length));
        parts.Add(Loc.T("Added") + " " + item.CreatedAt.LocalDateTime.ToString("g", CultureInfo.CurrentCulture));
        if (item.LastCopiedAt is { } copied) parts.Add(Loc.T("Copied") + " " + LibraryGrid.Relative(copied));
        if (item.IsTrashed) parts.Add(Loc.T("In Trash"));
        else if (!item.IsOnShelf) parts.Add(Loc.T("Not on the shelves"));
        return string.Join(" · ", parts);
    }

    private void Label(string text) =>
        _panel.Children.Add(new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = _palette.Muted, Margin = new Thickness(0, 14, 0, 5) });

    private void Field(string label, TextBox box, bool editable)
    {
        Label(label);
        box.IsReadOnly = !editable;
        _panel.Children.Add(box);
    }

    private Border Button(string glyph, string label, string action, bool active = false)
    {
        var button = new Border
        {
            Height = 30,
            Padding = new Thickness(10, 0, 12, 0),
            Margin = new Thickness(0, 0, 6, 6),
            CornerRadius = new CornerRadius(8),
            Background = active ? _palette.Highlight : _palette.Chip,
            Cursor = Cursors.Hand,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = glyph, FontFamily = ShelfPalette.Icons, FontSize = 12, Foreground = active ? _palette.Accent : _palette.Foreground, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = label, FontSize = 12, Foreground = _palette.Foreground, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        button.MouseEnter += (_, _) => button.Background = _palette.ChipActive;
        button.MouseLeave += (_, _) => button.Background = active ? _palette.Highlight : _palette.Chip;
        button.MouseLeftButtonUp += (_, e) =>
        {
            ActionRequested?.Invoke(this, action);
            e.Handled = true;
        };
        return button;
    }

    private Border Chip(string text, string tooltip, Action remove)
    {
        var close = new TextBlock { Text = Glyphs.Close, FontFamily = ShelfPalette.Icons, FontSize = 9, Foreground = _palette.Muted, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand, ToolTip = tooltip };
        close.MouseLeftButtonUp += (_, e) =>
        {
            remove();
            e.Handled = true;
        };
        return new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = _palette.Chip,
            Padding = new Thickness(10, 4, 8, 5),
            Margin = new Thickness(0, 0, 6, 6),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new TextBlock { Text = text, FontSize = 12, Foreground = _palette.Foreground, VerticalAlignment = VerticalAlignment.Center },
                    close,
                },
            },
        };
    }
}
