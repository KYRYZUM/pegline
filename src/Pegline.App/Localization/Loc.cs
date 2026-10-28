using System.Globalization;
using System.Text.Json;

namespace Pegline.App.Localization;

/// <summary>
/// Interface strings keyed by their English text. Missing translations fall back to English.
/// Turkish lives in code; the other catalogs (es, fr, pt-BR, az, zh-Hans, ja, ko) are embedded JSON files.
/// </summary>
internal static class Loc
{
    private static Dictionary<string, string>? _active;

    public static string Language { get; private set; } = "en";

    public static void Use(string preference)
    {
        var language = Pegline.Core.Models.AppLanguages.Resolve(preference, CultureInfo.CurrentUICulture);
        _active = Catalog(language);
        Language = _active is null ? "en" : language;
    }

    public static string T(string english) => _active?.GetValueOrDefault(english) ?? english;

    public static string F(string english, params object[] args) => string.Format(CultureInfo.CurrentCulture, T(english), args);

    private static Dictionary<string, string>? Catalog(string language)
    {
        if (language == "tr") return Turkish.Strings;
        using var stream = typeof(Loc).Assembly.GetManifestResourceStream("Pegline.Catalog." + language + ".json");
        if (stream is null) return null;
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
        }
        catch (JsonException ex)
        {
            App.Log(ex);
            return null;
        }
    }
}
