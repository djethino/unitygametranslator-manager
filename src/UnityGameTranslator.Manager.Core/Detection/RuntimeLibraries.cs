using System.Text.RegularExpressions;

namespace UnityGameTranslator.Manager.Core.Detection;

/// <summary>
/// Which .NET libraries a Mono game lacks for the mod, and which copies may be put beside it.
///
/// 🔴 **Why this exists** (issue #28, measured on a test game 2026-09-21). Unity copies into a game
/// only the class libraries the game itself references, and can strip those of every member the
/// game does not call. A game can therefore ship without `netstandard.dll` or `System.Net.Http.dll`,
/// or with a `System.dll` missing `Queue&lt;T&gt;` — and the mod, which is built for .NET Standard,
/// dies at load with a TypeLoadException the player reads as "a DLL is missing". The loader starts
/// perfectly; nothing in its own log says anything.
///
/// The remedy is the loaders' own: a folder of complete libraries that the runtime searches before
/// the game's (see <see cref="Install.LoaderSearchPath"/>). This class decides what goes in it.
///
/// ⚠ **The smallest set that holds, never the whole archive.** Replacing a library also changes
/// what the GAME loads; the fewer replaced, the less can differ from what its developer tested.
/// </summary>
public static class RuntimeLibraries
{
    /// <summary>
    /// Whether an assembly name belongs to the runtime's class libraries — the ones a game may lack
    /// and a copy of Unity's may supply. UnityEngine and the game's own code never qualify.
    /// </summary>
    public static bool IsClassLibrary(string assembly) =>
        assembly.Equals("mscorlib", StringComparison.OrdinalIgnoreCase)
        || assembly.Equals("netstandard", StringComparison.OrdinalIgnoreCase)
        || assembly.Equals("System", StringComparison.OrdinalIgnoreCase)
        || assembly.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
        || assembly.StartsWith("Mono.", StringComparison.OrdinalIgnoreCase)
        || assembly.Equals("Microsoft.CSharp", StringComparison.OrdinalIgnoreCase);

    // ── Resolution ────────────────────────────────────────────────────────────────────────────

    public enum StopKind { MissingAssembly, MissingType, MissingMember }

    /// <summary>Where a reference stopped resolving, and in which assembly.</summary>
    /// <param name="Assembly">
    /// The assembly that failed to answer — absent, or present without the type or member. It is
    /// the one a complete copy would have to replace, which is why it is carried rather than the
    /// assembly the reference first named (often only the netstandard facade).
    /// </param>
    public sealed record Stop(StopKind Kind, string Assembly, TypeUse Use);

    /// <summary>
    /// Assemblies looked up by name through layers, first answer wins — exactly how a runtime reads
    /// a search-path override before the game's own folder.
    /// </summary>
    public sealed class Layers
    {
        private readonly Func<string, AssemblyShape?>[] _layers;

        public Layers(params Func<string, AssemblyShape?>[] layers) => _layers = layers;

        public AssemblyShape? Find(string assembly)
        {
            foreach (var layer in _layers)
            {
                if (layer(assembly) is { } shape) return shape;
            }

            return null;
        }
    }

    /// <summary>Null when the reference resolves, otherwise where it stopped.</summary>
    public static Stop? Resolve(TypeUse use, Layers layers)
    {
        var found = FindType(use.Assembly, use.Type, layers, use, depth: 0, out var stop);
        if (found is null) return stop;

        if (use.Member is null) return null;

        return HasMember(found.Value.Shape, found.Value.Type, use.Member, layers, depth: 0)
            ? null
            : new Stop(StopKind.MissingMember, found.Value.Shape.Name, use);
    }

    /// <summary>Every reference that does not resolve, each once.</summary>
    public static IReadOnlyList<Stop> Unresolved(IEnumerable<TypeUse> uses, Layers layers) =>
        uses.Select(u => Resolve(u, layers)).OfType<Stop>().Distinct().ToList();

