using System.Collections.Concurrent;
using System.Text.Json;
using UnityGameTranslator.Manager.Core.Catalog;
using UnityGameTranslator.Manager.Core.Detection;
using UnityGameTranslator.Manager.Core.Model;

namespace UnityGameTranslator.Manager.Core.Install;

public enum EngineModuleSourceKind { Editor, Game, UnityDownload }

/// <summary>
/// One place a game's missing engine modules could come from.
/// </summary>
/// <param name="Id">Stable across runs — what a person's choice is remembered by.</param>
/// <param name="Name">The game's name, for a game; null otherwise.</param>
/// <param name="SameRelease">The same Unity release as the game's; false for an older one of its branch.</param>
/// <param name="Managed">The folder holding the modules, for a copy on this computer.</param>
/// <param name="Player">The native player built with those modules, when there is one to read.</param>
public sealed record EngineModuleSource(EngineModuleSourceKind Kind, string Id, string? Name, UnityVersion Version,
                                        bool SameRelease, string? Managed, string? Player)
{
    /// <summary>
    /// Where they come from, named — what the card and the confirmation say.
    /// ⚠ ASCII only: it is also printed by `report`.
    /// </summary>
    public string Label => Kind switch
    {
        EngineModuleSourceKind.Editor => $"the Unity {Version} editor on this computer",
        EngineModuleSourceKind.Game => $"{Name} (Unity {Version}) on this computer",
        _ => $"Unity's download server ({RuntimeLibraryOrigins.UnityDownloadHost})",
    };
}

/// <summary>A source and what stands against it — nothing, when it can be used.</summary>
public sealed record EngineModuleCandidate(EngineModuleSource Source, IReadOnlyList<string> Problems)
{
    public bool Usable => Problems.Count == 0;
}

/// <summary>
/// Where a game's missing engine modules can come from, and whether a given copy may be used.
///
/// 🔴 **The order is the user's decision (2026-09-21)**: the same Unity release already on this
/// computer first — an editor, then another game; then Unity's own download; then, only when
/// neither exists, the closest OLDER release of the same branch, said as such. Never a newer one:
/// a set from a later 2021.3 than the game's crashed it at start, and <see cref="Verify"/> catches
/// that without launching anything.
///
/// 🔴 **And every copy is held to all of this before it is offered, not only before it is used:**
///   · it is the complete set the game ships, not some of it — a partial set broke a game's data;
///   · every file carries Unity's valid signature (<see cref="UnitySignature"/>) — a copy from
///     another game is only as trustworthy as that game, and the signature is what says Unity built
///     it. The one exception is Unity's own server, for the builds Unity never signed (Linux, and
///     every version before 2020): there the origin is what says it (user's decision, 2026-09-21),
///     and a signature that is present but fails is still refused;
///   · it is not stripped itself, read against its own player (<see cref="EngineModules.Stripped"/>);
///   · every native call it makes exists in THIS game's player (<see cref="EngineModules.MissingFromPlayer"/>);
///   · everything the game's own stripped copy kept, it has too (<see cref="EngineModules.Lacks"/>) —
///     which is how a callback the player makes by name is caught.
/// </summary>
public static class EngineModuleSources
{
    /// <summary>Every place the modules could come from, in the order they are preferred, each with its verdict.</summary>
    /// <param name="games">Other games on this computer; the game itself is left out.</param>
    /// <param name="online">Whether this computer can reach Unity's server now — Unity's download is offered only then.</param>
    public static IReadOnlyList<EngineModuleCandidate> Find(GameInstall game, EngineModuleNeed need, IEnumerable<GameInstall> games,
                                                            bool online)
    {
        if (UnityVersions.Parse(need.Build ?? game.UnityVersion) is not { } version || need.CannotSupply is not null
            || EngineModules.PlatformOf(game) is not { } platform)
            return Array.Empty<EngineModuleCandidate>();

        var local = new List<EngineModuleSource>();

        // ⚠ No copy on this computer at all where Unity signs nothing: none could ever be verified,
        // and listing them only to refuse each one would bury the one source that can serve.
        if (EngineModules.UnitySigns(platform, version))
        {
            foreach (var (editorVersion, managed, player) in Editors(game.Architecture))
            {
                if (Fits(editorVersion, version) is not { } same) continue;
                local.Add(new EngineModuleSource(EngineModuleSourceKind.Editor, $"editor:{managed}", null, editorVersion,
                                                 same, managed, player));
            }

            foreach (var other in games)
            {
                if (string.Equals(Path.GetFullPath(other.Path), Path.GetFullPath(game.Path), StringComparison.OrdinalIgnoreCase)) continue;
                if (other.Runtime != UnityRuntime.Mono || other.DataDirectory is null || EngineModules.PlatformOf(other) != platform) continue;
                if (UnityVersions.Parse(other.UnityVersion) is not { } otherVersion || Fits(otherVersion, version) is not { } same) continue;

                var managed = Path.Combine(other.DataDirectory, "Managed");
                if (!File.Exists(Path.Combine(managed, "UnityEngine.CoreModule.dll"))) continue;

                local.Add(new EngineModuleSource(EngineModuleSourceKind.Game, $"game:{Path.GetFullPath(other.Path)}", other.Name,
                                                 otherVersion, same, managed, EngineModules.PlayerBinary(other.Path, other.ExecutablePath)));
            }
        }

        var candidates = local.Where(s => s.SameRelease)
                              .OrderBy(s => s.Kind)
                              .Select(s => new EngineModuleCandidate(s, Verify(game, need, s)))
                              .ToList();

        // Checked when it is fetched: nothing can be said about files not yet downloaded, except
        // that they come from Unity itself — and the same checks then apply to them.
        if (online && need.Changeset is not null && need.Build is not null)
        {
            candidates.Add(new EngineModuleCandidate(new EngineModuleSource(
                EngineModuleSourceKind.UnityDownload, $"unity:{need.Build}", null, version, true, null, null),
                Array.Empty<string>()));
        }

        candidates.AddRange(local.Where(s => !s.SameRelease)
                                 .OrderByDescending(s => s.Version.Patch)
                                 .ThenBy(s => s.Kind)
                                 .Select(s => new EngineModuleCandidate(s, Verify(game, need, s))));

        return candidates;
    }

