#if DEBUG
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Pegline.App.Library;
using Pegline.App.Shelves;

namespace Pegline.App;

/// <summary>Debug-only: renders a window far off-screen, without activation, to a PNG for visual checks.</summary>
internal static class DevSnapshot
{
    public static void Capture(Window? window, string target, string output, Action done)
    {
        if (window is null)
        {
            done();
            return;
        }
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -20000;
        window.Top = -20000;
        if (window is LibraryWindow library) library.ShowForSnapshot(target.Contains("recent", StringComparison.Ordinal) ? Pegline.Core.Library.LibraryScope.Recent : null, target.Contains("list", StringComparison.Ordinal));
        else if (window is ShelfWindow shelves) shelves.ShowForSnapshot();
        window.Dispatcher.BeginInvoke(() =>
        {
            var element = (FrameworkElement)window.Content;
            var width = (int)Math.Ceiling(element.ActualWidth);
            var height = (int)Math.Ceiling(element.ActualHeight);
            var bitmap = new RenderTargetBitmap(Math.Max(1, width), Math.Max(1, height), 96, 96, PixelFormats.Pbgra32);
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                context.DrawRectangle(window.Background ?? Brushes.Black, null, new Rect(0, 0, width, height));
                context.DrawRectangle(new VisualBrush(element), null, new Rect(0, 0, width, height));
            }
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(output)) encoder.Save(stream);
            done();
        }, DispatcherPriority.ApplicationIdle);
    }
}
#endif
