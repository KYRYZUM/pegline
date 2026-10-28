using Microsoft.Win32;

namespace Pegline.App.Platform;

/// <summary>"Start Pegline when I sign in": the per-user Run entry, read back from Windows rather than remembered.</summary>
internal static class LoginItem
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "Pegline";

    /// <summary>Development builds never register themselves; they run from a build folder that moves.</summary>
#if DEBUG
    public static bool IsAvailable => false;
#else
    public static bool IsAvailable => Environment.ProcessPath is not null;
#endif

    private static string Command => $"\"{Environment.ProcessPath}\" --login";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(Name) is string value && value.Contains(Environment.ProcessPath ?? "\0", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Returns false when Windows refused the change (policy or permissions).</summary>
    public static bool Set(bool enabled)
    {
        if (!IsAvailable) return false;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled) key.SetValue(Name, Command);
            else key.DeleteValue(Name, throwOnMissingValue: false);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
        {
            App.Log(ex);
            return false;
        }
    }
}
