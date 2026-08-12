using UnityGameTranslator.Manager.Core.Detection;
using UnityGameTranslator.Manager.Core.Model;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Manager.Core.Settings;

/// <summary>
/// Which language to translate a game INTO, and nothing else.
///
/// ⚠ **"auto" must never reach target_language.** It used to: a tool set to follow the system
/// wrote the literal string "auto", and the mod resolves that at launch from the machine's own
/// locale. So the same install produced a different target on a different machine, on a Steam Deck
/// set to another language, or after somebody changed their Windows display language — and a game
/// carrying a French translation would quietly start asking for English. The tool knows the answer
/// at the moment it writes; leaving the question open serves nobody.
///
/// ⚠ **A translation already in place decides.** Its target is not a preference, it is what the
/// file IS. Overwriting the target of a game that holds a French translation with somebody's
/// German default does not translate that game into German: it leaves the mod hunting for German
/// while a French file sits beside it, which reads as "the translation stopped working".
///
/// ⚠ **The SOURCE is never written, by anything here.** This tool cannot read what language a
/// game's own text is in, and the one thing that looks like an answer — the source declared on a
/// published translation — is a statement about the person who made it, not a measurement of the
/// game. Writing it is not labelling, it is instructing: the mod puts it in every prompt
/// ("Translating video game from English to French"), and with strict_source_language it tells the
/// model to answer with a skip marker for anything not in that language. A line skipped that way
/// is cached with the tag "S", which the merge treats as immutable — so one wrong guess about a
/// game's source language permanently retires the lines it hit, and counts them as settled in the
/// quality bar. Auto-detection is the mod's job, it works line by line, and it was never ours to
/// override.
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
    /// The language this game should translate INTO, in order of authority.
    ///
    /// 1. **A published translation this game holds.** Its target comes from what was published,
    ///    verbatim. It is fixed: the file was uploaded under it, the site lists it under it, and a
    ///    game aimed anywhere else cannot use it.
    /// 2. **A local translation nobody has published.** Whatever target the game already names is
    ///    kept — somebody is building that file in that language, and retargeting it mid-work
    ///    would orphan everything they have done.
    /// 3. **Nothing installed.** The person's own target, resolved. Nothing to preserve, and this
    ///    is the choice the whole tool is organised around.
    /// </summary>
    /// <param name="defaultTargetCode">The person's target, already resolved by <see cref="Resolve"/>.</param>
    /// <returns>A language NAME, as the mod stores it. Never null, never "auto".</returns>
    public static string TargetFor(GameReport report, LoaderDescriptor descriptor,
                                   string defaultTargetCode)
    {
        // The published entry of the very file installed here — matched on lineage, so it IS this
        // translation rather than another one for the same game.
        if (report.MatchingOnline is { TargetLanguage: { Length: > 0 } published })
            return published;

        if (report.LocalTranslation is not null)
        {
            // ReadLanguages already reports "auto" and blanks as null, so a target here is a real
            // one somebody settled on.
            var (_, target) = LocalTranslationProbe.ReadLanguages(report.Game.Path, descriptor);
            if (target is { Length: > 0 }) return target;
        }

        return Languages.NameOf(defaultTargetCode);
    }
}
