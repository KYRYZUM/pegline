using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace Pegline.App.Platform;

/// <summary>Hands files, folders and web links to Windows. Each method returns an error message, or null on success.</summary>
internal static class ShellActions
{
    public static string? OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return "Unsupported address.";
        return Start(uri.AbsoluteUri, "");
    }

    public static string? Open(string path) => File.Exists(path) || Directory.Exists(path) ? Start(path, "") : "File not found.";

    public static string? Reveal(string path) => File.Exists(path) ? Start("explorer.exe", $"/select,\"{path}\"") : "File not found.";

    private static string? Start(string target, string arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target, arguments) { UseShellExecute = true });
            return null;
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException or InvalidOperationException)
        {
            return ex.Message;
        }
    }
}
