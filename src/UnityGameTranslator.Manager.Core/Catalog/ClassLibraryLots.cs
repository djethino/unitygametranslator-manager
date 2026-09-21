using System.Collections.Concurrent;
using System.Text;

namespace UnityGameTranslator.Manager.Core.Catalog;

/// <summary>
/// Our own copies of the Windows build of Unity's .NET class libraries, one per generation of
/// Unity's Mono — what fills the gap BepInEx's archive leaves from Unity 2021.2 on, where its
/// `mscorlib`, `System` and `System.Core` are Linux builds (see <see cref="RuntimeLibraryOrigins"/>).
///
/// 🔴 **A generation, not a Unity version** (measured 2026-09-21). The engine checks the `mscorlib`
/// it loads against the interface version it was built for — `System.Environment.mono_corlib_version`,
/// a GUID. Every build from 2021.3.1 to 6000.2 on this machine carries the same one, editors and
/// games alike, and a `mscorlib` of another 2021.3 release with that GUID started a stripped
/// 2021.3.6 game perfectly. So one lot serves a whole generation, and a game is served when its
/// engine carries the lot's GUID — read in the engine binary itself, not inferred from a version.
///
/// ⚠ Each lot is taken from `MonoBleedingEdge/lib/mono/unityjit-win32` of the NEWEST editor of its
/// generation at hand: within a generation the libraries only grow (2022.2.11's `mscorlib` holds
/// 2021.3.6's and 13 more members), and a game whose own stripped copy holds something the lot lacks
/// is refused by the same-family check before anything is written.
///
/// ⚠ **Compiled in with its SHA-256**, unlike BepInEx's archive: this is a file we publish, so we
/// can say exactly which bytes it is, and a copy that differs is refused.
/// </summary>
public static class ClassLibraryLots
{
    /// <param name="CorlibVersion">The `mono_corlib_version` GUID the lot's `mscorlib` carries.</param>
    /// <param name="TakenFrom">The Unity editor the files were copied from ("2022.2.11f1").</param>
    public sealed record Lot(string CorlibVersion, string TakenFrom, string Url, string Sha256, long Size);

    /// <summary>
    /// ⚠ Empty until the lots are published — where they are hosted is the user's decision
    /// (analyse/manager-runtime-libraries.md, F5). Until then a game that needs one is refused with
    /// the reason it always had.
    /// </summary>
    public static IReadOnlyList<Lot> All { get; } = Array.Empty<Lot>();

    /// <summary>The lot this game's engine accepts, or null.</summary>
    public static Lot? For(Model.GameInstall game) => For(game, All);

    /// <summary>The same question over a given list — how the checks ask it.</summary>
    public static Lot? For(Model.GameInstall game, IEnumerable<Lot> lots)
    {
        if (!game.IsWindowsBuild || MonoEngine(game) is not { } engine) return null;

        return lots.FirstOrDefault(lot => Carries(engine, lot.CorlibVersion));
    }

    /// <summary>
    /// The Mono runtime a game embeds: `MonoBleedingEdge/EmbedRuntime/mono-2.0-bdwgc.dll` beside a
    /// Windows build. Null for the older runtime, which has no generation GUID to match.
    /// </summary>
    public static string? MonoEngine(Model.GameInstall game)
    {
        var path = Path.Combine(game.Path, "MonoBleedingEdge", "EmbedRuntime", "mono-2.0-bdwgc.dll");
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Whether an engine binary names this corlib version. Remembered against the file's size and time.
    /// </summary>
    public static bool Carries(string engine, string corlibVersion)
    {
        var info = new FileInfo(engine);
        var stamp = $"{info.FullName.ToLowerInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{corlibVersion}";

        return CarriesMemo.GetOrAdd(stamp, _ =>
            Encoding.ASCII.GetString(File.ReadAllBytes(engine)).Contains(corlibVersion, StringComparison.OrdinalIgnoreCase));
    }

    private static readonly ConcurrentDictionary<string, bool> CarriesMemo = new();
}
