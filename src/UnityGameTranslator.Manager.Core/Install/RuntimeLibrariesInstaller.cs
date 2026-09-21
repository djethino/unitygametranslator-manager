using System.IO.Compression;
using System.Net;
using System.Text;
using UnityGameTranslator.Common;
using UnityGameTranslator.Manager.Core.Catalog;
using UnityGameTranslator.Manager.Core.Detection;
using UnityGameTranslator.Manager.Core.Model;
using UnityGameTranslator.Manager.Core.Net;

namespace UnityGameTranslator.Manager.Core.Install;

/// <summary>
/// Puts beside a game what it lacks for the mod — the .NET libraries, and the engine modules its
/// build stripped — tells its loader where they are, and takes it all back out.
///
/// Why, and how each batch is chosen: <see cref="RuntimeLibraries"/>, <see cref="EngineModules"/>,
/// <see cref="EngineModuleSources"/>. This class only acts — and holds every choice to the real
/// files before a byte is written into the game.
/// </summary>
public static class RuntimeLibrariesInstaller
{
    /// <summary>
    /// Where a game stands, reconciled from its files: what it lacks (read at scan), where the
    /// missing modules would come from, what our receipt says was added, and whether that still
    /// stands.
    /// </summary>
    /// <param name="games">The other games on this computer — possible sources of engine modules.</param>
    /// <param name="chosenModuleSource">The source a person chose for this game (<see cref="Settings.GamePreference.ModuleSource"/>).</param>
    public static RuntimeLibrariesState StateOf(GameInstall game, DetectedLoader? loader,
                                                IEnumerable<GameInstall> games, string? chosenModuleSource)
    {
        var need = game.RuntimeLibraries;
        var installed = ReceiptStore.Read(game.Path)?.RuntimeLibraries;

        var candidates = need?.Modules is { } modules
            ? EngineModuleSources.Find(game, modules, games)
            : Array.Empty<EngineModuleCandidate>();
        var source = EngineModuleSources.Choose(candidates, chosenModuleSource);

        RuntimeLibrariesState With(RuntimeLibrariesStatus status, string? detail = null) =>
            new(status, need, installed, detail)
            {
                ModuleSources = candidates,
                ModuleSource = source,
                ChosenSourceGone = chosenModuleSource is not null && candidates.Count > 0 && source?.Source.Id != chosenModuleSource,
            };

        if (installed is null) return need is null ? RuntimeLibrariesState.None : With(RuntimeLibrariesStatus.Missing);
        if (need is null) return With(RuntimeLibrariesStatus.NoLongerNeeded);

        if (need.Missing.Count > 0)
        {
            var archive = RuntimeLibraries.ArchiveName(game.UnityVersion);

            if (installed.Files.Count == 0)
                return With(RuntimeLibrariesStatus.Missing, "the .NET libraries were never added");

            if (!string.Equals(archive, installed.Unity, StringComparison.OrdinalIgnoreCase))
            {
                return With(RuntimeLibrariesStatus.WrongVersion,
                    $".NET libraries chosen for Unity {installed.Unity}, the game is now on {archive ?? "an unreadable version"}");
            }
        }

        if (need.Modules is { } lacking)
        {
            if (installed.Modules is not { } ours)
                return With(RuntimeLibrariesStatus.Missing, "the engine modules were never added");

            var now = UnityVersions.Parse(lacking.Build ?? game.UnityVersion);
            var then = UnityVersions.Parse(ours.GameUnity);
            if (now is null || then is null || !UnityVersions.SameRelease(now, then))
            {
                return With(RuntimeLibrariesStatus.WrongVersion,
                    $"engine modules chosen for Unity {ours.GameUnity}, the game is now on {now?.ToString() ?? "an unreadable version"}");
            }
        }

        var files = new FileOperations(game.Path);
        var absent = AllFiles(installed).Count(f => !files.TryResolveInsideGame(f.Path, out var path) || !File.Exists(path));
        if (absent > 0)
        {
            return With(RuntimeLibrariesStatus.Missing,
                $"{Composition.Amount(absent, "file is", "files are")} missing from {LoaderSearchPath.Folder}/");
        }

        // The loader is told, or the files sit there unread. A loader update rewrites this file.
        var setting = loader is null ? null : LoaderSearchPath.For(loader.Id, game.IsWindowsBuild);
        if (setting is null || !ListsOurs(game, setting, installed.ConfigEntry))
        {
            return With(RuntimeLibrariesStatus.Missing,
                loader is null ? "no mod loader is installed to read them" : $"{loader.Display} is not told where they are");
        }

        return With(RuntimeLibrariesStatus.InPlace);
    }

