using System.Net;
using System.Text;
using Pegline.Core.Links;
using Pegline.Core.Storage;

namespace Pegline.Core.Tests;

public sealed class LinkTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pegline-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", null)]
    [InlineData("https://tr.wikipedia.org/wiki/İstanbul", null)]
    [InlineData("https://example.com/page?utm_source=x&id=4", null)]
    [InlineData("http://example.com/", "not https")]
    [InlineData("https://user:pass@example.com/", "credentials")]
    [InlineData("https://example.com:8443/", "port")]
    [InlineData("https://localhost/", "local host")]
    [InlineData("https://printer.local/", "local host")]
    [InlineData("https://nas/", "local host")]
    [InlineData("https://192.168.1.10/", "private address")]
    [InlineData("https://10.0.0.1/", "private address")]
    [InlineData("https://[::1]/", "private address")]
    [InlineData("https://example.com/reset?token=abc", "sensitive parameter")]
    [InlineData("https://example.com/cb?Access_Token=abc", "sensitive parameter")]
    [InlineData("https://bucket.s3.amazonaws.com/f?X-Amz-Signature=1", "sensitive parameter")]
    [InlineData("https://example.com/oauth?oauth_verifier=1", "sensitive parameter")]
    public void OnlyPublicHttpsPagesWithoutSecretsAreLookedUp(string url, string? refusal) => Assert.Equal(refusal, LinkPolicy.Refusal(url));

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("1.1.1.1", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("172.20.0.5", false)]
    [InlineData("100.100.1.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("2606:4700::1111", true)]
    [InlineData("fe80::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("::ffff:192.168.0.1", false)]
    public void ResolvedAddressesMustBePublic(string address, bool isPublic) => Assert.Equal(isPublic, LinkPolicy.IsPublic(IPAddress.Parse(address)));

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=42", "dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?si=abc", "dQw4w9WgXcQ")]
    [InlineData("https://m.youtube.com/shorts/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/embed/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/@channel", null)]
    [InlineData("https://notyoutube.com/watch?v=dQw4w9WgXcQ", null)]
    public void YouTubeVideosAreRecognizedInEveryLinkForm(string url, string? id)
    {
        Assert.Equal(id, LinkPolicy.YouTubeId(url));
        if (id is not null) Assert.Equal("youtube:" + id, LinkPolicy.CacheKey(url));
    }

    [Fact]
    public void PagesDescribeThemselvesThroughOpenGraphOrTheirTitle()
    {
        var page = new Uri("https://example.com/a/b");
        var html = """
            <html><head><meta charset="utf-8"><title>Fallback</title>
            <meta property="og:title" content="Çalışma &amp; Planı">
            <meta content="Örnek Site" property="og:site_name">
            <meta name="author" content="Ayşe Yılmaz">
            <meta property="og:image" content="/img/cover.png">
            </head><body></body></html>
            """;
        var metadata = LinkMetadata.FromHtml(Encoding.UTF8.GetBytes(html), "text/html", page);
        Assert.Equal("Çalışma & Planı", metadata.Title);
        Assert.Equal("Örnek Site", metadata.SiteName);
        Assert.Equal("Ayşe Yılmaz", metadata.Author);
        Assert.Equal("https://example.com/img/cover.png", metadata.ImageUrl);

        var plain = LinkMetadata.FromHtml(Encoding.UTF8.GetBytes("<title>\n  Just a   <b>title</b>\n</title><meta property='og:image' content='http://insecure/x.png'>"), "text/html; charset=utf-8", page);
        Assert.Equal("Just a title", plain.Title);
        Assert.Equal("", plain.ImageUrl); // never downgraded to HTTP

        Assert.Equal("example.com", LinkMetadata.FromHtml([], "text/html", page).Title);
    }

    [Fact]
    public void LegacyCodePagesAreDecoded()
    {
        var bytes = CodePagesEncodingProviderBytes("<title>Şehir Işıkları</title>", 1254);
        Assert.Equal("Şehir Işıkları", LinkMetadata.FromHtml(bytes, "text/html; charset=windows-1254", new Uri("https://example.com")).Title);
    }

    [Fact]
    public void YouTubeThumbnailsOnlyComeFromYouTubeImageHosts()
    {
        var good = LinkMetadata.FromYouTubeOEmbed(Encoding.UTF8.GetBytes("""{"title":"Video","author_name":"Kanal","thumbnail_url":"https://i.ytimg.com/vi/x/hqdefault.jpg"}"""));
        Assert.Equal(("Video", "Kanal", "YouTube", "https://i.ytimg.com/vi/x/hqdefault.jpg"), (good.Title, good.Author, good.SiteName, good.ImageUrl));
        var bad = LinkMetadata.FromYouTubeOEmbed(Encoding.UTF8.GetBytes("""{"title":"Video","thumbnail_url":"https://evil.example/x.jpg"}"""));
        Assert.Equal("", bad.ImageUrl);
    }

    [Fact]
    public void LookupStateIsKeptAndANameAlwaysWins()
    {
        using var store = new LibraryStore(_directory);
        var link = store.InsertLink("https://example.com/");
        store.SetDetails(link.Id, name: "Benim adım");
        store.SetLinkMetadata(link.Id, "Example Domain", "Example", "", "");
        var item = store.Find(link.Id)!;
        Assert.Equal(("ready", "Benim adım", "Example Domain"), (item.LinkMetadataState, item.DisplayTitle, item.Title));
        Assert.NotNull(item.LinkMetadataAttemptedAt);

        store.ResetLinkLookup([link.Id]);
        Assert.Equal("", store.Find(link.Id)!.LinkMetadataState);
        store.MarkLinkLookup(link.Id, "failed");
        Assert.Equal("failed", store.Find(link.Id)!.LinkMetadataState);

        // A trashed link is not updated by a lookup that finishes late.
        store.MoveToTrash([link.Id]);
        store.SetLinkMetadata(link.Id, "Late", "", "", "");
        Assert.Equal("Example Domain", store.Find(link.Id)!.Title);
    }

    private static byte[] CodePagesEncodingProviderBytes(string text, int codePage)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(codePage).GetBytes(text);
    }
}
