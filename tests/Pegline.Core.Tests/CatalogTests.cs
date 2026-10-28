using System.Text.Json;
using System.Text.RegularExpressions;
using Pegline.Core.Models;

namespace Pegline.Core.Tests;

/// <summary>Every interface language carries every string the Turkish catalog has, with the same placeholders.</summary>
public sealed partial class CatalogTests
{
    private static string Localization()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Pegline.slnx")))
                return Path.Combine(dir.FullName, "src", "Pegline.App", "Localization");
        throw new DirectoryNotFoundException("Repository root not found.");
    }

    private static Dictionary<string, string> Turkish()
    {
        var text = File.ReadAllText(Path.Combine(Localization(), "Turkish.cs"));
        return EntryPattern().Matches(text).ToDictionary(m => Unescape(m.Groups[1].Value), m => Unescape(m.Groups[2].Value));
    }

    private static string Unescape(string value) => value.Replace("\\\"", "\"").Replace("\\\\", "\\");

    private static IEnumerable<string> Holes(string text) => HolePattern().Matches(text).Select(m => m.Value).Distinct().Order();

    public static TheoryData<string> Languages() => [.. AppLanguages.Available.Where(l => l is not ("en" or "tr"))];

    [Theory]
    [MemberData(nameof(Languages))]
    public void CatalogIsCompleteAndKeepsPlaceholders(string language)
    {
        var catalog = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(Localization(), "Catalogs", language + ".json")))!;
        var turkish = Turkish();
        Assert.All(turkish.Keys, key => Assert.True(catalog.ContainsKey(key), key));
        Assert.All(catalog, entry =>
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Value), entry.Key);
            Assert.Equal(Holes(entry.Key), Holes(entry.Value));
        });
    }

    [Fact]
    public void TurkishKeepsPlaceholders() =>
        Assert.All(Turkish(), entry => Assert.Equal(Holes(entry.Key), Holes(entry.Value)));

    [GeneratedRegex(@"\[""((?:[^""\\]|\\.)*)""\]\s*=\s*""((?:[^""\\]|\\.)*)""")]
    private static partial Regex EntryPattern();

    [GeneratedRegex(@"\{\d+\}")]
    private static partial Regex HolePattern();
}
