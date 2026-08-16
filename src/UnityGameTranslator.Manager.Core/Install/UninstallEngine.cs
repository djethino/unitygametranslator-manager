using UnityGameTranslator.Manager.Core.Api;
using UnityGameTranslator.Manager.Core.Detection;
using UnityGameTranslator.Manager.Core.Model;
using UnityGameTranslator.Manager.Core.Platform;

namespace UnityGameTranslator.Manager.Core.Install;

/// <summary>What the user chose to remove. Each level is opt-in and independent.</summary>
/// <param name="UserDataFiles">
/// Exactly which data files go, relative to the mod's folder — or null for "everything the
/// inventory found", which is what a caller that never asked should mean.
///
/// ⚠ A list, not a flag, because "delete my settings and translations" is several decisions
/// wearing one word: somebody may want the fonts gone and the translation kept, and the old
/// all-or-nothing forced them to choose between a stale config and losing work.
/// </param>
public sealed record UninstallChoice(
    bool RemovePlugin = true,
    bool RemoveLoader = false,
    bool RemoveUserData = false,
    IReadOnlyList<string>? UserDataFiles = null);

public sealed record UninstallOutcome(
    bool Success,
    string Message,
    IReadOnlyList<string> Removed,
    IReadOnlyList<string> Kept,
    string? BackupPath);

/// <summary>
/// Removes what we installed, and only that.
///
/// It works from the receipt, never from a list of what a loader is supposed to contain. Three
/// rules follow, and they are the whole point of the design:
///   1. a file is removed only if we wrote it AND its hash is unchanged — otherwise the user
///      edited it, so we leave it and say so;
///   2. a directory is removed only if we created it and it is now empty;
///   3. the loader goes only if we installed it and nothing else depends on it.
/// </summary>
public sealed class UninstallEngine
{
    private readonly IPlatform _platform;
    private readonly LoaderCatalogDocument _catalog;

    public UninstallEngine(IPlatform platform, LoaderCatalogDocument catalog)
    {
        _platform = platform;
        _catalog = catalog;
    }

    public event Action<string>? Status;

    /// <summary>What can be removed for this game, given what is actually installed.</summary>
    public UninstallChoice Available(GameInstall game)
    {
        var receipt = ReceiptStore.Read(game.Path);

        // ⚠ No receipt does not mean nothing of ours is here. A mod dropped in by hand, or put
        // there by a build script, or installed before receipts existed, left the one screen that
        // could remove it showing no button at all — so the answer to "how do I get rid of this"
        // was "edit the folder yourself".
        //
        // What we may act on then is strictly what we can RECOGNISE: our own assembly, by name, at
        // the two documented locations. Never a folder, never a sweep — those hold translations,
        // and nothing here has a record of who wrote them.
        if (receipt is null)
        {
            return FindUnrecordedPlugin(game) is null
                ? new UninstallChoice(false, false, false)
                : new UninstallChoice(RemovePlugin: true, RemoveLoader: false, RemoveUserData: true);
        }

        var loaderRemovable = receipt.Loader?.InstalledByUs == true
                              && CountForeignMods(game, receipt) == 0;

        return new UninstallChoice(
            RemovePlugin: receipt.Plugin is not null,
            RemoveLoader: loaderRemovable,
            RemoveUserData: true);
    }

    /// <summary>
    /// Our assembly sitting in this game with nothing claiming to have put it there, or null.
    ///
    /// ⚠ Recognised by NAME at the documented locations, never by sweeping a folder. It is the only
    /// thing we can be certain is ours without a receipt to read.
    /// </summary>
    private string? FindUnrecordedPlugin(GameInstall game)
    {
        // The installed loader first: it names the one folder our assembly belongs in.
        var detected = LoaderProbe.Detect(game.Path, _catalog);

        var candidates = detected is not null
            ? _catalog.Loaders.Where(l => l.Id == detected.Id)
            // 🔴 **No loader does not mean no plugin of ours.** Removing a loader and leaving the
            // assembly behind puts a game in exactly that state — and this method, which is the
            // only way to clean it, used to give up at the first line because it had no loader to
            // ask. The plugin was then unreachable from the tool that put it there: "no receipt,
            // and no assembly to remove", about a folder holding UnityGameTranslator.dll.
            //
            // Every loader the catalog knows is asked instead. Still by NAME, still only at the
            // documented locations — four folders looked at, never a sweep.
            : _catalog.Loaders;

        foreach (var descriptor in candidates)
        {
            if (LocalTranslationProbe.FindInstalledPlugin(game.Path, descriptor) is { } found)
                return Path.Combine(found.Directory, LocalTranslationProbe.PluginAssemblyName);
        }

        return null;
    }

