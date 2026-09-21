using UnityGameTranslator.Manager.Core.Detection;
using UnityGameTranslator.Manager.Core.Install;

namespace UnityGameTranslator.Manager.Core.Checks;

/// <summary>
/// Which .NET libraries a game lacks for the mod, which copies may be put beside it, and the edits
/// that tell a loader where to find them.
///
/// ⚠ The assemblies here are built by hand, so each case says exactly what it tests. The same
/// rules were run on real games before these cases were written (2026-09-21): seven games where the
/// mod works answered "nothing missing", a game lacking netstandard got the four libraries later
/// proven in play, and a game from 2021.3 was refused on the Linux-only build of its archive.
/// </summary>
internal static class RuntimeLibrariesChecks
{
    private static TypeShape Type(params string[] members) =>
        new(new HashSet<string>(members, StringComparer.Ordinal), null);

    private static TypeShape Derived(TypeName baseName, params string[] members) =>
        new(new HashSet<string>(members, StringComparer.Ordinal), baseName);

    private static AssemblyShape Assembly(string name, Dictionary<string, TypeShape> types,
                                          Dictionary<string, string>? forwards = null,
                                          string[]? natives = null, TypeUse[]? uses = null) =>
        new(name, types, forwards, natives, uses);

    private static Func<string, AssemblyShape?> Set(params AssemblyShape[] shapes) =>
        name => shapes.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public static void WhatAGameLacks()
    {
        Program.Section("Runtime libraries: what a game lacks for the mod");

        var mscorlib = Assembly("mscorlib", new()
        {
            ["System.Object"] = Type(".ctor", "ToString"),
            ["System.Collections.Generic.List`1"] = Type(".ctor", "Add"),
        });
        var system = Assembly("System", new()
        {
            ["System.Collections.Generic.Queue`1"] = Type(".ctor", "Enqueue"),
            ["System.Uri"] = Derived(new TypeName("mscorlib", "System.Object"), ".ctor"),
        });
        var strippedSystem = Assembly("System", new()
        {
            ["System.Uri"] = Derived(new TypeName("mscorlib", "System.Object"), ".ctor"),
        });
        var facade = Assembly("netstandard", new(), new Dictionary<string, string>
        {
            ["System.Collections.Generic.List`1"] = "mscorlib",
            ["System.Collections.Generic.Queue`1"] = "System",
            ["System.Net.Http.HttpClient"] = "System.Net.Http",
        });

        var needs = new[]
        {
            new TypeUse("netstandard", "System.Collections.Generic.List`1", "Add"),
            new TypeUse("netstandard", "System.Collections.Generic.Queue`1", "Enqueue"),
            new TypeUse("System", "System.Uri", "ToString"),
            new TypeUse("UnityEngine.CoreModule", "UnityEngine.GameObject", "Find"),
        };

        var complete = new RuntimeLibraries.Layers(Set(mscorlib, system, facade,
            Assembly("System.Net.Http", new() { ["System.Net.Http.HttpClient"] = Type(".ctor") })));

        Program.Check(RuntimeLibraries.Missing(needs, complete).Count == 0,
            "a game with everything: nothing missing",
            "and UnityEngine is never counted — it is not a class library");

        Program.Check(RuntimeLibraries.Missing(needs, new RuntimeLibraries.Layers(Set(mscorlib, system)))
                          .SequenceEqual(new[] { "netstandard" }),
            "no netstandard: netstandard, and nothing behind it yet",
            "what stops first is what the list names; the install finds the rest");

        Program.Check(RuntimeLibraries.Missing(needs, new RuntimeLibraries.Layers(Set(mscorlib, strippedSystem, facade)))
                          .SequenceEqual(new[] { "System" }),
            "Queue<T> stripped out of System: System, not netstandard",
            "the facade only forwards — the library to replace is the one that failed to answer");

        Program.Check(RuntimeLibraries.Resolve(new TypeUse("System", "System.Uri", "ToString"), complete) is null,
            "a member found on the type it derives from", "ToString is Object's, asked of Uri");

        Program.Check(RuntimeLibraries.Resolve(new TypeUse("System", "System.Uri", "Nope"), complete)
                          is { Kind: RuntimeLibraries.StopKind.MissingMember, Assembly: "System" },
            "a member found nowhere stops at the type's own library", "");
    }

