using System.Globalization;

namespace Pegline.App.Localization;

/// <summary>
/// Interface strings keyed by their English text. Missing translations fall back to English.
/// Further catalogs (es, fr, pt-BR, az, zh-Hans, ja, ko) plug into <see cref="Catalogs"/>.
/// </summary>
internal static class Loc
{
    private static readonly Dictionary<string, Dictionary<string, string>> Catalogs = new()
    {
        ["tr"] = Turkish.Strings,
    };

    private static Dictionary<string, string>? _active;

    public static string Language { get; private set; } = "en";

    public static void Use(string preference)
    {
        var language = preference == "system" ? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName : preference;
        Language = Catalogs.ContainsKey(language) ? language : "en";
        _active = Catalogs.GetValueOrDefault(Language);
    }

    public static string T(string english) => _active?.GetValueOrDefault(english) ?? english;

    public static string F(string english, params object[] args) => string.Format(CultureInfo.CurrentCulture, T(english), args);
}