    /// <summary>Which loader's layout this assembly was sitting in, judged by its folder.</summary>
    private LoaderDescriptor? LoaderOwning(string pluginPath)
    {
        var normalised = pluginPath.Replace(Path.DirectorySeparatorChar, '/');

        return _catalog.Loaders.FirstOrDefault(l =>
        {
            var dir = l.PluginDir.Replace(Path.DirectorySeparatorChar, '/').Trim('/');
            return dir.Length > 0
                   && normalised.Contains("/" + dir + "/", StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>Drops our backup folder once nothing is left in it. Safe to call at any time.</summary>
    private static void SweepEmptyBackups(GameInstall game)
    {
        var root = Path.Combine(game.Path, FileOperations.BackupDirectory);
        if (!Directory.Exists(root)) return;

        try
        {
            foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                                               .OrderByDescending(d => d.Length))
            {
                FileOperations.TryRemoveEmptyDirectory(directory);
            }

            FileOperations.TryRemoveEmptyDirectory(root);
        }
        catch
        {
            // Tidying, never a reason to fail an uninstall.
        }
    }

    public UninstallOutcome Apply(GameInstall game, UninstallChoice choice)
    {
        var receipt = ReceiptStore.Read(game.Path);
        if (receipt is null)
        {
            // Nothing recorded, but our assembly is here: remove that one file and say so. The
            // data is deliberately left alone — with no receipt we cannot tell a translation
            // somebody wrote from one they downloaded, and the screen that asks about data is the
            // one that knows.
            if (choice.RemovePlugin && FindUnrecordedPlugin(game) is { } orphan)
            {
                try
                {
                    File.Delete(orphan);
                    FileOperations.TryRemoveEmptyDirectory(Path.GetDirectoryName(orphan)!);

                    var name = Path.GetRelativePath(game.Path, orphan).Replace('\\', '/');

                    // ⚠ With no loader left in this game, the tree it ran from answers to nobody:
                    // a cache, a log and a row of empty folders no program will ever open again.
                    // This is the state a game lands in when the loader went first and the
                    // assembly stayed — so it is where the leftovers have to be swept, and the
                    // only pass that will ever come here.
                    var swept = new List<string> { name };
                    if (LoaderProbe.Detect(game.Path, _catalog) is null)
                        RemoveLoaderLeftovers(game, LoaderOwning(orphan), swept);

                    SweepEmptyBackups(game);

                    return new UninstallOutcome(true,
                        $"Removed {name}. It was not installed by UnityGameTranslator Manager, so "
                        + "nothing else was touched — your settings and translations are still there.",
                        swept, Array.Empty<string>(), null);
                }
                catch (Exception ex)
                {
                    return new UninstallOutcome(false,
                        $"The plugin could not be removed ({ex.Message}).",
                        Array.Empty<string>(), Array.Empty<string>(), null);
                }
            }

            return new UninstallOutcome(false,
                "No install receipt here, and no UnityGameTranslator assembly to remove.",
                Array.Empty<string>(), Array.Empty<string>(), null);
        }

        if (_platform.IsGameRunning(game))
        {
            return new UninstallOutcome(false, "The game is running. Close it and try again.",
                Array.Empty<string>(), Array.Empty<string>(), null);
        }

        var removed = new List<string>();
        var kept = new List<string>();
        string? backupPath = null;
        var files = new FileOperations(game.Path);

        // User data first: it is the only irreplaceable thing here, so it gets saved before
        // anything else is touched, even if the rest fails afterwards.
        if (choice.RemoveUserData)
        {
            // ⚠ The session BEFORE the files. The marker is one of the files about to go, and
            // deleting it only forgets the session — it does not end it. What is left behind is a
            // browser tab still accepting saves into a session for a game folder that no longer
            // exists, and a slot held on the site until it expires a day later.
            EndAnyEditSession(game, receipt);

            backupPath = BackupUserData(game, receipt, choice, removed, kept);
        }

        if (choice.RemovePlugin)
        {
            if (receipt.Plugin is not null)
            {
                RemoveRecorded(files, receipt.Plugin.Files, receipt.Plugin.DirsCreated, removed, kept);
                receipt.Plugin = null;
            }

            // 🔴 **A receipt that does not mention the plugin does not mean there is none.**
            // Installing the loader alone writes a receipt with no Plugin section — that is a real
            // plan, and the ordinary one on a game whose mod was already current. Uninstalling
            // then walked straight past our own assembly and reported success: the folder still
            // held UnityGameTranslator.dll, and nothing said so.
            //
            // Recognised by NAME at the documented locations, exactly as the no-receipt path does.
            // Never a folder and never a sweep — those hold translations.
            if (FindUnrecordedPlugin(game) is { } orphan)
            {
                try
                {
                    File.Delete(orphan);
                    removed.Add(Path.GetRelativePath(game.Path, orphan)
                                    .Replace(Path.DirectorySeparatorChar, '/'));
                    FileOperations.TryRemoveEmptyDirectory(Path.GetDirectoryName(orphan)!);
                }
                catch (Exception ex)
                {
                    kept.Add($"{Path.GetFileName(orphan)} ({ex.GetType().Name})");
                }
            }
        }

        if (choice.RemoveLoader && receipt.Loader is { InstalledByUs: true } loader)
        {
            var foreign = CountForeignMods(game, receipt);
            if (foreign > 0)
            {
                kept.Add($"{loader.Id} (kept: {foreign} other mod(s) still use it)");
            }
            else
            {
                RemoveRecorded(files, loader.Files, loader.DirsCreated, removed, kept);
                receipt.Loader = null;

                // What the loader produced while it ran. Only now: with the loader gone and no
                // other mod left, a cache and a log answer to nothing.
                RemoveLoaderLeftovers(game, _catalog.Loaders.FirstOrDefault(l => l.Id == loader.Id),
                                      removed);
            }
        }

        RestoreBackups(game, files, removed);

        // The receipt stays only while it still describes something WE installed. A record of a
        // loader that was already there is not an install of ours: keeping it would make the
        // tool believe it manages a game it no longer touches.
        if (receipt.Plugin is null && receipt.Loader?.InstalledByUs != true)
        {
            ReceiptStore.Delete(game.Path);
            FileOperations.TryRemoveEmptyDirectory(
                Path.Combine(game.Path, FileOperations.BackupDirectory));
        }
        else
        {
            ReceiptStore.Write(game.Path, receipt);
        }

        var message = removed.Count == 0
            ? "Nothing was removed."
            : $"Removed {removed.Count} item(s).";
        if (kept.Count > 0) message += $" {kept.Count} left in place — see the details.";

        return new UninstallOutcome(true, message, removed, kept, backupPath);
    }

    /// <summary>
    /// Removes recorded files whose content still matches what we wrote, then the directories we
    /// created, deepest first and only while empty.
    /// </summary>
    private void RemoveRecorded(FileOperations files, List<ReceiptFile> recorded,
                                List<string> dirsCreated, List<string> removed, List<string> kept)
    {
        foreach (var file in recorded)
        {
            string path;
            try { path = files.ResolveInsideGame(file.Path); }
            catch (Exception ex) { kept.Add($"{file.Path} ({ex.Message})"); continue; }

            if (!File.Exists(path)) continue; // already gone: nothing to report

            // A changed hash means the user (or another tool) rewrote this file. Deleting it
            // would destroy work we never made.
            //
            // ⚠ Except our own assembly, and the exception is the point of the rule rather than a
            // hole in it. What this guard protects is WORK — a translation, a config, a font
            // somebody replaced. UnityGameTranslator.dll is none of those: a different hash there
            // means a build from elsewhere, a dev deployment, a partial update. Keeping it made
            // "Uninstall" leave the mod running in a game it claimed to have cleaned, which is the
            // one outcome this button must never produce.
            var ours = string.Equals(Path.GetFileName(file.Path),
                                     LocalTranslationProbe.PluginAssemblyName,
                                     StringComparison.OrdinalIgnoreCase);

            var current = FileOperations.HashFile(path);
            if (!ours && !string.Equals(current, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                kept.Add($"{file.Path} (changed since install — left untouched)");
                continue;
            }

            try
            {
                File.Delete(path);
                removed.Add(file.Path);
                Status?.Invoke($"Removed {file.Path}");
            }
            catch (Exception ex)
            {
                kept.Add($"{file.Path} ({ex.GetType().Name})");
            }
        }

        foreach (var dir in Enumerable.Reverse(dirsCreated))
        {
            try
            {
                if (FileOperations.TryRemoveEmptyDirectory(files.ResolveInsideGame(dir)))
                    removed.Add(dir + "/");
            }
            catch
            {
                // A directory we cannot remove is a directory someone else is using.
            }
        }
    }

    /// <summary>Puts back anything we overwrote at install time.</summary>
    private void RestoreBackups(GameInstall game, FileOperations files, List<string> removed)
    {
        var backupRoot = Path.Combine(game.Path, FileOperations.BackupDirectory);
        if (!Directory.Exists(backupRoot)) return;

        foreach (var backup in Directory.EnumerateFiles(backupRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(backupRoot, backup).Replace('\\', '/');
            try
            {
                var target = files.ResolveInsideGame(relative);
                if (File.Exists(target)) continue; // still in use by something we kept

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(backup, target);
                removed.Add($"restored {relative}");
            }
            catch
            {
                // Leaving a backup in place is safe; losing the original is not.
            }
        }

        // ⚠ The folder itself, once it holds nothing. It is ours, it is hidden, and a game left
        // with an empty .ugt-manager-backup after a full uninstall is a trace of a tool somebody
        // has just removed. Only when empty: a backup we failed to restore stays, and stays
        // findable.
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(backupRoot, "*", SearchOption.AllDirectories)
                                               .OrderByDescending(d => d.Length))
            {
                FileOperations.TryRemoveEmptyDirectory(directory);
            }

            FileOperations.TryRemoveEmptyDirectory(backupRoot);
        }
        catch
        {
            // Tidying. Never a reason to report a failed uninstall.
        }
    }

    /// <summary>
    /// What the loader PRODUCED while it ran — caches and logs — once the loader itself is gone.
    ///
    /// 🔴 **Not "files we installed", and that is why nothing removed them.** The rule this class
    /// rests on is that we only delete what we wrote; a cache written by BepInEx on its first
    /// launch was never ours. But once the loader is removed and no other mod is left, those files
    /// answer to nothing at all — an uninstall that reports success and leaves BepInEx/cache,
    /// BepInEx/LogOutput.log and a tree of empty folders has not finished.
    ///
    /// ⚠ **config/ is deliberately kept.** It holds settings — BepInEx's own, and any other mod's
    /// that ever ran here. Caches and logs regenerate in seconds; a configuration does not, and
    /// somebody reinstalling later would notice its absence and not know why.
    /// </summary>
    private static void RemoveLoaderLeftovers(GameInstall game, LoaderDescriptor? descriptor,
                                              List<string> removed)
    {
        if (descriptor is null) return;

        // The loader's own tree: the parent of its plugin folder, unless that folder IS the tree
        // (MelonLoader's Mods/ sits beside UserData/ rather than inside anything of ours).
        var root = descriptor.PluginDirShared
            ? null
            : Path.GetDirectoryName(Path.Combine(game.Path, descriptor.PluginDir));

        if (root is null || !Directory.Exists(root)) return;

        foreach (var name in new[] { "cache", "LogOutput.log", "preloader.log" })
        {
            var path = Path.Combine(root, name);

            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                else if (File.Exists(path)) File.Delete(path);
                else continue;

                removed.Add(Path.GetRelativePath(game.Path, path)
                                .Replace(Path.DirectorySeparatorChar, '/'));
            }
            catch
            {
                // A log still held open by a crashed game is not a failed uninstall.
            }
        }

        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                                           .OrderByDescending(d => d.Length))
        {
            FileOperations.TryRemoveEmptyDirectory(directory);
        }

        FileOperations.TryRemoveEmptyDirectory(root);
    }

    /// <summary>
    /// Copies settings and translations out before deleting them. A translation can be months of
    /// work, and someone uninstalling a mod is not necessarily throwing that away.
    /// </summary>
    /// <summary>
    /// Copies the chosen data aside, then removes it. Returns where the copy went, or null.
    ///
    /// ⚠ The copy happens FIRST and the removal only follows a copy that worked. A translation is
    /// the one irreplaceable thing in a game folder, and an uninstall that half-succeeded must
    /// leave it recoverable rather than merely reported.
    ///
    /// ⚠ Works from what was TICKED, not from a list of names. Four names were hard-coded here, so
    /// fonts, replacement images and dated backups survived every "remove my data" — the folder
    /// stayed half full and nothing said which half.
    /// </summary>
    /// <summary>
    /// End a browser session open on this game before its translation is removed.
    ///
    /// ⚠ Only one we can prove is ours to end. A marker whose key does not decrypt belongs to
    /// another account of this computer; we are already refusing to write into their setup, and
    /// closing their editor would be the same trespass by another route.
    ///
    /// ⚠ Best effort, and deliberately not awaited into a failure: an uninstall must not be
    /// blocked by a site that does not answer. What is at stake is a slot on the site, not the
    /// user's data — the data is copied aside a few lines below.
    /// </summary>
    private void EndAnyEditSession(GameInstall game, Receipt receipt)
    {
        var descriptor = _catalog.Loaders.FirstOrDefault(l => l.Id == receipt.Loader?.Id)
                         ?? _catalog.Loaders.FirstOrDefault(l => l.Id == receipt.Plugin?.Build);
        if (descriptor is null) return;

        var marker = EditSessionMarkers.Read(game.Path, descriptor);
        if (marker is null || !marker.Endable) return;

        try
        {
            new EditSessionClient().CloseAsync(marker.ModKey!).Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // Unreachable site, or slower than five seconds. The session then expires on its own;
            // stopping an uninstall over it would be a worse answer than letting it lapse.
        }
    }

    private string? BackupUserData(GameInstall game, Receipt receipt, UninstallChoice choice,
                                   List<string> removed, List<string> kept)
    {
        var descriptor = _catalog.Loaders.FirstOrDefault(l => l.Id == receipt.Loader?.Id)
                         ?? _catalog.Loaders.FirstOrDefault(l => l.Id == receipt.Plugin?.Build);
        if (descriptor is null) return null;

        var userData = UserDataInventory.FolderFor(game.Path, descriptor);
        if (userData is null) return null;

        var chosen = choice.UserDataFiles
                     ?? UserDataInventory.Scan(game.Path, descriptor)
                                         .SelectMany(g => g.Items)
                                         .Select(i => i.RelativePath)
                                         .ToList();

        if (chosen.Count == 0) return null;

        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var backupDir = Path.Combine(_platform.UserDataDirectory, "removed", $"{SafeName(game.Name)}-{stamp}");

        var any = false;

        foreach (var relative in chosen)
        {
            var source = Path.Combine(userData, relative.Replace('/', Path.DirectorySeparatorChar));

            // ⚠ Never outside the mod's folder, whatever the list says. A relative path is data,
            // and data that walks up with ".." would have this delete something it never listed.
            var full = Path.GetFullPath(source);
            if (!full.StartsWith(Path.GetFullPath(userData), StringComparison.OrdinalIgnoreCase))
            {
                kept.Add($"{relative} (outside the mod's folder)");
                continue;
            }

            if (!File.Exists(full)) continue;

            try
            {
                var target = Path.Combine(backupDir, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(full, target, overwrite: true);
                File.Delete(full);
                removed.Add(relative);
                any = true;
            }
            catch (Exception ex)
            {
                kept.Add($"{relative} ({ex.GetType().Name})");
            }
        }

        // Folders the mod made for its own files — fonts/, images/ — go once they are empty, and
        // only then: one left behind by a file we could not remove still holds that file.
        foreach (var directory in Directory.EnumerateDirectories(userData, "*", SearchOption.AllDirectories)
                                           .OrderByDescending(d => d.Length))
        {
            FileOperations.TryRemoveEmptyDirectory(directory);
        }

        return any ? backupDir : null;
    }

    /// <summary>Mods other than ours sharing the loader. Any of them blocks removing it.</summary>
    private int CountForeignMods(GameInstall game, Receipt receipt) =>
        ForeignMods(game, receipt).Count;

    /// <summary>
    /// The same neighbours, named — so a refusal can be shown rather than merely counted.
    ///
    /// ⚠ An unknown layout answers with one unnamed neighbour rather than none. Reporting "nothing
    /// depends on this" about a folder we cannot read is the mistake that costs somebody else's
    /// mods, and it is the one direction this must never fail in.
    /// </summary>
    public IReadOnlyList<string> ForeignMods(GameInstall game, Receipt? receipt = null)
    {
        receipt ??= ReceiptStore.Read(game.Path);

        var descriptor = _catalog.Loaders.FirstOrDefault(l => l.Id == receipt?.Loader?.Id);
        if (descriptor is null) return new[] { "another mod (this loader's layout is unknown here)" };

        return LoaderProbe.Detect(game.Path, _catalog)?.ForeignMods ?? Array.Empty<string>();
    }

    private static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
