using System.Text.Json.Serialization;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Manager.Core.Model;

/// <summary>A mod loader found already installed in a game folder.</summary>
public sealed class DetectedLoader
{
    public required string Id { get; init; }
    public required string Display { get; init; }

    /// <summary>Version read from the loader's own files, or null when unreadable.</summary>
    public string? Version { get; init; }

    /// <summary>Where our plugin goes for this loader, relative to the game root.</summary>
    public required string PluginDir { get; init; }

    /// <summary>
    /// True when our receipt says we installed it. Anything else is the user's, and we leave
    /// it strictly alone.
    /// </summary>
    public bool InstalledByUs { get; set; }

    /// <summary>Other plugins/mods sitting next to ours. Blocks removing the loader.</summary>
    /// <summary>
    /// Other mods sharing this loader, named — see LoaderProbe.FindForeignMods.
    ///
    /// ⚠ Named rather than counted: any of them blocks removing the loader, and a refusal the
    /// reader cannot check reads as the tool being awkward. The dialogue lists them.
    /// </summary>
    public IReadOnlyList<string> ForeignMods { get; set; } = Array.Empty<string>();

    public int ForeignPluginCount => ForeignMods.Count;
}

/// <summary>The translation file already present in the game, if any.</summary>
public sealed class LocalTranslation
{
    public required string Path { get; init; }

    /// <summary>Lineage identifier shared by every fork of this translation.</summary>
    public string? Uuid { get; init; }

    public string? GameName { get; init; }
    public string? SteamId { get; init; }

    /// <summary>Number of real translation entries, metadata keys excluded.</summary>
    public int EntryCount { get; init; }

    /// <summary>
    /// What the file is made of, counted here the way the website counts it — same five buckets,
    /// same rules, so the bar on this card and the bar on the site's page cannot disagree about
    /// one file. Null on a file we could not read.
    ///
    /// Counted locally rather than taken from the server on purpose: the moment somebody plays,
    /// their file stops being the published one, and the published figures would then describe
    /// somebody else's copy while sitting under the words "on this machine".
    /// </summary>
    public TagCounts? Counts { get; init; }

    /// <summary>Entries changed since the last sync, as recorded by the mod.</summary>
    public int LocalChanges { get; init; }

    /// <summary>
    /// Entries that differ from the snapshot taken at the last sync — MEASURED here, against the
    /// ancestor file sitting beside the translation.
    ///
    /// ⚠ Null means there is no ancestor to measure against, and that is not zero. Zero is a claim
    /// that nothing was changed; null is "nobody can say", and the difference decides whether it is
    /// safe to offer replacing this file with the published one.
    ///
    /// ⚠ Preferred over <see cref="LocalChanges"/> wherever both exist. That one is a counter the
    /// mod keeps as it writes, so it describes what the MOD did — a file edited by any other means
    /// carries a number that no longer describes it.
    /// </summary>
    public int? ChangedSinceAncestor { get; init; }

    /// <summary>
    /// Whether anything here has been changed since the last sync, and whether that can be known
    /// at all. Null when there is neither an ancestor to measure nor a counter to trust.
    /// </summary>
    public bool? HasLocalWork =>
        ChangedSinceAncestor is { } measured ? measured > 0
        : SourceHash is null ? null
        : LocalChanges > 0;

    /// <summary>
    /// The server's hash at the last sync, from _source.hash. Null on a file that has never been
    /// synced, or that predates the field.
    ///
    /// It is what separates "the server moved on" from "I edited this", and without it the only
    /// honest answer about a local file is that we do not know.
    /// </summary>
    public string? SourceHash { get; init; }

    public DateTimeOffset? LastWrite { get; init; }
}

/// <summary>A translation offered by the community site for this game.</summary>
public sealed class OnlineTranslation
{
    [JsonPropertyName("id")] public int Id { get; set; }

    /// <summary>Lineage id. The API calls it file_uuid; it matches the mod's local _uuid.</summary>
    [JsonPropertyName("file_uuid")] public string? Uuid { get; set; }

    [JsonPropertyName("source_language")] public string? SourceLanguage { get; set; }
    [JsonPropertyName("target_language")] public string? TargetLanguage { get; set; }

