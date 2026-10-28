using System.Globalization;

namespace Pegline.Core.Models;

/// <summary>Interface languages: the stored preference ("system" or a code) and what it resolves to.</summary>
public static class AppLanguages
{
    /// <summary>Languages with an interface catalog, English first.</summary>
    public static readonly IReadOnlyList<string> Available = ["en", "tr", "es", "fr", "pt-BR", "az", "zh-Hans", "ja", "ko"];

    /// <summary>Each language in its own words, so it can be found whatever the current interface language.</summary>
    public static string NativeName(string code) => code switch
    {
        "en" => "English",
        "tr" => "Türkçe",
        "es" => "Español",
        "fr" => "Français",
        "pt-BR" => "Português (Brasil)",
        "az" => "Azərbaycan dili",
        "zh-Hans" => "简体中文",
        "ja" => "日本語",
        "ko" => "한국어",
        _ => code,
    };

    /// <summary>The catalog to use. "system" follows the Windows display language, falling back to English.</summary>
    public static string Resolve(string preference, CultureInfo system)
    {
        if (preference != "system") return Available.Contains(preference) ? preference : "en";
        for (var culture = system; !culture.Equals(CultureInfo.InvariantCulture); culture = culture.Parent)
        {
            var name = culture.Name;
            if (Available.Contains(name)) return name;
            // Any Portuguese uses the Brazilian catalog; Chinese only in its simplified script.
            if (culture.TwoLetterISOLanguageName == "pt") return "pt-BR";
            if (name is "zh-CN" or "zh-SG" or "zh-Hans-CN" or "zh-Hans-SG") return "zh-Hans";
            if (culture.TwoLetterISOLanguageName is "zh") continue;
            if (Available.Contains(culture.TwoLetterISOLanguageName)) return culture.TwoLetterISOLanguageName;
        }
        return "en";
    }
}