    private static (AssemblyShape Shape, TypeShape Type)? FindType(string assembly, string fullName, Layers layers,
                                                                   TypeUse use, int depth, out Stop? stop)
    {
        stop = null;

        // A forward that points back at itself is a broken facade, not a type; ten hops is far
        // more than any real chain (netstandard → mscorlib is one).
        if (depth > 10)
        {
            stop = new Stop(StopKind.MissingType, assembly, use);
            return null;
        }

        if (layers.Find(assembly) is not { } shape)
        {
            stop = new Stop(StopKind.MissingAssembly, assembly, use);
            return null;
        }

        if (shape.Types.TryGetValue(fullName, out var type)) return (shape, type);

        // A nested type is forwarded with its outer type.
        var outer = fullName.Split('+')[0];
        if (shape.Forwards.TryGetValue(fullName, out var target) || shape.Forwards.TryGetValue(outer, out target))
            return FindType(target, fullName, layers, use, depth + 1, out stop);

        stop = new Stop(StopKind.MissingType, shape.Name, use);
        return null;
    }

    /// <summary>A member on the type or on anything it derives from.</summary>
    private static bool HasMember(AssemblyShape shape, TypeShape type, string member, Layers layers, int depth)
    {
        if (type.Members.Contains(member)) return true;
        if (type.Base is not { } baseName || depth > 20) return false;

        var baseAssembly = baseName.Assembly ?? shape.Name;
        var found = FindType(baseAssembly, baseName.FullName, layers, new TypeUse(baseAssembly, baseName.FullName),
                             depth: 0, out _);

        return found is not null && HasMember(found.Value.Shape, found.Value.Type, member, layers, depth + 1);
    }

    // ── What a game lacks ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The class libraries a game would have to be given for these references to resolve — the
    /// assemblies where resolution stopped, by name.
    /// </summary>
    public static IReadOnlyList<string> Missing(IEnumerable<TypeUse> needs, Layers game) =>
        Unresolved(needs.Where(u => IsClassLibrary(u.Assembly)), game)
            .Select(s => s.Assembly)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

    // ── The smallest set that holds ───────────────────────────────────────────────────────────

    /// <summary>What to put beside the game, and what would still not resolve.</summary>
    public sealed record Selection(IReadOnlyList<string> Chosen, IReadOnlyList<Stop> Unresolved)
    {
        public bool Complete => Unresolved.Count == 0;
    }

    /// <summary>
    /// The fewest libraries from <paramref name="archive"/> that make every need resolve.
    ///
    /// ⚠ Grown one round at a time from where resolution stops, never from a list: a stripped
    /// `System.dll` is replaced because a need stopped IN it, and a complete `System.dll` then asks
    /// things of `mscorlib` the game's stripped one may lack — so the libraries added are held to
    /// the same test as the mod, until nothing moves.
    ///
    /// ⚠ A reference an added library makes that does not resolve EVEN with the whole archive in
    /// front of the game is ignored: that is how Unity ships the library (the archive's
    /// `System.Runtime.Serialization` names a `System.ServiceModel.Internals` it does not carry),
    /// and a runtime only fails such a reference if the code that makes it ever runs.
    /// </summary>
    public static Selection Select(IReadOnlyCollection<TypeUse> needs,
                                   Func<string, AssemblyShape?> game,
                                   Func<string, AssemblyShape?> archive)
    {
        var chosen = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var classNeeds = needs.Where(u => IsClassLibrary(u.Assembly)).ToList();
        var everything = new Layers(archive, game);

        IReadOnlyList<Stop> stops = Array.Empty<Stop>();

        // Bounded by the number of libraries an archive can hold; a loop that has not settled after
        // that many rounds is adding nothing new, and the stops it has are the answer.
        for (var round = 0; round < 64; round++)
        {
            var view = new Layers(n => chosen.Contains(n) ? archive(n) : null, game);

            var found = Unresolved(classNeeds, view).ToList();

            foreach (var name in chosen)
            {
                if (archive(name) is not { } added) continue;

                foreach (var use in added.Uses)
                {
                    if (!IsClassLibrary(use.Assembly)) continue;
                    if (Resolve(use, view) is not { } stop) continue;
                    if (Resolve(use, everything) is not null) continue; // Unity ships it that way

                    found.Add(stop);
                }
            }

            stops = found.Distinct().ToList();
            if (stops.Count == 0) break;

            var more = stops.Select(s => s.Assembly)
                            .Where(a => !chosen.Contains(a) && archive(a) is not null)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();

            if (more.Count == 0) break;
            chosen.UnionWith(more);
        }

        return new Selection(chosen.ToList(), stops);
    }

