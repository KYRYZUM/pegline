using System.Globalization;
using System.Text;

namespace Pegline.Core.Actions;

/// <summary>Searches palette commands by their user-facing words, ranking title matches above keyword matches.</summary>
public static class CommandFilter
{
    /// <summary>
    /// The unfiltered list keeps its curated order. A search keeps commands whose title, subtitle,
    /// group or keywords contain every token, preferring exact titles and word starts over
    /// incidental substrings ("pen" inside "Open"); the original order breaks ties.
    /// </summary>
    public static IReadOnlyList<ActionCommand> Filter(IReadOnlyList<ActionCommand> commands, string query)
    {
        var tokens = Tokens(query);
        if (tokens.Length == 0) return commands;
        var phrase = string.Join(' ', tokens);
        return
        [
            .. commands
                .Select((command, index) => (command, index))
                .Where(entry =>
                {
                    var text = Fold(string.Join(' ', [entry.command.Title, entry.command.Subtitle, entry.command.Group, .. entry.command.Keywords]));
                    return tokens.All(text.Contains);
                })
                .OrderBy(entry => Relevance(entry.command, tokens, phrase))
                .ThenBy(entry => entry.index)
                .Select(entry => entry.command),
        ];
    }

    /// <summary>Case, width and diacritic insensitive form; Turkish dotless ı folds to i.</summary>
    public static string Fold(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormKD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            builder.Append(c == 'ı' ? 'i' : char.ToLowerInvariant(c));
        }
        return builder.ToString();
    }

    private static string[] Tokens(string value) =>
        Fold(value).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private static string Normalized(string value) => string.Join(' ', Tokens(value));

    private static int Relevance(ActionCommand command, string[] tokens, string phrase)
    {
        var title = Normalized(command.Title);
        if (title == phrase) return 0;
        if (title.StartsWith(phrase, StringComparison.Ordinal)) return 1;
        var words = title.Split(' ');
        if (tokens.All(token => words.Any(word => word.StartsWith(token, StringComparison.Ordinal)))) return 2;
        if (tokens.All(title.Contains)) return 3;
        var aliases = command.Keywords.Select(Normalized).ToList();
        if (aliases.Contains(phrase)) return 4;
        if (aliases.Any(alias => alias.StartsWith(phrase, StringComparison.Ordinal))) return 5;
        var aliasWords = aliases.SelectMany(alias => alias.Split(' ')).ToList();
        if (tokens.All(token => aliasWords.Any(word => word.StartsWith(token, StringComparison.Ordinal)))) return 6;
        return 7;
    }
}
