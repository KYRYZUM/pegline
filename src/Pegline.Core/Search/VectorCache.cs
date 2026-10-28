using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace Pegline.Core.Search;

/// <summary>
/// The derived visual index: one small file per image pixels (keyed by the content hash of the displayed PNG),
/// stamped with the model that produced it. It never touches records; deleting it only costs re-indexing.
/// An empty entry means "seen, nothing the model can use" (slivers, long strips).
/// </summary>
public sealed partial class VectorCache
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("PGLVEC01");
    private const int HeaderSize = 8 + 32 + 32 + 4;
    private readonly string _directory;
    private readonly byte[] _model;
    private readonly Dictionary<string, IReadOnlyList<float[]>> _memory = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public VectorCache(string directory, byte[] modelId)
    {
        if (modelId.Length != 32) throw new ArgumentException("A model id is a SHA-256.", nameof(modelId));
        _directory = directory;
        _model = modelId;
        Directory.CreateDirectory(directory);
    }

    /// <summary>The index key of a media file name ("&lt;sha256&gt;.png"); null for anything else.</summary>
    public static string? KeyOf(string mediaFile) => MediaName().Match(mediaFile) is { Success: true } match ? match.Groups[1].Value : null;

    public bool Has(string key) => Load(key) is not null;

    /// <summary>The stored views, or null when this image has not been indexed by this model.</summary>
    public IReadOnlyList<float[]>? Load(string key)
    {
        if (!IsKey(key)) return null;
        lock (_gate)
            if (_memory.TryGetValue(key, out var cached)) return cached;
        try
        {
            var path = PathOf(key);
            var info = new FileInfo(path);
            if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Length > HeaderSize + VisualRanking.MaxViews * VisualRanking.Dimensions * 4) return null;
            var record = File.ReadAllBytes(path);
            if (record.Length < HeaderSize || !record.AsSpan(0, 8).SequenceEqual(Magic) || !record.AsSpan(8, 32).SequenceEqual(_model)
                || !record.AsSpan(40, 32).SequenceEqual(Convert.FromHexString(key))) return null;
            var count = BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(72));
            if (count is < 0 or > VisualRanking.MaxViews || record.Length != HeaderSize + count * VisualRanking.Dimensions * 4) return null;
            var views = new List<float[]>(count);
            for (var view = 0; view < count; view++)
            {
                var values = new float[VisualRanking.Dimensions];
                for (var i = 0; i < values.Length; i++)
                    values[i] = BinaryPrimitives.ReadSingleLittleEndian(record.AsSpan(HeaderSize + (view * VisualRanking.Dimensions + i) * 4));
                // Stored vectors are unit length; anything else is a damaged file.
                if (VisualRanking.Normalized(values) is null || Math.Abs(values.Sum(v => v * v) - 1) > 0.02) return null;
                views.Add(values);
            }
            lock (_gate) _memory[key] = views;
            return views;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(string key, IReadOnlyList<float[]> views)
    {
        if (!IsKey(key)) throw new ArgumentException("Invalid index key.", nameof(key));
        if (views.Count > VisualRanking.MaxViews || views.Any(v => v.Length != VisualRanking.Dimensions)) throw new ArgumentException("Invalid vectors.", nameof(views));
        var record = new byte[HeaderSize + views.Count * VisualRanking.Dimensions * 4];
        Magic.CopyTo(record, 0);
        _model.CopyTo(record, 8);
        Convert.FromHexString(key).CopyTo(record, 40);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(72), views.Count);
        for (var view = 0; view < views.Count; view++)
            for (var i = 0; i < VisualRanking.Dimensions; i++)
                BinaryPrimitives.WriteSingleLittleEndian(record.AsSpan(HeaderSize + (view * VisualRanking.Dimensions + i) * 4), views[view][i]);
        var path = PathOf(key);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temporary, record);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        lock (_gate) _memory[key] = [.. views.Select(v => (float[])v.Clone())];
    }

    /// <summary>Removes entries for pixels no record shows any more (deleted, edited or expired images).</summary>
    public int Prune(IReadOnlySet<string> live)
    {
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(_directory, "*.bin"))
        {
            var key = Path.GetFileNameWithoutExtension(file);
            if (IsKey(key) && live.Contains(key)) continue;
            try
            {
                File.Delete(file);
                removed++;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            lock (_gate) _memory.Remove(key);
        }
        return removed;
    }

    private string PathOf(string key) => Path.Combine(_directory, key + ".bin");

    private static bool IsKey(string key) => key.Length == 64 && key.All(Uri.IsHexDigit) && key.All(c => !char.IsUpper(c));

    [GeneratedRegex("^([0-9a-f]{64})\\.png$")]
    private static partial Regex MediaName();
}
