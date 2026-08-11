using System.Text.Json;
using System.Text.Json.Serialization;
using UnityGameTranslator.Manager.Core.Model;
using UnityGameTranslator.Manager.Core.Platform;

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
public sealed class GameOverrides
{
    private const string FileName = "game-overrides.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private Dictionary<string, GameOverride> _entries;

    public GameOverrides(IPlatform platform)
    {
        _path = Path.Combine(platform.UserDataDirectory, FileName);
        _entries = Load();
    }

    public GameOverride? For(string gamePath) =>
        _entries.TryGetValue(Key(gamePath), out var value) ? value : null;

    public void Set(string gamePath, GameOverride value)
    {
        _entries[Key(gamePath)] = value;
        Save();
    }

    public void Clear(string gamePath)
    {
        if (_entries.Remove(Key(gamePath))) Save();
    }

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

        // The verdict override comes last, and only for refusals that cost nothing but time.
        if (value.IgnoreVerdict && ModdabilityProbe.CanBeOverridden(game.Verdict))
        {
            game.VerdictOverridden = true;
            game.OverriddenVerdict = game.Verdict;
            game.Verdict = ModdabilityVerdict.Ok;
        }
    }

    private static string Key(string gamePath) => Path.GetFullPath(gamePath).ToLowerInvariant();

    private Dictionary<string, GameOverride> Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, GameOverride>>(
                    File.ReadAllText(_path), JsonOptions);
                if (loaded is not null) return loaded;
            }
        }
        catch
        {
            // Losing these means asking again, which is recoverable; refusing to start is not.
        }
        return new Dictionary<string, GameOverride>();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_entries, JsonOptions));
            File.Move(temp, _path, overwrite: true);
        }
        catch
        {
            // Not persisting only costs the answer at the next launch.
        }
    }
}
