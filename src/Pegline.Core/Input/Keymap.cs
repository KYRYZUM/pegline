namespace Pegline.Core.Input;

/// <summary>
/// Local browsing bindings. A gesture is a canonical string such as "Ctrl+Shift+ArrowLeft" or "P".
/// Remapping an action replaces its default and its default aliases; an empty override disables it.
/// </summary>
public sealed class Keymap
{
    public static readonly IReadOnlyDictionary<string, string> Defaults = new Dictionary<string, string>
    {
        ["library"] = "L", ["previous"] = "ArrowLeft", ["next"] = "ArrowRight", ["up"] = "ArrowUp", ["down"] = "ArrowDown",
        ["first"] = "Ctrl+ArrowLeft", ["last"] = "Ctrl+ArrowRight", ["previousPage"] = "Alt+ArrowLeft", ["nextPage"] = "Alt+ArrowRight",
        ["copy"] = "Ctrl+C", ["paste"] = "Ctrl+Enter", ["selectAll"] = "Ctrl+A", ["pin"] = "P", ["pinnedOnly"] = "F",
        ["preview"] = "Space", ["close"] = "Escape", ["switchShelf"] = "Tab", ["toggleShelves"] = "Ctrl+Shift+B",
        ["export"] = "Ctrl+Shift+E", ["trash"] = "Delete", ["remove"] = "S", ["undo"] = "Backspace",
        ["toggleLinks"] = "Shift+L", ["toggleTexts"] = "Shift+T", ["toggleImages"] = "Shift+I",
        ["folder"] = "Shift+P", ["name"] = "Alt+P", ["tag"] = "Ctrl+Shift+T", ["search"] = "Shift+S",
        ["librarySearch"] = "Ctrl+F", ["allTypes"] = "Ctrl+0", ["images"] = "Ctrl+1", ["texts"] = "Ctrl+2", ["links"] = "Ctrl+3",
        ["open"] = "Ctrl+O", ["edit"] = "E", ["copyOCR"] = "Ctrl+Shift+C", ["actions"] = "Ctrl+K", ["markup"] = "M", ["crop"] = "C",
        ["select"] = "V", ["pen"] = "B", ["marker"] = "H", ["rectangle"] = "R", ["ellipse"] = "O", ["arrow"] = "A", ["line"] = "D",
        ["annotationText"] = "T", ["finishEditing"] = "Ctrl+Enter", ["focusNextPane"] = "F6", ["focusPreviousPane"] = "Shift+F6",
        ["undoTrash"] = "Ctrl+Z", ["newNote"] = "Ctrl+N", ["newFolder"] = "Ctrl+Shift+N",
        ["extendPrevious"] = "Shift+ArrowLeft", ["extendNext"] = "Shift+ArrowRight", ["extendUp"] = "Shift+ArrowUp", ["extendDown"] = "Shift+ArrowDown",
        ["select1"] = "1", ["select2"] = "2", ["select3"] = "3", ["select4"] = "4", ["select5"] = "5",
        ["select6"] = "6", ["select7"] = "7", ["select8"] = "8", ["select9"] = "9",
    };

    public static readonly IReadOnlyDictionary<string, string[]> DefaultAliases = new Dictionary<string, string[]>
    {
        ["copy"] = ["Enter"],
        ["pin"] = ["Ctrl+P"],
        ["pinnedOnly"] = ["Ctrl+Shift+F"],
        ["switchShelf"] = ["Shift+Tab"],
        ["focusNextPane"] = ["Ctrl+Tab"],
        ["focusPreviousPane"] = ["Ctrl+Shift+Tab"],
    };

    private static readonly string[] ModifierOrder = ["Ctrl", "Alt", "Shift", "Win"];

    private readonly Dictionary<string, string> _overrides;
    private Dictionary<string, List<string>> _byGesture = [];

    public Keymap(IReadOnlyDictionary<string, string>? overrides = null)
    {
        _overrides = overrides?.ToDictionary(p => p.Key, p => Canonical(p.Value)) ?? [];
        Rebuild();
    }

    public IReadOnlyDictionary<string, string> Overrides => _overrides;

    /// <summary>Sets an override. <c>null</c> restores the default, an empty string disables the action.</summary>
    public void Remap(string action, string? gesture)
    {
        if (!Defaults.ContainsKey(action)) throw new ArgumentException($"Unknown action '{action}'.");
        if (gesture is null) _overrides.Remove(action);
        else _overrides[action] = Canonical(gesture);
        Rebuild();
    }

    /// <summary>Gestures currently bound to an action (primary first).</summary>
    public IReadOnlyList<string> GesturesFor(string action)
    {
        if (_overrides.TryGetValue(action, out var custom))
            return custom.Length == 0 ? [] : [custom];
        var list = new List<string> { Defaults[action] };
        if (DefaultAliases.TryGetValue(action, out var aliases)) list.AddRange(aliases);
        return list;
    }

    /// <summary>Display text for the primary gesture, or empty when disabled.</summary>
    public string Primary(string action) => GesturesFor(action).FirstOrDefault() ?? "";

    /// <summary>Actions bound to a gesture. A context chooses the first action it supports.</summary>
    public IReadOnlyList<string> Resolve(string gesture) =>
        _byGesture.TryGetValue(Canonical(gesture), out var actions) ? actions : [];

    public static string Canonical(string gesture)
    {
        if (string.IsNullOrWhiteSpace(gesture)) return "";
        var parts = gesture.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var modifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string key = "";
        foreach (var part in parts)
        {
            var normalized = part switch
            {
                _ when part.Equals("Control", StringComparison.OrdinalIgnoreCase) => "Ctrl",
                _ when part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) => "Ctrl",
                _ when part.Equals("Alt", StringComparison.OrdinalIgnoreCase) => "Alt",
                _ when part.Equals("Shift", StringComparison.OrdinalIgnoreCase) => "Shift",
                _ when part.Equals("Win", StringComparison.OrdinalIgnoreCase) || part.Equals("Meta", StringComparison.OrdinalIgnoreCase) => "Win",
                _ => null,
            };
            if (normalized is not null) modifiers.Add(normalized);
            else key = NormalizeKey(part);
        }
        var ordered = ModifierOrder.Where(modifiers.Contains).ToList();
        if (key.Length > 0) ordered.Add(key);
        return string.Join('+', ordered);
    }

    /// <summary>Accepts DOM-style codes ("KeyX", "Digit1") as well as plain names.</summary>
    public static string NormalizeKey(string key)
    {
        if (key.Length == 4 && key.StartsWith("Key", StringComparison.Ordinal)) return key[3..].ToUpperInvariant();
        if (key.Length == 6 && key.StartsWith("Digit", StringComparison.Ordinal)) return key[5..];
        if (key.Length == 1) return key.ToUpperInvariant();
        return key switch
        {
            "Left" => "ArrowLeft",
            "Right" => "ArrowRight",
            "Up" => "ArrowUp",
            "Down" => "ArrowDown",
            "Esc" => "Escape",
            "Return" => "Enter",
            "Del" => "Delete",
            " " or "Spacebar" => "Space",
            _ => key,
        };
    }

    private void Rebuild()
    {
        var map = new Dictionary<string, List<string>>();
        foreach (var action in Defaults.Keys)
            foreach (var gesture in GesturesFor(action))
            {
                if (!map.TryGetValue(gesture, out var list)) map[gesture] = list = [];
                list.Add(action);
            }
        _byGesture = map;
    }
}
