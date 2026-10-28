using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pegline.Core.Search;

/// <summary>Ranks images in a scope against a description. Never throws except for cancellation.</summary>
public interface IVisualSearch
{
    Task<IReadOnlyList<Guid>> SearchAsync(string text, IReadOnlyList<Guid> eligible, CancellationToken cancel);
}

/// <summary>
/// The pinned MobileCLIP-S2 ONNX export: every file's exact size and SHA-256. Models are bundled at build time
/// (scripts/prepare-search-models.ps1); the running app never downloads them or sends queries anywhere.
/// </summary>
public sealed class SearchModelConfig
{
    public const string ImageModel = "mobileclip-s2-image.onnx";
    public const string TextModel = "mobileclip-s2-text.onnx";
    public const string Vocab = "clip-vocab.json";
    public const string Merges = "clip-merges.txt";
    public const string Source = "model-source.json";

    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; }
    [JsonPropertyName("version")] public string Version { get; init; } = "";
    [JsonPropertyName("files")] public Dictionary<string, PinnedFile> Files { get; init; } = [];

    public sealed class PinnedFile
    {
        [JsonPropertyName("sha256")] public string Sha256 { get; init; } = "";
        [JsonPropertyName("bytes")] public long Bytes { get; init; }
    }

    /// <summary>The configuration this build pins (embedded search-model-config.json).</summary>
    public static SearchModelConfig Pinned()
    {
        using var stream = typeof(SearchModelConfig).Assembly.GetManifestResourceStream("Pegline.Core.Search.search-model-config.json")
            ?? throw new InvalidDataException("Missing model configuration.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    public static SearchModelConfig Parse(string json)
    {
        var config = JsonSerializer.Deserialize<SearchModelConfig>(json) ?? throw new InvalidDataException("Invalid model configuration.");
        if (config.SchemaVersion != 1 || config.Version.Length == 0
            || !new[] { ImageModel, TextModel, Vocab, Merges, Source }.All(config.Files.ContainsKey)
            || config.Files.Any(f => f.Key.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')) || f.Value.Bytes is <= 0 or > 600L * 1024 * 1024
                || f.Value.Sha256.Length != 64 || !f.Value.Sha256.All(Uri.IsHexDigit)))
            throw new InvalidDataException("Invalid model configuration.");
        return config;
    }

    /// <summary>
    /// Checks every pinned file in a folder; returns the model id (SHA-256 of model-source.json) that stamps the
    /// index, or null when a file is missing, a link, resized or altered.
    /// </summary>
    public byte[]? Verify(string directory)
    {
        try
        {
            if (!Directory.Exists(directory) || new DirectoryInfo(directory).Attributes.HasFlag(FileAttributes.ReparsePoint)) return null;
            byte[]? id = null;
            foreach (var (name, pinned) in Files)
            {
                var info = new FileInfo(Path.Combine(directory, name));
                if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Length != pinned.Bytes) return null;
                using var stream = info.OpenRead();
                var hash = SHA256.HashData(stream);
                if (!Convert.ToHexStringLower(hash).Equals(pinned.Sha256, StringComparison.OrdinalIgnoreCase)) return null;
                if (name == Source) id = hash;
            }
            return id;
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
}