    // ── What may be put beside a game ─────────────────────────────────────────────────────────

    /// <summary>
    /// Whether a native library name is one of the Unix shims Mono's newer class libraries call.
    ///
    /// ⚠ Measured, not assumed (2026-09-21): from Unity 2021.2 the class libraries differ per
    /// platform, and the Linux build of `System.dll` imports `System.Native` — which exists on no
    /// Windows machine. Put in a Windows game, every file operation then fails. No Windows build of
    /// a game carries one (read on three games from 2021.3 to 6000.0).
    /// </summary>
    public static bool IsUnixShim(string nativeLibrary) =>
        nativeLibrary.Equals("System.Native", StringComparison.OrdinalIgnoreCase)
        || (nativeLibrary.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
            && nativeLibrary.EndsWith(".Native", StringComparison.OrdinalIgnoreCase));

    /// <summary>The chosen libraries that call a Unix-only native library.</summary>
    public static IReadOnlyList<string> UnixOnly(IEnumerable<string> chosen, Func<string, AssemblyShape?> archive) =>
        chosen.Where(n => archive(n)?.NativeImports.Any(IsUnixShim) == true).ToList();

    /// <summary>
    /// The chosen libraries whose copy is not from the same family as the game's own — the game's
    /// stripped copy holds a type the complete one does not. Named with one example type each.
    ///
    /// ⚠ Only where the game HAS a copy to compare with. A library the game lacks entirely cannot
    /// be compared; the archive is then trusted for being of the game's exact Unity version.
    ///
    /// ⚠ Compiler-generated names are left out (`&lt;PrivateImplementationDetails&gt;` and the
    /// like): they are produced per build, and their presence says nothing about the version.
    /// </summary>
    public static IReadOnlyList<string> NotSameFamily(IEnumerable<string> chosen,
                                                      Func<string, AssemblyShape?> game,
                                                      Func<string, AssemblyShape?> archive)
    {
        var mismatched = new List<string>();

        foreach (var name in chosen)
        {
            if (game(name) is not { } own || archive(name) is not { } copy) continue;

            var alien = own.Types.Keys.FirstOrDefault(t =>
                !t.Contains('<') && !t.StartsWith("__", StringComparison.Ordinal)
                && !copy.Types.ContainsKey(t));

            if (alien is not null) mismatched.Add($"{name} ({alien})");
        }

        return mismatched;
    }

    // ── Which archive, and whether one can serve ──────────────────────────────────────────────

    private static readonly Regex UnityVersionPattern =
        new(@"^(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)(?<suffix>[abfp]\d+)?", RegexOptions.CultureInvariant);

    /// <summary>
    /// The name BepInEx's archive files a Unity version under: "2018.4.36f1" → "2018.4.36".
    ///
    /// Final and patch releases share their base number there; alphas and betas keep their suffix
    /// ("2021.2.0a10"), because that is how the archive lists them. Null when the version cannot be
    /// read — and then nothing is guessed.
    /// </summary>
    public static string? ArchiveName(string? unityVersion)
    {
        if (string.IsNullOrWhiteSpace(unityVersion)) return null;

        var match = UnityVersionPattern.Match(unityVersion.Trim());
        if (!match.Success) return null;

        var baseName = $"{match.Groups["major"].Value}.{match.Groups["minor"].Value}.{match.Groups["patch"].Value}";
        var suffix = match.Groups["suffix"].Value;

        return suffix.StartsWith('a') || suffix.StartsWith('b') ? baseName + suffix : baseName;
    }

    /// <summary>
    /// Whether this Unity version's class libraries differ per platform — 2021.2 and later.
    ///
    /// ⚠ What makes the archive useless for part of a Windows game from then on: it carries one
    /// build, and that build is Linux's (measured on seven versions, 2021.2.0 to 6000.2.6).
    /// </summary>
    public static bool PerPlatform(string unityVersion)
    {
        var match = UnityVersionPattern.Match(unityVersion.Trim());
        if (!match.Success) return false;

        var major = int.Parse(match.Groups["major"].Value);
        var minor = int.Parse(match.Groups["minor"].Value);

        return major > 2021 || (major == 2021 && minor >= 2);
    }