    /// <summary>Every file of ours, both batches.</summary>
    public static IReadOnlyList<ReceiptFile> AllFiles(ReceiptRuntimeLibraries installed) =>
        installed.Files.Concat(installed.Modules?.Files ?? Enumerable.Empty<ReceiptFile>()).ToList();

    private static bool ListsOurs(GameInstall game, LoaderSearchPath.Setting setting, string entry)
    {
        var files = new FileOperations(game.Path);
        if (!files.TryResolveInsideGame(setting.File, out var path) || !File.Exists(path)) return false;

        return LoaderSearchPath.Lists(File.ReadAllText(path), setting, entry);
    }

    // ── Adding them ──────────────────────────────────────────────────────────────────────────

    /// <summary>What the person agreed to, for this install: which source of engine modules, and whether it may be Unity's server.</summary>
    /// <param name="ModuleSource">
    /// The source the plan announced. The install uses THAT one or refuses — it never quietly
    /// switches to another, since the source is what was agreed to. Null only where nothing was
    /// announced (a loader put back over a game whose modules are already in place).
    /// </param>
    /// <param name="UnityDownloadAccepted">The person saw that Unity's server and terms are involved, and went on.</param>
    /// <param name="RestoreOnly">
    /// Nobody asked for these — a loader was put back, dropping our entry. What was there is put
    /// back; engine modules that were never added are not decided here.
    /// </param>
    public sealed record Agreement(EngineModuleSource? ModuleSource, bool UnityDownloadAccepted, bool RestoreOnly = false);

