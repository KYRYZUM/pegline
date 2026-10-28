using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pegline.Core.Search;

/// <summary>
/// CLIP's byte pair encoding tokenizer, the one MobileCLIP's text encoder was trained with: lower case, split into
/// words, each word into learned sub-word pieces. Same rules and the same first 48,894 merges as the reference app.
/// </summary>
public sealed partial class ClipTokenizer
{
    public const int Start = 49406;
    public const int End = 49407;
    public const int Context = 77;
    public const int MaxQueryChars = 2048;
    private const int MergeCount = 49152 - 256 - 2;

    private readonly Dictionary<string, int> _vocab;
    private readonly Dictionary<string, int> _ranks;
    private readonly string[] _byteToUnicode = MakeByteToUnicode();
    private readonly Dictionary<string, string[]> _cache = [];
    private readonly Lock _gate = new();

    public ClipTokenizer(Dictionary<string, int> vocab, string merges)
    {
        _vocab = vocab;
        _ranks = new Dictionary<string, int>(MergeCount, StringComparer.Ordinal);
        var lines = merges.Split('\n');
        for (var i = 1; i < lines.Length && i <= MergeCount; i++)
            _ranks.TryAdd(lines[i].TrimEnd('\r'), i - 1);
    }

    public static ClipTokenizer FromFiles(string vocabPath, string mergesPath) =>
        new(JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(vocabPath, Encoding.UTF8)) ?? throw new InvalidDataException("Invalid CLIP vocabulary."),
            File.ReadAllText(mergesPath, Encoding.UTF8));

    /// <summary>Start mark, up to 75 pieces, end mark.</summary>
    public IReadOnlyList<int> Encode(string text)
    {
        if (text.Length > MaxQueryChars) text = text[..MaxQueryChars];
        var clean = string.Join(' ', text.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var ids = new List<int> { Start };
        lock (_gate)
        {
            foreach (Match match in Words().Matches(clean))
            {
                var word = string.Concat(Encoding.UTF8.GetBytes(match.Value).Select(b => _byteToUnicode[b]));
                foreach (var piece in Bpe(word))
                    if (_vocab.TryGetValue(piece, out var id) && id is >= 0 and <= End) ids.Add(id);
            }
        }
        if (ids.Count > Context - 1) ids.RemoveRange(Context - 1, ids.Count - (Context - 1));
        ids.Add(End);
        return ids;
    }

    /// <summary>The text model's input: 77 ids padded with zeros.</summary>
    public long[] Tensor(string text)
    {
        var output = new long[Context];
        var ids = Encode(text);
        for (var i = 0; i < ids.Count; i++) output[i] = ids[i];
        return output;
    }

    /// <summary>Merges the characters of a word, best ranked pair first, until no learned merge applies.</summary>
    private string[] Bpe(string word)
    {
        if (_cache.TryGetValue(word, out var cached)) return cached;
        var parts = new List<string>();
        // The byte alphabet is all BMP characters; one char is one symbol.
        foreach (var c in word) parts.Add(c.ToString());
        if (parts.Count == 0) return [];
        parts[^1] += "</w>";
        while (parts.Count > 1)
        {
            var best = int.MaxValue;
            var selected = -1;
            for (var i = 0; i < parts.Count - 1; i++)
            {
                if (_ranks.TryGetValue(parts[i] + " " + parts[i + 1], out var rank) && rank < best)
                {
                    best = rank;
                    selected = i;
                }
            }
            if (selected < 0) break;
            var first = parts[selected];
            var second = parts[selected + 1];
            var merged = new List<string>(parts.Count);
            for (var i = 0; i < parts.Count; i++)
            {
                if (i < parts.Count - 1 && parts[i] == first && parts[i + 1] == second)
                {
                    merged.Add(first + second);
                    i++;
                }
                else merged.Add(parts[i]);
            }
            parts = merged;
        }
        if (_cache.Count >= 2048) _cache.Clear();
        var result = parts.ToArray();
        _cache[word] = result;
        return result;
    }

    /// <summary>GPT-2's reversible map from bytes to printable characters.</summary>
    private static string[] MakeByteToUnicode()
    {
        var bytes = Enumerable.Range(33, 94).Concat(Enumerable.Range(161, 12)).Concat(Enumerable.Range(174, 82)).ToList();
        var chars = new List<int>(bytes);
        var next = 256;
        for (var b = 0; b < 256; b++)
        {
            if (bytes.Contains(b)) continue;
            bytes.Add(b);
            chars.Add(next++);
        }
        var map = new string[256];
        for (var i = 0; i < bytes.Count; i++) map[bytes[i]] = ((char)chars[i]).ToString();
        return map;
    }

    [GeneratedRegex(@"<\|startoftext\|>|<\|endoftext\|>|'s|'t|'re|'ve|'m|'ll|'d|[\p{L}]+|[\p{N}]|[^\s\p{L}\p{N}]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Words();
}
