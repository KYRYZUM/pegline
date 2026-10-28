namespace Pegline.Core.Transfer;

/// <summary>
/// Metadata about the keyboard focus in another app, captured when Pegline opened. Only identity and
/// capability are kept (window, process, UI Automation runtime id, editable, password); never a field's value.
/// </summary>
public sealed record PasteTarget(long Window, uint ProcessId, string RuntimeId, bool Editable, bool Secure)
{
    /// <summary>True when UI Automation described the focused element; false when only the window is known.</summary>
    public bool HasElement => RuntimeId.Length > 0;
}

public enum PasteVerdict
{
    Allowed,
    NoTarget,
    Secure,
    Changed,
}

public static class PastePolicy
{
    /// <summary>
    /// Decides whether Ctrl+V may be sent. <paramref name="captured"/> is the focus seen before Pegline took over,
    /// <paramref name="current"/> the focus after the previous window was restored. A password field is always
    /// refused, and the restored focus must be the same window, process and, when both sides expose one, the same
    /// element. Non-text targets (a spreadsheet cell, a paint canvas) are allowed: the app decides what it accepts.
    /// When UI Automation could not describe the element at all, the window identity alone decides.
    /// </summary>
    public static PasteVerdict Decide(PasteTarget? captured, PasteTarget? current)
    {
        if (captured is null || captured.Window == 0) return PasteVerdict.NoTarget;
        if (captured.Secure || current?.Secure == true) return PasteVerdict.Secure;
        if (current is null || current.Window != captured.Window || current.ProcessId != captured.ProcessId) return PasteVerdict.Changed;
        if (captured.HasElement && current.HasElement && current.RuntimeId != captured.RuntimeId) return PasteVerdict.Changed;
        return PasteVerdict.Allowed;
    }

    public static string Message(PasteVerdict verdict) => verdict switch
    {
        PasteVerdict.NoTarget => "Paste failed: the previous app is no longer available.",
        PasteVerdict.Secure => "Pegline does not paste into password fields.",
        PasteVerdict.Changed => "The previous field changed, so nothing was pasted. The copy is on the clipboard.",
        _ => "",
    };
}
