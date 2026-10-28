using System.Windows.Threading;

namespace Pegline.App.Platform;

/// <summary>
/// Watches for the pointer pushed against the top edge of a screen and resting there, with no button held,
/// the way the shelves are reached without a shortcut. Runs only while the setting is on.
/// </summary>
internal sealed class EdgeReveal
{
    /// <summary>Long enough that sweeping across browser tabs at the top never counts.</summary>
    private static readonly TimeSpan RestDelay = TimeSpan.FromMilliseconds(350);

    private readonly DispatcherTimer _timer;
    private readonly Func<bool> _allowed;
    private readonly Action _reveal;
    private DateTime? _since;
    private bool _armed = true;

    public EdgeReveal(Dispatcher dispatcher, Func<bool> allowed, Action reveal)
    {
        _allowed = allowed;
        _reveal = reveal;
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(80) };
        _timer.Tick += (_, _) => Tick();
    }

    public bool Enabled
    {
        get => _timer.IsEnabled;
        set
        {
            if (value == _timer.IsEnabled) return;
            _since = null;
            _armed = true;
            if (value) _timer.Start();
            else _timer.Stop();
        }
    }

    private void Tick()
    {
        if (!AtTopEdge())
        {
            // Leaving the edge re-arms it, so one rest opens the shelves once.
            _since = null;
            _armed = true;
            return;
        }
        if (!_armed || ButtonHeld() || !_allowed())
        {
            _since = null;
            return;
        }
        var now = DateTime.UtcNow;
        _since ??= now;
        if (now - _since < RestDelay) return;
        _since = null;
        _armed = false;
        _reveal();
    }

    private static bool AtTopEdge()
    {
        if (!Win32.GetCursorPos(out var point)) return false;
        var info = new Win32.MONITORINFO { Size = System.Runtime.InteropServices.Marshal.SizeOf<Win32.MONITORINFO>() };
        if (!Win32.GetMonitorInfo(Win32.MonitorFromPoint(point, 2), ref info)) return false;
        // A taskbar docked at the top owns that edge.
        return point.Y <= info.Monitor.Top && info.Work.Top == info.Monitor.Top;
    }

    private static bool ButtonHeld() => (Win32.GetAsyncKeyState(0x01) & 0x8000) != 0 || (Win32.GetAsyncKeyState(0x02) & 0x8000) != 0;
}
