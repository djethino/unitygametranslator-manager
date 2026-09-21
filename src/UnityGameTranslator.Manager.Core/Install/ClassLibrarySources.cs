using System.Collections.Concurrent;
using UnityGameTranslator.Manager.Core.Catalog;
using UnityGameTranslator.Manager.Core.Detection;
using UnityGameTranslator.Manager.Core.Model;

namespace UnityGameTranslator.Manager.Core.Install;

public enum ClassLibrarySourceKind { Editor, Game, UnityDownload }

/// <summary>One place a game's missing .NET libraries could come from.</summary>
/// <param name="Id">Stable across runs — what a person's choice is remembered by.</param>
/// <param name="Name">The game's name, for a game; null otherwise.</param>
/// <param name="SameRelease">The game's own Unity release; false for another release of its Mono generation.</param>
/// <param name="Folders">Where the copies are, for a copy on this computer — an editor's profile and its `Facades/`, or a game's Managed folder.</param>
/// <param name="Profile">The profile the libraries are taken from ("unityjit-win32").</param>
public sealed record ClassLibrarySource(ClassLibrarySourceKind Kind, string Id, string? Name, UnityVersion Version,
                                        bool SameRelease, IReadOnlyList<string>? Folders, string Profile)
{
    /// <summary>Where they come from, named. ⚠ ASCII only: it is also printed by `report`.</summary>
    public string Label => Kind switch
    {
        ClassLibrarySourceKind.Editor => $"the Unity {Version} editor on this computer",
        ClassLibrarySourceKind.Game => $"{Name} (Unity {Version}) on this computer",
        _ => $"Unity's {Version} editor package ({RuntimeLibraryOrigins.UnityDownloadHost})",
    };
}

/// <summary>A source and what stands against it — nothing, when it can be used.</summary>
public sealed record ClassLibraryCandidate(ClassLibrarySource Source, IReadOnlyList<string> Problems)
{
    public bool Usable => Problems.Count == 0;
}

/// <summary>
/// Where a game's missing .NET libraries can come from — in the order the user chose (2026-09-21):
///   1. a Unity editor installed on this computer — the game's release, then another of the same
///      Mono generation ("the most reliable source", and it works offline);
///   2. another game on this computer, from whose folder the libraries are copied — offered, named,
///      and said plainly to be unverifiable: these libraries carry no signature, and this tool hosts
///      nothing it copies, so the person judges whether that game is one they trust
///      (<see cref="LocalCopies.Disclaimer"/>);
///   3. Unity's own editor package for the game's exact build, read from Unity's server only as far
///      as the profile, when this computer can go online;
///   4. nothing — and then the card says that being online is the way.
/// </summary>
public static class ClassLibrarySources
{
    /// <summary>Every place the libraries could come from, in the order they are preferred, each with its verdict.</summary>
    /// <param name="games">Other games on this computer; the game itself is left out.</param>
    /// <param name="online">Whether this computer can reach Unity's server now.</param>
    public static IReadOnlyList<ClassLibraryCandidate> Find(GameInstall game, string? build, string? changeset,
                                                           IEnumerable<GameInstall> games, bool online)
    {
        if (EngineModules.PlatformOf(game) is not { } platform
            || UnityVersions.Parse(build ?? game.UnityVersion) is not { } version
            || MonoProfiles.MonoEngine(game) is not { } engine
            || game.DataDirectory is null)
            return Array.Empty<ClassLibraryCandidate>();

        var profile = MonoProfiles.Profile(platform, version);
        var editors = new List<ClassLibrarySource>();
        var others = new List<ClassLibrarySource>();

        foreach (var (editorVersion, root) in UnityEditors.Installed())
        {
            if (UnityEditors.ClassLibraries(root, profile) is not { } folder) continue;

            var same = UnityVersions.SameRelease(editorVersion, version);
            if (!same && !SameGeneration(Path.Combine(folder, "mscorlib.dll"), engine)) continue;

            editors.Add(new ClassLibrarySource(ClassLibrarySourceKind.Editor, $"editor:{folder}", null, editorVersion, same,
                                               new[] { folder, Path.Combine(folder, "Facades") }, profile));
        }

        foreach (var other in games)
        {
            if (string.Equals(Path.GetFullPath(other.Path), Path.GetFullPath(game.Path), StringComparison.OrdinalIgnoreCase)) continue;
            if (other.Runtime != UnityRuntime.Mono || other.DataDirectory is null || EngineModules.PlatformOf(other) != platform) continue;
            if (UnityVersions.Parse(other.UnityVersion) is not { } otherVersion) continue;

            var managed = Path.Combine(other.DataDirectory, "Managed");
            var mscorlib = Path.Combine(managed, "mscorlib.dll");
            if (!File.Exists(mscorlib)) continue;

            var same = UnityVersions.SameRelease(otherVersion, version);
            if (!same && !SameGeneration(mscorlib, engine)) continue;

            others.Add(new ClassLibrarySource(ClassLibrarySourceKind.Game, $"game:{Path.GetFullPath(other.Path)}", other.Name,
                                              otherVersion, same, new[] { managed }, profile));
        }

        var needs = RuntimeLibraries.EmbeddedModNeeds;
        var gameManaged = Path.Combine(game.DataDirectory, "Managed");

        var candidates = editors.OrderByDescending(s => s.SameRelease).ThenBy(s => Distance(s.Version, version))
                                .Select(s => new ClassLibraryCandidate(s, Array.Empty<string>()))
                                .ToList();

        candidates.AddRange(others.OrderByDescending(s => s.SameRelease).ThenBy(s => Distance(s.Version, version))
                                  .Select(s => new ClassLibraryCandidate(s, Verify(game, needs, gameManaged, s))));

        if (online && build is not null && changeset is not null)
        {
            candidates.Add(new ClassLibraryCandidate(new ClassLibrarySource(
                ClassLibrarySourceKind.UnityDownload, $"unity:{build}", null, version, true, null, profile), Array.Empty<string>()));
        }

        return candidates;
    }

