using UnityGameTranslator.Common;

namespace UnityGameTranslator.Manager.Core.Ai;

/// <summary>
/// How a game's own mod asks a backend for a line — read from that game's config.json, so the
/// Manager can ask the same question while the game is closed.
///
/// ⚠ **The game's settings, never Mod defaults.** A retranslation answered here must be the one the
/// game would have given: same backend, same model, same warmth, same number of attempts. Mod
/// defaults are what the Manager WRITES into a game; they say nothing about what a game holds today.
///
/// ⚠ An absent key reads as the mod's own default (<c>common/spec/config/schema.json</c>), and the
/// numbers are bounded exactly as the mod bounds them (<see cref="LineTranslation.ClampAttempts"/>,
/// <see cref="LineTranslation.ClampTemperature"/>). Both are held by the config contract's cases,
/// which the mod and this tool read alike.
///
/// ⚠ The keys are in CLEAR here, decrypted with the machine's own scheme (<see cref="Secrets"/>) —
/// they go to the provider the game already sends them to, and nowhere else. The site is told the
/// backend's NAME (<see cref="Label"/>), never an address and never a key.
/// </summary>
public sealed record GameAiSettings(
    bool EnableAi,
    string Backend,
    string AiUrl,
    string AiModel,
    string? AiApiKey,
    string? GoogleApiKey,
    string? DeeplApiKey,
    bool DeeplUseFree,
    int AttemptsAllowed,
    double TemperatureNormal,
    double TemperatureRepair,
    double TemperatureRetranslate,
    int? SeedRetranslate,
    string? GameContext,
    bool StrictSourceLanguage)
{
    /// <summary>Translation runs in this game: switched on AND a backend chosen.</summary>
    public bool IsTranslationEnabled => LineTranslation.IsEnabled(EnableAi, Backend);

    /// <summary>What the browser editor's Retranslate tooltip names, or null when nothing will answer.</summary>
    public string? Label => IsTranslationEnabled ? LineTranslation.BackendLabel(EnableAi, Backend, AiModel) : null;

    /// <summary>
    /// How long one request may take before no answer is expected any more — the mod's default
    /// <c>timeout_ms</c>. A deadlock escape, not a patience limit: a slow local model answers in
    /// minutes and its answer is wanted. A game's own shorter value is not read, on purpose — the
    /// mod applies it through a floor and a migration of its own, and waiting longer here only means
    /// waiting for an answer somebody asked for.
    /// </summary>
    public static readonly TimeSpan RequestCeiling = TimeSpan.FromMinutes(5);

    /// <summary>A game whose config could not be read: translation off, nothing to name.</summary>
    public static readonly GameAiSettings Unknown = new(
        false, "none", Endpoints.OllamaDefault, "", null, null, null, true,
        Placeholders.MaxAttempts, 0.0, 0.3, 0.8, null, null, false);
}