    /// <summary>
    /// The libraries a per-platform archive carries in their Linux build only — the ones that call
    /// `System.Native`. Measured on 2021.3.14 (2026-09-21); the install reads the real files and
    /// refuses on what it finds, so this list only has to be right enough to FORECAST.
    /// </summary>
    public static readonly IReadOnlyList<string> LinuxBoundWhenPerPlatform = new[] { "mscorlib", "System", "System.Core" };

    /// <summary>
    /// Whether the missing libraries can be supplied, said before anything is downloaded — null when
    /// they can (to be confirmed on the files at install), the reason otherwise.
    /// </summary>
    /// <param name="windowsBuild">True for a Windows build of the game, including one run through Proton.</param>
    public static string? CannotSupply(IReadOnlyCollection<string> missing, string? unityVersion, bool windowsBuild)
    {
        if (missing.Count == 0) return null;

        if (ArchiveName(unityVersion) is null)
            return "the game's Unity version could not be read, so no matching copy can be chosen";

        if (!windowsBuild || !PerPlatform(unityVersion!)) return null;

        var bound = missing.Where(m => LinuxBoundWhenPerPlatform.Contains(m, StringComparer.OrdinalIgnoreCase)).ToList();
        if (bound.Count == 0) return null;

        return $"for Unity {ArchiveName(unityVersion)}, the only copy of {string.Join(", ", bound)} published is "
             + "built for Linux, and this game is built for Windows";
    }

    // ── The mod's own needs, when no build of it is at hand ───────────────────────────────────

    /// <summary>
    /// What the mod asks of the class libraries, as of the build this tool was compiled with.
    ///
    /// ⚠ **The scan's answer, never the install's.** A game list cannot download the plugin to read
    /// it, so it reads this — generated from a real build by `generate-mod-requirements.ps1` and
    /// shipped inside the binary. The install reads the plugin it has just put in place, so a newer
    /// mod needing one more type is caught there even when this list is a release behind.
    /// </summary>
    public static IReadOnlyList<TypeUse> EmbeddedModNeeds => _embedded.Value;

    private static readonly Lazy<IReadOnlyList<TypeUse>> _embedded = new(() =>
    {
        using var stream = typeof(RuntimeLibraries).Assembly
            .GetManifestResourceStream("UnityGameTranslator.Manager.Core.Resources.mod-requirements.txt");

        if (stream is null) return Array.Empty<TypeUse>();

        using var reader = new StreamReader(stream);
        return ParseNeeds(reader.ReadToEnd());
    });

    /// <summary>Lines of "assembly|type" or "assembly|type|member"; '#' starts a comment.</summary>
    public static IReadOnlyList<TypeUse> ParseNeeds(string text)
    {
        var needs = new List<TypeUse>();

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var parts = line.Split('|');
            if (parts.Length < 2) continue;

            needs.Add(new TypeUse(parts[0], parts[1], parts.Length > 2 && parts[2].Length > 0 ? parts[2] : null));
        }

        return needs;
    }

    /// <summary>The inverse of <see cref="ParseNeeds"/>, sorted so a regenerated file diffs cleanly.</summary>
    public static string FormatNeeds(IEnumerable<TypeUse> needs) =>
        string.Join("\n", needs.Where(u => IsClassLibrary(u.Assembly))
                               .Select(u => u.Member is null ? $"{u.Assembly}|{u.Type}" : $"{u.Assembly}|{u.Type}|{u.Member}")
                               .Distinct(StringComparer.Ordinal)
                               .OrderBy(l => l, StringComparer.Ordinal)) + "\n";

    // ── Reading a game's own libraries ────────────────────────────────────────────────────────

    /// <summary>
    /// The game's Managed folder as a lookup by name, reading each library at most once.
    ///
    /// ⚠ An unreadable file answers "absent": a library the runtime cannot read either is one it
    /// cannot load, and the answer the mod gets is the same.
    /// </summary>
    public static Func<string, AssemblyShape?> Folder(string directory)
    {
        var read = new Dictionary<string, AssemblyShape?>(StringComparer.OrdinalIgnoreCase);

        return name =>
        {
            if (read.TryGetValue(name, out var known)) return known;

            var path = Path.Combine(directory, name + ".dll");
            AssemblyShape? shape = null;

            if (File.Exists(path))
            {
                try { shape = AssemblyShape.Read(path); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or BadImageFormatException) { }
            }

            read[name] = shape;
            return shape;
        };
    }
}