    /// <summary>The source to use: the one a person chose, while it is still usable; the first usable one otherwise.</summary>
    public static EngineModuleCandidate? Choose(IReadOnlyList<EngineModuleCandidate> candidates, string? chosenId) =>
        candidates.FirstOrDefault(c => c.Usable && c.Source.Id == chosenId) ?? candidates.FirstOrDefault(c => c.Usable);

    /// <summary>Same release (true), older in the branch (false), or neither (null).</summary>
    private static bool? Fits(UnityVersion candidate, UnityVersion game) =>
        UnityVersions.SameRelease(candidate, game) ? true
        : UnityVersions.OlderInBranch(candidate, game) ? false
        : null;

    // ── Holding a copy to the rules ───────────────────────────────────────────────────────────

    /// <summary>
    /// What stands against using these modules in this game — empty when nothing does. Remembered
    /// against the stamps of every file read, since a game list asks again at each redraw.
    /// </summary>
    public static IReadOnlyList<string> Verify(GameInstall game, EngineModuleNeed need, EngineModuleSource source)
    {
        if (source.Managed is null || game.DataDirectory is null) return Array.Empty<string>();

        var gameManaged = Path.Combine(game.DataDirectory, "Managed");
        var gamePlayer = EngineModules.PlayerBinary(game.Path, game.ExecutablePath);

        var stamp = string.Join("|", need.Set.SelectMany(n => new[]
                                {
                                    Stamp(Path.Combine(source.Managed, n + ".dll")),
                                    Stamp(Path.Combine(gameManaged, n + ".dll")),
                                })
                                .Append(Stamp(source.Player)).Append(Stamp(gamePlayer)));

        return VerifyMemo.GetOrAdd(stamp, _ => VerifyNow(need, source.Managed, source.Player, gameManaged, gamePlayer, source.Label,
                                                         fromUnityServer: false));
    }

    private static readonly ConcurrentDictionary<string, IReadOnlyList<string>> VerifyMemo = new();

    private static string Stamp(string? path)
    {
        if (path is null || !File.Exists(path)) return $"{path}:absent";
        var info = new FileInfo(path);
        return $"{info.FullName.ToLowerInvariant()}:{info.Length}:{info.LastWriteTimeUtc.Ticks}";
    }