    /// <summary>Who published it. The API field is "uploader".</summary>
    [JsonPropertyName("uploader")] public string? Author { get; set; }

    [JsonPropertyName("line_count")] public int LineCount { get; set; }

    /// <summary>
    /// Whether this lineage takes contributions — its Main's own decision.
    ///
    /// Null on a server that predates the field, and null is not "no": showing "Solo work"
    /// because a server said nothing would turn a missing value into somebody's decision.
    /// </summary>
    [JsonPropertyName("accepts_branches")] public bool? AcceptsBranches { get; set; }
    [JsonPropertyName("download_count")] public int DownloadCount { get; set; }
    [JsonPropertyName("vote_count")] public int VoteCount { get; set; }

    /// <summary>
    /// This account's own vote: 1, -1, or null for none.
    ///
    /// ⚠ Only present when the request carried a token. Null therefore means "not voted OR not
    /// asked as anybody" — the caller knows which, this field does not.
    /// </summary>
    [JsonPropertyName("user_vote")] public int? UserVote { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("notes")] public string? Notes { get; set; }

    /// <summary>How the translation was produced, e.g. "ai_corrected". Helps the user choose.</summary>
    [JsonPropertyName("type")] public string? Type { get; set; }

    /// <summary>Superseded by the three measures below, kept because older answers carry it.</summary>
    [JsonPropertyName("quality_score")] public double? QualityScore { get; set; }

    /// <summary>
    /// What the file is made of. Same five counts, same order and same meaning as the mod's
    /// quality bar and the website's — one denominator across the three, or the same file would
    /// look different depending on where it was read.
    /// </summary>
    [JsonPropertyName("human_count")] public int HumanCount { get; set; }
    [JsonPropertyName("validated_count")] public int ValidatedCount { get; set; }
    [JsonPropertyName("ai_count")] public int AiCount { get; set; }
    [JsonPropertyName("capture_count")] public int CaptureCount { get; set; }

    /// <summary>Neither translated nor missing: shown on its own, never inside the bar.</summary>
    [JsonPropertyName("skipped_count")] public int SkippedCount { get; set; }

    /// <summary>Share of what the file met in game that is actually translated.</summary>
    [JsonPropertyName("completeness")] public double? Completeness { get; set; }

    /// <summary>Share of the file a human settled.</summary>
    [JsonPropertyName("review_coverage")] public double? ReviewCoverage { get; set; }

    /// <summary>
    /// Share of the GAME this file reaches, relative to the other translations of that game.
    /// Cannot be worked out client-side — it needs every other translation — so it is taken as
    /// the server sends it.
    /// </summary>
    [JsonPropertyName("game_coverage")] public double? GameCoverage { get; set; }

    /// <summary>
    /// The server's hash of this file. Written into the game as _source.hash on download, which
    /// is how the mod later tells "the server moved" from "I edited this".
    /// </summary>
    [JsonPropertyName("file_hash")] public string? FileHash { get; set; }

    /// <summary>When it was published. The mod flags a newcomer from this.</summary>
    [JsonPropertyName("created_at")] public DateTimeOffset? CreatedAt { get; set; }

    /// <summary>
    /// Date the *content* changed. Deliberately not updated_at: a vote or a download bumps
    /// updated_at, so it would show a translation as fresher than it is.
    /// </summary>
    [JsonPropertyName("content_updated_at")] public DateTimeOffset? ContentUpdatedAt { get; set; }

    [JsonPropertyName("resources_url")] public string? ResourcesUrl { get; set; }

    /// <summary>
    /// Which translation this one was forked from, when it was forked from one.
    ///
    /// ⚠ Absent on anything nobody forked AND on a server that predates the field — both read as
    /// "started from nothing", which is what silence says. A present block whose author is null is
    /// a different thing: a fork whose source account is gone, and the credit still stands.
    ///
    /// ⚠ Deliberately not derivable from anything else here. A fork leads its own lineage and is a
    /// Main in every other respect, which is what makes this the only trace of where it came from.
    /// </summary>
    [JsonPropertyName("origin")] public OnlineOrigin? Origin { get; set; }

