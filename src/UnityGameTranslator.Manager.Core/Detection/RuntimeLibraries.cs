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

                // 🔴 **What an added library forwards becomes the GAME's need too** (measured
                // 2026-09-21). A game that never had netstandard gets one, and its own code then
                // resolves types through it: `DataView` forwarded to the game's stripped
                // System.Data, which lacked it — a TypeLoadException in the game's own loading,
                // with the mod not even installed. So each forward that lands on a copy the game
                // HAS, but stripped of that type, adds the complete copy. A target the game lacks
                // entirely is not added: nothing of the game's reached it before, and nothing will.
                foreach (var forwarded in added.Forwards.Keys)
                {
                    var use = new TypeUse(name, forwarded);
                    if (Resolve(use, view) is not { Kind: StopKind.MissingType or StopKind.MissingMember } stop) continue;
                    if (Resolve(use, everything) is not null) continue;

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
    ///
    /// ⚠ **Types visible from outside only** (measured 2026-09-21). Two patch releases of one Mono
    /// generation differ in their plumbing — a compiler-generated `EmbeddedAttribute`, a marshalling
    /// struct, a TLS helper — and holding a copy to those refused a library for 17 of 25 games whose
    /// public surface it covered. The game's code can only name what is visible; the library's own
    /// insides travel with the library.
    /// </summary>
    public static IReadOnlyList<string> NotSameFamily(IEnumerable<string> chosen,
                                                      Func<string, AssemblyShape?> game,
                                                      Func<string, AssemblyShape?> archive)
    {
        var mismatched = new List<string>();

        foreach (var name in chosen)
        {
            if (game(name) is not { } own || archive(name) is not { } copy) continue;

            var alien = own.Types.Where(t => t.Value.Visible).Select(t => t.Key).FirstOrDefault(t =>
                !t.Contains('<') && !t.StartsWith("__", StringComparison.Ordinal)
                && !copy.Types.ContainsKey(t));

            if (alien is not null) mismatched.Add($"{name} ({alien})");
        }

        return mismatched;
    }

    // ── Which release ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A Unity release as the receipt records the .NET copies' one: "2018.4.36f1" → "2018.4.36".
    /// Alphas and betas keep their suffix. Null when the version cannot be read — and then nothing
    /// is guessed.
    /// </summary>
    public static string? ReleaseName(string? unityVersion) => UnityVersions.Parse(unityVersion) switch
    {
        null => null,
        { Kind: 'a' or 'b' } v => v.ToString(),
        { } v => v.Release,
    };

    // ── The mod's own needs, when no build of it is at hand ───────────────────────────────────

    /// <summary>
    /// What the mod asks of the class libraries and of Unity's engine modules, as of the build this
    /// tool was compiled with.
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

    /// <summary>
    /// The inverse of <see cref="ParseNeeds"/>, sorted so a regenerated file diffs cleanly. Keeps
    /// what the mod asks of the class libraries AND of Unity's engine modules — the two batches a
    /// game can lack.
    /// </summary>
    public static string FormatNeeds(IEnumerable<TypeUse> needs) =>
        string.Join("\n", needs.Where(u => IsClassLibrary(u.Assembly) || EngineModules.IsEngineModule(u.Assembly))
                               .Select(u => u.Member is null ? $"{u.Assembly}|{u.Type}" : $"{u.Assembly}|{u.Type}|{u.Member}")
                               .Distinct(StringComparer.Ordinal)
                               .OrderBy(l => l, StringComparer.Ordinal)) + "\n";

    // ── A game, as the scan sees it ───────────────────────────────────────────────────────────

    /// <summary>
    /// What this Mono game lacks for the mod — .NET libraries, engine modules its build stripped, or
    /// both — read from its own folder; null when it lacks nothing, when it has no Managed folder,
    /// or when it is not Mono.
    ///
    /// ⚠ Read against <see cref="EmbeddedModNeeds"/>, so no download and no plugin are needed; and
    /// remembered against the libraries' own stamps, so a list redrawn forty times reads a game
    /// once, and reads it again the day an update replaces a library.
    /// </summary>
    /// <param name="loaderCannotStart">
    /// The game's mscorlib lacks what loaders call (<see cref="CorlibProbe"/>): mscorlib is then
    /// needed whatever the mod asks, because nothing starts without it.
    /// </param>
    public static Model.RuntimeLibraryNeed? NeedOf(Model.GameInstall game, bool loaderCannotStart)
    {
        if (game.Runtime != Model.UnityRuntime.Mono || game.DataDirectory is null) return null;

        var managed = Path.Combine(game.DataDirectory, "Managed");
        if (!Directory.Exists(managed)) return null;

        var missing = MissingMemo.GetOrAdd(StampOf(managed, IsClassLibrary, null),
            _ => Missing(EmbeddedModNeeds, new Layers(Folder(managed))));

        var all = loaderCannotStart && !missing.Contains("mscorlib", StringComparer.OrdinalIgnoreCase)
            ? missing.Prepend("mscorlib").ToList()
            : missing;

        // ⚠ Its own memory, keyed on the engine modules and the player: a game update replacing
        // UnityPlayer.dll changes the answer while no class library moved.
        var modules = ModulesMemo.GetOrAdd(
            StampOf(managed, EngineModules.IsEngineModule, EngineModules.PlayerBinary(game.Path, game.ExecutablePath)),
            _ => new Holder(EngineModules.NeedOf(game, EmbeddedModNeeds))).Need;

        if (all.Count == 0 && modules is null) return null;

        // The build and its changeset, which Unity's downloads are filed under — read from the player
        // only for a game that lacks .NET libraries (the modules' reading has its own).
        var build = all.Count == 0 || EngineModules.PlayerBinary(game.Path, game.ExecutablePath) is not { } player
            ? null
            : EngineModules.BuildOf(player, game.UnityVersion);

        return new Model.RuntimeLibraryNeed(all, loaderCannotStart, ReleaseName(build?.Version ?? game.UnityVersion),
                                            Install.ClassLibrarySources.CannotSupply(game, all, build?.Version, build?.Changeset),
                                            modules)
        {
            Build = build?.Version,
            Changeset = build?.Changeset,
        };
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyList<string>> MissingMemo = new();

    /// <summary>A remembered answer that may be "nothing" — a dictionary cannot hold a null value.</summary>
    private sealed record Holder(Model.EngineModuleNeed? Need);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Holder> ModulesMemo = new();

    /// <summary>The folder and the size and time of every library in it that <paramref name="counts"/>, and of one more file.</summary>
    private static string StampOf(string managed, Func<string, bool> counts, string? extra)
    {
        var parts = new List<string> { managed.ToLowerInvariant() };

        var files = Directory.EnumerateFiles(managed, "*.dll")
                             .Where(f => counts(Path.GetFileNameWithoutExtension(f)))
                             .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);

        foreach (var file in extra is null ? files : files.Append(extra))
        {
            var info = new FileInfo(file);
            parts.Add($"{info.FullName.ToLowerInvariant()}:{info.Length}:{info.LastWriteTimeUtc.Ticks}");
        }

        return string.Join("|", parts);
    }

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
