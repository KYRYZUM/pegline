using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Pegline.Core.Capture;

namespace Pegline.App.Platform;

internal enum ClipKind
{
    Text,
    Link,
    Image,
}

internal sealed record ClipboardContent(ClipKind Kind, string Text, byte[]? Png);

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
            if (formats.Count == 0 || ClipboardAdmission.IsPrivate(names) || formats.Contains(Win32.CF_HDROP)) return null;
            if (formats.Contains(HistoryFormat) && ClipboardAdmission.ExcludedFromHistory(ReadBytes(HistoryFormat, 16)))
                return null;

            if (formats.Contains(Win32.CF_UNICODETEXT))
            {
                var text = ReadText();
                if (ClipboardAdmission.AdmitsText(text))
                {
                    var url = ClipboardAdmission.PlainUrl(text);
                    return url is not null ? new ClipboardContent(ClipKind.Link, url, null) : new ClipboardContent(ClipKind.Text, text!, null);
                }
            }

            if (formats.Contains(PngFormat))
            {
                var png = ReadBytes(PngFormat, (int)ClipboardAdmission.MaxImageBytes);
                if (png.Length > 0) return new ClipboardContent(ClipKind.Image, "", png);
            }
            foreach (var dibFormat in new[] { Win32.CF_DIBV5, Win32.CF_DIB })
            {
                if (!formats.Contains(dibFormat)) continue;
                var dib = ReadBytes(dibFormat, (int)ClipboardAdmission.MaxImageBytes);
                var png = DibToPng(dib);
                if (png is not null) return new ClipboardContent(ClipKind.Image, "", png);
            }
            return null;
        }
        finally
        {
            Win32.CloseClipboard();
        }
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
            var frame = decoder.Frames[0];
            if ((long)frame.PixelWidth * frame.PixelHeight > Core.Storage.MediaStore.MaxPixels) return null;
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(frame);
            using var output = new MemoryStream();
            encoder.Save(output);
            return output.ToArray();
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _debounce.Stop();
        Win32.RemoveClipboardFormatListener(_window.Handle);
        _window.Message -= OnMessage;
    }

    private sealed class ClipboardBusyException : Exception;
}