    public static void WhatGoesBesideAGame()
    {
        Program.Section("Runtime libraries: the smallest set that holds");

        var gameCorlib = Assembly("mscorlib", new()
        {
            ["System.Object"] = Type(".ctor"),
            ["System.String"] = Derived(new TypeName(null, "System.Object"), "Concat"),
        });

        var archiveCorlib = Assembly("mscorlib", new()
        {
            ["System.Object"] = Type(".ctor"),
            ["System.String"] = Derived(new TypeName(null, "System.Object"), "Concat", "Join"),
        });

        var archiveFacade = Assembly("netstandard", new(), new Dictionary<string, string>
        {
            ["System.String"] = "mscorlib",
            ["System.Net.Http.HttpClient"] = "System.Net.Http",
        });

        // A complete System.Net.Http that asks mscorlib for something the game's stripped copy lacks.
        var archiveHttp = Assembly("System.Net.Http",
            new() { ["System.Net.Http.HttpClient"] = Type(".ctor") },
            uses: new[] { new TypeUse("mscorlib", "System.String", "Join") });

        // And a library whose own references Unity never satisfies — its archive lacks them too.
        var archiveSerialization = Assembly("System.Runtime.Serialization",
            new() { ["System.Runtime.Serialization.DataContractAttribute"] = Type(".ctor") },
            uses: new[] { new TypeUse("System.ServiceModel.Internals", "System.Runtime.Fx", "Assert") });

        var archive = Set(archiveCorlib, archiveFacade, archiveHttp, archiveSerialization);
        var game = Set(gameCorlib);

        var needs = new[]
        {
            new TypeUse("netstandard", "System.String", "Concat"),
            new TypeUse("netstandard", "System.Net.Http.HttpClient", ".ctor"),
        };

        var selection = RuntimeLibraries.Select(needs, game, archive);
        Program.Check(selection.Complete && selection.Chosen.SequenceEqual(new[] { "mscorlib", "netstandard", "System.Net.Http" }),
            "grown until the ADDED libraries resolve too",
            "HttpClient needs String.Join, which only a complete mscorlib has");

        var withSerialization = RuntimeLibraries.Select(
            needs.Append(new TypeUse("System.Runtime.Serialization", "System.Runtime.Serialization.DataContractAttribute", ".ctor")).ToList(),
            game, archive);
        Program.Check(withSerialization.Complete && withSerialization.Chosen.Contains("System.Runtime.Serialization"),
            "a reference Unity itself never satisfies is not held against it",
            "the archive's own library names an assembly the archive does not carry");

        var impossible = RuntimeLibraries.Select(new[] { new TypeUse("netstandard", "System.Nothing") }, game, archive);
        Program.Check(!impossible.Complete,
            "a need no copy can meet is reported, not dropped", "the install then refuses rather than half-works");

        Program.Check(RuntimeLibraries.Select(new[] { new TypeUse("mscorlib", "System.Object", ".ctor") }, game, archive)
                          .Chosen.Count == 0,
            "nothing lacking: nothing chosen", "a game is only changed where it has to be");
    }

    public static void WhichCopiesMayBeUsed()
    {
        Program.Section("Runtime libraries: which copies may be put beside a game");

        var linuxSystem = Assembly("System", new() { ["System.Uri"] = Type() }, natives: new[] { "System.Native", "kernel32.dll" });
        var neutralHttp = Assembly("System.Net.Http", new() { ["System.Net.Http.HttpClient"] = Type() });
        var security = Assembly("System.Security", new() { ["X"] = Type() }, natives: new[] { "System.Net.Security.Native" });
        var archive = Set(linuxSystem, neutralHttp, security);

        Program.Check(RuntimeLibraries.UnixOnly(new[] { "System", "System.Net.Http", "System.Security" }, archive)
                          .SequenceEqual(new[] { "System", "System.Security" }),
            "a library calling System.Native, or System.*.Native, is Linux-only",
            "measured: no Windows build of a game carries one");

        var game = Set(Assembly("System", new() { ["System.Uri"] = Type(), ["Interop+Kernel32"] = Type() }));
        Program.Check(RuntimeLibraries.NotSameFamily(new[] { "System" }, game, archive).Count == 1,
            "a game type the copy lacks: not the same family", "a Windows build compared with a Linux one, measured");

        var generated = Set(Assembly("System", new() { ["System.Uri"] = Type(), ["<PrivateImplementationDetails>"] = Type() }));
        Program.Check(RuntimeLibraries.NotSameFamily(new[] { "System" }, generated, archive).Count == 0,
            "compiler-generated names are not held against a copy", "they are produced per build");

        Program.Check(RuntimeLibraries.NotSameFamily(new[] { "System.Net.Http" }, game, archive).Count == 0,
            "a library the game lacks cannot be compared, and is not refused for it", "");
    }

