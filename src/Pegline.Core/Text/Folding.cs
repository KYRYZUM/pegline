using System.Globalization;
using System.Text;

namespace Pegline.Core.Text;

/// <summary>Search form of a string: case, width and diacritic insensitive; Turkish dotless ı folds to i.</summary>
public static class Folding
{
    public static string Fold(string value)
    {
        if (value.Length == 0) return value;
        var decomposed = value.Normalize(NormalizationForm.FormKD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            builder.Append(c == 'ı' ? 'i' : char.ToLowerInvariant(c));
        }
        return builder.ToString();
    }

    /// <summary>Whitespace-separated folded search terms.</summary>
    public static string[] Terms(string value) => Fold(value).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Equality on the folded form, independent of the current culture's casing rules.</summary>
    public static readonly StringComparer Comparer = new FoldedComparer();

    private sealed class FoldedComparer : StringComparer
    {
        public override int Compare(string? x, string? y) => string.CompareOrdinal(x is null ? null : Fold(x), y is null ? null : Fold(y));

        public override bool Equals(string? x, string? y) => x is null || y is null ? x == y : Fold(x) == Fold(y);

        public override int GetHashCode(string obj) => Fold(obj).GetHashCode(StringComparison.Ordinal);
    }
}
