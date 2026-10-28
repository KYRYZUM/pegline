using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Pegline.App.Localization;
using Pegline.App.Shelves;

namespace Pegline.App.Preview;

/// <summary>
/// Plays a recording with a play / pause button, a seek bar and the time. Click the picture or press K to play
/// or pause. A range (used while trimming) keeps playback between two points.
/// </summary>
internal sealed class VideoPlayer : Grid
{
    public const double BarHeight = 46;
    private readonly MediaElement _media = new()
    {
        LoadedBehavior = MediaState.Manual,
        UnloadedBehavior = MediaState.Manual,
        ScrubbingEnabled = true,
        Stretch = Stretch.Uniform,
    };
    private readonly Slider _seek = new() { Minimum = 0, Maximum = 1, VerticalAlignment = VerticalAlignment.Center, IsMoveToPointEnabled = true, Focusable = false };
    private readonly TextBlock _playGlyph = new();
    private readonly TextBlock _time = new();
    private readonly DispatcherTimer _tick;
    private readonly ShelfPalette _palette;
    private bool _playing;
    private bool _seeking;
    private string _source = "";

    public VideoPlayer(ShelfPalette palette)
    {
        _palette = palette;
        RowDefinitions.Add(new RowDefinition());
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(BarHeight) });
        var stage = new Border { Background = Brushes.Black, Child = _media, Cursor = Cursors.Hand };
        stage.MouseLeftButtonUp += (_, e) =>
        {
            TogglePlay();
            e.Handled = true;
        };
        Children.Add(stage);

        var bar = new Grid { Margin = new Thickness(10, 0, 14, 0) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition());
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _playGlyph.FontFamily = ShelfPalette.Icons;
        _playGlyph.FontSize = 15;
        _playGlyph.Foreground = palette.Foreground;
        _playGlyph.HorizontalAlignment = HorizontalAlignment.Center;
        _playGlyph.VerticalAlignment = VerticalAlignment.Center;
        var play = new Border { Width = 34, Height = 30, CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = _playGlyph };
        play.MouseEnter += (_, _) => play.Background = palette.Chip;
        play.MouseLeave += (_, _) => play.Background = Brushes.Transparent;
        play.MouseLeftButtonUp += (_, e) =>
        {
            TogglePlay();
            e.Handled = true;
        };
        bar.Children.Add(play);
        _seek.Margin = new Thickness(8, 0, 12, 0);
        Grid.SetColumn(_seek, 1);
        bar.Children.Add(_seek);
        _time.Foreground = palette.Muted;
        _time.FontSize = 12;
        _time.VerticalAlignment = VerticalAlignment.Center;
        // Digits keep their width so the running clock does not jitter.
        System.Windows.Documents.Typography.SetNumeralAlignment(_time, FontNumeralAlignment.Tabular);
        Grid.SetColumn(_time, 2);
        bar.Children.Add(_time);
        Grid.SetRow(bar, 1);
        Children.Add(bar);

        _seek.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => _seeking = true));
        _seek.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) =>
        {
            _seeking = false;
            Seek(TimeSpan.FromSeconds(_seek.Value));
        }));
        _seek.ValueChanged += (_, e) =>
        {
            if (_seeking || Math.Abs(e.NewValue - _media.Position.TotalSeconds) > 0.25) Seek(TimeSpan.FromSeconds(e.NewValue), keepSlider: true);
        };
        _media.MediaOpened += (_, _) =>
        {
            if (_media.NaturalDuration.HasTimeSpan) _seek.Maximum = Math.Max(0.1, _media.NaturalDuration.TimeSpan.TotalSeconds);
            if (RangeStart is { } start) _media.Position = start;
            MediaReady?.Invoke(this, EventArgs.Empty);
            Update();
        };
        _media.MediaEnded += (_, _) =>
        {
            Pause();
            _media.Position = RangeStart ?? TimeSpan.Zero;
            Update();
        };
        _media.MediaFailed += (_, e) =>
        {
            _time.Text = Loc.T("This video cannot be played");
            App.Log(e.ErrorException);
        };
        _tick = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(80) };
        _tick.Tick += (_, _) =>
        {
            if (RangeEnd is { } end && _media.Position >= end)
            {
                Pause();
                _media.Position = end;
            }
            Update();
        };
        Update();
    }

    /// <summary>While trimming: playback starts at and stops at these points.</summary>
    public TimeSpan? RangeStart { get; set; }
    public TimeSpan? RangeEnd { get; set; }

    public TimeSpan Position => _media.Position;

    public TimeSpan Duration => _media.NaturalDuration.HasTimeSpan ? _media.NaturalDuration.TimeSpan : TimeSpan.Zero;

    public bool IsPlaying => _playing;

    public event EventHandler? MediaReady;

    public event EventHandler? PositionChanged;

    public void Load(string path, bool autoplay)
    {
        if (_source == path)
        {
            if (autoplay && !_playing) Play();
            return;
        }
        Unload();
        _source = path;
        _media.Source = new Uri(path);
        // Manual mode: opening needs a Play or Pause.
        if (autoplay) Play();
        else
        {
            _media.Play();
            _media.Pause();
        }
    }

    public void Unload()
    {
        if (_source.Length == 0) return;
        Pause();
        _media.Stop();
        _media.Close(); // releases the file
        _media.Source = null;
        _source = "";
        Update();
    }

    public void TogglePlay()
    {
        if (_playing) Pause();
        else Play();
    }

    public void Play()
    {
        if (_media.Source is null) return;
        if (RangeEnd is { } end && _media.Position >= end - TimeSpan.FromMilliseconds(100)) _media.Position = RangeStart ?? TimeSpan.Zero;
        if (RangeStart is { } start && _media.Position < start) _media.Position = start;
        _media.Play();
        _playing = true;
        _tick.Start();
        Update();
    }

    public void Pause()
    {
        _media.Pause();
        _playing = false;
        _tick.Stop();
        Update();
    }

    public void Seek(TimeSpan position, bool keepSlider = false)
    {
        if (_media.Source is null) return;
        if (position < TimeSpan.Zero) position = TimeSpan.Zero;
        if (Duration > TimeSpan.Zero && position > Duration) position = Duration;
        _media.Position = position;
        if (!keepSlider) _seek.Value = position.TotalSeconds;
        Update();
    }

    private void Update()
    {
        _playGlyph.Text = _playing ? "" : "";
        if (!_seeking) _seek.Value = Math.Min(_seek.Maximum, _media.Position.TotalSeconds);
        if (_media.Source is not null) _time.Text = Format(_media.Position) + " / " + Format(Duration);
        PositionChanged?.Invoke(this, EventArgs.Empty);
    }

    public static string Format(TimeSpan time) =>
        time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss\.f");
}