    /// <summary>
    /// The checks themselves, on folders — also what an install runs on modules it has just
    /// downloaded, whose player is not at hand (<paramref name="donorPlayer"/> null).
    /// </summary>
    /// <param name="fromUnityServer">
    /// The files were just read from Unity's own server. 🔴 **Only then may a module carry no
    /// signature at all** (user's decision, 2026-09-21): Unity signs nothing in Linux builds nor
    /// before 2020, and its own server over HTTPS is the one origin that needs no signature to be
    /// Unity's. A signature that is THERE and fails is refused whatever the origin — that is a file
    /// altered after Unity built it.
    /// </param>
    public static IReadOnlyList<string> VerifyNow(EngineModuleNeed need, string donorManaged, string? donorPlayer,
                                                  string gameManaged, string? gamePlayer, string label, bool fromUnityServer)
    {
        var problems = new List<string>();

        var absent = need.Set.Where(n => !File.Exists(Path.Combine(donorManaged, n + ".dll"))).ToList();
        if (absent.Count > 0)
        {
            problems.Add($"{label} lacks {absent.Count} of the {need.Set.Count} modules this game ships ({absent[0]})");
            return problems;
        }

        foreach (var name in need.Set)
        {
            var result = UnitySignature.Check(Path.Combine(donorManaged, name + ".dll"));
            if (result.IsValid || (fromUnityServer && result.Verdict == UnitySignature.Verdict.NotSigned)) continue;

            problems.Add($"{name} in {label} is not verifiably Unity's ({Describe(result)})");
            return problems;
        }

        var donor = RuntimeLibraries.Folder(donorManaged);
        var game = RuntimeLibraries.Folder(gameManaged);

        if (donorPlayer is not null && File.Exists(donorPlayer)
            && EngineModules.Stripped(donor, need.Set, EngineModules.NativeNames(donorPlayer)) is { Count: > 0 } stripped)
        {
            problems.Add($"the copy in {label} was stripped by its own build ({string.Join(", ", stripped.Keys)})");
            return problems;
        }

        // ⚠ Not for Unity's own download: it is fetched for the game's exact build — its changeset —
        // so its native calls ARE the game's player's, by construction.
        if (!fromUnityServer && gamePlayer is not null && File.Exists(gamePlayer))
        {
            var shapes = need.Set.Select(donor).OfType<AssemblyShape>().ToList();
            var donorNames = donorPlayer is not null && File.Exists(donorPlayer) ? EngineModules.NativeNames(donorPlayer) : null;
            var calls = EngineModules.MissingFromPlayer(shapes, EngineModules.NativeNames(gamePlayer), donorNames);
            if (calls.Count > 0)
            {
                problems.Add($"{label} calls {calls.Count} engine functions this game's player does not have ({calls[0]})");
                return problems;
            }
        }

        foreach (var name in need.Set)
        {
            if (game(name) is not { } own || donor(name) is not { } theirs) continue;

            var lacking = EngineModules.Lacks(own, theirs);
            if (lacking.Count == 0) continue;

            problems.Add($"{name} in {label} lacks {lacking.Count} things this game uses ({lacking[0]})");
            return problems;
        }

        return problems;
    }

    private static string Describe(UnitySignature.Result result) => result.Verdict switch
    {
        UnitySignature.Verdict.NotSigned => "it carries no signature",
        UnitySignature.Verdict.Tampered => "it was changed after Unity signed it",
        UnitySignature.Verdict.OtherPublisher => $"it is signed by {result.Signer}",
        UnitySignature.Verdict.Untrusted => "its signature does not lead to a trusted authority",
        _ => "its signature could not be read",
    };

    // ── Unity editors on this computer ────────────────────────────────────────────────────────

    /// <summary>
    /// Every installed Unity editor carrying the Windows player's Mono modules, with its version,
    /// its modules folder and its player, for the game's architecture.
    /// </summary>
    public static IEnumerable<(UnityVersion Version, string Managed, string Player)> Editors(GameArchitecture architecture)
    {
        var variation = architecture == GameArchitecture.X86 ? "win32_player_nondevelopment_mono" : "win64_player_nondevelopment_mono";

        foreach (var (version, root) in UnityEditors.Installed())
        {
            if (UnityEditors.PlaybackEngine(root, "windowsstandalonesupport") is not { } support) continue;

            var folder = Path.Combine(support, "Variations", variation);
            var managed = Path.Combine(folder, "Data", "Managed");
            var player = Path.Combine(folder, "UnityPlayer.dll");

            if (File.Exists(Path.Combine(managed, "UnityEngine.CoreModule.dll")) && File.Exists(player))
                yield return (version, managed, player);
        }
    }
}
