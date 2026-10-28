using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Pegline.Core.Storage;

/// <summary>Content-addressed media files (sha256 + extension). Identical bytes share one file.</summary>
public sealed partial class MediaStore
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    public const long MaxPngBytes = 64L * 1024 * 1024;
    public const long MaxPixels = 80_000_000;

    public MediaStore(string directory)
    {
        Directory = directory;
        System.IO.Directory.CreateDirectory(directory);
    }

    public string Directory { get; }

    public static bool IsManagedName(string name) => ManagedName().IsMatch(name);

    public string PathOf(string name)
    {
        if (!IsManagedName(name))
            throw new InvalidOperationException("Unsafe managed media name.");
        return Path.Combine(Directory, name);
    }

    /// <summary>Validates PNG bytes and returns their pixel size.</summary>
    public static (int Width, int Height) ReadPngSize(ReadOnlySpan<byte> png)
    {
        if (png.Length < 24 || png.Length > MaxPngBytes || !png[..8].SequenceEqual(PngSignature))
            throw new InvalidDataException("Invalid PNG image.");
        var width = BinaryPrimitives.ReadUInt32BigEndian(png[16..20]);
        var height = BinaryPrimitives.ReadUInt32BigEndian(png[20..24]);
        if (width == 0 || height == 0 || (long)width * height > MaxPixels)
            throw new InvalidDataException("Image is too large.");
        return ((int)width, (int)height);
    }

    public string PutPng(byte[] png)
    {
        ReadPngSize(png);
        return Put(png, ".png");
    }

    public string Put(byte[] bytes, string extension)
    {
        var name = Convert.ToHexStringLower(SHA256.HashData(bytes)) + extension;
        var destination = PathOf(name);
        if (File.Exists(destination))
        {
            Refresh(destination);
            return name;
        }
        var temporary = Path.Combine(Directory, "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllBytes(temporary, bytes);
            try
            {
                File.Move(temporary, destination);
            }
            catch (IOException) when (File.Exists(destination))
            {
                // Another writer stored the same content first.
            }
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
        return name;
    }

    /// <summary>Copies a file into the store while hashing it, returning the managed name.</summary>
    public async Task<string> ImportAsync(string source, string extension, long maxBytes, CancellationToken cancellation = default)
    {
        var temporary = Path.Combine(Directory, "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            string hash;
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true))
            {
                if (input.Length > maxBytes)
                    throw new InvalidDataException("File is too large.");
                await using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[1 << 20];
                int read;
                while ((read = await input.ReadAsync(buffer, cancellation)) > 0)
                {
                    sha.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellation);
                }
                hash = Convert.ToHexStringLower(sha.GetHashAndReset());
            }
            var name = hash + extension.ToLowerInvariant();
            var destination = PathOf(name);
            if (!File.Exists(destination))
                File.Move(temporary, destination);
            else
                Refresh(destination);
            return name;
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    /// <summary>How long a newly stored file is safe from cleanup while its record is being updated.</summary>
    public TimeSpan CleanupGrace { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Storing existing content again counts as new for the cleanup grace period.</summary>
    private static void Refresh(string path)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Deletes managed files that no record references any more.</summary>
    public int DeleteUnreferenced(IReadOnlySet<string> referenced)
    {
        var deleted = 0;
        // A file stored moments ago may not be attached to its record yet (a link preview, a trim, an import
        // finishing on another thread); it is collected by a later cleanup if it stays unreferenced.
        var settled = DateTime.UtcNow - CleanupGrace;
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory))
        {
            var name = Path.GetFileName(file);
            if (!IsManagedName(name) || referenced.Contains(name) || File.GetLastWriteTimeUtc(file) > settled)
                continue;
            try
            {
                File.Delete(file);
                deleted++;
            }
            catch (IOException)
            {
                // Retried on the next cleanup.
            }
        }
        return deleted;
    }

    [GeneratedRegex("^[a-f0-9]{64}\\.(png|jpg|mp4|mov)$")]
    private static partial Regex ManagedName();
}
