using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Pegline.App.Localization;
using Pegline.App.Platform;
using Pegline.App.Preview;
using Pegline.App.Shelves;
using Pegline.Core.Input;
using Pegline.Core.Models;
using Pegline.Core.Storage;

namespace Pegline.App.Capture;

/// <summary>
/// Trim video: choose the part of a recording to keep. The original is never changed; the kept part is written
/// to a new file that is shown, copied and exported from then on, and "Use whole video" goes back to the original.
/// I / O set the start and end at the playhead, K plays the selection, Ctrl+S or Enter saves, Esc leaves.
/// </summary>
internal sealed class TrimWindow : HostedWindow
{
    private const double MinimumLength = 0.3;
    private readonly LibraryStore _store;
    private readonly ClipItem _item;
    private readonly ShelfPalette _palette;
    private readonly string _transfer;
    private readonly VideoPlayer _player;
    private readonly Slider _start = new() { Minimum = 0, IsMoveToPointEnabled = true, Focusable = false };
    private readonly Slider _end = new() { Minimum = 0, IsMoveToPointEnabled = true, Focusable = false };
    private readonly TextBlock _startText = new();
    private readonly TextBlock _endText = new();
    private readonly TextBlock _status = new();
    private readonly Border _save;
    private readonly double _duration;
    private CancellationTokenSource? _saving;

    private TrimWindow(LibraryStore store, ClipItem item, ShelfPalette palette, string transfer)
    {
        _store = store;
        _item = item;
        _palette = palette;
        _transfer = transfer;
        _duration = item.DurationSeconds;
        Title = Loc.T("Trim video");
        _player = new VideoPlayer(palette);
        _save = SurfaceParts.Button(palette, Loc.T("Save"), primary: true, Save);
        Content = Build();
        _start.Maximum = _end.Maximum = _duration;
        _start.Value = item.TrimStartSeconds ?? 0;
        _end.Value = item.TrimEndSeconds ?? _duration;
        _start.ValueChanged += (_, _) => RangeChanged(movedStart: true);
        _end.ValueChanged += (_, _) => RangeChanged(movedStart: false);
        RangeChanged(movedStart: true);
        PreviewKeyDown += OnKey;
        Closing += (_, _) =>
        {
            _saving?.Cancel();
            _player.Unload();
        };
    }

    /// <summary>Raised with a message after the recording was saved.</summary>
    public event EventHandler<string>? Saved;

    public static TrimWindow? Create(LibraryStore store, ClipItem item, ShelfPalette palette, string transfer, out string? error)
    {
        error = null;
        if (!item.Recording || item.IsTrashed || item.DurationSeconds <= 0 || !File.Exists(store.Media.PathOf(item.VideoFile)))
        {
            error = Loc.T("Select a video first");
            return null;
        }
        return new TrimWindow(store, item, palette, transfer);
    }

    public override void Present(Window parent)
    {
        var aspect = _item.PixelHeight > 0 ? (double)_item.PixelWidth / _item.PixelHeight : 16.0 / 9;
        var width = 960.0;
        ShowOver(parent, width, Math.Clamp(width / aspect, 300, 620) + VideoPlayer.BarHeight + 150);
        // The original is trimmed, never an earlier trimmed copy.
        _player.Load(_store.Media.PathOf(_item.VideoFile), autoplay: false);
        _player.MediaReady += (_, _) => _player.Seek(TimeSpan.FromSeconds(_start.Value));
        Keyboard.Focus(this);
    }

    private UIElement Build()
    {
        var surface = Surface(_palette, 16);
        var layout = new Grid { Margin = new Thickness(0, 10, 0, 14) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition());
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new DockPanel { Margin = new Thickness(18, 0, 14, 8), Height = 24, LastChildFill = false };
        header.Children.Add(new TextBlock { Text = Loc.T("Trim video"), FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = _palette.Muted, VerticalAlignment = VerticalAlignment.Center });
        var close = SurfaceParts.HeaderButton(_palette, Glyphs.Close, Loc.T("Close"), () => Close());
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        layout.Children.Add(header);

        _player.Margin = new Thickness(14, 0, 14, 0);
        Grid.SetRow(_player, 1);
        layout.Children.Add(_player);

        var range = new Grid { Margin = new Thickness(20, 10, 20, 0) };
        range.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        range.ColumnDefinitions.Add(new ColumnDefinition());
        range.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        range.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        range.RowDefinitions.Add(new RowDefinition());
        range.RowDefinitions.Add(new RowDefinition());
        AddRangeRow(range, 0, Loc.T("Start"), _start, _startText, "I", () => _start.Value = Math.Min(_player.Position.TotalSeconds, _end.Value - MinimumLength));
        AddRangeRow(range, 1, Loc.T("End"), _end, _endText, "O", () => _end.Value = Math.Max(_player.Position.TotalSeconds, _start.Value + MinimumLength));
        Grid.SetRow(range, 2);
        layout.Children.Add(range);

        var footer = new DockPanel { Margin = new Thickness(18, 12, 18, 0), Height = 28, LastChildFill = false };
        _status.FontSize = 12;
        _status.Foreground = _palette.Muted;
        _status.VerticalAlignment = VerticalAlignment.Center;
        footer.Children.Add(_status);
        DockPanel.SetDock(_save, Dock.Right);
        footer.Children.Add(_save);
        var cancel = SurfaceParts.Button(_palette, Loc.T("Cancel"), primary: false, () => Close());
        DockPanel.SetDock(cancel, Dock.Right);
        footer.Children.Add(cancel);
        var whole = SurfaceParts.Button(_palette, Loc.T("Use whole video"), primary: false, () =>
        {
            _start.Value = 0;
            _end.Value = _duration;
        });
        DockPanel.SetDock(whole, Dock.Right);
        footer.Children.Add(whole);
        Grid.SetRow(footer, 3);
        layout.Children.Add(footer);

        surface.Child = layout;
        return surface;
    }

