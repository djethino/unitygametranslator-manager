using System.Net;
using System.Text;
using UnityGameTranslator.Common;
using UnityGameTranslator.Manager.Core.Catalog;
using UnityGameTranslator.Manager.Core.Detection;
using UnityGameTranslator.Manager.Core.Model;

namespace UnityGameTranslator.Manager.Core.Install;

/// <summary>
/// Puts beside a game the .NET libraries it lacks for the mod, tells its loader where they are,
/// and takes both back out.
///
/// Why, and how the libraries are chosen: <see cref="RuntimeLibraries"/>. This class only acts —
/// and holds every choice to the real files before a byte is written into the game.
/// </summary>
public static class RuntimeLibrariesInstaller
{
    /// <summary>
    /// Where a game stands, reconciled from its files: what it lacks (read at scan), what our
    /// receipt says was added, and whether that still stands.
    /// </summary>
    public static RuntimeLibrariesState StateOf(GameInstall game, DetectedLoader? loader)
    {
        var need = game.RuntimeLibraries;
        var installed = ReceiptStore.Read(game.Path)?.RuntimeLibraries;

        if (installed is null)
        {
            return need is null
                ? RuntimeLibrariesState.None
                : new RuntimeLibrariesState(RuntimeLibrariesStatus.Missing, need, null);
        }

        if (need is null) return new RuntimeLibrariesState(RuntimeLibrariesStatus.NoLongerNeeded, null, installed);

        var archive = RuntimeLibraries.ArchiveName(game.UnityVersion);
        if (!string.Equals(archive, installed.Unity, StringComparison.OrdinalIgnoreCase))
        {
            return new RuntimeLibrariesState(RuntimeLibrariesStatus.WrongVersion, need, installed,
                $"chosen for Unity {installed.Unity}, the game is now on {archive ?? "an unreadable version"}");
        }

        var files = new FileOperations(game.Path);
        var absent = installed.Files.Count(f => !files.TryResolveInsideGame(f.Path, out var path) || !File.Exists(path));
        if (absent > 0)
        {
            return new RuntimeLibrariesState(RuntimeLibrariesStatus.Missing, need, installed,
                $"{Composition.Amount(absent, "file is", "files are")} missing from {LoaderSearchPath.Folder}/");
        }

        // The loader is told, or the files sit there unread. A loader update rewrites this file.
        var setting = loader is null ? null : LoaderSearchPath.For(loader.Id, game.IsWindowsBuild);
        if (setting is null || !ListsOurs(game, setting, installed.ConfigEntry))
        {
            return new RuntimeLibrariesState(RuntimeLibrariesStatus.Missing, need, installed,
                loader is null
                    ? "no mod loader is installed to read them"
                    : $"{loader.Display} is not told where they are");
        }

        return new RuntimeLibrariesState(RuntimeLibrariesStatus.InPlace, need, installed);
    }

    private static bool ListsOurs(GameInstall game, LoaderSearchPath.Setting setting, string entry)
    {
        var files = new FileOperations(game.Path);
        if (!files.TryResolveInsideGame(setting.File, out var path) || !File.Exists(path)) return false;

        return LoaderSearchPath.Lists(File.ReadAllText(path), setting, entry);
    }

