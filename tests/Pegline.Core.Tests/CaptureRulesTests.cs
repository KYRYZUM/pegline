using Pegline.Core.Capture;

namespace Pegline.Core.Tests;

/// <summary>What a copy must look like to be kept, and when two copied texts count as the same record.</summary>
public class CaptureRulesTests
{
    /// <summary>Copies of one text that differ only in line-ending style, composition or blank lines around it.</summary>
    public static TheoryData<string, string> SameText() => new()
    {
        { "Günaydın\ndünya", "Günaydın\r\ndünya" },
        { "Günaydın\ndünya", "\r\nGünaydın\rdünya\n\n" },
        { "Çay ve şeker", "\nÇay ve şeker\r\n" },
        { "e\u0301tude", "\u00e9tude" },
        { "liste\n\nbitti", "liste\r\n\r\nbitti" },
        { "\n", "\r\n" },
        { "\r", "\n" },
        { "\n\n", "\r\r" },
        { "ref_42-b", "\r\nref_42-b\n" },
    };

    [Theory]
    [MemberData(nameof(SameText))]
    public void EquivalentCopiesShareOneIdentity(string first, string second) =>
        Assert.Equal(TextIdentity.Of(first), TextIdentity.Of(second));

    [Fact]
    public void EverythingElseKeepsItsOwnIdentity()
    {
        // Case, spaces and tabs at either end, invisible characters, look-alike letters and the number of blank lines all count.
        string[] distinct =
        [
            "Kalem", "kalem", " Kalem", "Kalem ", "\tKalem", "Kalem\t", "Kalem\u200b", "Kalem\u2028",
            " ", "\t", "\n", "\n\n", "İ", "I", "\ufb03", "ffi", "ref_42-b", "ref_42-b ", "satır\nikinci", "satır\n\nikinci",
        ];
        var identities = distinct.Select(TextIdentity.Of).ToList();
        Assert.Equal(distinct.Length, identities.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("    ", false)]
    [InlineData("\r\n\t", false)]
    [InlineData("\u00a0\u2003\u3000", false)]
    [InlineData("", false)]
    [InlineData("\t Ankara 🇹🇷\r\nikinci satır  ", true)]
    [InlineData("\u200b", true)]
    [InlineData("\ufeff", true)]
    public void BlankTextIsNotKept(string value, bool kept) =>
        Assert.Equal(kept, ClipboardAdmission.AdmitsText(value));

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
