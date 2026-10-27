using System.IO;
using System.Windows.Media.Imaging;

namespace Pegline.App.Shelves;

/// <summary>Decoded card thumbnails keyed by managed file name and decode width.</summary>
internal sealed class ThumbnailCache
{
    private const int Capacity = 160;
    private readonly Dictionary<string, BitmapSource> _cache = [];
    private readonly LinkedList<string> _order = [];

    public BitmapSource? Get(string path, int decodeWidth)
    {
        var key = path + "|" + decodeWidth;
        if (_cache.TryGetValue(key, out var hit)) return hit;
        if (!File.Exists(path)) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.DecodePixelWidth = decodeWidth;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            _cache[key] = image;
            _order.AddLast(key);
            if (_order.Count > Capacity)
            {
                _cache.Remove(_order.First!.Value);
                _order.RemoveFirst();
            }
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }
}
