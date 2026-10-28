using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Pegline.Core.Links;

/// <summary>
/// Which links may be looked up automatically. Only public HTTPS pages: never local or private network hosts
/// (checked again on every resolved address), never links carrying credentials, tokens or signatures.
/// </summary>
public static partial class LinkPolicy
{
    public const int MaxRedirects = 3;
    public const int MaxPageBytes = 512 * 1024;
    public const int MaxOEmbedBytes = 64 * 1024;
    public const int MaxImageBytes = 5 * 1024 * 1024;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    /// <summary>Failed lookups are retried after this long.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromHours(1);

    private static readonly HashSet<string> SensitiveParameters =
    [
        "token", "accesstoken", "refreshtoken", "idtoken", "apikey", "auth", "authorization", "authentication", "authtoken", "code",
        "signature", "sig", "session", "sessionid", "state", "password", "secret", "clientsecret", "credential", "credentials",
        "accesskeyid", "key", "keypairid", "policy", "verifier", "jwt", "bearer",
    ];

    /// <summary>Why a link is not looked up, or null when it may be.</summary>
    public static string? Refusal(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "invalid";
        if (uri.Scheme != Uri.UriSchemeHttps) return "not https";
        if (uri.UserInfo.Length > 0) return "credentials";
        if (!uri.IsDefaultPort) return "port";
        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        if (host.Length == 0 || host == "localhost" || LocalSuffix().IsMatch(host) || !host.Contains('.') && !IPAddress.TryParse(host, out _)) return "local host";
        if (IPAddress.TryParse(host.Trim('[', ']'), out var address) && !IsPublic(address)) return "private address";
        foreach (var name in ParameterNames(uri.Query))
        {
            var normalized = new string([.. name.ToLowerInvariant().Where(char.IsAsciiLetterOrDigit)]);
            if (SensitiveParameters.Contains(normalized) || normalized.StartsWith("xamz", StringComparison.Ordinal)
                || normalized.StartsWith("xgoog", StringComparison.Ordinal) || normalized.StartsWith("oauth", StringComparison.Ordinal))
                return "sensitive parameter";
        }
        return null;
    }

    public static bool MayFetch(string url) => Refusal(url) is null;

    /// <summary>Global unicast only: no loopback, private, link-local, carrier NAT, multicast, documentation or test ranges.</summary>
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return !(b[0] == 0 || b[0] == 10 || b[0] == 127 || b[0] >= 224
                || b[0] == 100 && b[1] is >= 64 and <= 127
                || b[0] == 169 && b[1] == 254
                || b[0] == 172 && b[1] is >= 16 and <= 31
                || b[0] == 192 && (b[1] == 168 || b[1] == 0 || b[1] == 88 && b[2] == 99)
                || b[0] == 198 && (b[1] is 18 or 19 || b[1] == 51 && b[2] == 100)
                || b[0] == 203 && b[1] == 0 && b[2] == 113);
        }
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return false;
        var first = b[0] << 8 | b[1];
        var second = b[2] << 8 | b[3];
        return (first & 0xe000) == 0x2000
            && !(first == 0x2001 && ((second & 0xfe00) == 0 || second == 0x0db8))
            && first != 0x2002
            && !(first == 0x3fff && (second & 0xf000) == 0);
    }

    /// <summary>The video id of a YouTube watch, short, embed or youtu.be link.</summary>
    public static string? YouTubeId(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        var host = uri.Host.ToLowerInvariant();
        string? id = null;
        if (host == "youtu.be") id = uri.AbsolutePath.Trim('/').Split('/')[0];
        else if (host is "youtube.com" or "www.youtube.com" or "m.youtube.com" or "music.youtube.com")
        {
            var path = uri.AbsolutePath;
            if (path == "/watch") id = ParameterValue(uri.Query, "v");
            else if (YouTubePath().Match(path) is { Success: true } match) id = match.Groups[1].Value;
        }
        return id is not null && VideoId().IsMatch(id) ? id : null;
    }

    /// <summary>Links that show the same page share one lookup (a YouTube video, whatever form its link takes).</summary>
    public static string CacheKey(string url) => YouTubeId(url) is { } video ? "youtube:" + video : url;

    private static IEnumerable<string> ParameterNames(string query) =>
        query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => Uri.UnescapeDataString(p.Split('=', 2)[0].Replace('+', ' ')));

    private static string? ParameterValue(string query, string name) =>
        query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .FirstOrDefault(p => p[0] == name && p.Length == 2) is { } pair ? Uri.UnescapeDataString(pair[1]) : null;

    [GeneratedRegex(@"\.(local|internal|home|localhost|test|invalid|lan|corp|intranet)$")]
    private static partial Regex LocalSuffix();

    [GeneratedRegex(@"^/(?:shorts|embed|live|v)/([^/?#]+)")]
    private static partial Regex YouTubePath();

    [GeneratedRegex(@"^[A-Za-z0-9_-]{11}$")]
    private static partial Regex VideoId();
}
