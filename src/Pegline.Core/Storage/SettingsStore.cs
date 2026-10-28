using System.Text.Json;
using System.Text.Json.Serialization;
using Pegline.Core.Models;

namespace Pegline.Core.Storage;

/// <summary>Preferences stored as JSON next to the Library, written atomically.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _file;

    public SettingsStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _file = Path.Combine(directory, "settings.json");
        Current = Load();
    }

    public AppSettings Current { get; private set; }

    public event EventHandler? Changed;

    public void Update(Action<AppSettings> change)
    {
        var copy = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(Current, Json), Json)!;
        change(copy);
        Validate(copy);
        Write(copy);
        Current = copy;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private AppSettings Load()
    {
        if (!File.Exists(_file))
            return new AppSettings { Version = AppSettings.CurrentVersion };
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_file), Json) ?? new AppSettings();
            Validate(settings);
            if (Migrate(settings)) Write(settings);
            return settings;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            // Keep the unreadable file for recovery and continue with defaults.
            File.Copy(_file, _file + ".unreadable", overwrite: true);
            return new AppSettings();
        }
    }

    /// <summary>Upgrades an older file. Returns true when something changed and the file should be rewritten.</summary>
    private static bool Migrate(AppSettings settings)
    {
        if (settings.Version >= AppSettings.CurrentVersion) return false;
        // Version 0 shipped with image and text capture off and no way to turn them on besides the tray.
        if (settings.Version < 1)
        {
            settings.CaptureText = true;
            settings.CaptureImages = true;
        }
        // Version 1 peeked at new images only, behind one switch; texts and links now peek too unless it was off.
        if (settings.Version < 2)
            settings.RevealNewItems = settings.OpenAfterScreenshot ? NewItemReveal.Everything : NewItemReveal.Never;
        settings.Version = AppSettings.CurrentVersion;
        return true;
    }

    private static void Validate(AppSettings settings)
    {
        settings.VisibleKinds = [.. settings.VisibleKinds.Distinct().Order()];
        foreach (var kind in Enum.GetValues<ItemKind>())
        {
            var days = settings.RetentionDays.GetValueOrDefault(kind, 30);
            if (days is < 0 or > 3650) throw new ArgumentException("Invalid history duration.");
            settings.RetentionDays[kind] = days;
        }
        if (!AppSettings.Languages.Contains(settings.Language)) settings.Language = "system";
        if (settings.WatchFolders.Count > AppSettings.MaxWatchFolders || settings.WatchFolders.Any(f => !Path.IsPathFullyQualified(f)))
            throw new ArgumentException("Invalid watched folder.");
        var defaults = new AppSettings().GlobalShortcuts;
        foreach (var (action, value) in defaults)
            settings.GlobalShortcuts.TryAdd(action, value);
        if (settings.RecentPinFolderIds.Count > 8)
            settings.RecentPinFolderIds = settings.RecentPinFolderIds[..8];
    }

    private void Write(AppSettings settings)
    {
        var temporary = _file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Json));
            File.Move(temporary, _file, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
