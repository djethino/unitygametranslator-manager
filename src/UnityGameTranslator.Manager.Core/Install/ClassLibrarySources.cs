using UnityGameTranslator.Manager.Core.Catalog;
using UnityGameTranslator.Manager.Core.Detection;
using UnityGameTranslator.Manager.Core.Model;

namespace UnityGameTranslator.Manager.Core.Install;

public enum ClassLibrarySourceKind { Editor, UnityDownload }

/// <summary>One place a game's missing .NET libraries could come from.</summary>
/// <param name="Id">Stable across runs — what a person's choice is remembered by.</param>
/// <param name="SameRelease">The game's own Unity release; false for another release of its Mono generation.</param>
/// <param name="Folder">The editor's profile folder, for an editor on this computer.</param>
/// <param name="Profile">The profile the libraries are taken from ("unityjit-win32").</param>
public sealed record ClassLibrarySource(ClassLibrarySourceKind Kind, string Id, UnityVersion Version, bool SameRelease,
                                        string? Folder, string Profile)
{
    /// <summary>Where they come from, named. ⚠ ASCII only: it is also printed by `report`.</summary>
    public string Label => Kind == ClassLibrarySourceKind.Editor
        ? $"the Unity {Version} editor on this computer"
        : $"Unity's {Version} editor package ({RuntimeLibraryOrigins.UnityDownloadHost})";
}

/// <summary>
/// Where a game's missing .NET libraries can come from.
///
/// 🔴 **Unity, and nothing else** (user's decisions, 2026-09-21). These libraries carry no signature,
/// so nothing can tell a genuine copy from a planted one — a copy is only as trustworthy as where it
/// was taken. Hence, in this order:
///   · an editor installed on this computer — the game's own release, then another release of the
///     same Mono generation (the editor being "the most reliable source");
///   · Unity's own editor package for the game's exact build, read from Unity's server only as far
///     as the profile (a few hundred MB of 1 to 3 GB), agreed to first.
/// Never another game: a pirated game on the same computer can carry anything. And no longer
/// BepInEx's archive, lighter but a third party publishing no checksum.
/// </summary>
public static class ClassLibrarySources
{
    /// <summary>Every place the libraries could come from, in the order they are preferred.</summary>
    public static IReadOnlyList<ClassLibrarySource> Find(GameInstall game, string? build, string? changeset)
    {
        if (EngineModules.PlatformOf(game) is not { } platform
            || UnityVersions.Parse(build ?? game.UnityVersion) is not { } version
            || MonoProfiles.MonoEngine(game) is not { } engine)
            return Array.Empty<ClassLibrarySource>();

        var profile = MonoProfiles.Profile(platform, version);
        var same = new List<ClassLibrarySource>();
        var generation = new List<ClassLibrarySource>();

        foreach (var (editorVersion, root) in UnityEditors.Installed())
        {
            if (UnityEditors.ClassLibraries(root, profile) is not { } folder) continue;

            var source = new ClassLibrarySource(ClassLibrarySourceKind.Editor, $"editor:{folder}", editorVersion,
                                                UnityVersions.SameRelease(editorVersion, version), folder, profile);

            if (source.SameRelease) same.Add(source);
            else if (SameGeneration(folder, engine)) generation.Add(source);
        }

        var candidates = new List<ClassLibrarySource>(same);

        // Closest release first: within a generation, what differs between two releases is least
        // where they are nearest.
        candidates.AddRange(generation.OrderBy(s => Distance(s.Version, version)));

        if (build is not null && changeset is not null)
        {
            candidates.Add(new ClassLibrarySource(ClassLibrarySourceKind.UnityDownload, $"unity:{build}", version,
                                                  true, null, profile));
        }

        return candidates;
    }

    /// <summary>The source to use: the one a person chose, while it is still offered; the first one otherwise.</summary>
    public static ClassLibrarySource? Choose(IReadOnlyList<ClassLibrarySource> candidates, string? chosenId) =>
        candidates.FirstOrDefault(c => c.Id == chosenId) ?? candidates.FirstOrDefault();

    /// <summary>
    /// Why no copy of the .NET libraries can be found for this game — said at scan, before anything
    /// is downloaded. Null when one can.
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

        if (Find(game, build, changeset).Count == 0)
            return "no Unity editor of this generation is installed, and this build could not be identified to download Unity's";

        return null;
    }

    /// <summary>Whether an editor's `mscorlib` is of a generation the game's engine accepts.</summary>
    private static bool SameGeneration(string folder, string engine) =>
        MonoProfiles.CorlibVersionOf(Path.Combine(folder, "mscorlib.dll")) is { } generation
        && MonoProfiles.Carries(engine, generation);

    private static int Distance(UnityVersion a, UnityVersion b) =>
        Math.Abs((a.Major - b.Major) * 10000 + (a.Minor - b.Minor) * 100 + (a.Patch - b.Patch));
}
