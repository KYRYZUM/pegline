using System.Globalization;
using Pegline.Core.Models;

namespace Pegline.Core.Shelves;

/// <summary>Literal, case-insensitive matching across everything a shelf card stands for.</summary>
public static class ShelfSearch
{
    private const CompareOptions Options = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace | CompareOptions.IgnoreWidth;

    public static bool Matches(ClipItem item, string query)
    {
        query = query.Trim();
        if (query.Length == 0) return true;
        return Contains(item.Text, query) || Contains(item.Url, query) || Contains(item.Title, query)
            || Contains(item.Name, query) || Contains(item.Note, query) || Contains(item.OcrText, query)
            || Contains(item.LinkSiteName, query) || Contains(item.LinkAuthor, query)
            || item.Tags.Any(t => Contains(t, query));
    }

    private static bool Contains(string value, string query) =>
        value.Length > 0 && CultureInfo.InvariantCulture.CompareInfo.IndexOf(value, query, Options) >= 0;
}
