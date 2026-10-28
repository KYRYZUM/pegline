using Pegline.Core.Storage;

namespace Pegline.Core.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pegline-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void FreshSettingsCaptureEverything()
    {
        var settings = new SettingsStore(_directory).Current;
        Assert.True(settings.CaptureText);
        Assert.True(settings.CaptureImages);
        Assert.True(settings.CaptureLinks);
    }

    [Fact]
    public void AnUnversionedFileWithCaptureOffIsUpgradedOnce()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "settings.json"), """{ "CaptureText": false, "CaptureImages": false, "CaptureLinks": true, "Theme": "Soft" }""");

        var store = new SettingsStore(_directory);
        Assert.True(store.Current.CaptureText);
        Assert.True(store.Current.CaptureImages);

        // A deliberate choice made after the upgrade is kept.
        store.Update(s => s.CaptureImages = false);
        Assert.False(new SettingsStore(_directory).Current.CaptureImages);
        Assert.True(new SettingsStore(_directory).Current.CaptureText);
    }
}
