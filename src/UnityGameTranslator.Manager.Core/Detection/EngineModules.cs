using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace UnityGameTranslator.Manager.Core.Detection;

/// <summary>
/// Unity's engine modules (`UnityEngine.dll`, `UnityEngine.*Module.dll`) as a game ships them: which
/// ones its build stripped, what the native player they talk to carries, and whether a copy from
/// elsewhere fits that player.
///
/// 🔴 **Why stripping is read from the engine and not from the mod's needs** (measured 2026-09-21 on
/// 42 games). The mod is compiled against a recent Unity; on a 2018 game, four to eight of its
/// references to the engine do not resolve — newer APIs, never called there because the code that
/// makes them checks first. Reading "unresolved" as "stripped" would refuse every older game.
///
/// What tells a stripped module apart is the native player. Unity registers every engine method
/// implemented in native code under its managed name ("UnityEngine.Application::get_productName"),
/// and those names sit in the player binary as plain text. A module built with the player defines
/// every one of them for its own types; a stripped module has lost the ones the game never called.
/// Measured: **zero** such names missing on 40 unstripped games from 2017.3 to 6000.5, some four
/// thousand on the two stripped ones. So a game lacks engine modules when the mod's references stop
/// in a module the player shows to be stripped — both, never one alone.
///
/// ⚠ The same names answer the question a DONOR raises: a module from another build calls into the
/// player by these names, and a call the game's player does not carry is a native crash at start
/// (measured: a set from a newer 2021.3 than the game's). See <see cref="MissingFromPlayer"/>.
/// </summary>
public static class EngineModules
{
    /// <summary>
    /// Whether an assembly is one of Unity's engine modules — the facade `UnityEngine` or a
    /// `UnityEngine.*Module`. Not `UnityEngine.UI` and the other packages, which each project
    /// compiles for itself and no other build can stand in for.
    /// </summary>
    public static bool IsEngineModule(string assembly) =>
        assembly.Equals("UnityEngine", StringComparison.OrdinalIgnoreCase)
        || (assembly.StartsWith("UnityEngine.", StringComparison.OrdinalIgnoreCase)
            && assembly.EndsWith("Module", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The engine modules in a Managed folder, by assembly name, sorted. Empty for an engine older
    /// than the split into modules (2017.2), where `UnityEngine.dll` holds everything and is not a
    /// set this tool knows how to replace.
    /// </summary>
    public static IReadOnlyList<string> InFolder(string managed)
    {
        if (!File.Exists(Path.Combine(managed, "UnityEngine.CoreModule.dll"))) return Array.Empty<string>();

        return Directory.EnumerateFiles(managed, "UnityEngine*.dll")
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .Where(IsEngineModule)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The native player binary of a game folder: `UnityPlayer.dll` (Windows), `UnityPlayer.so`
    /// (Linux) — or, before Unity split the player out, the executable itself, which then holds it.
    /// </summary>
    public static string? PlayerBinary(string gameFolder, string? executable)
    {
        foreach (var name in new[] { "UnityPlayer.dll", "UnityPlayer.so" })
        {
            var path = Path.Combine(gameFolder, name);
            if (File.Exists(path)) return path;
        }

        return executable is not null && File.Exists(executable) ? executable : null;
    }

    // ── What the player carries ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Every "Type::Method" name the player binary holds as text, nested types joined with '+' as
    /// <see cref="AssemblyShape"/> joins them (the player writes '/').
    ///
    /// ⚠ Remembered against the file's size and time: a player is 20 to 80 MB and a game list asks
    /// again at every redraw.
    /// </summary>
    public static IReadOnlySet<string> NativeNames(string playerBinary)
    {
        var info = new FileInfo(playerBinary);
        var stamp = $"{info.FullName.ToLowerInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";

        return NamesMemo.GetOrAdd(stamp, _ => ReadNativeNames(File.ReadAllBytes(playerBinary)));
    }

    private static readonly ConcurrentDictionary<string, IReadOnlySet<string>> NamesMemo = new();

    /// <summary>The names in a binary, found around each "::" rather than by a regex over 50 MB.</summary>
    public static IReadOnlySet<string> ReadNativeNames(byte[] binary)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 1; i < binary.Length - 2; i++)
        {
            if (binary[i] != (byte)':' || binary[i + 1] != (byte)':') continue;

            var start = i;
            while (start > 0 && IsTypeChar(binary[start - 1])) start--;

            var end = i + 2;
            while (end < binary.Length && IsMemberChar(binary[end])) end++;

            // A type starts with a letter or '_', and a member follows.
            if (start < i && end > i + 2 && (char.IsAsciiLetter((char)binary[start]) || binary[start] == (byte)'_'))
            {
                var type = Encoding.ASCII.GetString(binary, start, i - start).Replace('/', '+');
                names.Add(type + "::" + Encoding.ASCII.GetString(binary, i + 2, end - (i + 2)));
            }

            i = end - 1;
        }

        return names;
    }

    private static bool IsMemberChar(byte b) => char.IsAsciiLetterOrDigit((char)b) || b == (byte)'_';

    private static bool IsTypeChar(byte b) => IsMemberChar(b) || b == (byte)'.' || b == (byte)'/';

    // ── What a module lacks against its player ────────────────────────────────────────────────

    /// <summary>
    /// For each module, how many methods the player names on the module's own types that the
    /// module does not define — zero for a module built with that player, many for a stripped one.
    /// Only modules with at least one are returned.
    /// </summary>
    public static IReadOnlyDictionary<string, int> Stripped(Func<string, AssemblyShape?> modules,
                                                           IEnumerable<string> names,
                                                           IReadOnlySet<string> nativeNames)
    {
        var byType = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var name in nativeNames)
        {
            var separator = name.IndexOf("::", StringComparison.Ordinal);
            var type = name[..separator];
            if (!byType.TryGetValue(type, out var members)) byType[type] = members = new List<string>();
            members.Add(name[(separator + 2)..]);
        }

        var stripped = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var moduleName in names)
        {
            if (modules(moduleName) is not { } module) continue;

            var lacking = 0;
            foreach (var (typeName, type) in module.Types)
            {
                if (byType.TryGetValue(typeName, out var members))
                    lacking += members.Count(m => !type.Members.Contains(m));
            }

            if (lacking > 0) stripped[moduleName] = lacking;
        }

