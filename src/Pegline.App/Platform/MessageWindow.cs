using System.Windows.Interop;

namespace Pegline.App.Platform;

/// <summary>A hidden message-only window that receives clipboard and hotkey notifications on the UI thread.</summary>
internal sealed class MessageWindow : IDisposable
{
    private readonly HwndSource _source;

    public MessageWindow()
    {
        var parameters = new HwndSourceParameters("PeglineMessageWindow")
        {
            ParentWindow = Win32.HWND_MESSAGE,
            Width = 0,
            Height = 0,
            WindowStyle = 0,
        };
        _source = new HwndSource(parameters);
        _source.AddHook(Hook);
    }

    public IntPtr Handle => _source.Handle;

    public event Func<int, IntPtr, IntPtr, bool>? Message;

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (Message is { } listeners)
            foreach (Func<int, IntPtr, IntPtr, bool> listener in listeners.GetInvocationList())
                if (listener(msg, wParam, lParam))
                {
                    handled = true;
                    break;
                }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        _source.RemoveHook(Hook);
        _source.Dispose();
    }
}