    public override string ToString()
    {
        var langs = $"{SourceLanguage ?? "?"} -> {TargetLanguage ?? "?"}";
        var details = new List<string> { $"{LineCount} lines" };
        if (Status is not null) details.Add(Status);
        if (DownloadCount > 0) details.Add($"{DownloadCount} downloads");

        // The date shown is content_updated_at, never updated_at: a vote or a download bumps
        // updated_at, so it would make an abandoned translation look freshly maintained.
        if (ContentUpdatedAt is { } date) details.Add(date.ToString("yyyy-MM-dd"));

        return $"{langs} by {Author ?? "unknown"} ({string.Join(", ", details)})";
    }
}

/// <summary>
/// Where a fork came from, as the site records it.
///
/// ⚠ <see cref="Lines"/> is the snapshot taken at the instant of the fork and never recomputed —
/// the original keeps growing, so asking again would answer a different question.
/// </summary>
public sealed class OnlineOrigin
{
    /// <summary>The account it was taken from. Null once that account is gone.</summary>
    [JsonPropertyName("author")] public string? Author { get; set; }

    /// <summary>How many lines were received. Null on a row written before the column existed.</summary>
    [JsonPropertyName("lines")] public int? Lines { get; set; }
}

/// <summary>
/// The signed-in account's own place in one translation lineage.
///
/// Two positions exist and no more: whoever publishes leads — a Main — and whoever contributes
/// privately writes a branch, reviewed by that Main. A fork IS a main: it is published, it has its
/// own contributors, and the only thing it is not is the first file of the lineage.
///
/// Absent entirely for a translation somebody else wrote and this player merely downloaded, which
/// is the ordinary case and deserves no badge at all.
/// </summary>
public sealed class LineagePosition
{
    public required string Uuid { get; init; }

    /// <summary>Published: this account leads the lineage.</summary>
    public required bool IsMain { get; init; }

    /// <summary>
    /// How many people contribute to this Main. Null on a branch — a branch has no branches to
    /// answer about, which is a different thing from having none.
    ///
    /// ⚠ **Not what a screen shows.** See <see cref="BranchesWithWork"/>: this counts everybody,
    /// including a contributor who took the file months ago and never came back.
    /// </summary>
    public int? BranchesCount { get; init; }

    /// <summary>
    /// What is actually waiting: contributions their Main has not been through in their current
    /// state, AND that are holding something a merge would offer.
    ///
    /// 🔴 This is the number a row or a badge shows. Either filter dropped produces noise — an
    /// unreviewed contribution offering nothing sends somebody to an empty screen, and work already
    /// arbitrated comes back for ever until the number means nothing at all.
    ///
    /// ⚠ Null on a server too old to say. Unknown is not zero, so a screen falls back to the raw
    /// count rather than announcing that nothing is waiting.
    /// </summary>
    public int? BranchesWithWork { get; init; }

    /// <summary>How many lines those contributions hold, counted once each. Null if unknown.</summary>
    public int? LinesAvailable { get; init; }

    /// <summary>
    /// How many rows need a DECISION — lines the Main does not hold, plus lines both sides hold
    /// differently, the ones it will keep its own on included.
    ///
    /// 🔴 **Not <see cref="LinesAvailable"/>, and neither follows from the other.** That one is
    /// what would be taken; this is what has to be looked at. On the lineage this was measured
    /// against, 56 and 38 — the 18 in between being two machine translations that differ, where the
    /// Main keeps its own. One answers "how long will this take", the other "is there anything here
    /// for me", and a card showing a single figure cannot answer both.
    ///
    /// ⚠ Null on a server too old to say. Unknown is not zero: the words come from
    /// <see cref="Common.Contributions.WhatKindOfWork"/>, which says nothing rather than a zero.
    /// </summary>
    public int? LinesToReview { get; init; }

    /// <summary>
    /// Of those rows, the ones the Main does not hold at all, by the contribution's tag.
    ///
    /// 🔴 **The tag is what says whether an evening is worth it.** 21 new lines all written by hand
    /// is not the proposition 21 machine lines are, and a count alone tells the two apart from
    /// neither.
    /// </summary>
    public TagTally LinesNew { get; init; }

