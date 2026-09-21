using System.Text.Json.Serialization;
using UnityGameTranslator.Manager.Core.Model;
using UnityGameTranslator.Manager.Core.Platform;
using UnityGameTranslator.Manager.Core.Settings;

namespace UnityGameTranslator.Manager.Core.Detection;

/// <summary>What the user told us about a game we could not read on our own.</summary>
public sealed class GameOverride
{
    [JsonPropertyName("runtime")] public UnityRuntime? Runtime { get; set; }
    [JsonPropertyName("architecture")] public GameArchitecture? Architecture { get; set; }

    /// <summary>
    /// Proceed despite a refusal. Never applies to an anti-cheat: everything else we refuse is
    /// recoverable — the install is recorded and can be removed — while a banned account is not.
    /// </summary>
    [JsonPropertyName("ignore_verdict")] public bool IgnoreVerdict { get; set; }
}

/// <summary>
/// Answers the user gave for games we could not identify, remembered between runs.
///
/// The tool refuses to guess a runtime or an architecture, because guessing wrong produces a
/// game that silently starts without the mod — the single most confusing outcome possible. But
/// refusing is not the end of the conversation: the player may simply know, and being told "no"
/// forever by a tool that admits it could not read the file is worse than letting them answer.
/// </summary>
public sealed class GameOverrides : PerGameStore<GameOverride>
{
    public GameOverrides(IPlatform platform) : base(platform, "game-overrides.json") { }

    /// <summary>
    /// Applies what the user said, then re-runs the verdict so the rest of the tool sees a game
    /// that is simply known rather than one carrying an exception.
    /// </summary>
    public void Apply(GameInstall game)
    {
        var value = For(game.Path);
        if (value is null) return;

        if (value.Runtime is { } runtime && runtime != UnityRuntime.Unknown)
        {
            game.Runtime = runtime;
            game.RuntimeIsAssumed = true;
        }

        if (value.Architecture is { } architecture && architecture != GameArchitecture.Unknown)
        {
            game.Architecture = architecture;
            game.ArchitectureIsAssumed = true;
        }

        ModdabilityProbe.Evaluate(game);

        // 🔴 **An overrule lasts for the session, and beyond only while what it let in is still
        // installed** (user's decision, 2026-09-21: « il devrait être sur la session, sauf si loader
        // et/ou mod pas désinstallé »). It was kept for ever, so a game tried once and cleaned up
        // stayed "ready to install" long after anybody remembered why. Read from the receipt at each
        // load — the state, never a flag remembering a transition.
        if (value.IgnoreVerdict
            && !GivenThisSession(game.Path)
            && !StillHoldsWhatItLetIn(game.Path, game.Verdict))
        {
            value.IgnoreVerdict = false;
            if (value.Runtime is null && value.Architecture is null) Clear(game.Path);
            else Set(game.Path, value);
        }

        // The verdict override comes last, and only for refusals that cost nothing but time.
        if (value.IgnoreVerdict && ModdabilityProbe.CanBeOverridden(game.Verdict))
        {
            game.VerdictOverridden = true;
            game.OverriddenVerdict = game.Verdict;
            game.Verdict = ModdabilityVerdict.Ok;
        }
    }

    /// <summary>
    /// Overrules given by this process. Static because the window rebuilds its inventory — and
    /// with it this store — whenever the settings change, and a session outlives that.
    /// </summary>
    private static readonly HashSet<string> ThisSession = new(StringComparer.OrdinalIgnoreCase);

    protected override void Written(string gamePath, GameOverride value)
    {
        lock (ThisSession)
        {
            if (value.IgnoreVerdict) ThisSession.Add(Canonical(gamePath));
            else ThisSession.Remove(Canonical(gamePath));
        }
    }

    /// <summary>
    /// Whether the part the refusal was about is still in the game — the loader when no loader can
    /// start on it, the mod when the mod cannot load, either for the rest (a runtime or an
    /// architecture we could not read, a locked folder).
    /// </summary>
    private static bool StillHoldsWhatItLetIn(string gamePath, ModdabilityVerdict refused)
    {
        var receipt = Install.ReceiptStore.Read(gamePath);
        if (receipt is null) return false;

        return refused switch
        {
            ModdabilityVerdict.StrippedRuntime => receipt.Loader is not null,
            ModdabilityVerdict.MissingRuntimeLibraries => receipt.Plugin is not null,
            _ => receipt.Loader is not null || receipt.Plugin is not null,
        };
    }

    private static bool GivenThisSession(string gamePath)
    {
        lock (ThisSession) return ThisSession.Contains(Canonical(gamePath));
    }

    private static string Canonical(string gamePath) => Path.GetFullPath(gamePath);
}