    /// <summary>
    /// Chooses, checks and writes what this game lacks. Returns what the receipt should record — the
    /// previous record when nothing had to change, null when nothing is needed.
    /// </summary>
    /// <param name="loader">The loader that will read them — installed now or already there.</param>
    /// <param name="loaderVersion">Its version, when known; a loader too old to honour the setting is refused.</param>
    /// <param name="previous">What an earlier install recorded, so files no longer needed can go.</param>
    public static async Task<ReceiptRuntimeLibraries?> ApplyAsync(
        GameInstall game, LoaderDescriptor loader, string? loaderVersion, FileOperations files,
        ReceiptRuntimeLibraries? previous, string staging, ArchiveCache cache, Agreement agreement,
        Action<string>? status, CancellationToken ct)
    {
        var setting = LoaderSearchPath.For(loader.Id, game.IsWindowsBuild)
            ?? throw new InvalidOperationException(
                $"{loader.Display} cannot be told where to find the libraries this game lacks.");

        if (setting.MinimumLoaderVersion is { } minimum
            && loaderVersion is { Length: > 0 }
            && Versions.Compare(loaderVersion, minimum) < 0)
        {
            throw new InvalidOperationException(
                $"{loader.Display} {loaderVersion} cannot be told where to find the libraries this game lacks: "
                + $"that needs {minimum} or later. Update the loader first.");
        }

        if (game.DataDirectory is null)
            throw new InvalidOperationException("The game's data folder was not found.");

        var managed = Path.Combine(game.DataDirectory, "Managed");

        // 🔴 **The plugin just put in place, not the list this tool shipped with.** The list is what
        // a game list can afford; the plugin is what will actually run. A newer mod needing one more
        // type is caught here even when this tool is a release behind it.
        var plugin = Path.Combine(game.Path, loader.PluginDir.Replace('/', Path.DirectorySeparatorChar),
                                  LocalTranslationProbe.PluginAssemblyName);

        var needs = File.Exists(plugin)
            ? AssemblyShape.Read(plugin).Uses
            : RuntimeLibraries.EmbeddedModNeeds;

        using var http = Http.Create(TimeSpan.FromMinutes(10));

        // ── Everything is chosen and checked first; nothing is written until both batches hold ──
        var classLibraries = await ChooseClassLibrariesAsync(game, managed, needs, staging, cache, status, ct).ConfigureAwait(false);
        var modules = await ChooseModulesAsync(game, managed, needs, agreement, previous?.Modules, http, staging, cache, status, ct)
                                .ConfigureAwait(false);

        if (classLibraries is null && modules is null) return previous;

        var before = files.WrittenFiles.Count;
        var dirsBefore = files.CreatedDirectories.Count;

        if (classLibraries is not null)
        {
            status?.Invoke($"Adding {Composition.Amount(classLibraries.Chosen.Count, ".NET library", ".NET libraries")}: "
                           + string.Join(", ", classLibraries.Chosen.Select(c => c.Name)) + "...");

            foreach (var (name, folder) in classLibraries.Chosen)
                files.PlaceFile(Path.Combine(folder, name + ".dll"), $"{LoaderSearchPath.Folder}/{name}.dll");
        }

        var classFiles = files.WrittenFiles.Skip(before).ToList();

        if (modules is { Kept: null })
        {
            status?.Invoke($"Adding Unity's {Composition.Amount(modules.Names.Count, "engine module", "engine modules")} "
                           + $"from {modules.Source!.Label}...");

            foreach (var name in modules.Names)
                files.PlaceFile(Path.Combine(modules.Folder!, name + ".dll"), $"{LoaderSearchPath.Folder}/{name}.dll");
        }

        // Kept as they were: the same files, the same record — nothing written, nothing to undo.
        var moduleFiles = modules?.Kept?.Files ?? files.WrittenFiles.Skip(before + classFiles.Count).ToList();
        var created = files.CreatedDirectories.Skip(dirsBefore).ToList();

        // ⚠ Through PlaceFile so a failure after this point puts the loader's file back — but NOT
        // into the receipt's list of files: it is the loader's, and removal edits it back.
        var configPath = files.ResolveInsideGame(setting.File);
        var (text, bom) = ReadText(configPath);
        var updated = LoaderSearchPath.Add(text, setting, LoaderSearchPath.Folder);

        if (!string.Equals(updated, text, StringComparison.Ordinal))
        {
            var temporary = Path.Combine(staging, "search-path.cfg");
            WriteText(temporary, updated, bom);
            files.PlaceFile(temporary, setting.File);
        }

        // Ours, from before, that this choice no longer includes — a game update needing fewer.
        if (previous is not null)
        {
            var written = classFiles.Concat(moduleFiles).ToList();
            RemoveFiles(files, AllFiles(previous).Where(
                old => !written.Any(w => string.Equals(w.Path, old.Path, StringComparison.OrdinalIgnoreCase))), null, null);
        }

        return new ReceiptRuntimeLibraries
        {
            Unity = classLibraries?.ArchiveName ?? "",
            Source = classLibraries?.Sources ?? "",
            Files = classFiles,
            Modules = modules is null ? null : modules.Kept ?? new ReceiptEngineModules
            {
                Unity = modules.Source!.Version.ToString(),
                GameUnity = modules.GameRelease,
                SourceKind = modules.Source.Kind switch
                {
                    EngineModuleSourceKind.Editor => "editor",
                    EngineModuleSourceKind.Game => "game",
                    _ => "unity",
                },
                SourceId = modules.Source.Id,
                Source = modules.Source.Label,
                Files = moduleFiles,
            },
            DirsCreated = created.Union(previous?.DirsCreated ?? Enumerable.Empty<string>(),
                                        StringComparer.OrdinalIgnoreCase).ToList(),
            ConfigFile = setting.File,
            ConfigEntry = LoaderSearchPath.Folder,
            ConfigCreated = text is null || previous?.ConfigCreated == true,
        };
    }

