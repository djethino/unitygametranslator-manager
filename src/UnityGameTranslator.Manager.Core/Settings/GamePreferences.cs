using System.Text.Json.Serialization;
using UnityGameTranslator.Manager.Core.Model;
using UnityGameTranslator.Manager.Core.Platform;

namespace UnityGameTranslator.Manager.Core.Settings;

/// <summary>
/// What the user decided for ONE game, as opposed to what they decided in general.
///
/// ⚠ Deliberately not stored in game-overrides.json, which means something else entirely: what
/// somebody told us because we could not read it ourselves — a runtime, an architecture, a
/// refusal overruled. Those are corrections to our own detection and they are reported back as
/// assumptions on every screen. These are preferences, and reporting a preference as "what you
/// told us, not what we read" would be nonsense. Two meanings, two files.
///
/// ⚠ Null is a real value here and is not the same as false: it means "nothing decided for this
/// game", so the defaults still apply and keep applying when they change. Writing the resolved
/// value at creation time would freeze today's default into every game, and changing it later
/// would then change nothing.
/// </summary>
public sealed class GamePreference
{
    /// <summary>
    /// Which generation of this record an entry was written by. Absent means the first, from before
    /// <see cref="ApplyModDefaults"/> could say "nobody has decided" — see
    /// <see cref="GamePreferences.AfterLoad"/>, which is the only thing that reads it.
    ///
    /// 🔴 **It defaults to 1, not to <see cref="Current"/>, and that is the whole mechanism.** An
    /// entry written before this field existed has no "schema" in the file, so deserialising it
    /// leaves whatever the initialiser put there — set it to Current and every old entry would
    /// announce itself as already migrated, which is exactly the silence the migration exists to
    /// break. Saving is what stamps the current number.
    /// </summary>
    [JsonPropertyName("schema")] public int Schema { get; set; } = 1;

    /// <summary>
    /// The generation this code writes. Bumped when a stored value changes MEANING rather than
    /// shape — a new field needs nothing, a field whose old default was a claim needs this.
    /// </summary>
    public const int Current = 2;

    /// <summary>
    /// Whether the DEFAULTS are what gets written into this game, or this game has answers of its
    /// own (<see cref="Mod"/>).
    ///
    /// ⚠ **Nullable, and it was a plain bool defaulting to true.** That default was a claim about
    /// every game nobody had decided about — including a game discovered with a configuration
    /// somebody had already set up inside it, which the very first one-click therefore offered to
    /// overwrite without anyone choosing that. Null means "nobody has decided", and the answer is
    /// then read from the game itself; see <see cref="UsesModDefaults"/>.
    ///
    /// ⚠ It cannot be inferred from "this game has no entry in game-preferences.json": an entry is
    /// created the moment anything else here is touched — a translation picked, a context written,
    /// the box beside the one-click. Undecided has to be storable, hence the null.
    /// </summary>
    [JsonPropertyName("apply_mod_defaults")] public bool? ApplyModDefaults { get; set; }

    /// <summary>
    /// What this game answers for itself, where it answers anything. Null until it answers
    /// something — see <see cref="GameModOverrides"/>, which never freezes a value merely shown.
    /// </summary>
    [JsonPropertyName("mod")] public GameModOverrides? Mod { get; set; }

    /// <summary>
    /// Whether the defaults are what this game gets, resolving "nobody decided" from the game.
    ///
    /// 🔴 The rule, in one line: **a game that is already configured keeps its own configuration.**
    /// Somebody who set a game up from inside the mod has answered; discovering that game here must
    /// not turn their answer into something the next click overwrites. A game that has never been
    /// configured has nothing to lose and everything to gain from the defaults, so it follows them.
    /// </summary>
    /// <param name="snapshot">
    /// What the game holds, or null when it could not be read. ⚠ Null resolves to "follow the
    /// defaults", not to "leave it alone": with nothing readable there is nothing to protect, and
    /// refusing to configure a game because we failed to read it would be a refusal nobody asked
    /// for and nothing on screen could explain.
    /// </param>
    public bool UsesModDefaults(GameConfigSnapshot? snapshot) =>
        ApplyModDefaults ?? !(snapshot?.IsConfigured ?? false);

    /// <summary>
    /// Whether the mod starts translating as soon as the game launches, or waits.
    ///
    /// Null follows the defaults — a backend chosen there means translating. Set, it wins, and
    /// that is the point: somebody may have a model to pull, a context to write or a glossary to
    /// prepare before a single line is spent, and that is true of one game rather than of them
    /// all. Written into the game as enable_ai, which since 2026-08-12 is the mod's real switch
    /// for every backend rather than a synonym for "the backend is an AI".
    /// </summary>
    [JsonPropertyName("start_translation")] public bool? StartTranslation { get; set; }

    /// <summary>
    /// What this game is about, in the words sent to the AI — the mod's game_context.
    ///
    /// Per game by nature, which is why it is here and not in the defaults: it is the one wizard
    /// question whose answer cannot be shared between two games.
    /// </summary>
    [JsonPropertyName("game_context")] public string? GameContext { get; set; }

    /// <summary>
    /// Whether the hotkey from the defaults replaces the one this game already carries.
    ///
    /// Not nullable, and no default to fall back on: unlike the settings above, this is not a
    /// preference the defaults can express. A hotkey is the one setting a game may legitimately
    /// know better than we do — inside it, the mod captured the key against the real keyboard,
    /// which is the only measurement that exists — so the question is never "do I replace hotkeys"
    /// but "do I replace THIS one", and false is the only safe answer to give on its behalf.
    ///
    /// ⚠ False does NOT mean the hotkey is never written: a game that has none yet gets ours
    /// regardless, because leaving it out would let first_run_completed claim the question was
    /// answered while the mod sat on its own default. See GameConfigWriter.Intended.
    /// </summary>
    [JsonPropertyName("replace_hotkey")] public bool ReplaceHotkey { get; set; }

