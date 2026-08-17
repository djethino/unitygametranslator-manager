using System.Text.Json;
using System.Text.Json.Nodes;
using UnityGameTranslator.Common;
using UnityGameTranslator.Manager.Core.Detection;
using UnityGameTranslator.Manager.Core.Model;

namespace UnityGameTranslator.Manager.Core.Install;

/// <summary>
/// The same history the mod keeps, read and written from here.
///
/// 🔴 **The mod owns this mechanism, and this class is a second window onto it.** Somebody may
/// never install the Manager — that is everybody today — so a safety net living only here would
/// not be one. Every rule (the folder, the two families, their limits, which assets a copy
/// carries, the words a row reads) comes from <see cref="Backups"/>; what differs between the two
/// products is the file access and the drawing, nothing else.
///
/// ⚠ **It also reads what earlier versions left behind.** Three shapes predate this — the
/// `removed/` folder this tool used, and the mod's `translations.json.backup` and `.prepurge` —
/// and each of them is somebody's translation. Listing them as ordinary rows is what stops them
/// being stranded; they age out on their own as the new mechanism takes over.
/// </summary>
public static class TranslationBackupStore
{
    private const string TranslationFile = "translations.json";
    private const string AncestorFile = "translations.json.ancestor";

    /// <summary>Marks an id that names a file left by an earlier version rather than a folder.</summary>
    private const string LegacyPrefix = "legacy:";

    // ── Where ─────────────────────────────────────────────────────────────

    private static string? Root(string gamePath, LoaderDescriptor descriptor)
    {
        var folder = UserDataInventory.FolderFor(gamePath, descriptor);
        return folder is null ? null : Path.Combine(folder, Backups.FolderName);
    }

    private static string Target(string gamePath, LoaderDescriptor descriptor) =>
        Path.Combine(UserDataInventory.FolderFor(gamePath, descriptor)!,
                     LocalTranslationProbe.TranslationFileName);

    // ── Reading ───────────────────────────────────────────────────────────

    /// <summary>Every copy this game holds, newest first, whatever version wrote it.</summary>
    public static IReadOnlyList<BackupEntry> List(string gamePath, LoaderDescriptor descriptor)
    {
        var entries = new List<BackupEntry>();

        var data = UserDataInventory.FolderFor(gamePath, descriptor);
        if (data is null) return entries;

        var root = Path.Combine(data, Backups.FolderName);

        if (Directory.Exists(root))
        {
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                var name = Path.GetFileName(directory);
                if (!Backups.IsBackupFolder(name, out var saved)) continue;

                // A folder with no translation is not a copy of anything: a write that was
                // interrupted. Offering it would promise a restore that puts nothing back.
                if (!File.Exists(Path.Combine(directory, TranslationFile))) continue;

                entries.Add(ReadAbout(directory, name, saved));
            }
        }

        entries.AddRange(Legacy(data));

