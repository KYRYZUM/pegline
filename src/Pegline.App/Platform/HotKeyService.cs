namespace Pegline.App.Platform;

/// <summary>Registers system-wide shortcuts. Each action owns one registration that can be suspended while recording.</summary>
internal sealed class HotKeyService : IDisposable
{
    private readonly MessageWindow _window;
    private readonly Dictionary<int, string> _actions = [];
    private readonly Dictionary<string, string> _gestures = [];
    private int _nextId = 1;
    private int _suspended;

    public HotKeyService(MessageWindow window)
    {
        _window = window;
        _window.Message += OnMessage;
    }

    public event EventHandler<string>? Pressed;

    /// <summary>Replaces all registrations. Returns actions whose shortcut could not be registered (already taken).</summary>
    public IReadOnlyList<string> Apply(IReadOnlyDictionary<string, string> shortcuts)
    {
        UnregisterAll();
        _gestures.Clear();
        foreach (var (action, gesture) in shortcuts) _gestures[action] = gesture;
        return _suspended > 0 ? [] : RegisterAll();
    }

    /// <summary>Temporarily releases owned registrations so a recorder can receive the same combination.</summary>
    public IDisposable Suspend()
    {
        if (_suspended++ == 0) UnregisterAll();
        return new Lease(this);
    }

    private IReadOnlyList<string> RegisterAll()
    {
        var failed = new List<string>();
        foreach (var (action, gesture) in _gestures)
        {
            if (!KeyGestures.TryParseHotKey(gesture, out var modifiers, out var vk)) continue;
            var id = _nextId++;
            if (Win32.RegisterHotKey(_window.Handle, id, modifiers | Win32.MOD_NOREPEAT, vk)) _actions[id] = action;
            else failed.Add(action);
        }
        return failed;
    }

    private void UnregisterAll()
    {
        foreach (var id in _actions.Keys) Win32.UnregisterHotKey(_window.Handle, id);
        _actions.Clear();
    }

    private bool OnMessage(int msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg != Win32.WM_HOTKEY || !_actions.TryGetValue(wParam.ToInt32(), out var action)) return false;
        Pressed?.Invoke(this, action);
        return true;
    }

    public void Dispose()
    {
        UnregisterAll();
        _window.Message -= OnMessage;
    }

    private sealed class Lease(HotKeyService owner) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (_released) return;
            _released = true;
            if (--owner._suspended == 0) owner.RegisterAll();
        }
    }
}