    public static void WhichArchiveServesAGame()
    {
        Program.Section("Runtime libraries: which archive, and whether it can serve");

        Program.Check(RuntimeLibraries.ArchiveName("2018.4.36f1") == "2018.4.36", "a final release drops its suffix", "the archive files it that way");
        Program.Check(RuntimeLibraries.ArchiveName("2021.2.0a10") == "2021.2.0a10", "an alpha keeps it", "so does the archive");
        Program.Check(RuntimeLibraries.ArchiveName("2019.4.40p1") == "2019.4.40", "a patch release uses its base", "");
        Program.Check(RuntimeLibraries.ArchiveName(null) is null && RuntimeLibraries.ArchiveName("banana") is null,
            "an unreadable version names no archive", "nothing is guessed");

        Program.Check(!RuntimeLibraries.PerPlatform("2021.1.28") && RuntimeLibraries.PerPlatform("2021.2.0")
                      && RuntimeLibraries.PerPlatform("6000.0.50f1"),
            "per-platform from 2021.2 on", "measured on seven versions");

        Program.Check(RuntimeLibraries.CannotSupply(new[] { "netstandard", "System.Net.Http" }, "2021.3.14f1", windowsBuild: true) is null,
            "2021.3, Windows, only neutral libraries: can be supplied", "the case a Linux-only archive still serves");
        Program.Check(RuntimeLibraries.CannotSupply(new[] { "netstandard", "System" }, "2021.3.6f1", windowsBuild: true) is { } refused
                      && refused.Contains("System"),
            "2021.3, Windows, needing System: cannot, and says which", "the refusal kept for such a game");
        Program.Check(RuntimeLibraries.CannotSupply(new[] { "System" }, "2018.4.36f1", windowsBuild: true) is null,
            "before 2021.2 the one build serves Windows", "");
        Program.Check(RuntimeLibraries.CannotSupply(new[] { "System" }, "2021.3.6f1", windowsBuild: false) is null,
            "a Linux build of the game takes the Linux copy", "");
        Program.Check(RuntimeLibraries.CannotSupply(new[] { "System" }, null, windowsBuild: true) is not null,
            "no Unity version: cannot", "no copy can be chosen");
    }

    public static void HowALoaderIsTold()
    {
        Program.Section("Runtime libraries: the loader's search-path setting");

        var doorstop = LoaderSearchPath.For("bepinex5", windowsBuild: true)!;
        var melon = LoaderSearchPath.For("melonloader", windowsBuild: true)!;
        var entry = LoaderSearchPath.Folder;

        const string emptyIni = "[General]\nenabled = true\n\n[UnityMono]\n# comment\ndll_search_path_override =\ndebug_enabled = false\n";
        var added = LoaderSearchPath.Add(emptyIni, doorstop, entry);
        Program.Check(added.Contains($"dll_search_path_override = {entry}\n") && added.Contains("debug_enabled = false"),
            "an empty setting takes our folder, the rest untouched", "BepInEx 5 ships it empty");
        Program.Check(LoaderSearchPath.Remove(added, doorstop, entry) == emptyIni,
            "and removing it gives the file back as it was", "byte for byte");

        const string bepinex6 = "[UnityMono]\r\ndll_search_path_override = \"BepInEx\\core\"\r\n";
        var alongside = LoaderSearchPath.Add(bepinex6, doorstop, entry);
        Program.Check(alongside.Contains($"dll_search_path_override = \"{entry};BepInEx\\core\"\r\n"),
            "an existing value is kept, ours first, quotes and CRLF kept",
            "BepInEx 6 already lists its own folder there");
        Program.Check(LoaderSearchPath.Entries(alongside, doorstop).SequenceEqual(new[] { entry, "BepInEx\\core" }),
            "both entries read back in order", "");
        Program.Check(LoaderSearchPath.Add(alongside, doorstop, entry.ToUpperInvariant() + "/") == alongside,
            "adding it twice changes nothing", "compared as a path");
        Program.Check(LoaderSearchPath.Remove(alongside, doorstop, entry) == bepinex6,
            "removing ours leaves theirs exactly", "");

        Program.Check(LoaderSearchPath.Add(null, melon, entry) == $"[unityengine]\nmono_search_path_override = \"{entry}\"\n",
            "MelonLoader before its first launch: a file of our one key",
            "it reads a partial file, completes it and writes it back whole");

        const string loaderCfg = "[loader]\ndebug_mode = false\n\n[unityengine]\nversion_override = \"\"\n";
        var inserted = LoaderSearchPath.Add(loaderCfg, melon, entry);
        Program.Check(inserted.Contains($"[unityengine]\nmono_search_path_override = \"{entry}\"\nversion_override"),
            "a section without the key gets it, quoted as TOML wants", "");
        Program.Check(LoaderSearchPath.Remove(inserted, melon, entry).Contains("mono_search_path_override = \"\""),
            "removed from TOML: an empty string, still valid", "");

        Program.Check(LoaderSearchPath.For("bepinex6-il2cpp", true) is null,
            "IL2CPP: no class library to complete", "");
        Program.Check(melon.MinimumLoaderVersion == "0.7.1" && LoaderSearchPath.For("melonloader", false)!.Separator == ':',
            "MelonLoader: from 0.7.1, separated as the system separates", "read in its sources");
    }

