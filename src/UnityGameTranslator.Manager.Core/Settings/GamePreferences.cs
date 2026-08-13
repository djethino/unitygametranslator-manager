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
    /// Whether our settings are written into this game's config.json.
    ///
    /// Not nullable: it is a decision about this game and nothing else, and true is the answer
    /// that matches what the tool is for.
    /// </summary>
    [JsonPropertyName("apply_mod_defaults")] public bool ApplyModDefaults { get; set; } = true;

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
}
