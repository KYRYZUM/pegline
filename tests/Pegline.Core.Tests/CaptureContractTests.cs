using System.Text.Json;
using Pegline.Core.Capture;

namespace Pegline.Core.Tests;

public class CaptureContractTests
{
    private static JsonDocument Load(string name) => JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Contracts", name)));

    [Fact]
    public void TextIdentityGroupsEquivalentCopiesAndSeparatesTheRest()
    {
        var cases = Load("text-identity.json").RootElement.GetProperty("cases").EnumerateArray()
            .Select(c => (Id: c.GetProperty("id").GetString()!, Copies: c.GetProperty("copies").EnumerateArray().Select(v => v.GetString()!).ToList()))
            .ToList();
        var keys = new Dictionary<string, string>();
        foreach (var (id, copies) in cases)
        {
            var key = TextIdentity.Of(copies[0]);
            Assert.All(copies, copy => Assert.Equal(key, TextIdentity.Of(copy)));
            Assert.False(keys.ContainsKey(key), $"{id} collides with {keys.GetValueOrDefault(key)}");
            keys[key] = id;
        }
    }

    [Fact]
    public void ClipboardAdmissionRejectsWhitespaceOnlyText()
    {
        foreach (var c in Load("clipboard-admission.json").RootElement.GetProperty("textCases").EnumerateArray())
            Assert.True(c.GetProperty("capture").GetBoolean() == ClipboardAdmission.AdmitsText(c.GetProperty("value").GetString()), c.GetProperty("name").GetString());
    }

    [Theory]
    [InlineData("ExcludeClipboardContentFromMonitorProcessing")]
    [InlineData("Clipboard Viewer Ignore")]
    [InlineData("com.agilebits.onepassword")]
    [InlineData("Pegline.Internal.v1")]
    public void PrivateFormatsExcludeTheWholeCopy(string format) =>
        Assert.True(ClipboardAdmission.IsPrivate(["UnicodeText", format]));

    [Fact]
    public void ClipboardHistoryOptOutIsRespected()
    {
        Assert.True(ClipboardAdmission.ExcludedFromHistory(BitConverter.GetBytes(0u)));
        Assert.False(ClipboardAdmission.ExcludedFromHistory(BitConverter.GetBytes(1u)));
        Assert.True(ClipboardAdmission.ExcludedFromHistory([1, 0]));
    }

    [Theory]
    [InlineData("https://example.com", "https://example.com/")]
    [InlineData("http://example.com/a?b=c#d", "http://example.com/a?b=c#d")]
    [InlineData("https://user:pass@example.com/", null)]
    [InlineData("see https://example.com", null)]
    [InlineData("ftp://example.com/file", null)]
    [InlineData("example.com", null)]
    [InlineData("https://example.com/\n", null)]
    public void OnlyAStandaloneWebAddressBecomesALink(string value, string? expected) =>
        Assert.Equal(expected, ClipboardAdmission.PlainUrl(value));
}
