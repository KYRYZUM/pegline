using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Pegline.App.Shelves;

namespace Pegline.App.Platform;

/// <summary>A borderless, shadowed, topmost tool window placed in device pixels on a given monitor.</summary>
internal abstract class FloatingWindow : Window
{
    /// <summary>Transparent space around the surface that holds its drop shadow.</summary>
    protected const double ShadowMargin = 18;

    protected FloatingWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Manual;
        WindowStartupLocation = WindowStartupLocation.Manual;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        SourceInitialized += (_, _) =>
            Win32.SetWindowLong(Handle, Win32.GWL_EXSTYLE, Win32.GetWindowLong(Handle, Win32.GWL_EXSTYLE) | Win32.WS_EX_TOOLWINDOW);
    }

    public IntPtr Handle => new WindowInteropHelper(this).EnsureHandle();

    public bool IsOpen { get; private set; }

    protected static Border Surface(ShelfPalette palette, double radius) => new()
    {
        Margin = new Thickness(ShadowMargin),
        CornerRadius = new CornerRadius(radius),
        Background = palette.Surface,
        BorderBrush = palette.PanelBorder,
        BorderThickness = new Thickness(1),
        Effect = new DropShadowEffect { BlurRadius = 26, ShadowDepth = 6, Direction = 270, Opacity = 0.42 },
    };

    /// <summary>
    /// Shows the window with its surface (<paramref name="width"/> × <paramref name="height"/> DIPs)
    /// centered on <paramref name="anchor"/> and kept 16 DIPs inside the work area.
    /// </summary>
    protected void ShowAt(Win32.RECT work, double scale, Win32.RECT anchor, double width, double height)
    {
        var margin = ShadowMargin * scale;
        var surfaceWidth = Math.Min(width * scale, work.Right - work.Left - 32 * scale);
        var surfaceHeight = Math.Min(height * scale, work.Bottom - work.Top - 32 * scale);
        var centerX = (anchor.Left + anchor.Right) / 2.0;
        var centerY = (anchor.Top + anchor.Bottom) / 2.0;
        var left = Math.Clamp(centerX - surfaceWidth / 2, work.Left + 16 * scale, Math.Max(work.Left + 16 * scale, work.Right - 16 * scale - surfaceWidth));
        var top = Math.Clamp(centerY - surfaceHeight / 2, work.Top + 16 * scale, Math.Max(work.Top + 16 * scale, work.Bottom - 16 * scale - surfaceHeight));
        Width = surfaceWidth / scale + 2 * ShadowMargin;
        Height = surfaceHeight / scale + 2 * ShadowMargin;
        if (!IsVisible)
        {
            Opacity = 0;
            Show();
        }
        for (var pass = 0; pass < 2; pass++) // a second pass settles a DPI change between monitors
            Win32.SetWindowPos(Handle, Win32.HWND_TOPMOST, (int)Math.Round(left - margin), (int)Math.Round(top - margin),
                (int)Math.Round(surfaceWidth + 2 * margin), (int)Math.Round(surfaceHeight + 2 * margin), Win32.SWP_SHOWWINDOW | Win32.SWP_NOACTIVATE);
        Opacity = 1;
        IsOpen = true;
        Activate();
        Win32.ForceForeground(Handle);
    }

    protected override void OnClosed(EventArgs e)
    {
        IsOpen = false;
        base.OnClosed(e);
    }

    /// <summary>The surface's usable size in DIPs after clamping to the monitor.</summary>
    protected Size SurfaceSize => new(Math.Max(0, Width - 2 * ShadowMargin), Math.Max(0, Height - 2 * ShadowMargin));
}
