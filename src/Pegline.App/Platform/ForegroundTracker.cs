using System.Diagnostics;

namespace Pegline.App.Platform;

/// <summary>Remembers the application that was active before Pegline opened so focus can return to it.</summary>
internal sealed class ForegroundTracker
{
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_V = 0x56;
    private static readonly uint OwnProcess = (uint)Environment.ProcessId;
    private IntPtr _previous;

    public void Remember()
    {
        var window = Win32.GetForegroundWindow();
        Win32.GetWindowThreadProcessId(window, out var process);
        if (window != IntPtr.Zero && process != OwnProcess) _previous = window;
    }

    public bool HasPrevious => _previous != IntPtr.Zero && Win32.IsWindow(_previous);

    public bool Restore() => HasPrevious && Win32.ForceForeground(_previous);

    /// <summary>Returns focus to the previous app and sends Ctrl+V there.</summary>
    public async Task<bool> PasteIntoPreviousAsync()
    {
        if (!Restore()) return false;
        // Let the target process its activation before the keystroke arrives.
        var watch = Stopwatch.StartNew();
        while (Win32.GetForegroundWindow() != _previous && watch.ElapsedMilliseconds < 400)
            await Task.Delay(20);
        if (Win32.GetForegroundWindow() != _previous) return false;
        await Task.Delay(40);
        Win32.SendChord(VK_CONTROL, VK_V);
        return true;
    }
}