    /// <summary>Of those rows, the ones both sides hold differently, by the contribution's tag.</summary>
    public TagTally LinesDiffering { get; init; }

    /// <summary>
    /// A branch whose Main is gone. Null means unknown, never "the Main is fine": an older site
    /// does not send the field, and silence is not reassurance.
    /// </summary>
    public bool? MainMissing { get; init; }

    /// <summary>
    /// Whether this lineage takes contributions: this account's own answer on a Main, its Main's
    /// answer on a branch. Null on a site that predates the question — and null is not "no".
    /// </summary>
    public bool? AcceptsBranches { get; init; }

    /// <summary>
    /// A branch whose Main has closed to contributions since. Nothing on this side changes when it
    /// happens — the file still opens and still saves — so it has to be said, or it is found out
    /// at the moment of publishing, after the work.
    /// </summary>
    public bool? BranchFrozen { get; init; }

    public int SiteId { get; init; }
    public string? SourceLanguage { get; init; }
    public string? TargetLanguage { get; init; }
    public string? GameName { get; init; }

    /// <summary>
    /// The one sentence that says where this account stands, written once for every screen that
    /// shows it — the game card and the translation chooser both do, and two copies of a sentence
    /// are two chances to end up telling the same person two different things about one file.
    /// </summary>
    /// <param name="mainOwner">
    /// Who owns the Main, when the caller happens to know: the published entry of this lineage IS
    /// the Main, so a community search already carries the name. Left out rather than guessed.
    /// </param>
    /// <param name="withKinds">
    /// Whether to append what the contributions hold. False for a caller that draws those
    /// qualities as chips — see <see cref="Kinds"/>.
    /// </param>
    public string Describe(string? mainOwner = null, bool withKinds = true)
    {
        if (!IsMain)
        {
            // 🔴 Said first, because it changes what the rest of the sentence promises: "reviewed
            // by its owner" is no longer true once they have closed, and leaving it there would
            // keep somebody working towards a review that will never come.
            if (BranchFrozen == true)
            {
                return mainOwner is null
                    ? "This is your branch, and the Main no longer takes contributions: it can no "
                      + "longer be sent. Turn it into your own translation to carry on."
                    : $"This is your branch, and {mainOwner} no longer takes contributions: it can "
                      + "no longer be sent. Turn it into your own translation to carry on.";
            }

            return mainOwner is null
                ? "This is your branch: your changes are reviewed by the Main's owner."
                : $"This is your branch: your changes are reviewed by {mainOwner}.";
        }

        // ⚠ What is WAITING, not how many people contribute. Falls back to the raw count only when
        // the site could not answer: unknown is not zero, and saying "you are its Main" full stop
        // would tell somebody their contributions are settled when nobody knows.
        var waiting = BranchesWithWork ?? BranchesCount ?? 0;

        if (waiting == 0) return "This translation is yours: you are its Main.";

        // The socle's words, so this sentence and the mod's card say one thing.
        var said = "This translation is yours, and there is work waiting: "
                 + Contributions.WhatIsWaiting(waiting, LinesAvailable);

        // ⚠ Appended rather than folded into the sentence above: WhatIsWaiting answers "how much is
        // there for me", and this answers "how much is there to look at, and what is it". Two
        // questions, and the second decides whether the review gets opened at all — so it must not
        // be buried inside a clause about the first.
        // ⚠ Left out when the caller is going to DRAW the qualities instead of printing them —
        // see Kinds below. Everything else about the sentence is unchanged, so a screen that can
        // only print (a log, a tooltip, the CLI) still gets the whole thing.
        if (!withKinds) return said;

        var kinds = Contributions.WhatKindOfWork(LinesToReview, LinesNew, LinesDiffering);

        return kinds.Length == 0 ? said : said + " — " + kinds + ".";
    }