    /// <summary>
    /// Chooses, checks and writes the libraries this game lacks. Returns what the receipt should
    /// record — the previous record when nothing had to change, null when nothing is needed.
    /// </summary>
    /// <param name="loader">The loader that will read them — installed now or already there.</param>
    /// <param name="loaderVersion">Its version, when known; a loader too old to honour the setting is refused.</param>
    /// <param name="previous">What an earlier install recorded, so files no longer needed can go.</param>
    public static async Task<ReceiptRuntimeLibraries?> ApplyAsync(
        GameInstall game, LoaderDescriptor loader, string? loaderVersion, FileOperations files,
        ReceiptRuntimeLibraries? previous, string staging, ArchiveCache cache, Action<string>? status,
        CancellationToken ct)
    {
        var setting = LoaderSearchPath.For(loader.Id, game.IsWindowsBuild)
            ?? throw new InvalidOperationException(
                $"{loader.Display} cannot be told where to find the missing .NET libraries.");

        if (setting.MinimumLoaderVersion is { } minimum
            && loaderVersion is { Length: > 0 }
            && Versions.Compare(loaderVersion, minimum) < 0)
        {
            throw new InvalidOperationException(
                $"{loader.Display} {loaderVersion} cannot be told where to find the missing .NET libraries: "
                + $"that needs {minimum} or later. Update the loader first.");
        }

        var archiveName = RuntimeLibraries.ArchiveName(game.UnityVersion)
            ?? throw new InvalidOperationException(
                "The game's Unity version could not be read, so no matching .NET libraries can be chosen.");

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

        var url = LoaderOrigins.ClassLibrariesUrl(archiveName);
        status?.Invoke($"Downloading the .NET libraries for Unity {archiveName}...");

        FetchedArchive fetched;
        try
        {
            // No checksum is published for these. What makes them acceptable is checked below, on
            // the files themselves, against the game's own.
            fetched = await new ArchiveFetcher(staging, cache: cache)
                .FetchAsync(url, null, "corlibs",
                            // One entry per Unity version: a machine with games on two versions
                            // would otherwise download them in turn, for ever.
                            new ArchiveCacheKey($"corlibs-{archiveName}", archiveName), null, ct)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException(
                $"No copy of Unity {archiveName}'s .NET libraries is published, so the ones this game lacks cannot be added.");
        }

        var gameLibraries = RuntimeLibraries.Folder(managed);
        var archiveLibraries = RuntimeLibraries.Folder(fetched.ExtractedPath);

        var selection = RuntimeLibraries.Select(needs, gameLibraries, archiveLibraries);
        if (!selection.Complete)
        {
            var lacking = selection.Unresolved.Select(s => s.Use.Member is null ? s.Use.Type : $"{s.Use.Type}.{s.Use.Member}")
                                              .Distinct().Take(3);
            throw new InvalidOperationException(
                $"Even with the .NET libraries published for Unity {archiveName}, the mod would lack "
                + $"{string.Join(", ", lacking)}. Nothing was added.");
        }

        if (game.IsWindowsBuild && RuntimeLibraries.UnixOnly(selection.Chosen, archiveLibraries) is { Count: > 0 } linux)
        {
            throw new InvalidOperationException(
                $"For Unity {archiveName}, the only copy of {string.Join(", ", linux)} published is built for Linux, "
                + "and this game is built for Windows. Nothing was added.");
        }

        if (RuntimeLibraries.NotSameFamily(selection.Chosen, gameLibraries, archiveLibraries) is { Count: > 0 } alien)
        {
            throw new InvalidOperationException(
                $"The .NET libraries published for Unity {archiveName} do not match this game's own "
                + $"({string.Join("; ", alien)}). Nothing was added.");
        }

        if (selection.Chosen.Count == 0) return previous;

        status?.Invoke($"Adding {Composition.Amount(selection.Chosen.Count, ".NET library", ".NET libraries")}: "
                       + string.Join(", ", selection.Chosen) + "...");

        var before = files.WrittenFiles.Count;
        var dirsBefore = files.CreatedDirectories.Count;

        foreach (var name in selection.Chosen)
            files.PlaceFile(Path.Combine(fetched.ExtractedPath, name + ".dll"), $"{LoaderSearchPath.Folder}/{name}.dll");

        var written = files.WrittenFiles.Skip(before).ToList();
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
        if (previous is not null) RemoveFiles(files, previous.Files.Where(
            old => !written.Any(w => string.Equals(w.Path, old.Path, StringComparison.OrdinalIgnoreCase))), null, null);

        return new ReceiptRuntimeLibraries
        {
            Unity = archiveName,
            Source = url,
            Files = written,
            DirsCreated = created.Union(previous?.DirsCreated ?? Enumerable.Empty<string>(),
                                        StringComparer.OrdinalIgnoreCase).ToList(),
            ConfigFile = setting.File,
            ConfigEntry = LoaderSearchPath.Folder,
            ConfigCreated = text is null || previous?.ConfigCreated == true,
        };
    }

    /// <summary>
    /// Takes our entry back out of the loader's configuration and removes our libraries — each only
    /// while it is still the file we wrote.
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

        RemoveFiles(files, recorded.Files, removed, kept);

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
