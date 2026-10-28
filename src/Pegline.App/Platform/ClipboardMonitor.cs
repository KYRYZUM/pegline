using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Pegline.Core.Capture;

namespace Pegline.App.Platform;

internal enum ClipKind
{
    Text,
    Link,
    Image,
    Files,
}

internal sealed record ClipboardContent(ClipKind Kind, string Text, byte[]? Png, IReadOnlyList<string>? Files = null);

/// <summary>
/// Listens for clipboard changes and reads one consistent copy. Private, history-excluded, file and own copies
/// are ignored as a whole; a copy that changes while it is read is retried.
/// </summary>
internal sealed class ClipboardMonitor : IDisposable
{
    private static readonly uint PngFormat = Win32.RegisterClipboardFormat("PNG");
    private static readonly uint HistoryFormat = Win32.RegisterClipboardFormat("CanIncludeInClipboardHistory");

    private readonly MessageWindow _window;
    private readonly DispatcherTimer _debounce;
    private int _attempts;

    public ClipboardMonitor(MessageWindow window)
    {
        _window = window;
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            ReadCurrent();
        };
        _window.Message += OnMessage;
        Win32.AddClipboardFormatListener(_window.Handle);
    }

    public event EventHandler<ClipboardContent>? Captured;

    private bool OnMessage(int msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg != Win32.WM_CLIPBOARDUPDATE) return false;
        _attempts = 0;
        _debounce.Stop();
        _debounce.Start();
        return true;
    }

    private void ReadCurrent()
    {
        var before = Win32.GetClipboardSequenceNumber();
        ClipboardContent? content;
        try
        {
            content = Read();
        }
        catch (ClipboardBusyException)
        {
            Retry();
            return;
        }
        if (Win32.GetClipboardSequenceNumber() != before)
        {
            Retry();
            return;
        }
        if (content is not null) Captured?.Invoke(this, content);
    }

    private void Retry()
    {
        if (++_attempts > 8) return;
        _debounce.Interval = TimeSpan.FromMilliseconds(60 * _attempts);
        _debounce.Start();
    }

    private ClipboardContent? Read()
    {
        if (!Win32.OpenClipboard(_window.Handle)) throw new ClipboardBusyException();
        try
        {
            var formats = new List<uint>();
            var names = new List<string>();
            for (uint format = 0; (format = Win32.EnumClipboardFormats(format)) != 0;)
            {
                formats.Add(format);
                var name = new StringBuilder(256);
                if (Win32.GetClipboardFormatName(format, name, name.Capacity) > 0) names.Add(name.ToString());
            }
            if (formats.Count == 0 || ClipboardAdmission.IsPrivate(names)) return null;
            if (formats.Contains(HistoryFormat) && ClipboardAdmission.ExcludedFromHistory(ReadBytes(HistoryFormat, 16)))
                return null;

            // Image files copied in Explorer or a chat app: the originals beat any thumbnail next to them.
            var files = formats.Contains(Win32.CF_HDROP);
            if (files && ClipboardAdmission.CopiedImages(ReadFiles()) is { Count: > 0 } images)
                return new ClipboardContent(ClipKind.Files, "", null, images);

            var text = formats.Contains(Win32.CF_UNICODETEXT) ? ReadText() : null;
            var hasImage = formats.Contains(PngFormat) || formats.Contains(Win32.CF_DIBV5) || formats.Contains(Win32.CF_DIB);
            if (ClipboardAdmission.PrefersImage(hasImage, text, ClipboardAdmission.IsDocumentCopy(names)) && ReadImage(formats) is { } png)
                return new ClipboardContent(ClipKind.Image, "", png);

            // Other files are not collected, nor the file names some apps add as text.
            if (files || !ClipboardAdmission.AdmitsText(text)) return null;
            var url = ClipboardAdmission.PlainUrl(text);
            return url is not null ? new ClipboardContent(ClipKind.Link, url, null) : new ClipboardContent(ClipKind.Text, text!, null);
        }
        finally
        {
            Win32.CloseClipboard();
        }
    }

    private static byte[]? ReadImage(List<uint> formats)
    {
        if (formats.Contains(PngFormat))
        {
            var png = ReadBytes(PngFormat, (int)ClipboardAdmission.MaxImageBytes);
            if (png.Length > 0 && IsPng(png)) return png;
        }
        foreach (var dibFormat in new[] { Win32.CF_DIBV5, Win32.CF_DIB })
        {
            if (!formats.Contains(dibFormat)) continue;
            if (DibToPng(ReadBytes(dibFormat, (int)ClipboardAdmission.MaxImageBytes)) is { } png) return png;
        }
        return null;
    }

    private static bool IsPng(byte[] bytes)
    {
        try
        {
            Core.Storage.MediaStore.ReadPngSize(bytes);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static List<string> ReadFiles()
    {
        var paths = new List<string>();
        var drop = Win32.GetClipboardData(Win32.CF_HDROP);
        if (drop == IntPtr.Zero) return paths;
        var count = Win32.DragQueryFile(drop, uint.MaxValue, null, 0);
        for (uint i = 0; i < count && paths.Count < 200; i++)
        {
            var length = Win32.DragQueryFile(drop, i, null, 0);
            if (length == 0 || length > 32767) continue;
            var path = new StringBuilder((int)length + 1);
            if (Win32.DragQueryFile(drop, i, path, path.Capacity) > 0) paths.Add(path.ToString());
        }
        return paths;
    }

    private static byte[] ReadBytes(uint format, int max)
    {
        var handle = Win32.GetClipboardData(format);
        if (handle == IntPtr.Zero) return [];
        var size = (long)(ulong)Win32.GlobalSize(handle);
        if (size <= 0 || size > max) return [];
        var pointer = Win32.GlobalLock(handle);
        if (pointer == IntPtr.Zero) return [];
        try
        {
            var bytes = new byte[size];
            Marshal.Copy(pointer, bytes, 0, (int)size);
            return bytes;
        }
        finally
        {
            Win32.GlobalUnlock(handle);
        }
    }

    private static string? ReadText()
    {
        var handle = Win32.GetClipboardData(Win32.CF_UNICODETEXT);
        if (handle == IntPtr.Zero) return null;
        var size = (long)(ulong)Win32.GlobalSize(handle);
        if (size <= 0 || size > (ClipboardAdmission.MaxTextChars + 1) * 2L) return null;
        var pointer = Win32.GlobalLock(handle);
        if (pointer == IntPtr.Zero) return null;
        try
        {
            var text = Marshal.PtrToStringUni(pointer, (int)(size / 2));
            var end = text.IndexOf('\0');
            return end >= 0 ? text[..end] : text;
        }
        finally
        {
            Win32.GlobalUnlock(handle);
        }
    }

    /// <summary>Wraps a packed DIB in a BMP file header and re-encodes it as PNG.</summary>
    internal static byte[]? DibToPng(byte[] dib)
    {
        if (dib.Length < 40) return null;
        var headerSize = BinaryPrimitives.ReadInt32LittleEndian(dib);
        if (headerSize < 40 || headerSize > dib.Length) return null;
        var bitCount = BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(14));
        var compression = BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(16));
        var colorsUsed = BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(32));
        var masks = headerSize == 40 && compression == 3 ? 12 : 0; // BI_BITFIELDS
        var palette = bitCount <= 8 ? (colorsUsed == 0 ? 1 << bitCount : (int)colorsUsed) * 4 : (int)colorsUsed * 4;
        var offset = 14 + headerSize + masks + palette;

        var file = new byte[14 + dib.Length];
        file[0] = (byte)'B';
        file[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(2), file.Length);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(10), offset);
        dib.CopyTo(file, 14);
        try
        {
            using var input = new MemoryStream(file);
            var decoder = new BmpBitmapDecoder(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            BitmapSource frame = decoder.Frames[0];
            if ((long)frame.PixelWidth * frame.PixelHeight > Core.Storage.MediaStore.MaxPixels) return null;
            // Many apps write 32-bit DIBs whose alpha bytes are all zero; read literally that is an invisible picture.
            if (frame.Format == PixelFormats.Bgra32 && AlphaIsEmpty(frame)) frame = new FormatConvertedBitmap(frame, PixelFormats.Bgr32, null, 0);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(frame));
            using var output = new MemoryStream();
            encoder.Save(output);
            return output.ToArray();
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    private static bool AlphaIsEmpty(BitmapSource frame)
    {
        var stride = frame.PixelWidth * 4;
        var row = new byte[stride];
        for (var y = 0; y < frame.PixelHeight; y++)
        {
            frame.CopyPixels(new System.Windows.Int32Rect(0, y, frame.PixelWidth, 1), row, stride, 0);
            for (var x = 3; x < stride; x += 4)
                if (row[x] != 0) return false;
        }
        return true;
    }

    public void Dispose()
    {
        _debounce.Stop();
        Win32.RemoveClipboardFormatListener(_window.Handle);
        _window.Message -= OnMessage;
    }

    private sealed class ClipboardBusyException : Exception;
}