    // ── The .NET batch ───────────────────────────────────────────────────────────────────────

    private sealed record ClassLibraryChoice(IReadOnlyList<(string Name, string Folder)> Chosen, string ArchiveName, string Sources);

    /// <summary>
    /// The smallest set of .NET libraries that makes the mod's references resolve, and the folder
    /// each copy comes from. Null when the game lacks none.
    ///
    /// ⚠ **File by file between two sources** (user's decision, 2026-09-21): BepInEx's archive of
    /// the game's exact Unity version first; our lot of the Windows build for the few files the
    /// archive carries only in their Linux build. Never a copy from another game: .NET libraries are
    /// not signed, so nothing could tell a genuine one from a planted one.
    /// </summary>
    private static async Task<ClassLibraryChoice?> ChooseClassLibrariesAsync(
        GameInstall game, string managed, IReadOnlyList<TypeUse> needs, string staging, ArchiveCache cache,
        Action<string>? status, CancellationToken ct)
    {
        var gameLibraries = RuntimeLibraries.Folder(managed);

        // Nothing to fetch when the game already holds everything — known from its own files.
        if (RuntimeLibraries.Missing(needs, new RuntimeLibraries.Layers(gameLibraries)).Count == 0
            && game.RuntimeLibraries?.LoaderCannotStart != true)
            return null;

        var archiveName = RuntimeLibraries.ArchiveName(game.UnityVersion)
            ?? throw new InvalidOperationException(
                "The game's Unity version could not be read, so no matching .NET libraries can be chosen.");

        var lot = game.IsWindowsBuild && RuntimeLibraries.PerPlatform(archiveName) ? ClassLibraryLots.For(game) : null;

        var url = RuntimeLibraryOrigins.ClassLibrariesUrl(archiveName);
        status?.Invoke($"Downloading the .NET libraries for Unity {archiveName} from {RuntimeLibraryOrigins.ClassLibrariesHost}...");

        string? archiveFolder = null;
        try
        {
            // No checksum is published for these. What makes them acceptable is checked below, on
            // the files themselves, against the game's own.
            archiveFolder = (await new ArchiveFetcher(Path.Combine(staging, "corlibs"), cache: cache)
                .FetchAsync(url, null, "corlibs",
                            // One entry per Unity version: a machine with games on two versions
                            // would otherwise download them in turn, for ever.
                            new ArchiveCacheKey($"corlibs-{archiveName}", archiveName), null, ct)
                .ConfigureAwait(false)).ExtractedPath;
        }
        catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.NotFound && lot is not null)
        {
            // Our lot alone may do: it carries every library of its generation.
        }
        catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException(
                $"No copy of Unity {archiveName}'s .NET libraries is published, so the ones this game lacks cannot be added.");
        }

        string? lotFolder = null;
        if (lot is not null)
        {
            status?.Invoke($"Downloading the Windows build of the .NET libraries (Unity {lot.TakenFrom})...");
            lotFolder = (await new ArchiveFetcher(Path.Combine(staging, "class-libraries"), cache: cache)
                .FetchAsync(lot.Url, lot.Sha256, "class-libraries",
                            new ArchiveCacheKey($"class-libraries-{lot.CorlibVersion}", lot.Sha256), lot.Size, ct)
                .ConfigureAwait(false)).ExtractedPath;

            // The checksum said these are the bytes published; this says they are the generation
            // they were published for — the one the game's engine will check.
            var lotCorlib = Path.Combine(lotFolder, "mscorlib.dll");
            if (!File.Exists(lotCorlib)
                || !string.Equals(ClassLibraryLots.CorlibVersionOf(lotCorlib), lot.CorlibVersion, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The Windows .NET libraries downloaded are not the generation they are listed for. Nothing was added.");
            }
        }

        var fromArchive = archiveFolder is null ? (_ => null) : RuntimeLibraries.Folder(archiveFolder);
        var fromLot = lotFolder is null ? (_ => null) : RuntimeLibraries.Folder(lotFolder);

        // The archive's copy, unless it is a Linux build (or absent) and the lot has one.
        bool TakesLot(string name) =>
            lotFolder is not null && fromLot(name) is not null
            && (fromArchive(name) is not { } shape || (game.IsWindowsBuild && shape.NativeImports.Any(RuntimeLibraries.IsUnixShim)));

        AssemblyShape? Source(string name) => TakesLot(name) ? fromLot(name) : fromArchive(name);

        var selection = RuntimeLibraries.Select(needs, gameLibraries, Source);
        if (!selection.Complete)
        {
            var lacking = selection.Unresolved.Select(s => s.Use.Member is null ? s.Use.Type : $"{s.Use.Type}.{s.Use.Member}")
                                              .Distinct().Take(3);
            throw new InvalidOperationException(
                $"Even with the .NET libraries published for Unity {archiveName}, the mod would lack "
                + $"{string.Join(", ", lacking)}. Nothing was added.");
        }

        // The loader needs mscorlib even when the mod does not ask for it by name.
        var chosen = selection.Chosen.ToList();
        if (game.RuntimeLibraries?.LoaderCannotStart == true && !chosen.Contains("mscorlib", StringComparer.OrdinalIgnoreCase)
            && Source("mscorlib") is not null)
            chosen.Add("mscorlib");

        if (game.IsWindowsBuild && RuntimeLibraries.UnixOnly(chosen, Source) is { Count: > 0 } linux)
        {
            throw new InvalidOperationException(
                $"For Unity {archiveName}, the only copy of {string.Join(", ", linux)} published is built for Linux, "
                + "and this game is built for Windows. Nothing was added.");
        }

        if (RuntimeLibraries.NotSameFamily(chosen, gameLibraries, Source) is { Count: > 0 } alien)
        {
            throw new InvalidOperationException(
                $"The .NET libraries published for Unity {archiveName} do not match this game's own "
                + $"({string.Join("; ", alien)}). Nothing was added.");
        }

        if (chosen.Count == 0) return null;

        var sources = new List<string>();
        if (chosen.Any(n => !TakesLot(n))) sources.Add(url);
        if (chosen.Any(TakesLot)) sources.Add(lot!.Url);

        return new ClassLibraryChoice(chosen.Select(n => (n, TakesLot(n) ? lotFolder! : archiveFolder!)).ToList(),
                                      archiveName, string.Join(" + ", sources));
    }

    // ── The engine-module batch ──────────────────────────────────────────────────────────────

    /// <param name="Kept">The modules already in place, left exactly as they are — null when a set is written.</param>
    private sealed record ModuleChoice(EngineModuleSource? Source, string? Folder, IReadOnlyList<string> Names,
                                       string GameRelease, ReceiptEngineModules? Kept = null);

    /// <summary>
    /// The complete set of engine modules this game ships, from the source the person agreed to,
    /// held to every rule. Null when the game's build stripped none of what the mod calls.
    /// </summary>
    private static async Task<ModuleChoice?> ChooseModulesAsync(
        GameInstall game, string managed, IReadOnlyList<TypeUse> needs, Agreement agreement, ReceiptEngineModules? previous,
        HttpClient http, string staging, ArchiveCache cache, Action<string>? status, CancellationToken ct)
    {
        if (EngineModules.NeedOf(game, needs) is not { } need) return null;

        if (need.CannotSupply is not null)
            throw new InvalidOperationException($"This game's engine modules cannot be replaced: {need.CannotSupply}. Nothing was added.");

        var gameRelease = need.Build ?? game.UnityVersion ?? "";
        var gamePlayer = EngineModules.PlayerBinary(game.Path, game.ExecutablePath);

        // 🔴 **What is in place and intact stays, unless another source was asked for.** A loader put
        // back drops our entry from its configuration, and the install that restores it must not
        // fetch the modules again — from Unity that would need an agreement nobody is being asked
        // for, over files that never moved.
        if (previous is not null
            && (agreement.ModuleSource is null || agreement.ModuleSource.Id == previous.SourceId)
            && UnityVersions.Parse(previous.GameUnity) is { } then && UnityVersions.Parse(gameRelease) is { } now
            && UnityVersions.SameRelease(then, now)
            && Intact(game, previous.Files))
        {
            return new ModuleChoice(null, null, need.Set, gameRelease, previous);
        }

        if (agreement.RestoreOnly) return null;

        var source = agreement.ModuleSource
            ?? throw new InvalidOperationException(
                "No source was chosen for this game's engine modules. Choose one in its Compatibility card. Nothing was added.");

        if (source.Kind != EngineModuleSourceKind.UnityDownload)
        {
            // Checked again now, not trusted from the report: the files may have changed since.
            var problems = EngineModuleSources.VerifyNow(need, source.Managed!, source.Player, managed, gamePlayer, source.Label,
                                                         fromUnityServer: false);
            if (problems.Count > 0) throw new InvalidOperationException($"{problems[0]}. Nothing was added.");

            return new ModuleChoice(source, source.Managed!, need.Set, gameRelease);
        }

        if (!agreement.UnityDownloadAccepted)
        {
            throw new InvalidOperationException(
                $"This game's engine modules would be downloaded from Unity ({RuntimeLibraryOrigins.UnityDownloadHost}), "
                + "which was not agreed to. Nothing was added.");
        }

        if (need.Build is null || need.Changeset is null)
            throw new InvalidOperationException("This game's Unity build could not be identified, so Unity's download cannot be found. Nothing was added.");

        var platform = EngineModules.PlatformOf(game)
            ?? throw new InvalidOperationException("This game is not built for Windows or Linux. Nothing was added.");

        var folder = await DownloadModulesAsync(need, platform, http, staging, cache, status, ct).ConfigureAwait(false);

        var downloaded = EngineModuleSources.VerifyNow(need, folder, null, managed, gamePlayer, source.Label, fromUnityServer: true);
        if (downloaded.Count > 0) throw new InvalidOperationException($"{downloaded[0]}. Nothing was added.");

        return new ModuleChoice(source, folder, need.Set, gameRelease);
    }

    /// <summary>Every recorded file present and still the bytes we wrote.</summary>
    private static bool Intact(GameInstall game, IEnumerable<ReceiptFile> recorded)
    {
        var files = new FileOperations(game.Path);

        return recorded.All(f => files.TryResolveInsideGame(f.Path, out var path) && File.Exists(path)
                                 && string.Equals(FileOperations.HashFile(path), f.Sha256, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Unity's engine modules for this build, from Unity's server: its index for the build, then the
    /// start of the platform's Build Support package, read until the modules have gone by.
    ///
    /// ⚠ Kept in the archive cache as a zip of the modules, keyed on the platform and the build: a
    /// second game on the same release reads them from disk. They are checked again on the way out,
    /// like everything the cache hands back, and signature-checked again by the caller.
    /// </summary>
    private static async Task<string> DownloadModulesAsync(EngineModuleNeed need, EngineModules.Platform platform, HttpClient http,
                                                           string staging, ArchiveCache cache, Action<string>? status,
                                                           CancellationToken ct)
    {
        var build = need.Build!;
        var folder = Path.Combine(staging, "engine-modules");
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        Directory.CreateDirectory(folder);

        var key = new ArchiveCacheKey($"unity-engine-modules-{platform.ToString().ToLowerInvariant()}-{build}", build);
        if (cache.TryPath(key, null, ".zip") is { } cached)
        {
            ZipFile.ExtractToDirectory(cached, folder);
            return folder;
        }

        status?.Invoke($"Reading Unity's list of downloads for {build}...");

        string index;
        await using (var stream = await Download.OpenAsync(http, RuntimeLibraryOrigins.UnityBuildIndexUrl(build, need.Changeset!), null, null, ct)
                                                .ConfigureAwait(false))
        using (var reader = new StreamReader(stream))
        {
            index = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        }

        var package = EngineModules.PackageSections(platform).Select(section => UnityBuildIndex.Section(index, section))
                                                             .FirstOrDefault(p => p is not null)
            ?? throw new InvalidOperationException($"Unity does not list a {platform} Build Support package for {build}. Nothing was added.");

        status?.Invoke($"Downloading Unity's engine modules for {build} from {RuntimeLibraryOrigins.UnityDownloadHost}...");

        await using (var stream = await Download.OpenAsync(http, RuntimeLibraryOrigins.UnityBuildFileUrl(need.Changeset!, package.Url),
                                                            package.Size, null, ct).ConfigureAwait(false))
        {
            // The reads are synchronous inside; the stream is a network one, so off this thread.
            await Task.Run(() => UnityPackage.ExtractEngineModules(stream, folder), ct).ConfigureAwait(false);
        }

        var zip = Path.Combine(staging, "engine-modules.zip");
        if (File.Exists(zip)) File.Delete(zip);
        ZipFile.CreateFromDirectory(folder, zip);
        cache.Store(key, zip, FileOperations.HashFile(zip), ".zip");

        return folder;
    }

    // ── Taking them back out ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Takes our entry back out of the loader's configuration and removes our libraries and
    /// modules — each only while it is still the file we wrote.
    /// </summary>
    public static void Remove(GameInstall game, ReceiptRuntimeLibraries recorded, List<string> removed, List<string> kept)
    {
        var files = new FileOperations(game.Path);

        // The configuration first: a folder of libraries nothing points at is inert, the reverse is
        // a loader told to search a folder that is gone.
        var setting = LoaderSearchPath.ForFile(recorded.ConfigFile, game.IsWindowsBuild);

        if (setting is not null && files.TryResolveInsideGame(recorded.ConfigFile, out var configPath) && File.Exists(configPath))
        {
            try
            {
                var (text, bom) = ReadText(configPath);
                var restored = LoaderSearchPath.Remove(text!, setting, recorded.ConfigEntry);

                // Created by us and still holding nothing but our key: the loader never wrote it, so it
                // goes the way it came.
                if (recorded.ConfigCreated
                    && string.Equals(text, LoaderSearchPath.Add(null, setting, recorded.ConfigEntry), StringComparison.Ordinal))
                {
                    File.Delete(configPath);
                    removed.Add(recorded.ConfigFile);
                }
                else if (!string.Equals(restored, text, StringComparison.Ordinal))
                {
                    WriteText(configPath, restored, bom);
                    removed.Add($"{recorded.ConfigFile} ({recorded.ConfigEntry} entry)");
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                kept.Add($"{recorded.ConfigFile} ({e.GetType().Name}) — remove {recorded.ConfigEntry} from it by hand");
            }
        }

        RemoveFiles(files, AllFiles(recorded), removed, kept);

        foreach (var dir in Enumerable.Reverse(recorded.DirsCreated))
        {
            if (files.TryResolveInsideGame(dir, out var path) && FileOperations.TryRemoveEmptyDirectory(path))
                removed.Add(dir + "/");
        }
    }

    /// <summary>Each file only while it is still what we wrote; a changed one is left and said.</summary>
    private static void RemoveFiles(FileOperations files, IEnumerable<ReceiptFile> recorded,
                                    List<string>? removed, List<string>? kept)
    {
        foreach (var file in recorded)
        {
            if (!files.TryResolveInsideGame(file.Path, out var path) || !File.Exists(path)) continue;

            try
            {
                if (!string.Equals(FileOperations.HashFile(path), file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    kept?.Add($"{file.Path} (changed since it was added — left untouched)");
                    continue;
                }

                File.Delete(path);
                removed?.Add(file.Path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                kept?.Add($"{file.Path} ({e.GetType().Name})");
            }
        }
    }

    /// <summary>
    /// A configuration file's text, and whether it began with a byte-order mark — kept so that
    /// taking our entry back out gives the loader its file back byte for byte.
    /// </summary>
    private static (string? Text, bool Bom) ReadText(string path)
    {
        if (!File.Exists(path)) return (null, false);

        var bytes = File.ReadAllBytes(path);
        var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;

        return (new UTF8Encoding(false).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0)), bom);
    }

    private static void WriteText(string path, string text, bool bom) =>
        File.WriteAllText(path, text, new UTF8Encoding(bom));
}
