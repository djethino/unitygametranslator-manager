using UnityGameTranslator.Common;

namespace UnityGameTranslator.Manager.Core.Api;

/// <summary>
/// Which two languages a publication travels under, and which of them the person is asked.
///
/// 🔴 **Written because the publish path refused the very case it exists for.** It read both
/// languages from the game's config.json and refused when either said "auto" — and the source
/// says "auto" on almost every translation nobody has published yet, because the mod detects the
/// source line by line and only fixes it at the first publication, when it ASKS. So the tool sent
/// people back into the game for a question it should have put itself, and the mod's own rule was
/// the one to copy:
///
/// · the **target** is what the file IS. It settles with the first translated line
///   (TranslatorCore.SettleTargetLanguageOnFirstLine) and is never a question again;
/// · the **source** is declared once, at the first publication. Before that "auto" is a working
///   mode — detect — and not an answer, so it is asked here, prefilled when the game already names
///   one (somebody who uses strict source language set it beforehand);
/// · on an **update or a contribution** neither is asked: the server keeps the pair the lineage
///   was published with and ignores what an upload sends (TranslationService::resolveLanguages).
///
/// ⚠ A published translation without both is unusable by whoever just wants to play: a game in
/// French offered a translation "into Chinese" from nobody knows what is a file that cannot work.
/// The site refuses such an upload (`required` + the catalogue's names, "auto" excluded); this
/// rule is what lets the tool never present a door that ends there.
///
/// Pure on purpose — the caller reads the three files and the server's answer; this only decides.
/// </summary>
public static class PublishLanguages
{
    /// <summary>A pair of language names, either of them unknown.</summary>
    public readonly record struct Pair(string? Source, string? Target);

    /// <summary>
    /// What the publish window shows and asks about languages.
    /// </summary>
    /// <param name="Target">The language the publication goes INTO. Fixed, never asked. Null only with <see cref="Refusal"/>.</param>
    /// <param name="Source">
    /// The language it goes FROM when that is settled — an update, a contribution, or a first
    /// publication whose game already names one. Null when it has to be asked.
    /// </param>
    /// <param name="SourceIsAsked">
    /// True on a first publication: the person declares the source, and <see cref="Source"/>
    /// is then only what the picker opens on, or null when nothing suggests one.
    /// </param>
    /// <param name="Refusal">Non-null when nothing can be published, and why — said before any window opens.</param>
    public readonly record struct Ask(string? Target, string? Source, bool SourceIsAsked, string? Refusal)
    {
        /// <summary>Whether a window may open at all.</summary>
        public bool CanProceed => Refusal is null;
    }

    /// <summary>
    /// Said when a first publication has no target to publish into — a file the mod has never
    /// translated a line of, which the site would refuse anyway as empty.
    /// </summary>
    public const string NoTargetYet =
        "This translation has no target language yet. The mod sets it with the first line it "
        + "translates: play once with the mod, then publish.";

    /// <summary>
    /// Said on an update or a contribution when the site did not say which pair the lineage was
    /// published under, and nothing on this machine names it either. Only an older site answers
    /// that way; the pair is fixed there and cannot be invented here.
    /// </summary>
    public const string LineagePairUnknown =
        "The site did not say which languages this translation is published in, and this game "
        + "does not name them either. Open the game once, signed in: the mod takes them from the site.";

    /// <summary>
    /// The mod's own words on the same screen, kept to the letter — one fact, one sentence, in
    /// every product (UploadSetupPanel.UpdateValidation).
    /// </summary>
    public const string ChooseSource = "Please select a source language (original game language)";
    public const string SameLanguage = "Source and target must be different!";

    /// <summary>
    /// Decide, from what the server holds and what this machine says.
    /// </summary>
    /// <param name="outcome">What the upload would become, as the server answered.</param>
    /// <param name="lineage">The pair the lineage is published under, as the server answered — null fields when it did not say.</param>
    /// <param name="file">What the translation file states about itself (`_source_language`, `_target_language`).</param>
    /// <param name="config">What the game's config.json names, "auto" already null.</param>
    public static Ask Decide(PublishOutcome outcome, Pair lineage, Pair file, Pair config)
    {
        if (outcome != PublishOutcome.NewTranslation)
        {
            // 🔴 **A file that STATES another pair is not an update of this lineage.** A backup
            // restored from a time the game was played in another language, or a file edited by
            // hand, says so in its own `_target_language`; sending it would push content of one
            // language into a lineage declared as another. The mod refuses this (UploadPanel) and
            // the rule is the socle's — one wording, both products.
            // ⚠ Only two STATED languages can disagree: a file that says nothing, or "auto",
            // publishes fine, and that is the ordinary state of most files.
            var side = TranslationLanguages.PublicationConflict(file.Source, file.Target,
                                                                lineage.Source, lineage.Target);
            if (side != TranslationLanguages.Side.None)
            {
                return new Ask(null, null, false,
                    TranslationLanguages.ExplainConflict(side, file.Source, file.Target,
                                                         lineage.Source, lineage.Target));
            }

            // The server's answer first: it is the one that will be kept. The file and the config
            // follow the server on every launch, so they are the same answer one step older — and
            // still a real one on a site too old to have said.
            var target = Stated(lineage.Target) ?? Stated(file.Target) ?? Stated(config.Target);
            var source = Stated(lineage.Source) ?? Stated(file.Source) ?? Stated(config.Source);

            return target is null || source is null
                ? new Ask(null, null, false, LineagePairUnknown)
                : new Ask(target, source, false, null);
        }

        // ⚠ The FILE first, then the config, never the other way round: the mod keeps the config
        // following the file, so a config that disagrees is a config that has not been updated yet.
        var into = Stated(file.Target) ?? Stated(config.Target);
        if (into is null) return new Ask(null, null, false, NoTargetYet);

        // Asked, prefilled with whatever was already declared — in the file by an earlier upload
        // that did not go through, or in the config by somebody who set it for strict source.
        return new Ask(into, Stated(file.Source) ?? Stated(config.Source), true, null);
    }

    /// <summary>
    /// Why a chosen source cannot be sent with this target, or null when it can. Re-judged as the
    /// picker changes, so the refusal disappears the moment it stops being true.
    /// </summary>
    public static string? Complaint(string? source, string target)
    {
        if (Stated(source) is null) return ChooseSource;

        // Through the catalogue when it knows both, as text otherwise: the same language can be
        // named two ways, and a raw comparison would let "Chinese" be published into "Simplified
        // Chinese". A name the catalogue has never heard of can only be compared as written.
        var a = Languages.Canonical(Languages.CodeOf(source));
        var b = Languages.Canonical(Languages.CodeOf(target));

        var same = a is not null && b is not null
            ? string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
            : string.Equals(source!.Trim(), target.Trim(), StringComparison.OrdinalIgnoreCase);

        return same ? SameLanguage : null;
    }

    /// <summary>A language somebody actually named, or null. "auto" and blanks are not answers.</summary>
    private static string? Stated(string? language) =>
        Languages.IsSettled(language) ? language!.Trim() : null;
}