    /// <summary>The source to use: the one a person chose, while it is still usable; the first usable one otherwise.</summary>
    public static ClassLibraryCandidate? Choose(IReadOnlyList<ClassLibraryCandidate> candidates, string? chosenId) =>
        candidates.FirstOrDefault(c => c.Usable && c.Source.Id == chosenId) ?? candidates.FirstOrDefault(c => c.Usable);

    /// <summary>
    /// Why no copy of the .NET libraries can EVER be found for this game — said at scan. Null when
    /// one may be. Being offline is not such a reason: it passes, and the card says what to do.
    /// </summary>
    public static string? CannotSupply(GameInstall game, IReadOnlyCollection<string> missing, string? build, string? changeset)
    {
        if (missing.Count == 0) return null;

        if (EngineModules.PlatformOf(game) is null)
            return "this game is not built for Windows or Linux, and Unity's .NET libraries for it cannot be found";

        if (UnityVersions.Parse(build ?? game.UnityVersion) is null)
            return "the game's Unity version could not be read, so no matching copy can be chosen";

        if (MonoProfiles.MonoEngine(game) is null)
            return "the game runs Unity's old .NET 3.5 runtime, which the mod cannot run on";

        // ⚠ Other games are not known at scan; an editor or Unity's download is. A game that could
        // lend them while neither exists is so rare it is not waited for.
        if (changeset is null && Find(game, build, changeset, Array.Empty<GameInstall>(), online: false).Count == 0)
            return "this build could not be identified to download Unity's libraries, and no Unity editor of its generation is installed";

        return null;
    }

    // ── Holding a copy from another game to the rules ─────────────────────────────────────────

    /// <summary>
    /// What stands against copying a game's libraries — empty when nothing does. Everything that can
    /// be held to the files is: that they make the mod's references resolve, that none is another
    /// system's build, that each is of the same family as the game's own. What cannot be held is
    /// whether the bytes are genuine: the card says so.
    /// </summary>
    private static IReadOnlyList<string> Verify(GameInstall game, IReadOnlyList<TypeUse> needs, string gameManaged, ClassLibrarySource source)
    {
        var donor = source.Folders![0];
        var stamp = $"{Stamp(Path.Combine(donor, "mscorlib.dll"))}|{Stamp(Path.Combine(donor, "System.dll"))}|"
                  + $"{Stamp(Path.Combine(gameManaged, "mscorlib.dll"))}|{Stamp(Path.Combine(gameManaged, "System.dll"))}";

        return VerifyMemo.GetOrAdd(stamp, _ =>
        {
            var copies = RuntimeLibraries.Folder(donor);
            var own = RuntimeLibraries.Folder(gameManaged);

            var selection = RuntimeLibraries.Select(needs, own, copies);
            if (!selection.Complete)
                return new[] { $"its own copies are stripped too, and lack what the mod needs ({selection.Unresolved[0].Use.Type})" };

            if (game.IsWindowsBuild && RuntimeLibraries.UnixOnly(selection.Chosen, copies) is { Count: > 0 } linux)
                return new[] { $"its copies of {string.Join(", ", linux)} are built for Linux" };

            if (RuntimeLibraries.NotSameFamily(selection.Chosen, own, copies) is { Count: > 0 } alien)
                return new[] { $"its libraries do not match this game's own ({alien[0]})" };

            return Array.Empty<string>();
        });
    }

    private static readonly ConcurrentDictionary<string, IReadOnlyList<string>> VerifyMemo = new();

    private static string Stamp(string path)
    {
        if (!File.Exists(path)) return $"{path}:absent";
        var info = new FileInfo(path);
        return $"{info.FullName.ToLowerInvariant()}:{info.Length}:{info.LastWriteTimeUtc.Ticks}";
    }

    /// <summary>Whether a `mscorlib` is of a generation the game's engine accepts.</summary>
    private static bool SameGeneration(string mscorlib, string engine) =>
        MonoProfiles.CorlibVersionOf(mscorlib) is { } generation && MonoProfiles.Carries(engine, generation);

    private static int Distance(UnityVersion a, UnityVersion b) =>
        Math.Abs((a.Major - b.Major) * 10000 + (a.Minor - b.Minor) * 100 + (a.Patch - b.Patch));
}