    /// <summary>
    /// The head of the drawn form: "21 to review", or nothing.
    ///
    /// 🔴 **H V A S are coloured chips on the website, and were grey prose here.** They are what
    /// says whether an evening is worth it — nine lines written by hand is not the proposition
    /// nine machine lines are — so a screen that can draw takes <see cref="Kinds"/> and this head,
    /// while anything that can only print keeps the whole sentence. The words and the order stay
    /// in the socle either way.
    /// </summary>
    public string ToReview => Contributions.ToReview(LinesToReview);

    /// <summary>What the contributions hold, in pieces, for a screen that draws them.</summary>
    public WorkKind[] Kinds => Contributions.KindsOfWork(LinesNew, LinesDiffering);

    /// <summary>
    /// Said only when <see cref="MainMissing"/> is true. Null is "we do not know" — an older site
    /// does not send the field — and reading silence as reassurance would be the one mistake that
    /// matters here.
    /// </summary>
    public const string OrphanNote =
        "The Main it contributed to is gone. Your work is untouched, but nobody upstream is "
        + "reviewing it any more — publishing it would give this translation a head again.";
}

/// <summary>Everything we know about a game, gathered in one place for display and decisions.</summary>
public sealed class GameReport
{
    public required GameInstall Game { get; init; }
    public DetectedLoader? InstalledLoader { get; set; }
    public LocalTranslation? LocalTranslation { get; set; }
    public IReadOnlyList<OnlineTranslation> OnlineTranslations { get; set; } = Array.Empty<OnlineTranslation>();

    /// <summary>
    /// Set when the community search failed rather than came back empty. Without it, a blocked
    /// network and a game nobody has translated look exactly the same to the user.
    /// </summary>
    public string? OnlineSearchError { get; set; }

    /// <summary>
    /// Whether the community was actually asked. False when offline, when community features are
    /// switched off, or when there was nothing to search by.
    ///
    /// ⚠ Distinct from an empty result AND from a failure. "Nobody has published a translation of
    /// this game" is a claim about the world, and an empty list is only evidence for it when
    /// somebody went and looked — an offline session would otherwise tell a player their game is
    /// untranslated, which is the one sentence that sends them off to redo existing work.
    /// </summary>
    public bool OnlineChecked { get; set; }

    /// <summary>Loader we would install if the user accepts the default. Null when none fits.</summary>
    public LoaderDescriptor? RecommendedLoader { get; set; }

    /// <summary>
    /// Every loader that could host this game on this system, best first.
    ///
    /// The recommendation is a default, never a decision taken for the user: some games work
    /// with one loader and not another for reasons no probe can see, so the alternatives have
    /// to stay reachable.
    /// </summary>
    public IReadOnlyList<LoaderDescriptor> EligibleLoaders { get; set; } = Array.Empty<LoaderDescriptor>();

    /// <summary>Why that recommendation — always shown, never a silent choice.</summary>
    public string? RecommendationReason { get; set; }

    /// <summary>Our plugin build id matching the loader in use, e.g. "bepinex6-il2cpp".</summary>
    public string? PluginBuildId { get; set; }

    /// <summary>Installed plugin version read from the deployed assembly, or null.</summary>
    public string? InstalledPluginVersion { get; set; }

    /// <summary>Where this loader expects our plugin, relative to the game. Null with no loader.</summary>
    public string? PluginDirectory { get; set; }