    /// <summary>
    /// 🔴 A sequence, not a rule: what an install left in place is undone by things that happen
    /// AFTER it — a loader update, a game update — and only reading the files again notices.
    /// </summary>
    public static void WhatStillStandsAfterwards()
    {
        Program.Section("Runtime libraries: what still stands afterwards, on real files");

        var root = Path.Combine(Path.GetTempPath(), "ugt-runtime-libraries-" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(Path.Combine(root, LoaderSearchPath.Folder));

            const string shipped = "[UnityMono]\r\n# comment\r\ndll_search_path_override =\r\ndebug_enabled = false\r\n";
            var configPath = Path.Combine(root, "doorstop_config.ini");
            var setting = LoaderSearchPath.For("bepinex5", windowsBuild: true)!;

            File.WriteAllText(configPath, LoaderSearchPath.Add(shipped, setting, LoaderSearchPath.Folder));
            File.WriteAllText(Path.Combine(root, "Game.exe"), "");

            var library = Path.Combine(root, LoaderSearchPath.Folder, "netstandard.dll");
            File.WriteAllText(library, "not really a library");

            var recorded = new Model.ReceiptRuntimeLibraries
            {
                Unity = "2018.4.36",
                Files = { new Model.ReceiptFile { Path = $"{LoaderSearchPath.Folder}/netstandard.dll", Sha256 = FileOperations.HashFile(library) } },
                DirsCreated = { LoaderSearchPath.Folder },
                ConfigFile = "doorstop_config.ini",
                ConfigEntry = LoaderSearchPath.Folder,
            };
            ReceiptStore.Write(root, new Model.Receipt { RuntimeLibraries = recorded });

            var game = new Model.GameInstall
            {
                Name = "test", Path = root, ExecutablePath = Path.Combine(root, "Game.exe"),
                Runtime = Model.UnityRuntime.Mono, UnityVersion = "2018.4.36f1",
                RuntimeLibraries = new Model.RuntimeLibraryNeed(new[] { "netstandard" }, false, "2018.4.36", null),
            };
            var loader = new Model.DetectedLoader { Id = "bepinex5", Display = "BepInEx 5", PluginDir = "BepInEx/plugins" };

            Program.Check(RuntimeLibrariesInstaller.StateOf(game, loader).Status == Model.RuntimeLibrariesStatus.InPlace,
                "added, and the loader told: in place", "");

            File.WriteAllText(configPath, shipped);
            var afterLoaderUpdate = RuntimeLibrariesInstaller.StateOf(game, loader);
            Program.Check(afterLoaderUpdate is { Status: Model.RuntimeLibrariesStatus.Missing, BlocksTheMod: true, WriteOffered: true },
                "the loader rewrote its configuration: missing again, and offered",
                "a loader update drops our entry without a word");

            File.WriteAllText(configPath, LoaderSearchPath.Add(shipped, setting, LoaderSearchPath.Folder));
            game.UnityVersion = "2019.4.1f1";
            Program.Check(RuntimeLibrariesInstaller.StateOf(game, loader).Status == Model.RuntimeLibrariesStatus.WrongVersion,
                "the game moved to another Unity: chosen for the wrong one", "a game update");
            game.UnityVersion = "2018.4.36f1";

            Program.Check(RuntimeLibrariesInstaller.StateOf(game, null).Status == Model.RuntimeLibrariesStatus.Missing,
                "no loader left to read them: missing", "");

            var removed = new List<string>();
            var kept = new List<string>();
            RuntimeLibrariesInstaller.Remove(game, recorded, removed, kept);

            Program.Check(File.ReadAllText(configPath) == shipped,
                "removed: the loader's configuration is back byte for byte",
                "so the loader's own receipt still recognises it as its own to remove");
            Program.Check(!File.Exists(library) && !Directory.Exists(Path.Combine(root, LoaderSearchPath.Folder)) && kept.Count == 0,
                "and our folder is gone", "");

            // A library somebody replaced by hand is theirs now.
            Directory.CreateDirectory(Path.Combine(root, LoaderSearchPath.Folder));
            File.WriteAllText(library, "somebody else's build");
            var keptAgain = new List<string>();
            RuntimeLibrariesInstaller.Remove(game, recorded, new List<string>(), keptAgain);
            Program.Check(File.Exists(library) && keptAgain.Count == 1,
                "a library changed since it was added is left, and said", "the rule every removal here follows");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* a temp folder left behind proves nothing */ }
        }
    }
}
