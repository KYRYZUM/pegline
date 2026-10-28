using System.Windows;

namespace Pegline.App.Platform;

/// <summary>A child surface (picker, image editor) that opens over the shelves, a preview or the Library.</summary>
internal abstract class HostedWindow : FloatingWindow
{
    public abstract void Present(Window parent);
}
