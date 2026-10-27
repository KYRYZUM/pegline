using System.Text;

namespace Pegline.Core.Capture;

/// <summary>
/// Deduplication key for copied text. The captured body is never changed; only the key ignores
/// Unicode composition, line-ending style and leading/trailing blank lines. Spaces, indentation,
/// case and internal blank lines stay significant.
/// </summary>
public static class TextIdentity
{
    public static string Of(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormC).Replace("\r\n", "\n").Replace('\r', '\n');
        var trimmed = normalized.Trim('\n');
        return trimmed.Length > 0 ? trimmed : normalized;
    }
}