        return stripped;
    }

    /// <summary>
    /// The native calls a set of modules makes that its own player carries and the game's does not —
    /// what makes a module from another build crash the game at start. Empty when there is none.
    ///
    /// 🔴 **Against the donor's own player, never in the absolute** (measured 2026-09-21). A module
    /// declares native calls its own player never registers — resolved only when first called, and
    /// some never are: every intact 2021.3 game "misses" two of its own, a 2018 game one, a game
    /// shipping the old networking module two hundred. Counted in the absolute, that refused perfectly
    /// good copies. What the crash comes from is a call the DONOR's engine had and the game's lacks.
    /// </summary>
    /// <param name="donorNames">
    /// The donor's player's names. Null when it is not at hand: every call the game's player lacks
    /// then counts — stricter, never looser.
    /// </param>
    public static IReadOnlyList<string> MissingFromPlayer(IEnumerable<AssemblyShape> modules, IReadOnlySet<string> gameNames,
                                                          IReadOnlySet<string>? donorNames) =>
        modules.SelectMany(m => m.InternalCalls)
               .Where(call => !gameNames.Contains(call) && (donorNames is null || donorNames.Contains(call)))
               .Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal).ToList();

    /// <summary>
    /// What a game's own module holds that a replacement lacks: a type, or a member of one, by name.
    ///
    /// ⚠ The game's copy is stripped down to what the game and the player actually use — so
    /// anything left in it is used, and a replacement lacking it breaks exactly that. This is how a
    /// callback the player makes into managed code by name (`InvokePanicFunction`, measured on a
    /// donor two patches older) is caught without knowing the player's list of callbacks.
    /// </summary>
    public static IReadOnlyList<string> Lacks(AssemblyShape own, AssemblyShape replacement)
    {
        var lacking = new List<string>();

        foreach (var (typeName, type) in own.Types)
        {
            // Compiler-generated names are produced per build and say nothing about the version.
            if (typeName.Contains('<') || typeName.StartsWith("__", StringComparison.Ordinal)) continue;

            if (!replacement.Types.TryGetValue(typeName, out var theirs))
            {
                lacking.Add(typeName);
                continue;
            }

            lacking.AddRange(type.Members.Where(m => !m.Contains('<') && !theirs.Members.Contains(m))
                                         .Select(m => $"{typeName}.{m}"));
        }

        return lacking;
    }

    // ── Which build a player is ───────────────────────────────────────────────────────────────

    private static readonly Regex BuildPattern =
        new(@"(?<version>\d{4}\.\d+\.\d+[abfp]\d+)(?: \(|_)(?<changeset>[0-9a-f]{12})\b", RegexOptions.CultureInvariant);

    /// <summary>
    /// The build a player states — "2021.3.6f1" and its changeset "7da38d85baf6", which Unity's
    /// downloads are filed under. Null when the binary does not state it.
    ///
    /// ⚠ Read from the text the engine logs at start ("2021.3.6f1 (7da38d85baf6)"), present on
    /// every player measured from 2017.3 to 6000.5. The file's version resource carries the
    /// changeset only from about 2021.3.20 on, and is not relied on.
    /// </summary>
    public static (string Version, string Changeset)? BuildOf(string playerBinary, string? expectedVersion)
    {
        var info = new FileInfo(playerBinary);
        var stamp = $"{info.FullName.ToLowerInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";

        var builds = BuildMemo.GetOrAdd(stamp, _ =>
            BuildPattern.Matches(Encoding.ASCII.GetString(File.ReadAllBytes(playerBinary)))
                        .Select(m => (m.Groups["version"].Value, m.Groups["changeset"].Value))
                        .Distinct()
                        .ToList());

        // A binary can mention other builds (a plugin's own stamp); the one matching the game's
        // stated version wins, and a lone candidate is taken as it is.
        if (expectedVersion is not null)
        {
            foreach (var build in builds)
                if (build.Item1.StartsWith(expectedVersion, StringComparison.OrdinalIgnoreCase)
                    || expectedVersion.StartsWith(build.Item1, StringComparison.OrdinalIgnoreCase))
                    return build;
        }

        return builds.Count == 1 ? builds[0] : null;
    }

    private static readonly ConcurrentDictionary<string, List<(string, string)>> BuildMemo = new();

    // ── A game, as the scan sees it ───────────────────────────────────────────────────────────

    /// <summary>
    /// The engine modules this game's build stripped of what the mod calls, or null when it lacks
    /// none — the old games whose references stop on newer APIs included, since their modules are
    /// complete.
    ///
    /// ⚠ The player is read only when the mod's references actually stop somewhere: on almost every
    /// game they do not, and a 50 MB read per game would make the list slow for nothing.
    /// </summary>
    public static Model.EngineModuleNeed? NeedOf(Model.GameInstall game, IReadOnlyCollection<TypeUse> modNeeds)
    {
        if (game.DataDirectory is null) return null;

        var managed = Path.Combine(game.DataDirectory, "Managed");
        var set = InFolder(managed);
        if (set.Count == 0) return null;

        var modules = RuntimeLibraries.Folder(managed);
        var stops = RuntimeLibraries.Unresolved(modNeeds.Where(u => IsEngineModule(u.Assembly)),
                                                new RuntimeLibraries.Layers(modules));
        if (stops.Count == 0) return null;

        if (PlayerBinary(game.Path, game.ExecutablePath) is not { } player) return null;

        var stripped = Stripped(modules, set, NativeNames(player));
        if (stripped.Count == 0) return null;

        // The facade forwards to the modules; a type missing from a stripped facade is a stripped
        // build's too, and replacing the set restores both.
        var hit = stops.Select(s => s.Assembly)
                       .Where(a => stripped.ContainsKey(a) || a.Equals("UnityEngine", StringComparison.OrdinalIgnoreCase))
                       .Distinct(StringComparer.OrdinalIgnoreCase)
                       .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
                       .ToList();
        if (hit.Count == 0) return null;

        var build = BuildOf(player, game.UnityVersion);
        return new Model.EngineModuleNeed(hit, set, build?.Version ?? game.UnityVersion, build?.Changeset,
                                          CannotSupply(game, build?.Version ?? game.UnityVersion, build?.Changeset));
    }

    public enum Platform { Windows, Linux }

    /// <summary>
    /// The system a game is built for, as far as engine modules go — a Windows build wherever it
    /// runs (Proton included), a Linux one, or null for anything else (a macOS build copied over).
    /// </summary>
    public static Platform? PlatformOf(Model.GameInstall game)
    {
        if (game.IsWindowsBuild) return Platform.Windows;
        if (File.Exists(Path.Combine(game.Path, "UnityPlayer.so"))) return Platform.Linux;

        // Before 2019.3 the Linux player was the executable itself: an ELF file.
        if (game.ExecutablePath is { } executable && File.Exists(executable))
        {
            try
            {
                using var stream = File.OpenRead(executable);
                var magic = new byte[4];
                if (stream.Read(magic, 0, 4) == 4 && magic[0] == 0x7F && magic[1] == 'E' && magic[2] == 'L' && magic[3] == 'F')
                    return Platform.Linux;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // An executable that cannot be opened says nothing about its system.
            }
        }

        return null;
    }

    /// <summary>
    /// The sections of Unity's list of downloads that hold a platform's build support, newest
    /// name first — read on 2018.4 ("Linux") and 2021.3 ("Linux-Mono").
    /// </summary>
    public static IReadOnlyList<string> PackageSections(Platform platform) => platform == Platform.Windows
        ? new[] { "Windows-Mono" }
        : new[] { "Linux-Mono", "Linux" };

    /// <summary>
    /// Whether Unity signs the engine modules of builds like this one — what decides whether a copy
    /// found on this computer can ever be verified.
    ///
    /// ⚠ Measured (2026-09-21): signed in Windows builds from 2020.3 late on (No Plan B 2020.3.48,
    /// Rain World 2020.3.45), unsigned in every 2017–2019 build on this machine and in 2020.3.2/.4;
    /// unsigned in Linux builds, Unity's own 2021.3 package included. 2020 is let through: a copy that
    /// turns out unsigned is refused by the check itself.
    /// </summary>
    public static bool UnitySigns(Platform platform, UnityVersion? version) =>
        platform == Platform.Windows && version is { Major: >= 2020 };

    /// <summary>
    /// Why no copy of the engine modules can be found for this game — said at scan, before any
    /// source is looked at. Null when one may be.
    ///
    /// 🔴 **The user's decision (2026-09-21), and its limit.** A copy found on this computer is used
    /// only when Unity's signature verifies — another game may have been tampered with. Where Unity
    /// signs nothing (Linux builds, versions before 2020), the one source left is Unity's own server,
    /// trusted for being Unity's, over HTTPS; so such a game can be served only when its build can be
    /// identified to find the download.
    /// </summary>
    public static string? CannotSupply(Model.GameInstall game, string? version, string? changeset)
    {
        if (PlatformOf(game) is not { } platform)
            return "this game is not built for Windows or Linux, and Unity's engine modules for it cannot be found";

        if (UnityVersions.Parse(version) is not { } parsed)
            return "the game's Unity version could not be read, so no matching engine modules can be chosen";

        if (!UnitySigns(platform, parsed) && changeset is null)
        {
            return "Unity did not sign the engine modules of this build, and the build could not be identified "
                 + "to download them from Unity";
        }

        return null;
    }
}
