namespace Pegline.Core.Models;

public enum ItemKind
{
    Image,
    Text,
    Link,
}

public sealed record CropRect(double X, double Y, double Width, double Height);

/// <summary>One captured or created record. Images, text and links share the same identity and organization fields.</summary>
public sealed class ClipItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ItemKind Kind { get; set; }

    public string Text { get; set; } = "";
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public string Name { get; set; } = "";
    public string Note { get; set; } = "";

    public bool Pinned { get; set; }
    public List<Guid> FolderIds { get; set; } = [];
    public List<string> Tags { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? RetainedAt { get; set; }
    public DateTimeOffset? LastCopiedAt { get; set; }
    public DateTimeOffset? TrashedAt { get; set; }
    public bool HiddenFromShelf { get; set; }

    /// <summary>Changes on every capture or copy so stale confirmations can detect a re-copy of the same identity.</summary>
    public Guid? CaptureRevision { get; set; }
    public Guid? TrashRevision { get; set; }

    // Managed media file names inside the media directory (sha256 + extension).
    public string ImageFile { get; set; } = "";
    public string RenderedFile { get; set; } = "";
    public string ThumbnailFile { get; set; } = "";
    public int PixelWidth { get; set; }
    public int PixelHeight { get; set; }
    public string OcrText { get; set; } = "";
    public string AnnotationsJson { get; set; } = "[]";
    public CropRect? Crop { get; set; }

    // Link enrichment.
    public string LinkSiteName { get; set; } = "";
    public string LinkAuthor { get; set; } = "";
    public string LinkThumbnailFile { get; set; } = "";

    // Screen recordings live on the Images shelf.
    public bool Recording { get; set; }
    public string VideoFile { get; set; } = "";
    public string RenderedVideoFile { get; set; } = "";
    public double DurationSeconds { get; set; }

    /// <summary>Original file path for items collected from a watched folder; never deleted by the app.</summary>
    public string SourcePath { get; set; } = "";

    /// <summary>Saved records survive ordinary history expiry.</summary>
    public bool IsSaved => RetainedAt is not null || Pinned || FolderIds.Count > 0 || Tags.Count > 0;

    public bool IsTrashed => TrashedAt is not null;

    public bool IsOnShelf => !IsTrashed && !HiddenFromShelf;

    public string DisplayFile => string.IsNullOrEmpty(RenderedFile) ? ImageFile : RenderedFile;

    public string DisplayTitle => Name.Length > 0 ? Name : Title;

    public ClipItem Clone()
    {
        var copy = (ClipItem)MemberwiseClone();
        copy.FolderIds = [.. FolderIds];
        copy.Tags = [.. Tags];
        return copy;
    }
}
