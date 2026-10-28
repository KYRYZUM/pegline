using System.Globalization;
using Pegline.Core.Input;
using Pegline.Core.Models;

namespace Pegline.Core.Tests;

public sealed class ShortcutSettingsTests
{
    [Fact]
    public void SettingsListsEveryBrowsingShortcutOnce()
    {
        var listed = ShortcutCatalog.Groups.SelectMany(g => g.Actions).ToList();
        Assert.Equal(listed.Count, listed.Distinct().Count());
        Assert.Equal(Keymap.Defaults.Keys.Order(), listed.Order());
    }

    [Fact]
    public void DefaultShortcutsNeverCollide()
    {
        var keymap = new Keymap();
        foreach (var action in Keymap.Defaults.Keys)
            foreach (var gesture in keymap.GesturesFor(action))
                Assert.True(ShortcutCatalog.Conflicts(keymap, action, gesture).Count == 0, $"{action} {gesture}");
    }

    [Fact]
    public void AKeyTakenInTheSamePlaceIsAConflict()
    {
        var keymap = new Keymap();
        Assert.Equal(["pin"], ShortcutCatalog.Conflicts(keymap, "edit", "P"));
        // Markup tools only listen inside the editor, so they may reuse a shelf key.
        Assert.Empty(ShortcutCatalog.Conflicts(keymap, "rectangle", "P"));
        Assert.Empty(ShortcutCatalog.Conflicts(keymap, "remove", "Ctrl+Z"));
        Assert.Equal(["undoTrash"], ShortcutCatalog.Conflicts(keymap, "newNote", "ctrl+z"));

        // A disabled action frees its key.
        keymap.Remap("pin", "");
        Assert.Empty(ShortcutCatalog.Conflicts(keymap, "edit", "P"));
    }

    [Theory]
    [InlineData("Ctrl+Alt+L", true)]
    [InlineData("Win+Shift+V", true)]
    [InlineData("Alt+F2", true)]
    [InlineData("Shift+L", false)]
    [InlineData("L", false)]
    [InlineData("Ctrl+Alt", false)]
    [InlineData("", false)]
    public void SystemWideShortcutsNeedAModifier(string gesture, bool valid) => Assert.Equal(valid, ShortcutCatalog.IsValidGlobal(gesture));

    [Fact]
    public void ASystemWideShortcutCannotBeUsedTwice()
    {
        var shortcuts = new AppSettings().GlobalShortcuts;
        Assert.Equal("toggle", ShortcutCatalog.GlobalConflict(shortcuts, "library", "alt+ctrl+t"));
        Assert.Null(ShortcutCatalog.GlobalConflict(shortcuts, "toggle", "Ctrl+Alt+T"));
        Assert.Null(ShortcutCatalog.GlobalConflict(shortcuts, "library", ShortcutCatalog.LibraryPreset));
        Assert.All(ShortcutCatalog.GlobalActions, a => Assert.True(shortcuts.ContainsKey(a)));
    }

    [Theory]
    [InlineData("system", "tr-TR", "tr")]
    [InlineData("system", "en-GB", "en")]
    [InlineData("system", "pt-PT", "pt-BR")]
    [InlineData("system", "zh-CN", "zh-Hans")]
    [InlineData("system", "zh-TW", "en")]
    [InlineData("system", "az-Latn-AZ", "az")]
    [InlineData("system", "ja-JP", "ja")]
    [InlineData("system", "de-DE", "en")]
    [InlineData("fr", "tr-TR", "fr")]
    [InlineData("xx", "tr-TR", "en")]
    public void TheLanguageFollowsTheChoiceOrWindows(string preference, string system, string expected) =>
        Assert.Equal(expected, AppLanguages.Resolve(preference, CultureInfo.GetCultureInfo(system)));

    [Fact]
    public void EveryLanguageHasANativeName()
    {
        Assert.All(AppLanguages.Available, code => Assert.NotEqual(code, AppLanguages.NativeName(code)));
        Assert.Equal(["system", .. AppLanguages.Available], AppSettings.Languages);
    }
}