    private void AddRangeRow(Grid grid, int row, string label, Slider slider, TextBlock time, string key, Action setHere)
    {
        var name = new TextBlock { Text = label, FontSize = 12, Foreground = _palette.Foreground, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(name, row);
        grid.Children.Add(name);
        slider.Margin = new Thickness(0, 4, 10, 4);
        slider.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetRow(slider, row);
        Grid.SetColumn(slider, 1);
        grid.Children.Add(slider);
        time.FontSize = 12;
        time.Foreground = _palette.Muted;
        time.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetRow(time, row);
        Grid.SetColumn(time, 2);
        grid.Children.Add(time);
        var here = SurfaceParts.Button(_palette, Loc.T("Set to playhead") + "  " + key, primary: false, setHere);
        here.Margin = new Thickness(0, 3, 0, 3);
        Grid.SetRow(here, row);
        Grid.SetColumn(here, 3);
        grid.Children.Add(here);
    }

    /// <summary>Keeps start before end, limits playback to the selection and shows the kept length.</summary>
    private void RangeChanged(bool movedStart)
    {
        if (_end.Value - _start.Value < MinimumLength)
        {
            if (movedStart) _start.Value = Math.Max(0, _end.Value - MinimumLength);
            else _end.Value = Math.Min(_duration, _start.Value + MinimumLength);
            return;
        }
        _startText.Text = VideoPlayer.Format(TimeSpan.FromSeconds(_start.Value));
        _endText.Text = VideoPlayer.Format(TimeSpan.FromSeconds(_end.Value));
        _player.RangeStart = TimeSpan.FromSeconds(_start.Value);
        _player.RangeEnd = TimeSpan.FromSeconds(_end.Value);
        if (!_player.IsPlaying) _player.Seek(TimeSpan.FromSeconds(movedStart ? _start.Value : _end.Value));
        if (_saving is null)
            _status.Text = IsWhole ? Loc.T("Whole video") : Loc.F("Keeps {0} of {1}", VideoPlayer.Format(TimeSpan.FromSeconds(_end.Value - _start.Value)), VideoPlayer.Format(TimeSpan.FromSeconds(_duration)));
    }

    private bool IsWhole => _start.Value < 0.05 && _end.Value > _duration - 0.05;

    private async void Save()
    {
        if (_saving is not null) return;
        _player.Pause();
        if (IsWhole)
        {
            if (_item.RenderedVideoFile.Length > 0) _store.SetRecordingTrim(_item.Id, null, null, null, null);
            Saved?.Invoke(this, Loc.T("Using the whole video"));
            Close();
            return;
        }
        var cancel = _saving = new CancellationTokenSource();
        _save.IsEnabled = false;
        double start = _start.Value, end = _end.Value;
        Directory.CreateDirectory(_transfer);
        var output = Path.Combine(_transfer, "trim-" + Guid.NewGuid().ToString("N") + ".mp4");
        try
        {
            var progress = new Progress<double>(value => _status.Text = Loc.F("Saving… {0}%", (int)(value * 100)));
            await Recordings.RenderTrimAsync(_store.Media.PathOf(_item.VideoFile), start, end, output, progress, cancel.Token);
            var video = await _store.Media.ImportAsync(output, ".mp4", Core.Capture.WatchedFiles.MaxVideoBytes, cancel.Token);
            var poster = await Recordings.ProbeAsync(_store.Media.PathOf(video), TimeSpan.FromSeconds(0.2));
            _store.SetRecordingTrim(_item.Id, video, poster.PosterPng, start, end);
            Saved?.Invoke(this, Loc.T("Trimmed video saved; the original is kept"));
            Close();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (Recordings.IsMediaFailure(ex))
        {
            App.Log(ex);
            _status.Text = Loc.T("The trimmed video could not be saved");
        }
        finally
        {
            _saving = null;
            _save.IsEnabled = true;
            try
            {
                if (File.Exists(output)) File.Delete(output);
            }
            catch (IOException)
            {
            }
        }
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        var gesture = KeyGestures.FromEvent(e);
        switch (gesture)
        {
            case "Escape":
                Close();
                break;
            case "Enter" or "Ctrl+S":
                Save();
                break;
            case "K" or "Space":
                _player.TogglePlay();
                break;
            case "I":
                _start.Value = Math.Min(_player.Position.TotalSeconds, _end.Value - MinimumLength);
                break;
            case "O":
                _end.Value = Math.Max(_player.Position.TotalSeconds, _start.Value + MinimumLength);
                break;
            case "ArrowLeft" or "ArrowRight":
                _player.Seek(_player.Position + TimeSpan.FromSeconds(gesture == "ArrowLeft" ? -1 : 1));
                break;
            default:
                return;
        }
        e.Handled = true;
    }
}
