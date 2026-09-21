using System.Collections.Concurrent;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;

namespace UnityGameTranslator.Manager.Core.Detection;

/// <summary>
/// Which of Unity's .NET profiles a game runs on, and which generation of Unity's Mono it is — what
/// decides which copy of the class libraries can stand in for the game's own.
///
/// ⚠ **A profile per system from Unity 2021.2** (measured 2026-09-21): an editor carries
/// `MonoBleedingEdge/lib/mono/unityjit-win32`, `unityjit-linux` and `unityjit-macos`, and a game is
/// built from the one of its system — the Linux one calls `System.Native`, which no Windows machine
/// has. Before 2021.2 every system used `4.5`.
///
/// ⚠ **A generation, not a Unity version.** The engine checks the `mscorlib` it loads against the
/// interface version it was built for — `System.Environment.mono_corlib_version`, a GUID. Every build
/// from 2021.3.1 to 6000.5 on this machine carries the same one, editors and games alike, and the
/// `mscorlib` of another 2021.3 release started a stripped 2021.3.6 game perfectly. So an editor of
/// another release serves when the game's engine carries its GUID — read in the engine binary itself.
/// </summary>
public static class MonoProfiles
{
    /// <summary>The profile folder a game of this system and version runs on.</summary>
    public static string Profile(EngineModules.Platform platform, UnityVersion version) =>
        version.Major > 2021 || (version.Major == 2021 && version.Minor >= 2)
            ? platform == EngineModules.Platform.Windows ? "unityjit-win32" : "unityjit-linux"
            : "4.5";

    /// <summary>
    /// The Mono runtime a game embeds, or null when it runs the older one (Unity's .NET 3.5
    /// runtime, which the mod cannot run on and no copy here would fit).
    /// </summary>
    public static string? MonoEngine(Model.GameInstall game)
    {
        var windows = Path.Combine(game.Path, "MonoBleedingEdge", "EmbedRuntime", "mono-2.0-bdwgc.dll");
        if (File.Exists(windows)) return windows;

        if (game.DataDirectory is null) return null;

        var linux = Path.Combine(game.DataDirectory, "MonoBleedingEdge", "x86_64", "libmonobdwgc-2.0.so");
        return File.Exists(linux) ? linux : null;
    }

    /// <summary>
    /// The generation a `mscorlib` declares — `System.Environment.mono_corlib_version` when it is a
    /// GUID. Null when it declares none, or an older numeric one (then only the same release is
    /// trusted to fit).
    /// </summary>
    public static string? CorlibVersionOf(string mscorlib)
    {
        var info = new FileInfo(mscorlib);
        var stamp = $"{info.FullName.ToLowerInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";

        return VersionMemo.GetOrAdd(stamp, _ => new Holder(ReadCorlibVersion(mscorlib))).Value;
    }

    private sealed record Holder(string? Value);

    private static readonly ConcurrentDictionary<string, Holder> VersionMemo = new();

    private static string? ReadCorlibVersion(string mscorlib)
    {
        using var stream = File.OpenRead(mscorlib);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata) return null;

        var r = pe.GetMetadataReader();
        foreach (var handle in r.FieldDefinitions)
        {
            var field = r.GetFieldDefinition(handle);
            if (r.GetString(field.Name) != "mono_corlib_version") continue;

            var constant = field.GetDefaultValue();
            if (constant.IsNil) continue;

            var value = r.GetConstant(constant);
            if (value.TypeCode != ConstantTypeCode.String) continue;

            var blob = r.GetBlobReader(value.Value);
            return blob.ReadUTF16(blob.Length);
        }

        return null;
    }

    /// <summary>
    /// Whether an engine binary names this corlib version — so a `mscorlib` of that generation is
    /// one it accepts. Remembered against the file's size and time.
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
