using System.Diagnostics;
using System.Windows.Automation;
using Pegline.Core.Transfer;

namespace Pegline.App.Platform;

/// <summary>
/// Remembers the app (and its focused element) that was active before Pegline appeared, so Ctrl+Enter can
/// paste back into exactly that field. UI Automation is asked for identity and capability only (runtime id,
/// password flag); a field's value or selection is never read.
/// </summary>
internal sealed class ForegroundTracker
{
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_V = 0x56;
    private static readonly uint OwnProcess = (uint)Environment.ProcessId;
    private static readonly TimeSpan CaptureBudget = TimeSpan.FromMilliseconds(300);
    private PasteTarget? _target;

    public void Remember()
    {
        var window = Win32.GetForegroundWindow();
        Win32.GetWindowThreadProcessId(window, out var process);
        if (window == IntPtr.Zero || process == OwnProcess) return;
        // Must run before Pegline activates, or the focused element would be Pegline's own.
        _target = Describe(window, process);
    }

    public bool HasPrevious => _target is { } target && Win32.IsWindow(new IntPtr(target.Window));

    public bool Restore() => HasPrevious && Win32.ForceForeground(new IntPtr(_target!.Window));

    /// <summary>Returns focus to the previous field and sends Ctrl+V there, unless the target is a password field or changed.</summary>
    public async Task<PasteVerdict> PasteIntoPreviousAsync()
    {
        var captured = _target;
        if (captured is null || !HasPrevious) return PasteVerdict.NoTarget;
        if (captured.Secure) return PasteVerdict.Secure;
        if (!Restore()) return PasteVerdict.NoTarget;
        var window = new IntPtr(captured.Window);
        var watch = Stopwatch.StartNew();
        while (Win32.GetForegroundWindow() != window && watch.ElapsedMilliseconds < 400)
            await Task.Delay(20);
        if (Win32.GetForegroundWindow() != window) return PasteVerdict.Changed;

        // The app restores its own focus after activation; give it a moment to settle on the same element.
        var verdict = PasteVerdict.Changed;
        while (watch.ElapsedMilliseconds < 900)
        {
            var current = await Task.Run(() => Describe(window, captured.ProcessId));
            verdict = PastePolicy.Decide(captured, current);
            if (verdict != PasteVerdict.Changed || Win32.GetForegroundWindow() != window) break;
            await Task.Delay(40);
        }
        if (verdict != PasteVerdict.Allowed) return verdict;
        await Task.Delay(30);
        if (Win32.GetForegroundWindow() != window) return PasteVerdict.Changed;
        Win32.SendChord(VK_CONTROL, VK_V);
        return PasteVerdict.Allowed;
    }

    /// <summary>The focused element's identity, or a window-only target when UI Automation cannot answer in time.</summary>
    private static PasteTarget Describe(IntPtr window, uint process)
    {
        var windowOnly = new PasteTarget(window.ToInt64(), process, "", Editable: false, Secure: false);
        // UI Automation calls cross into the target process; keep them off the UI thread and bounded.
        var probe = Task.Run(() =>
        {
            try
            {
                if (Win32.GetForegroundWindow() != window) return null;
                var element = AutomationElement.FocusedElement;
                if (element is null) return null;
                var current = element.Current;
                if ((uint)current.ProcessId == OwnProcess) return null;
                var editable = current.ControlType == ControlType.Edit || current.ControlType == ControlType.Document
                    || (element.TryGetCurrentPattern(ValuePattern.Pattern, out var value) && !((ValuePattern)value).Current.IsReadOnly);
                return new PasteTarget(window.ToInt64(), process, string.Join(".", element.GetRuntimeId()), editable, current.IsPassword);
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException or TimeoutException)
            {
                return null;
            }
        });
        return probe.Wait(CaptureBudget) && probe.Result is { } target ? target : windowOnly;
    }
}
