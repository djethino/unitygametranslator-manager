using UnityGameTranslator.Manager.Core.Detection;
using UnityGameTranslator.Manager.Core.Model;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Manager.Core.Settings;

/// <summary>
/// The two languages a game is set to, as NAMES — "English", "French" — because that is what the
/// mod stores and what the site publishes under.
/// </summary>
/// <param name="Source">
/// What the game is translated FROM, or null to leave the game's own answer alone.
///
/// ⚠ Null is the only legitimate "auto" left in this pair, and only in one situation: a
/// translation being built locally that has never been uploaded and whose source nobody has
/// stated. The mod detects it as it goes. The moment the file exists on the server, its source is
/// part of what was published and is no longer ours to leave open.
/// </param>
/// <param name="Target">What it is translated INTO. Never null, and never "auto".</param>
public sealed record LanguagePair(string? Source, string Target);

/// <summary>
/// Which languages to write into a game, and — more often — which ones NOT to touch.
///
/// ⚠ **"auto" must never reach target_language.** It used to: a tool set to follow the system
/// wrote the literal string "auto", and the mod resolves that at launch from the machine's own
/// locale. So the same install produced a different target on a different machine, on a Steam Deck
/// set to another language, or after somebody changed their Windows display language — and a game
/// carrying a French translation would quietly start asking for English. The tool knows the answer
/// at the moment it writes; leaving the question open serves nobody.
///
/// ⚠ **A translation already in place decides.** Its languages are not a preference, they are what
/// the file IS. Overwriting the target of a game that holds a French translation with somebody's
/// German default does not translate that game into German: it leaves the mod hunting for German
/// while a French file sits beside it, which reads as "the translation stopped working".
/// </summary>
public static class GameLanguages
{
    /// <summary>
    /// The language to reason with: the configured one, or the system's when set to "auto".
    ///
    /// Written once and used by both the settings store and the install path, because the two
    /// answering differently is the kind of divergence nobody notices until a game is aimed at a
    /// language nothing was published in.
    /// </summary>
    /// <returns>The canonical code, lowercase — two letters for most, more where the language
    /// needs it ("zh-tw").</returns>
    public static string Resolve(string? configured, string? systemLocale)
    {
        if (!string.IsNullOrWhiteSpace(configured)
            && !configured.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            // ⚠ Canonical, never truncated. Cutting to two letters turned "zh-tw" into "zh" —
            // Simplified Chinese — so someone who had explicitly PICKED Traditional had their own
            // choice overruled, silently, on every read.
            return Languages.Canonical(configured)!;
        }

        // Asked of the OS, not of CultureInfo: invariant globalization makes the latter answer
        // "iv", which showed up as "No iv translation yet" on every row.
        // ⚠ Resolved by the shared table, not cut to two letters: "zh-Hant-TW" is Traditional
        // Chinese, and truncating it answered Simplified.
        return Languages.FromLocale(systemLocale) ?? "en";
    }

    /// <summary>
    /// What this game should be set to, in order of authority.
    ///
    /// 1. **A published translation this game holds.** Both languages come from what was
    ///    published, verbatim. They are fixed: the file was uploaded under them, the site lists it
    ///    under them, and a game aimed anywhere else cannot use it.
    /// 2. **A local translation nobody has published.** Whatever target the game already names is
    ///    kept — somebody is building that file in that language, and retargeting it mid-work
    ///    would orphan everything they have done. The source stays open if it was open: that is
    ///    the one case where the mod detecting it is still the right answer.
    /// 3. **Nothing installed.** The person's own target, resolved. Nothing to preserve, and this
    ///    is the choice the whole tool is organised around.
    /// </summary>
    /// <param name="defaultTargetCode">The person's target, already resolved by <see cref="Resolve"/>.</param>
    public static LanguagePair Decide(GameReport report, LoaderDescriptor descriptor,
                                      string defaultTargetCode)
    {
        // The published entry of the very file installed here — matched on lineage, so it IS this
        // translation rather than another one for the same game.
        if (report.MatchingOnline is { TargetLanguage: { Length: > 0 } published } entry)
            return new LanguagePair(Blank(entry.SourceLanguage), published);

        if (report.LocalTranslation is not null)
        {
            var (source, target) = LocalTranslationProbe.ReadLanguages(report.Game.Path, descriptor);

            // ReadLanguages already reports "auto" and blanks as null, so a target here is a real
            // one somebody or something settled on.
            if (target is { Length: > 0 })
                return new LanguagePair(source, target);
        }

        return new LanguagePair(null, Languages.NameOf(defaultTargetCode));
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