    /// <summary>
    /// Copies of our assembly outside that folder, relative to the game.
    ///
    /// ⚠ Carried by the report rather than recomputed per screen, so the window and the CLI cannot
    /// disagree — `report` is what somebody pastes into an issue, and it said "up to date" about a
    /// plugin sitting where the loader may never read it.
    /// </summary>
    public IReadOnlyList<string> StrayPluginDirectories { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Whether <see cref="PluginDirectory"/> holds the assembly.
    ///
    /// ⚠ With a stray present this is what separates the two faults: true means a DUPLICATE (one
    /// of the two is outside what we update — remove the stray), false means a MISPLACED install
    /// (nothing to remove — it must be put back, taking whatever files it wrote with it).
    /// </summary>
    public bool PluginInPlace { get; set; }

    /// <summary>
    /// The plugin here against what is published on the chosen channel.
    ///
    /// Null when there is no loader to host it, so nothing can be said. Set even when nothing is
    /// installed — "not installed, 0.12.0 available" is exactly what the card needs to offer the
    /// install in the first place.
    /// </summary>
    public VersionStanding? PluginStanding { get; set; }

    /// <summary>
    /// The mod loader here against what the catalog carries.
    ///
    /// Deliberately a separate answer from the plugin's, because they are separate things with
    /// separate release cycles: one button that reinstalled both hid the case this exposes — a
    /// loader two versions behind under a plugin that is perfectly current, or the reverse.
    ///
    /// Works offline: the catalog has an embedded copy, so this never depends on the network.
    /// </summary>
    public VersionStanding? LoaderStanding { get; set; }

    /// <summary>
    /// A newer loader exists AND replacing it is ours to offer.
    ///
    /// ⚠ The distinction the whole card rests on. <see cref="LoaderStanding"/> reports what the
    /// catalog knows about the loader in this game, whoever put it there — that is information,
    /// and withholding it left people looking at a version months behind with nothing said. This
    /// is the other question: a loader installed by somebody else belongs to them, very likely to
    /// another mod that needs that exact version, and touching it is not ours to propose.
    ///
    /// Every path that ACTS or offers to act reads this one. Every path that merely informs reads
    /// LoaderStanding.
    /// </summary>
    /// <remarks>
    /// ⚠ <see cref="LoaderAdopted"/> is the one way a loader we did not install becomes ours to
    /// offer: an explicit per-game answer, never a default and never inferred.
    /// </remarks>
    public bool LoaderUpdateOffered =>
        (InstalledLoader is { InstalledByUs: true } || LoaderAdopted)
        && LoaderStanding is { UpdateAvailable: true };

    /// <summary>
    /// Set from GamePreference.AdoptLoader: this game's loader is ours to manage although we did
    /// not put it there. Read by everything that offers to act, so the answer lives in one place.
    /// </summary>
    public bool LoaderAdopted { get; set; }

    /// <summary>
    /// The community entry that is the same lineage as the local file, matched on uuid.
    ///
    /// This is the difference between "3 translations available" and "you already have this
    /// one, and it changed online since". Without it we would happily offer the user the file
    /// they are already using.
    /// </summary>
    public OnlineTranslation? MatchingOnline { get; set; }

    /// <summary>
    /// Where the signed-in account stands in the lineage of the LOCAL file, when it stands
    /// anywhere at all.
    ///
    /// Null covers three different situations on purpose — signed out, not read yet, and a
    /// translation this account has no part in — because the screen says nothing in all three.
    /// Telling them apart would only let the interface make a claim it cannot support.
    /// </summary>
    public LineagePosition? MyPosition { get; set; }

    /// <summary>Community entries that are NOT what the user already has locally.</summary>
    public IEnumerable<OnlineTranslation> AlternativeOnline =>
        MatchingOnline is null
            ? OnlineTranslations
            : OnlineTranslations.Where(t => !ReferenceEquals(t, MatchingOnline));

    /// <summary>
    /// The site account this GAME is signed in with, and the server that issued it — read from the
    /// mod's own config, never from ours.
    ///
    /// Worth surfacing because it is per game and easy to lose track of: publishing, contributing
    /// a branch and being credited all depend on the game being linked, and somebody who set one
    /// up months ago has no way of knowing which of their twenty are. ⚠ The token itself is never
    /// read — see LocalTranslationProbe.ReadSiteAccount.
    /// </summary>
    public (string? User, string? Server) SiteAccount { get; set; }

    /// <summary>
    /// Where the translation here stands against the published one — the same verdict the mod
    /// reaches, from the same shared rule, without the game ever being opened.
    ///
    /// ⚠ Null means the question does not arise, never that everything is fine: no translation
    /// here, none published for it, or a file we could not read. Anything that shows this must
    /// keep those apart from <see cref="SyncDirection.InSync"/>.
    /// </summary>
    public SyncDirection? Sync { get; set; }

    /// <summary>Blocking prerequisites, e.g. a missing .NET Desktop Runtime.</summary>
    public List<string> Blockers { get; } = new();

    /// <summary>Non-blocking things the user must know before installing.</summary>
    public List<string> Warnings { get; } = new();
}
