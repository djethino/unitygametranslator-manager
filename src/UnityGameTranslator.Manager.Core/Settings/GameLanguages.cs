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
/// ⚠ **The SOURCE is never written from anything HERE, and the distinction is the whole rule**
/// (revised 2026-09-05). Nothing in this class can read what language a game's own text is in, so
/// a source composed from settings or preferences is a guess — and this key is not a label but an
/// instruction: the mod puts it in every prompt ("Translating video game from English to French"),
/// and with strict_source_language it tells the model to answer with a skip marker for anything
/// not in that language. A line skipped that way is cached with the tag "S", which the merge
/// treats as immutable, so one wrong guess retires the lines it hit for good.
///
/// What DOES write it is taking a published translation (MainWindow.AlignGameLanguage): there the
/// pair is stated by its author and kept by the server, which ignores the languages an update
/// sends. Adopting that file is adopting its pair — writing half of it left the mod prompting
/// without a source on every line it ever translates for that game.
///
/// ⚠ The second writer is the first publication FROM this tool (MainWindow.PublishTranslationAsync,
/// via Api.PublishLanguages): the person declares the source there, exactly as the mod asks it at
/// its own first upload, and the server keeps it from then on. Same authority, same key, and the
/// same helper writes it (MainWindow.WriteSourceLanguage).
/// </summary>
public static class GameLanguages
{
    /// <summary>
    /// What is written where a source has not been declared. The mod detects it line by line, so
    /// this is a real answer rather than a blank — and it is what explains why no source filter
    /// can be preselected from it.
    /// </summary>
    public const string SourceUnstated = "auto-detected";

    /// <summary>
    /// What is written where a target has not been settled. ⚠ Worded as the gap it is, never as
    /// "auto": the mod resolves it from the machine's locale at launch, so a game left that way
    /// means something different on every machine — and over a translation that exists, it means
    /// the mod may be working towards a language that file is not in.
    /// </summary>
    public const string TargetUnstated = "no target set";

    /// <summary>Both languages of one translation, either of them unstated.</summary>
    public readonly record struct LanguagePair(string? Source, string? Target)
    {
        /// <summary>Whether anything at all is known — a game the mod has never run in says no.</summary>
        public bool Known => Source is not null || Target is not null;

        /// <summary>The source as it is written on a screen, named when it is not declared.</summary>
        public string SourceLabel => Source ?? SourceUnstated;

        /// <summary>The target as it is written on a screen, named when it is not settled.</summary>
        public string TargetLabel => Target ?? TargetUnstated;
    }

    /// <summary>
    /// The pair the translation a game holds IS — the one fact every screen about that file has to
    /// carry, whether or not anybody has published it.
    ///
    /// 🔴 **Written because the pair was only ever shown for a PUBLISHED translation.** It came out
    /// of the published entry, inside the line naming its author, so a game running a file nobody
    /// has put on the site said what it was made of, whose it was and where it stood — and never
    /// which languages it went between. That is the one property that decides whether the file is
    /// of any use at all, and it was the one hidden.
    ///
    /// The order of authority is <see cref="TargetFor"/>'s, for the same reason: what was published
    /// is what the file IS — the site lists it under that pair and it was uploaded under it — while
    /// the game's own configuration is what somebody is building towards when nothing is published.
    /// Field by field rather than wholesale: an old published entry missing its source must not
    /// erase the source this game names.
    ///
    /// ⚠ Pure on purpose — the caller reads the two files. Both answers are already at hand
    /// wherever this is asked, and a rule this small is only worth having if it can be checked.
    /// </summary>
    /// <param name="published">The published entry of THIS file's lineage, or null.</param>
    /// <param name="inGame">What the game's own config.json names, "auto" and blanks already null
    /// — see <see cref="Detection.LocalTranslationProbe.ReadLanguages"/>.</param>
    public static LanguagePair PairFor(OnlineTranslation? published,
                                       (string? Source, string? Target) inGame) =>
        new(Stated(published?.SourceLanguage) ?? Stated(inGame.Source),
            Stated(published?.TargetLanguage) ?? Stated(inGame.Target));

    /// <summary>A language somebody actually named, or null. Blanks are not answers.</summary>
    private static string? Stated(string? language) =>
        string.IsNullOrWhiteSpace(language) ? null : language;

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
