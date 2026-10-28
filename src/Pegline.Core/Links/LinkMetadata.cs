using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pegline.Core.Links;

/// <summary>What a page says about itself: a title, its site, an author and a preview image address.</summary>
public sealed partial record LinkMetadata(string Title, string SiteName = "", string Author = "", string ImageUrl = "")
{
    // Pages still declare legacy code pages (windows-1254, iso-8859-9…).
    static LinkMetadata() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public const int MaxTitle = 500;
    public const int MaxName = 200;

    /// <summary>Reads Open Graph / Twitter tags and the document title from the start of an HTML page.</summary>
    public static LinkMetadata FromHtml(byte[] body, string contentType, Uri page)
    {
        var html = Decode(body, contentType);
        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match tag in MetaTag().Matches(html))
        {
            string? key = null, content = null;
            foreach (Match attribute in Attribute().Matches(tag.Groups[1].Value))
            {
                var name = attribute.Groups[1].Value.ToLowerInvariant();
                var value = attribute.Groups[2].Success ? attribute.Groups[2].Value : attribute.Groups[3].Value;
                if (name is "property" or "name" or "itemprop") key ??= value.Trim();
                else if (name == "content") content = value;
            }
            if (key is not null && content is not null) meta.TryAdd(key, Clean(content));
        }
        string First(params string[] keys) => keys.Select(k => meta.GetValueOrDefault(k, "")).FirstOrDefault(v => v.Length > 0) ?? "";

        var title = First("og:title", "twitter:title");
        if (title.Length == 0 && TitleTag().Match(html) is { Success: true } match) title = Clean(Regex.Replace(match.Groups[1].Value, "<[^>]*>", ""));
        if (title.Length == 0) title = page.Host;
        var image = First("og:image:secure_url", "og:image", "og:image:url", "twitter:image", "twitter:image:src");
        if (image.Length > 0) image = Uri.TryCreate(page, image, out var absolute) && absolute.Scheme == Uri.UriSchemeHttps ? absolute.AbsoluteUri : "";
        var author = First("author", "article:author", "twitter:creator");
        if (author.StartsWith("http", StringComparison.OrdinalIgnoreCase)) author = ""; // a profile address, not a name
        return new LinkMetadata(Limit(title, MaxTitle), Limit(First("og:site_name", "application-name"), MaxName), Limit(author, MaxName), image);
    }

    /// <summary>YouTube's oEmbed answer; the thumbnail is only taken from YouTube's image hosts.</summary>
    public static LinkMetadata FromYouTubeOEmbed(byte[] body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        string Read(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
        var image = Read("thumbnail_url");
        if (!Uri.TryCreate(image, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !(uri.Host.Equals("i.ytimg.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".ytimg.com", StringComparison.OrdinalIgnoreCase)))
            image = "";
        return new LinkMetadata(Limit(Clean(Read("title")), MaxTitle), "YouTube", Limit(Clean(Read("author_name")), MaxName), image);
    }

    private static string Decode(byte[] body, string contentType)
    {
        var charset = CharsetIn().Match(contentType) is { Success: true } header ? header.Groups[1].Value : null;
        if (charset is null)
        {
            var head = Encoding.ASCII.GetString(body, 0, Math.Min(body.Length, 4096));
            if (MetaCharset().Match(head) is { Success: true } declared) charset = declared.Groups[1].Value;
        }
        try
        {
            var encoding = charset is null ? Encoding.UTF8 : Encoding.GetEncoding(charset.Trim('"', '\''));
            return encoding.GetString(body);
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8.GetString(body);
        }
    }

    private static string Clean(string value) => Whitespace().Replace(WebUtility.HtmlDecode(value), " ").Trim();

    private static string Limit(string value, int max) => value.Length <= max ? value : value[..max];

    [GeneratedRegex(@"<meta\b([^>]{0,4096})>", RegexOptions.IgnoreCase)]
    private static partial Regex MetaTag();

    [GeneratedRegex(@"([\w:-]+)\s*=\s*(?:""([^""]*)""|'([^']*)')")]
    private static partial Regex Attribute();

    [GeneratedRegex(@"<title[^>]*>([\s\S]{0,2000}?)</title>", RegexOptions.IgnoreCase)]
    private static partial Regex TitleTag();

    [GeneratedRegex(@"charset\s*=\s*([\w""'-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex CharsetIn();

    [GeneratedRegex(@"<meta[^>]+charset\s*=\s*[""']?([\w-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex MetaCharset();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