    /// <summary>
    /// The community translation the user picked for this game, by site id.
    ///
    /// Remembered because it can be chosen before there is anywhere to put it: with no loader
    /// installed there is no folder to write into, so choosing and installing are two moments.
    /// ⚠ The id, never the file: what was published may have moved on by the time it is fetched,
    /// and fetching is what tells us.
    /// </summary>
    [JsonPropertyName("translation_id")] public int? TranslationId { get; set; }

    /// <summary>
    /// Whether the one-click also brings a translation down.
    ///
    /// True by default — it is what makes one click enough — and unchecking it is the explicit
    /// "I want a blank sheet": someone starting a translation for a game does not want somebody
    /// else's work landing in it first.
    /// </summary>
    [JsonPropertyName("install_translation")] public bool InstallTranslation { get; set; } = true;

    /// <summary>
    /// Whether UnityGameTranslator Manager may update a loader it did not install, in THIS game.
    ///
    /// 🔴 **False unless somebody says otherwise, and never remembered across games.** A loader
    /// somebody else put there very likely belongs to another mod that needs that exact version,
    /// so replacing it is not ours to propose — that refusal is the rule the whole loader card
    /// rests on, and it stays the default.
    ///
    /// What this adds is a way to say "this one is mine to manage now", per game, deliberately.
    /// ⚠ It changes who may act, never what is done: an update writes the loader's own files over
    /// themselves and touches no other mod, exactly as it does on a loader we installed.
    /// </summary>
    [JsonPropertyName("adopt_loader")] public bool AdoptLoader { get; set; }

    /// <summary>
    /// A detached copy, for asking a question about answers that are not decided yet.
    ///
    /// ⚠ Exists because <see cref="GamePreferences.Read"/> hands back the STORED object: a
    /// confirmation that has to name what is pending would otherwise write it onto the live
    /// preference, and somebody pressing Cancel would find it kept. The copy is shown, the original
    /// is only touched once the answer comes back.
    /// </summary>
    public GamePreference Copy() => new()
    {
        Schema = Schema,
        ApplyModDefaults = ApplyModDefaults,
        Mod = Mod?.Copy(),
        StartTranslation = StartTranslation,
        GameContext = GameContext,
        ReplaceHotkey = ReplaceHotkey,
        TranslationId = TranslationId,
        InstallTranslation = InstallTranslation,
        AdoptLoader = AdoptLoader,
    };
}

/// <summary>
/// The per-game preferences, remembered between runs.
/// </summary>
public sealed class GamePreferences : PerGameStore<GamePreference>
{
    public GamePreferences(IPlatform platform) : base(platform, "game-preferences.json") { }

    /// <summary>
    /// What is remembered for this game, or a fresh set of defaults. Never null, so callers read
    /// preferences the same way whether or not this game has ever been touched.
    ///
    /// ⚠ NOT a copy: for a game that has one, this is the stored object itself, so changing a
    /// field changes what the next reader sees — it simply is not on disk until
    /// <see cref="PerGameStore{T}.Set"/> is called. Every screen here changes one field and saves
    /// it in the same breath, which is what makes that safe. A screen that wanted to offer Cancel
    /// would have to copy first, and would be wrong to assume this does it for them.
    /// </summary>
    public GamePreference Read(string gamePath) => For(gamePath) ?? new GamePreference();

    /// <summary>
    /// Whether translation should run in this game: what was decided here, or the default when
    /// nothing was.
    /// </summary>
    public bool StartsTranslation(string gamePath, InstallerSettings defaults) =>
        Read(gamePath).StartTranslation ?? defaults.EnableAi;

    /// <summary>
    /// Encrypted on the way out, decrypted on the way in — the same two-property split, the same
    /// scheme and the same one-way-to-disk rule the defaults follow in SettingsStore.
    ///
    /// ⚠ Here rather than in <see cref="PerGameStore{T}"/>: that class is shared with a store that
    /// holds no secret at all, and giving it a notion of secrecy it does not need is how the rule
    /// stops being read.
    /// </summary>
    protected override void BeforeSave(GamePreference value)
    {
        value.Mod?.ProtectSecrets();

        // Whatever generation it was read as, it is written as this one — the migration below has
        // already run, so re-running it on the next load would reopen a question now answered.
        value.Schema = GamePreference.Current;
    }

    protected override void AfterLoad(GamePreference value)
    {
        value.Mod?.UnprotectSecrets();

        // 🔴 **`true` in an old entry was never a decision.** ApplyModDefaults used to be a plain
        // bool defaulting to true, and it was serialised on every write — so every game anybody had
        // ever touched, for any reason at all (picking a translation, writing a description), came
        // back claiming somebody had chosen to have the defaults written into it. Left alone, the
        // rule that a game discovered already configured keeps its own configuration would never
        // fire for a single existing game, which is the whole of what it is for.
        //
        // ⚠ Only `true` is undone. `false` was never a default: nobody gets it without unticking
        // the box, so it IS a decision and it survives untouched. Undoing both would throw away the
        // one answer in the file that was genuinely given.
        if (value.Schema < 2 && value.ApplyModDefaults == true) value.ApplyModDefaults = null;

        value.Schema = GamePreference.Current;
    }
}
