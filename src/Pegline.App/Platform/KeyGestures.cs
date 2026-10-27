using System.Windows.Input;
using Pegline.Core.Input;

namespace Pegline.App.Platform;

/// <summary>Converts between WPF key events, canonical gesture strings and Win32 hotkey registrations.</summary>
internal static class KeyGestures
{
    public static string FromEvent(KeyEventArgs e)
    {
        var key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            _ => e.Key,
        };
        var name = KeyName(key);
        if (name.Length == 0) return "";
        var modifiers = Keyboard.Modifiers;
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(name);
        return Keymap.Canonical(string.Join('+', parts));
    }

    public static bool IsModifier(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System;

    public static string KeyName(Key key) => key switch
    {
        >= Key.A and <= Key.Z => ((char)('A' + (key - Key.A))).ToString(),
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => ((char)('0' + (key - Key.NumPad0))).ToString(),
        >= Key.F1 and <= Key.F24 => "F" + (key - Key.F1 + 1),
        Key.Left => "ArrowLeft",
        Key.Right => "ArrowRight",
        Key.Up => "ArrowUp",
        Key.Down => "ArrowDown",
        Key.Enter => "Enter",
        Key.Space => "Space",
        Key.Escape => "Escape",
        Key.Tab => "Tab",
        Key.Delete => "Delete",
        Key.Back => "Backspace",
        Key.Home => "Home",
        Key.End => "End",
        Key.PageUp => "PageUp",
        Key.PageDown => "PageDown",
        Key.Insert => "Insert",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.OemMinus => "-",
        Key.OemPlus => "=",
        Key.OemQuestion => "/",
        Key.OemSemicolon => ";",
        Key.OemQuotes => "'",
        Key.OemOpenBrackets => "[",
        Key.OemCloseBrackets => "]",
        Key.OemPipe => "\\",
        Key.OemTilde => "`",
        _ => "",
    };

    /// <summary>Parses a canonical gesture into RegisterHotKey arguments.</summary>
    public static bool TryParseHotKey(string gesture, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        gesture = Keymap.Canonical(gesture);
        if (gesture.Length == 0) return false;
        foreach (var part in gesture.Split('+'))
        {
            switch (part)
            {
                case "Ctrl": modifiers |= Win32.MOD_CONTROL; continue;
                case "Alt": modifiers |= Win32.MOD_ALT; continue;
                case "Shift": modifiers |= Win32.MOD_SHIFT; continue;
                case "Win": modifiers |= Win32.MOD_WIN; continue;
            }
            var key = KeyFromName(part);
            if (key == Key.None) return false;
            vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        }
        // A global shortcut needs at least one modifier other than Shift so typing never triggers it.
        return vk != 0 && (modifiers & (Win32.MOD_CONTROL | Win32.MOD_ALT | Win32.MOD_WIN)) != 0;
    }

    private static Key KeyFromName(string name)
    {
        foreach (var key in Enum.GetValues<Key>())
            if (KeyName(key) == name && key is not (>= Key.NumPad0 and <= Key.NumPad9))
                return key;
        return Key.None;
    }

    /// <summary>Human-readable label, e.g. "Ctrl+Alt+T".</summary>
    public static string Display(string gesture) => Keymap.Canonical(gesture)
        .Replace("ArrowLeft", "←").Replace("ArrowRight", "→").Replace("ArrowUp", "↑").Replace("ArrowDown", "↓");
}
