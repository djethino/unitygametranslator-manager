namespace UnityGameTranslator.Installer.Core.Model;

/// <summary>
/// ISO 639-1 code to language name.
///
/// Copied from the mod's LanguageHelper, deliberately and verbatim. The API answers with names
/// ("French"), the settings hold a code ("fr"), and the two have to agree or a player's language
/// silently matches nothing. Regenerate from LanguageHelper.IsoToName if that table changes.
///
/// No language is special-cased anywhere in this tool: it is a lookup, not a policy.
/// </summary>
public static class Languages
{
    private static readonly Dictionary<string, string> IsoToName = new(StringComparer.OrdinalIgnoreCase)
    {
        { "en", "English" },
        { "fr", "French" },
        { "de", "German" },
        { "es", "Spanish" },
        { "it", "Italian" },
        { "pt", "Portuguese" },
        { "ru", "Russian" },
        { "pl", "Polish" },
        { "ja", "Japanese" },
        { "ko", "Korean" },
        { "zh", "Simplified Chinese" },
        { "zh-cn", "Simplified Chinese" },
        { "zh-hans", "Simplified Chinese" },
        { "zh-tw", "Traditional Chinese" },
        { "zh-hant", "Traditional Chinese" },
        { "ar", "Arabic" },
        { "tr", "Turkish" },
        { "nl", "Dutch" },
        { "sv", "Swedish" },
        { "da", "Danish" },
        { "nb", "Norwegian Bokmål" },
        { "nn", "Norwegian Nynorsk" },
        { "no", "Norwegian Bokmål" },
        { "fi", "Finnish" },
        { "cs", "Czech" },
        { "hu", "Hungarian" },
        { "ro", "Romanian" },
        { "el", "Greek" },
        { "th", "Thai" },
        { "vi", "Vietnamese" },
        { "id", "Indonesian" },
        { "ms", "Malay" },
        { "uk", "Ukrainian" },
        { "bg", "Bulgarian" },
        { "sk", "Slovak" },
        { "hr", "Croatian" },
        { "sr", "Serbian" },
        { "sl", "Slovenian" },
        { "lt", "Lithuanian" },
        { "lv", "Latvian" },
        { "et", "Estonian" },
        { "is", "Icelandic" },
        { "fo", "Faroese" },
        { "ga", "Irish" },
        { "cy", "Welsh" },
        { "ca", "Catalan" },
        { "gl", "Galician" },
        { "eu", "Basque" },
        { "lb", "Luxembourgish" },
        { "af", "Afrikaans" },
        { "mk", "Macedonian" },
        { "bs", "Bosnian" },
        { "sq", "Albanian" },
        { "hy", "Armenian" },
        { "be", "Belarusian" },
        { "fa", "Persian" },
        { "tg", "Tajik" },
        { "hi", "Hindi" },
        { "bn", "Bengali" },
        { "ur", "Urdu" },
        { "pa", "Punjabi" },
        { "gu", "Gujarati" },
        { "mr", "Marathi" },
        { "ta", "Tamil" },
        { "te", "Telugu" },
        { "kn", "Kannada" },
        { "ml", "Malayalam" },
        { "or", "Oriya" },
        { "si", "Sinhala" },
        { "ne", "Nepali" },
        { "as", "Assamese" },
        { "sd", "Sindhi" },
        { "my", "Burmese" },
        { "km", "Khmer" },
        { "lo", "Lao" },
        { "tl", "Tagalog" },
        { "ceb", "Cebuano" },
        { "jv", "Javanese" },
        { "su", "Sundanese" },
        { "az", "Azerbaijani" },
        { "uz", "Uzbek" },
        { "kk", "Kazakh" },
        { "ba", "Bashkir" },
        { "tt", "Tatar" },
        { "he", "Hebrew" },
        { "iw", "Hebrew" },
        { "mt", "Maltese" },
        { "ka", "Georgian" },
        { "sw", "Swahili" },
        { "ht", "Haitian Creole" },
    };

    private static readonly Dictionary<string, string> NameToIso =
        BuildReverse();

    private static Dictionary<string, string> BuildReverse()
    {
        var reverse = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in IsoToName) reverse[pair.Value] = pair.Key;
        return reverse;
    }

    /// <summary>"fr" -> "French". Returns the code itself when unknown, never null.</summary>
    public static string NameOf(string isoCode) =>
        IsoToName.TryGetValue(isoCode, out var name) ? name : isoCode;

    /// <summary>"French" -> "fr". Returns null when the name is not one we know.</summary>
    public static string? CodeOf(string? languageName) =>
        languageName is not null && NameToIso.TryGetValue(languageName.Trim(), out var code)
            ? code
            : null;

    /// <summary>
    /// True when an API answer such as "French" is the language the player asked for. Compares
    /// on the code so that a name we do not know still matches when spelled identically.
    /// </summary>
    public static bool Matches(string? languageName, string isoCode)
    {
        if (string.IsNullOrWhiteSpace(languageName)) return false;

        var code = CodeOf(languageName);
        if (code is not null) return code.Equals(isoCode, StringComparison.OrdinalIgnoreCase);

        return languageName.Trim().Equals(NameOf(isoCode), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Every (code, name) pair, sorted by name, for a language picker.</summary>
    public static IEnumerable<(string Code, string Name)> All() =>
        IsoToName.Select(p => (p.Key, p.Value)).OrderBy(p => p.Item2, StringComparer.OrdinalIgnoreCase);
}
