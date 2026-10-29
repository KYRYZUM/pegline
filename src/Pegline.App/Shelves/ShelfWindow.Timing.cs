#if DEBUG
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows.Media;
using System.Windows.Threading;
using Pegline.App.Platform;

namespace Pegline.App.Shelves;

/// <summary>
/// Debug-only: slides the shelves down far off-screen, without taking focus, and reports how evenly the frames came.
/// <c>Pegline.exe --slide-timing out.txt</c>.
/// </summary>
public partial class ShelfWindow
{
    /// <summary>Set by the timing run so the shelves open off-screen instead of on the monitor under the pointer.</summary>
    private static Win32.RECT? _debugWorkArea;

    internal void MeasureSlides(int rounds, Action<string> done)
    {
        var (real, _) = Win32.MonitorUnderCursor();
        _debugWorkArea = new Win32.RECT { Left = -32000, Top = -32000, Right = -32000 + (real.Right - real.Left), Bottom = -32000 + (real.Bottom - real.Top) };
        var report = new StringBuilder();
        var round = 0;
        // As in the app, the shelves are prepared once while it starts.
        Prepare();

        void Next()
        {
            if (round++ == rounds)
            {
                done(report.ToString());
                return;
            }
            var frames = new List<(double Time, double Y)>();
            var clock = Stopwatch.StartNew();
            void Tick(object? sender, EventArgs e) => frames.Add((clock.Elapsed.TotalMilliseconds, Slide.Y));
            CompositionTarget.Rendering += Tick;
            ShowPassive(null, TimeSpan.FromSeconds(30));
            var blocked = clock.Elapsed.TotalMilliseconds;
            After(900, () =>
            {
                CompositionTarget.Rendering -= Tick;
                report.AppendLine(Summarize(round, blocked, frames));
                HideShelves(restoreFocus: false);
                After(600, Next);
            });
        }
        After(1500, Next);
    }

    private void After(int milliseconds, Action action)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        timer.Start();
    }

    /// <summary>The frames from the last one before the panel moved to the first one where it arrived.</summary>
    private static string Summarize(int round, double blocked, List<(double Time, double Y)> frames)
    {
        var c = CultureInfo.InvariantCulture;
        var start = frames.FindIndex(f => f.Y < 0);
        var moving = start < 0 ? -1 : frames.FindIndex(start, f => f.Y > frames[start].Y);
        var end = moving < 0 ? -1 : frames.FindIndex(moving, f => f.Y >= 0);
        if (moving < 1 || end < 0) return string.Format(c, "round {0}: no slide seen ({1} frames)", round, frames.Count);
        var from = moving - 1;
        var distance = -frames[from].Y;
        var gaps = Enumerable.Range(from + 1, end - from).Select(i => frames[i].Time - frames[i - 1].Time).ToList();
        var steps = Enumerable.Range(from + 1, end - from).Select(i => (frames[i].Y - frames[i - 1].Y) / distance * 100).ToList();
        return string.Format(c,
            "round {0}: call blocked {1:F0} ms, first move at {2:F0} ms, slide {3:F0} ms in {4} frames, longest frame {5:F0} ms, frames over 25 ms {6}, largest jump {7:F0}% of the way",
            round, blocked, frames[moving].Time, frames[end].Time - frames[from].Time, gaps.Count, gaps.Max(), gaps.Count(g => g > 25), steps.Max());
    }
}
#endif