        return entries.OrderByDescending(e => e.At).ToList();
    }

    private static BackupEntry ReadAbout(string directory, string id, bool saved)
    {
        var entry = new BackupEntry
        {
            Id = id,
            Reason = saved ? BackupReason.Saved : BackupReason.Unknown,
            WithAssets = saved,
            At = StampOf(id),
        };

        try
        {
            var about = Path.Combine(directory, Backups.AboutFileName);
            if (!File.Exists(about)) return entry;

            if (JsonNode.Parse(File.ReadAllText(about)) is not JsonObject json) return entry;

            if (json["at"]?.GetValue<string>() is { } at && DateTime.TryParse(at, out var parsed))
                entry.At = parsed;

            if (json["reason"]?.GetValue<string>() is { } reason
                && Enum.TryParse<BackupReason>(reason, ignoreCase: true, out var known))
            {
                entry.Reason = known;
            }

            entry.By = json["by"]?.GetValue<string>();
            entry.Label = json["label"]?.GetValue<string>();
            entry.Lines = json["lines"]?.GetValue<int>() ?? 0;
            entry.ByHand = json["by_hand"]?.GetValue<int>() ?? 0;
            entry.Uuid = json["uuid"]?.GetValue<string>();
            entry.WithAssets = json["assets"]?.GetValue<bool>() ?? saved;
        }
        catch
        {
            // A description we cannot read costs the row its details, never its existence: the
            // translation beside it is still restorable, and that is the part that matters.
        }

        return entry;
    }

    /// <summary>
    /// What earlier versions left: this tool's `removed/` folder, and the two loose files the mod
    /// used to write. Read-only rows — nothing new is ever written in those shapes.
    /// </summary>
    private static IEnumerable<BackupEntry> Legacy(string dataFolder)
    {
        var found = new List<BackupEntry>();

        void Add(string path, DateTime at)
        {
            found.Add(new BackupEntry
            {
                Id = LegacyPrefix + Path.GetRelativePath(dataFolder, path).Replace('\\', '/'),
                At = at,
                Reason = BackupReason.Unknown,
                Lines = LocalTranslationProbe.ReadLines(path)?.Count ?? 0,
                Uuid = UuidIn(path),
                WithAssets = false,
            });
        }

        try
        {
            var removed = Path.Combine(dataFolder, TranslationInstaller.BackupFolderName);
            if (Directory.Exists(removed))
            {
                foreach (var file in Directory.EnumerateFiles(removed, "translations-*.json"))
                    Add(file, File.GetLastWriteTime(file));
            }

            foreach (var loose in new[] { ".backup", ".prepurge" })
            {
                var file = Path.Combine(dataFolder, LocalTranslationProbe.TranslationFileName + loose);
                if (File.Exists(file)) Add(file, File.GetLastWriteTime(file));
            }
        }
        catch
        {
            // A folder we cannot read lists nothing, and says so by being empty.
        }

        return found;
    }

    private static DateTime StampOf(string id)
    {
        var dash = id.IndexOf('-');

        return dash >= 0
               && DateTime.TryParseExact(id[(dash + 1)..], "yyyyMMdd-HHmmss",
                                         System.Globalization.CultureInfo.InvariantCulture,
                                         System.Globalization.DateTimeStyles.None, out var at)
            ? at
            : DateTime.MinValue;
    }

    private static string? UuidIn(string path)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) is JsonObject json
                ? json["_uuid"]?.GetValue<string>()
                : null;
        }
        catch
        {
            return null;
        }
    }

    // ── Taking a copy ─────────────────────────────────────────────────────

    /// <summary>
    /// The copy an ACTION takes, before something replaces the translation wholesale.
    ///
    /// 🔴 Called from inside the write, never beside it. Nine call sites across two products each
    /// had to remember, and one forgot — see the note in <see cref="Backups"/>.
    /// </summary>
    public static void TakeAutomatic(string gamePath, LoaderDescriptor descriptor,
                                     BackupReason reason, string? by = null)
    {
        if (reason == BackupReason.Saved) return;

        Take(gamePath, descriptor, reason, by, label: null, withAssets: false);
        Prune(gamePath, descriptor);
    }

    /// <summary>The copy somebody asks for, with the assets the translation names.</summary>
    public static string? SaveCopy(string gamePath, LoaderDescriptor descriptor)
    {
        if (!Backups.CanSaveAnother(List(gamePath, descriptor))) return null;

        return Take(gamePath, descriptor, BackupReason.Saved, by: null, label: null,
                    withAssets: true);
    }

    private static string? Take(string gamePath, LoaderDescriptor descriptor, BackupReason reason,
                                string? by, string? label, bool withAssets)
    {
        try
        {
            var root = Root(gamePath, descriptor);
            if (root is null) return null;

            var source = Target(gamePath, descriptor);
            if (!File.Exists(source)) return null;   // nothing written yet is not a failure

            Directory.CreateDirectory(root);

            var id = UniqueId(root, reason);
            var directory = Path.Combine(root, id);
            Directory.CreateDirectory(directory);

            File.Copy(source, Path.Combine(directory, TranslationFile), overwrite: true);

            // 🔴 The ancestor travels with the translation. It describes the version both sides
            // agreed on; a file restored under a newer ancestor leaves the next merge comparing
            // against a state that never existed, and nothing would notice.
            var ancestor = Path.Combine(Path.GetDirectoryName(source)!,
                                        LocalTranslationProbe.AncestorFileName);
            if (File.Exists(ancestor))
                File.Copy(ancestor, Path.Combine(directory, AncestorFile), overwrite: true);

            if (withAssets) CopyAssets(gamePath, descriptor, source, directory);

            WriteAbout(directory, source, reason, by, label, withAssets);

            return id;
        }
        catch
        {
            // A copy that cannot be written must never stop the act it was protecting.
            return null;
        }
    }

    private static string UniqueId(string root, BackupReason reason)
    {
        var at = DateTime.Now;
        var id = Backups.NewId(reason, at);

        var attempt = 0;
        while (Directory.Exists(Path.Combine(root, id)) && attempt++ < 60)
        {
            at = at.AddSeconds(1);
            id = Backups.NewId(reason, at);
        }

        return id;
    }

    private static void CopyAssets(string gamePath, LoaderDescriptor descriptor, string source,
                                   string directory)
    {
        var data = UserDataInventory.FolderFor(gamePath, descriptor);
        if (data is null) return;

        foreach (var relative in Backups.AssetsToCopy(ImagesInUse(source), FontsInUse(data)))
        {
            try
            {
                var from = Path.Combine(data, relative.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(from)) continue;

                var to = Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(from, to, overwrite: true);
            }
            catch
            {
                // One asset lost costs that asset; the translation is what exists nowhere else.
            }
        }
    }

    private static IEnumerable<string?> ImagesInUse(string translationPath)
    {
        var names = new List<string?>();

        try
        {
            if (JsonNode.Parse(File.ReadAllText(translationPath)) is not JsonObject root) return names;
            if (root["_images"] is not JsonArray images) return names;

            foreach (var item in images)
            {
                if (item is not JsonObject obj) continue;

                names.Add(obj["file"]?.GetValue<string>()
                          ?? obj["replacement_file"]?.GetValue<string>()
                          ?? obj["original_file"]?.GetValue<string>());
            }
        }
        catch
        {
            // A translation we cannot parse carries no assets we can name.
        }

        return names;
    }

    /// <summary>
    /// The font SOURCES this game holds. Generated atlases are left out on purpose: rebuilt on
    /// demand, and the largest thing in that folder.
    /// </summary>
    private static IEnumerable<string?> FontsInUse(string dataFolder)
    {
        var names = new List<string?>();

        try
        {
            var fonts = Path.Combine(dataFolder, "fonts");
            if (!Directory.Exists(fonts)) return names;

            foreach (var file in Directory.EnumerateFiles(fonts))
            {
                var extension = Path.GetExtension(file);
                if (extension is ".ttf" or ".otf" or ".ttc"
                    || extension.Equals(".TTF", StringComparison.OrdinalIgnoreCase))
                {
                    names.Add(Path.GetFileName(file));
                }
            }
        }
        catch
        {
            // Unreadable folder, no fonts named.
        }

        return names;
    }

    private static void WriteAbout(string directory, string source, BackupReason reason,
                                   string? by, string? label, bool withAssets)
    {
        try
        {
            // ⚠ Counted from the FILE being copied, not from what the game reports: the copy is
            // of that file, and a row describing something else is a row that lies quietly.
            var lines = LocalTranslationProbe.ReadLines(source);

            var total = lines?.Count ?? 0;
            var byHand = 0;

            if (lines is not null)
            {
                foreach (var line in lines.Values)
                {
                    // The three tags a human is behind: written, settled, deliberately kept.
                    if (line.Tag is "H" or "V" or "S") byHand++;
                }
            }

            var about = new JsonObject
            {
                ["at"] = DateTime.Now.ToString("o"),
                ["reason"] = reason.ToString(),
                ["lines"] = total,
                ["by_hand"] = byHand,
                ["assets"] = withAssets,
            };

            if (!string.IsNullOrEmpty(by)) about["by"] = by;
            if (!string.IsNullOrEmpty(label)) about["label"] = label;
            if (UuidIn(source) is { Length: > 0 } uuid) about["uuid"] = uuid;

            File.WriteAllText(Path.Combine(directory, Backups.AboutFileName),
                              about.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // A row without its details is still a row that restores.
        }
    }

    private static void Prune(string gamePath, LoaderDescriptor descriptor)
    {
        try
        {
            var root = Root(gamePath, descriptor);
            if (root is null) return;

            foreach (var id in Backups.AutomaticToDrop(List(gamePath, descriptor)))
            {
                // ⚠ Never a legacy file: those are somebody's translation left by an older
                // version, and this rotation did not put them there.
                if (id.StartsWith(LegacyPrefix, StringComparison.Ordinal)) continue;

                var directory = Path.Combine(root, id);
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            // Tidying, never a reason to fail the act that triggered it.
        }
    }

    // ── Acting on one ─────────────────────────────────────────────────────

    /// <summary>
    /// Puts a copy back, after keeping what stands there now.
    ///
    /// 🔴 The current state is kept FIRST. Restoring is the one act here that replaces work, and
    /// somebody who picks the wrong row has to be able to walk back out of it.
    /// </summary>
    public static bool Restore(string gamePath, LoaderDescriptor descriptor, string id)
    {
        try
        {
            var data = UserDataInventory.FolderFor(gamePath, descriptor);
            if (data is null) return false;

            var target = Target(gamePath, descriptor);
            TakeAutomatic(gamePath, descriptor, BackupReason.Restored);

            if (id.StartsWith(LegacyPrefix, StringComparison.Ordinal))
            {
                var legacy = Path.Combine(data, id[LegacyPrefix.Length..]
                                                .Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(legacy)) return false;

                File.Copy(legacy, target, overwrite: true);

                // ⚠ No ancestor came with it, so the stale one goes rather than staying to
                // describe an agreement that never happened.
                DropAncestor(data);
                return true;
            }

            var directory = Path.Combine(data, Backups.FolderName, id);
            var source = Path.Combine(directory, TranslationFile);
            if (!File.Exists(source)) return false;

            File.Copy(source, target, overwrite: true);

            var ancestorSource = Path.Combine(directory, AncestorFile);
            var ancestorTarget = Path.Combine(data, LocalTranslationProbe.AncestorFileName);

            if (File.Exists(ancestorSource)) File.Copy(ancestorSource, ancestorTarget, overwrite: true);
            else DropAncestor(data);

            RestoreAssets(directory, data);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void DropAncestor(string dataFolder)
    {
        try
        {
            var ancestor = Path.Combine(dataFolder, LocalTranslationProbe.AncestorFileName);
            if (File.Exists(ancestor)) File.Delete(ancestor);
        }
        catch
        {
            // Leaving it is worse than losing it, but neither is worth failing the restore over.
        }
    }

    private static void RestoreAssets(string directory, string dataFolder)
    {
        foreach (var folder in Backups.AssetFolders)
        {
            try
            {
                var source = Path.Combine(directory, folder);
                if (!Directory.Exists(source)) continue;

                var target = Path.Combine(dataFolder, folder);
                Directory.CreateDirectory(target);

                foreach (var file in Directory.EnumerateFiles(source))
                    File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
            }
            catch
            {
                // One folder that cannot be put back does not undo the translation that was.
            }
        }
    }

    public static bool Delete(string gamePath, LoaderDescriptor descriptor, string id)
    {
        try
        {
            var data = UserDataInventory.FolderFor(gamePath, descriptor);
            if (data is null) return false;

            if (id.StartsWith(LegacyPrefix, StringComparison.Ordinal))
            {
                var legacy = Path.Combine(data, id[LegacyPrefix.Length..]
                                                .Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(legacy)) return false;

                File.Delete(legacy);
                return true;
            }

            var directory = Path.Combine(data, Backups.FolderName, id);
            if (!Directory.Exists(directory)) return false;

            Directory.Delete(directory, recursive: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Moves an automatic copy in with the deliberate ones, so it stops rotating.
    ///
    /// ⚠ A legacy file is promoted by being copied into a proper folder: it has no `about` of its
    /// own, and leaving it loose would keep it at the mercy of the next tidy-up.
    /// </summary>
    public static bool Keep(string gamePath, LoaderDescriptor descriptor, string id)
    {
        try
        {
            var data = UserDataInventory.FolderFor(gamePath, descriptor);
            if (data is null) return false;
            if (!Backups.CanSaveAnother(List(gamePath, descriptor))) return false;

            var root = Path.Combine(data, Backups.FolderName);
            Directory.CreateDirectory(root);

            if (id.StartsWith(LegacyPrefix, StringComparison.Ordinal))
            {
                var legacy = Path.Combine(data, id[LegacyPrefix.Length..]
                                                .Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(legacy)) return false;

                var moved = Path.Combine(root, UniqueId(root, BackupReason.Saved));
                Directory.CreateDirectory(moved);
                File.Move(legacy, Path.Combine(moved, TranslationFile));
                WriteAbout(moved, Path.Combine(moved, TranslationFile), BackupReason.Saved,
                           by: null, label: null, withAssets: false);
                return true;
            }

            var directory = Path.Combine(root, id);
            if (!Directory.Exists(directory)) return false;

            var destination = Path.Combine(root, Backups.NewId(BackupReason.Saved, StampOf(id)));
            if (Directory.Exists(destination)) return false;

            Directory.Move(directory, destination);

            // ⚠ The reason it was taken is kept, not overwritten with "Saved by you": "before
            // installing @Seniorito's translation" is precisely why it is worth keeping.
            Retouch(destination, about => about["kept"] = true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool Rename(string gamePath, LoaderDescriptor descriptor, string id, string? label)
    {
        try
        {
            var data = UserDataInventory.FolderFor(gamePath, descriptor);
            if (data is null) return false;

            var directory = Path.Combine(data, Backups.FolderName, id);
            if (!Directory.Exists(directory)) return false;

            Retouch(directory, about =>
            {
                if (string.IsNullOrWhiteSpace(label)) about.Remove("label");
                else about["label"] = label!.Trim();
            });

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void Retouch(string directory, Action<JsonObject> change)
    {
        var path = Path.Combine(directory, Backups.AboutFileName);

        var about = File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject held
            ? held
            : new JsonObject();

        change(about);

        File.WriteAllText(path, about.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
