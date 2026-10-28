using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Pegline.Core.Links;
using Pegline.Core.Models;
using Pegline.Core.Storage;

namespace Pegline.App.Links;

/// <summary>
/// Looks up a copied link's title, site, author and preview image (YouTube through its oEmbed endpoint).
/// Public HTTPS pages only: every resolved address is checked before connecting, redirects are re-checked,
/// responses are size and time bounded, and links with credentials or token-like parameters are never fetched.
/// At most two lookups run at once; the newest links go first.
/// </summary>
internal sealed class LinkPreviewService : IDisposable
{
    private const int Concurrency = 2;
    private const int ThumbnailSide = 640;
    private readonly LibraryStore _store;
    private readonly Dispatcher _dispatcher;
    private readonly Func<bool> _enabled;
    private readonly HttpClient _http;
    private readonly List<Guid> _wanted = [];
    private readonly HashSet<Guid> _running = [];
    private readonly Dictionary<string, (LinkMetadata Metadata, string Thumbnail)> _cache = [];
    private readonly CancellationTokenSource _stop = new();

    public LinkPreviewService(LibraryStore store, Dispatcher dispatcher, Func<bool> enabled)
    {
        _store = store;
        _dispatcher = dispatcher;
        _enabled = enabled;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false,
            // A proxy would make the connection target unverifiable; lookups go direct or not at all.
            UseProxy = false,
            ConnectTimeout = LinkPolicy.Timeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = ConnectPublicAsync,
        };
        _http = new HttpClient(handler) { Timeout = LinkPolicy.Timeout };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; Pegline/0.1; +link-preview)");
        _http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/json,image/*;q=0.8,*/*;q=0.5");
    }

    /// <summary>Looks up every link that has no preview yet (or failed over an hour ago), newest first.</summary>
    public void RequestAll() =>
        Request([.. _store.Items.Where(i => i.Kind == ItemKind.Link && !i.IsTrashed).OrderByDescending(i => i.CreatedAt).Select(i => i.Id)]);

    /// <summary>Queues links in front of older requests. <paramref name="force"/> refreshes ones already looked up.</summary>
    public void Request(IReadOnlyList<Guid> ids, bool force = false)
    {
        if (force)
        {
            foreach (var id in ids)
                if (_store.Find(id) is { Kind: ItemKind.Link } item) _cache.Remove(LinkPolicy.CacheKey(item.Url));
            _store.ResetLinkLookup(ids);
        }
        _wanted.RemoveAll(ids.Contains);
        _wanted.InsertRange(0, ids);
        Schedule();
    }

    private bool Due(ClipItem item) =>
        item.Kind == ItemKind.Link && !item.IsTrashed && item.LinkMetadataState switch
        {
            "" => true,
            "failed" => item.LinkMetadataAttemptedAt is not { } at || DateTimeOffset.Now - at > LinkPolicy.RetryAfter,
            _ => false,
        };

    private void Schedule()
    {
        if (!_enabled())
        {
            _wanted.Clear();
            return;
        }
        while (_running.Count < Concurrency && _wanted.Count > 0)
        {
            var id = _wanted[0];
            _wanted.RemoveAt(0);
            if (_running.Contains(id) || _store.Find(id) is not { } item || !Due(item)) continue;
            if (LinkPolicy.Refusal(item.Url) is not null)
            {
                _store.MarkLinkLookup(id, "blocked");
                continue;
            }
            _running.Add(id);
            _ = RunAsync(id, item.Url);
        }
    }

    private async Task RunAsync(Guid id, string url)
    {
        try
        {
            var key = LinkPolicy.CacheKey(url);
            if (!_cache.TryGetValue(key, out var found))
            {
                found = await Task.Run(() => LookUpAsync(url, _stop.Token));
                _cache[key] = found;
                if (_cache.Count > 128) _cache.Remove(_cache.Keys.First());
            }
            // Back on the UI thread: the link may have been trashed or changed meanwhile.
            if (_store.Find(id) is { Kind: ItemKind.Link, IsTrashed: false } current && current.Url == url && _enabled())
                _store.SetLinkMetadata(id, found.Metadata.Title, found.Metadata.SiteName, found.Metadata.Author, found.Thumbnail);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or InvalidDataException or System.Text.Json.JsonException or UriFormatException or NotSupportedException or ArgumentException)
        {
            if (!_stop.IsCancellationRequested) _store.MarkLinkLookup(id, "failed");
        }
        finally
        {
            _running.Remove(id);
            if (!_stop.IsCancellationRequested) Schedule();
        }
    }

    private async Task<(LinkMetadata, string)> LookUpAsync(string url, CancellationToken cancel)
    {
        LinkMetadata metadata;
        if (LinkPolicy.YouTubeId(url) is { } video)
        {
            var endpoint = "https://www.youtube.com/oembed?format=json&url=" + Uri.EscapeDataString("https://www.youtube.com/watch?v=" + video);
            var answer = await FetchAsync(endpoint, LinkPolicy.MaxOEmbedBytes, host => host == "www.youtube.com", cancel);
            metadata = LinkMetadata.FromYouTubeOEmbed(answer.Body);
        }
        else
        {
            var page = await FetchAsync(url, LinkPolicy.MaxPageBytes, null, cancel, allowPartial: true);
            metadata = page.ContentType.Contains("html", StringComparison.OrdinalIgnoreCase)
                ? LinkMetadata.FromHtml(page.Body, page.ContentType, page.Url)
                : new LinkMetadata(page.Url.Host);
        }
        var thumbnail = "";
        if (metadata.ImageUrl.Length > 0)
        {
            try
            {
                var image = await FetchAsync(metadata.ImageUrl, LinkPolicy.MaxImageBytes, null, cancel);
                if (Thumbnail(image.Body) is { } png) thumbnail = _store.Media.PutPng(png);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or NotSupportedException or ArgumentException or InvalidDataException or InvalidOperationException or FileFormatException)
            {
                // The title is still worth keeping without a picture.
            }
        }
        return (metadata, thumbnail);
    }

    private sealed record Response(byte[] Body, Uri Url, string ContentType);

    /// <summary>
    /// GET with manual redirects (each target re-checked) and a hard size limit. HTML pages may be cut at the
    /// limit (their head is what matters); anything else larger than the limit is refused.
    /// </summary>
    private async Task<Response> FetchAsync(string url, int maxBytes, Func<string, bool>? allowedHost, CancellationToken cancel, bool allowPartial = false)
    {
        var current = new Uri(url);
        for (var redirect = 0; ; redirect++)
        {
            if (LinkPolicy.Refusal(current.AbsoluteUri) is { } refusal) throw new HttpRequestException("Blocked: " + refusal);
            if (allowedHost is not null && !allowedHost(current.Host.ToLowerInvariant())) throw new HttpRequestException("Unexpected host.");
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(LinkPolicy.Timeout);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                if (redirect >= LinkPolicy.MaxRedirects) throw new HttpRequestException("Too many redirects.");
                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                continue;
            }
            if (response.StatusCode != HttpStatusCode.OK) throw new HttpRequestException("Unavailable: " + (int)response.StatusCode);
            var type = response.Content.Headers.ContentType?.ToString() ?? "";
            var partial = allowPartial && type.Contains("html", StringComparison.OrdinalIgnoreCase);
            if (!partial && response.Content.Headers.ContentLength > maxBytes) throw new HttpRequestException("Too large.");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var buffer = new MemoryStream();
            var block = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(block, timeout.Token)) > 0)
            {
                var room = maxBytes - (int)buffer.Length;
                if (read > room)
                {
                    if (!partial) throw new HttpRequestException("Too large.");
                    buffer.Write(block, 0, room);
                    break;
                }
                buffer.Write(block, 0, read);
            }
            return new Response(buffer.ToArray(), current, type);
        }
    }

    /// <summary>Resolves the host itself and connects only when every address is public (no DNS rebinding into the LAN).</summary>
    private static async ValueTask<Stream> ConnectPublicAsync(SocketsHttpConnectionContext context, CancellationToken cancel)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = IPAddress.TryParse(host.Trim('[', ']'), out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, cancel);
        if (addresses.Length == 0 || addresses.Any(a => !LinkPolicy.IsPublic(a))) throw new HttpRequestException("Private network destinations are blocked.");
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancel);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>The preview image reduced to at most 640 pixels, as PNG; null when it is not a usable picture.</summary>
    private static byte[]? Thumbnail(byte[] bytes)
    {
        var probe = BitmapDecoder.Create(new MemoryStream(bytes), BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
        if (probe.PixelWidth < 16 || probe.PixelHeight < 16 || (long)probe.PixelWidth * probe.PixelHeight > MediaStore.MaxPixels) return null;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(bytes);
        if (probe.PixelWidth >= probe.PixelHeight && probe.PixelWidth > ThumbnailSide) image.DecodePixelWidth = ThumbnailSide;
        else if (probe.PixelHeight > probe.PixelWidth && probe.PixelHeight > ThumbnailSide) image.DecodePixelHeight = ThumbnailSide;
        image.EndInit();
        image.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    public void Dispose()
    {
        _stop.Cancel();
        _http.Dispose();
    }
}
