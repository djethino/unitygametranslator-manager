using UnityGameTranslator.Manager.Core.Settings;

namespace UnityGameTranslator.Manager.Core.Model;

/// <summary>What pressing Play will actually produce, read from this game's own state.</summary>
public enum PlayPromise
{
    /// <summary>The game, as it shipped. Nothing is set up to change a word of it.</summary>
    Plain,

    /// <summary>A translation is in place and will be shown. Nothing new is produced.</summary>
    Translated,

    /// <summary>Lines are translated as they appear, by the backend this game is configured with.</summary>
    Translating,
}

/// <summary>
/// The promise the Play button is allowed to make.
///
/// 🔴 **In the Core, so the window and the CLI cannot answer it differently** — the rule this
/// project states for every shared decision. And read from the GAME, never from what this tool
/// intends for it: a preference is a claim about a machine somebody may have changed since, and
/// the one sentence that must not be wrong is the one printed on the button that starts it.
///
/// ⚠ A translation that is merely SELECTED does not count. Selecting is an intent the next install
/// carries out; pressing Play now runs what is on disk now. Promising "Play translated" over a
/// file that has not been downloaded yet would be found out within seconds, by the person who
/// trusted it.
/// </summary>
public static class PlayPromises
{
    public static PlayPromise For(GameReport report, GameConfigSnapshot config)
    {
        // No plugin, nothing to promise. The loader alone changes nothing anybody can see.
        if (report.InstalledPluginVersion is null) return PlayPromise.Plain;

        // Both halves, and neither is enough on its own: a backend that is configured but switched
        // off produces nothing, and the switch on its own has nothing to run.
        if (config.AutoTranslate == true && BackendIsUsable(config.Values))
            return PlayPromise.Translating;

        // ⚠ Lines, not merely a file. A translations.json with no entry in it — what a game holds
        // a minute after being set up — displays exactly as much as no file at all.
        return (report.LocalTranslation?.EntryCount ?? 0) > 0
            ? PlayPromise.Translated
            : PlayPromise.Plain;
    }

    /// <summary>
    /// Whether the configured backend has what it needs to answer.
    ///
    /// ⚠ Per backend, because they do not need the same things: a local model needs an address and
    /// a name and no key at all, while the two online services are a key each. Treating "a backend
    /// is named" as "a backend works" is what would put "Play &amp; translate" on a game that has
    /// chosen DeepL and never been given a key.
    /// </summary>
    public static bool BackendIsUsable(GameModOverrides values) => values.TranslationBackend switch
    {
        "llm" => !string.IsNullOrWhiteSpace(values.AiUrl) && !string.IsNullOrWhiteSpace(values.AiModel),
        "google" => !string.IsNullOrWhiteSpace(values.GoogleApiKey),
        "deepl" => !string.IsNullOrWhiteSpace(values.DeeplApiKey),
        _ => false,
    };

    /// <summary>
    /// The words on the button.
    ///
    /// ⚠ Plain international English, like everything else this tool shows: whoever reads it may
    /// have no other language in common with us. "Play &amp; translate" says two things happen;
    /// "Play translated" says one already has.
    /// </summary>
    public static string Label(PlayPromise promise) => promise switch
    {
        PlayPromise.Translating => "Play & translate",
        PlayPromise.Translated => "Play translated",
        _ => "Play",
    };

    /// <summary>Why the button says that, for the tooltip. Never a second guess at the same fact.</summary>
    public static string Explain(PlayPromise promise) => promise switch
    {
        PlayPromise.Translating => "This game is set up to translate lines as they appear.",
        PlayPromise.Translated => "This game holds a translation and will show it.",
        _ => "This game is not set up to change any text.",
    };
}
