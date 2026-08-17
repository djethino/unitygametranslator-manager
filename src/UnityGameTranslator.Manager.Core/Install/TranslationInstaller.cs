using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using UnityGameTranslator.Common;
using UnityGameTranslator.Manager.Core.Detection;
using UnityGameTranslator.Manager.Core.Model;
using UnityGameTranslator.Manager.Core.Platform;

namespace UnityGameTranslator.Manager.Core.Install;

/// <summary>What happened, and where the previous file went.</summary>
public sealed record TranslationWriteResult(
    bool Written,
    string? BackupPath,
    string? Failure);

/// <summary>A translation that was set aside when something replaced it.</summary>
/// <param name="Lines">How many translated entries it holds — the only size that means anything.</param>
/// <param name="Uuid">Its lineage, so a screen can say whether this account leads or contributed to it.</param>
public sealed record TranslationBackup(string Path, DateTime Replaced, int Lines, string? Uuid);

/// <summary>
/// Puts a downloaded translation into a game, and never loses what was there.
///
/// Two rules, both settled beforehand and neither open for convenience:
///
/// ⚠ **We never merge.** Three-way merge belongs to the mod: it holds the ancestor file, it knows
/// what the player edited since, and it has screens to arbitrate line by line. A second
/// implementation here would be a second truth about the same file, and the one that ran last
/// would win. When someone wants to keep their work AND take this one, the answer is to point
/// them at the mod, not to attempt it.
///
/// ⚠ **We always back up**, even when the file looks recoverable online. The proof that it is
/// recoverable rests on metadata that is sometimes absent, and a rule that skips the backup would
/// one day skip the one that mattered. What changes with that proof is what we SAY, not what we do.
/// </summary>
public sealed class TranslationInstaller
{
    /// <summary>Where replaced files go. Same folder the uninstaller already uses.</summary>
    public const string BackupFolderName = "removed";

    /// <summary>
    /// What a write refuses to do while the game is open.
    ///
    /// ⚠ **Not a locked file — a lost one.** The mod holds the whole translation in memory and
    /// rewrites it WHOLE on its own timer. A file written here while a game runs is not in
    /// conflict with anything: it is overwritten at the mod's next save, silently, and the person
    /// who took a translation sees it vanish minutes later with nothing said. That is worse than a
    /// refusal, which is why this is a refusal.
    ///
    /// ⚠ The check is <see cref="IPlatform.IsGameRunning"/> — the precise one, which opens each
    /// candidate process — and not the cheap sweep the game list uses. That one answers "not
    /// running" for a game belonging to another operating-system account, and this is exactly the
    /// machine where several accounts share one game folder.
    /// </summary>
    public const string GameRunningRefusal =
        "This game is open. The mod rewrites its translation file from memory while it runs, so "
        + "anything written now would be replaced without warning. Close the game and try again.";

    private readonly IPlatform? _platform;

    /// <summary>
    /// ⚠ The platform is what makes a write refuse while the game is open, and it is asked for
    /// rather than optional-by-default on purpose: a caller that forgets it does not get a
    /// silently unguarded writer, it gets a compile error. The null case exists only for callers
    /// holding a game that provably cannot be running — none today.
    /// </summary>
    public TranslationInstaller(IPlatform? platform)
    {
        _platform = platform;
    }

    /// <summary>
    /// Whether writing to this game is allowed right now. Null when it is.
    ///
    /// ⚠ Fails towards refusing: an answer we cannot get is not permission.
    /// </summary>
    public string? WhyNotNow(GameInstall game)
    {
        if (_platform is null) return null;

        try
        {
            return _platform.IsGameRunning(game) ? GameRunningRefusal : null;
        }
        catch
        {
            // Could not tell. The install engine treats this the same way — the cost of a needless
            // refusal is a second attempt; the cost of a wrong permission is somebody's work.
            return GameRunningRefusal;
        }
    }

    /// <summary>
    /// Writes the file, after moving any existing one aside.
    ///
    /// <paramref name="serverHash"/> is written as _source.hash, which is how the mod later tells
    /// "the server moved on" from "I edited this". Without it every downloaded file would look
    /// locally modified from the first launch, and the mod would offer to merge against nothing.
    /// </summary>
    /// <param name="installedFrom">
    /// Whose translation is being installed, as a mention. It is what the backup row will read,
    /// and the only thing that makes one row tell itself apart from another.
    /// </param>
    public TranslationWriteResult Install(GameInstall game, LoaderDescriptor descriptor,
                                          string json, string? serverHash,
                                          string? installedFrom = null)
    {
        if (WhyNotNow(game) is { } refusal) return new TranslationWriteResult(false, null, refusal);

        var gamePath = game.Path;
        var folder = Path.Combine(gamePath,
            descriptor.UserDataDir.Replace('/', Path.DirectorySeparatorChar));
        var target = Path.Combine(folder, LocalTranslationProbe.TranslationFileName);

        try
        {
            var prepared = StampSource(json, serverHash);

            Directory.CreateDirectory(folder);

            // 🔴 Kept BEFORE the write, named by the act — see Backups. The old `removed/` file
            // said only "something replaced this, at this time"; a row reading "before installing
            // @Seniorito's translation" is a memory, a date is a lottery.
            TranslationBackupStore.TakeAutomatic(gamePath, descriptor, BackupReason.Installed,
                                                 installedFrom);

            string? backup = null;
            if (File.Exists(target)) backup = MoveAside(target);

            // Written beside then moved: a file half-written by a crash or a full disk would take
            // the place of a translation that was working a second earlier.
            var temp = target + ".tmp";
            File.WriteAllText(temp, prepared, new UTF8Encoding(false));
            File.Move(temp, target, overwrite: true);

            // ⚠ **The one moment when an ancestor is free and exact.** What was just written IS the
            // published version, so the two sides provably agree right now — which is the whole
            // definition of an ancestor.
            //
            // Without it the FIRST merge is blind: once both sides have moved, nothing can tell
            // "I changed this" from "the published version moved", and every disagreement of equal
            // standing has to be put to the user. The mod writes one on its own downloads
            // (SaveAncestorFromRemote); taking the same translation from here left the file
            // without one, so it started life less mergeable than the identical file taken in game.
            //
            // ⚠ Additive, and in the mod's own format: a mod that predates any of this reads it if
            // it looks for one and is unaffected otherwise. Nothing to migrate, and no version of
            // either program has to move first.
            //
            // Never fatal: an ancestor we failed to write costs precision later, not the
            // translation now.
            try
            {
                var ancestor = Path.Combine(folder, LocalTranslationProbe.AncestorFileName);
                var ancestorTemp = ancestor + ".tmp";
                File.WriteAllText(ancestorTemp, json, new UTF8Encoding(false));
                File.Move(ancestorTemp, ancestor, overwrite: true);
            }
            catch
            {
                // The translation is in place, which is what was asked for.
            }

            return new TranslationWriteResult(true, backup, null);
        }
        catch (Exception ex)
        {
            return new TranslationWriteResult(false, null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Writes a merged translation, and the bookkeeping that has to go with it.
    ///
    /// ⚠ **A merge is not one write, it is three facts**, and getting the other two wrong is worse
    /// than not merging at all — the NEXT comparison would be computed against a baseline that
    /// never existed, inventing conflicts or hiding them:
    ///
    /// · the merged file itself;
    /// · _source.hash ← the published version's hash. It is the version we have now seen, whether
    ///   or not we kept all of it;
    /// · the ancestor ← the PUBLISHED content, NOT the merged one. The ancestor answers "what did
    ///   the two sides last agree on", and what they last agreed on is what was published. Writing
    ///   the merged file there would make every line we just kept look like common ground, so the
    ///   next merge would silently drop them.
    ///
    /// And _local_changes becomes what the merged file has that the published one does not — which
    /// is precisely what still needs publishing.
    /// </summary>
    /// <param name="remoteJson">The published file, written aside as the new ancestor.</param>
    public TranslationWriteResult WriteMerged(GameInstall game, LoaderDescriptor descriptor,
                                              string mergedJson, string remoteJson,
                                              string? serverHash, int aheadOfServer)
    {
        if (WhyNotNow(game) is { } refusal) return new TranslationWriteResult(false, null, refusal);

        var folder = Path.Combine(game.Path,
            descriptor.UserDataDir.Replace('/', Path.DirectorySeparatorChar));
        var target = Path.Combine(folder, LocalTranslationProbe.TranslationFileName);

        try
        {
            var prepared = StampMerged(mergedJson, serverHash, aheadOfServer);

            Directory.CreateDirectory(folder);

            TranslationBackupStore.TakeAutomatic(game.Path, descriptor, BackupReason.Merged);

            string? backup = null;
            if (File.Exists(target)) backup = MoveAside(target);

            var temp = target + ".tmp";
            File.WriteAllText(temp, prepared, new UTF8Encoding(false));
            File.Move(temp, target, overwrite: true);

            // ⚠ After the translation, never before: an ancestor updated over a write that then
            // failed would describe an agreement that never happened, and nothing would ever
            // notice. Losing it the other way round only costs the next merge its precision.
            var ancestor = Path.Combine(folder, LocalTranslationProbe.AncestorFileName);
            var ancestorTemp = ancestor + ".tmp";
            File.WriteAllText(ancestorTemp, remoteJson, new UTF8Encoding(false));
            File.Move(ancestorTemp, ancestor, overwrite: true);

            return new TranslationWriteResult(true, backup, null);
        }
        catch (Exception ex)
        {
            return new TranslationWriteResult(false, null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>The two facts a merged file has to carry about where it now stands.</summary>
    private static string StampMerged(string json, string? serverHash, int aheadOfServer)
    {
        var node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });

        if (node is not JsonObject root) return json;

        if (!string.IsNullOrWhiteSpace(serverHash))
        {
            if (root["_source"] is not JsonObject source)
            {
                source = new JsonObject();
                root["_source"] = source;
            }

            source["hash"] = serverHash;
        }

        root["_local_changes"] = aheadOfServer;

        return root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    /// <summary>
    /// Writes back a file that came home from a browser edit session.
    ///
    /// ⚠ **Not the same act as installing a downloaded translation, and the difference is not
    /// cosmetic.** A download comes from the server, so it is stamped with the server's hash and
    /// its local-change count is reset — it is, by definition, in step with what was published.
    /// A file that went to an editor and came back is the opposite: the server hash it carries is
    /// still true (nothing was published), and what changed is precisely the local work.
    ///
    /// ⚠ **So the count has to move, and leaving it alone would be dangerous rather than merely
    /// untidy.** <see cref="UnityGameTranslator.Common.Sync.Decide"/> reads "has local changes" to
    /// tell "I edited this" from "the server moved on". A file edited here whose count still said
    /// zero would be read as the second — and the interface would offer to DOWNLOAD over the work
    /// that had just been done.
    ///
    /// The count is raised by the number of entries that actually differ from what was sent. It is
    /// an approximation in one direction only: the mod recomputes it exactly against the ancestor
    /// at the next launch, and until then over-counting merely protects the file, while
    /// under-counting would offer to overwrite it.
    /// </summary>
    /// <param name="sentJson">What was uploaded when the session opened.</param>
    /// <param name="receivedJson">What the session holds now.</param>
    public TranslationWriteResult WriteEditedSession(GameInstall game, LoaderDescriptor descriptor,
                                                     string sentJson, string receivedJson)
    {
        if (WhyNotNow(game) is { } refusal) return new TranslationWriteResult(false, null, refusal);

        var gamePath = game.Path;
        var folder = Path.Combine(gamePath,
            descriptor.UserDataDir.Replace('/', Path.DirectorySeparatorChar));
        var target = Path.Combine(folder, LocalTranslationProbe.TranslationFileName);

        try
        {
            var prepared = StampEdits(sentJson, receivedJson);

            Directory.CreateDirectory(folder);

            TranslationBackupStore.TakeAutomatic(gamePath, descriptor, BackupReason.Edited);

            string? backup = null;
            if (File.Exists(target)) backup = MoveAside(target);

            var temp = target + ".tmp";
            File.WriteAllText(temp, prepared, new UTF8Encoding(false));
            File.Move(temp, target, overwrite: true);

            return new TranslationWriteResult(true, backup, null);
        }
        catch (Exception ex)
        {
            return new TranslationWriteResult(false, null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Raises _local_changes by what the editing session actually changed, leaving every other key
    /// — including _source.hash — exactly as it came back.
    /// </summary>
    private static string StampEdits(string sentJson, string receivedJson)
    {
        var node = JsonNode.Parse(receivedJson, documentOptions: new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });

        if (node is not JsonObject root) return receivedJson;

        var changed = CountChangedEntries(sentJson, root);
        if (changed <= 0) return receivedJson;

        var previous = root["_local_changes"]?.GetValue<int?>() ?? 0;
        root["_local_changes"] = previous + changed;

        return root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    /// <summary>
    /// How many translated entries differ between what was sent and what came back.
    ///
    /// ⚠ Metadata keys are skipped: they are about the file, not about its content, and the site
    /// rewrites some of them on its own. Counting those would report work nobody did.
    /// </summary>
    private static int CountChangedEntries(string sentJson, JsonObject received)
    {
        JsonNode? sentNode;
        try
        {
            sentNode = JsonNode.Parse(sentJson);
        }
        catch
        {
            // Unreadable: every entry is treated as new, which errs towards protecting the file.
            return received.Count;
        }

        if (sentNode is not JsonObject sent) return received.Count;

        var changed = 0;

        foreach (var entry in received)
        {
            if (UnityGameTranslator.Common.ContentHash.IsMetadataKey(entry.Key)) continue;

            var before = sent[entry.Key];
            if (before is null)
            {
                changed++;
                continue;
            }

            // Compared as written JSON: an entry is {"v":…,"t":…} or a bare string depending on
            // the file's age, and normalising the two shapes here would be a third opinion about
            // what an entry is.
            if (!string.Equals(before.ToJsonString(), entry.Value?.ToJsonString(), StringComparison.Ordinal))
                changed++;
        }

        return changed;
    }

    /// <summary>
    /// Records the server's hash in the file, leaving everything else exactly as received.
    ///
    /// Edited as a JSON tree for the same reason config.json is: the file carries the mod's own
    /// metadata and possibly keys we have never heard of, and rebuilding it from a model here
    /// would silently drop them.
    /// </summary>
    private static string StampSource(string json, string? serverHash)
    {
        if (string.IsNullOrWhiteSpace(serverHash)) return json;

        var node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });

        if (node is not JsonObject root) return json;

        if (root["_source"] is not JsonObject source)
        {
            source = new JsonObject();
            root["_source"] = source;
        }

        source["hash"] = serverHash;

        // Freshly taken from the server, so nothing has been changed locally yet. Leaving a count
        // inherited from whoever uploaded it would make the mod believe the player had edits they
        // never made, and offer to merge them.
        root["_local_changes"] = 0;

        return root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    /// <summary>
    /// Moves the current file into the backup folder under a dated name, and returns where.
    ///
    /// Dated rather than overwritten: someone who takes two translations in a row to compare them
    /// would otherwise destroy the first backup with the second, which is exactly when they most
    /// need both.
    /// </summary>
    private static string MoveAside(string target)
    {
        var folder = Path.Combine(Path.GetDirectoryName(target)!, BackupFolderName);
        Directory.CreateDirectory(folder);

        var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
        var backup = Path.Combine(folder, $"translations-{stamp}.json");

        var attempt = 1;
        while (File.Exists(backup))
            backup = Path.Combine(folder, $"translations-{stamp}-{++attempt}.json");

        File.Move(target, backup);
        Prune(folder);
        return backup;
    }

    /// <summary>
    /// How many replaced translations are kept before the oldest is dropped.
    ///
    /// 🔴 **Bounded, because nothing ever emptied this folder.** Dating each copy is right — the
    /// reason is written above MoveAside — but "keep them all, for ever" is not a decision anybody
    /// took, it is what happens when nobody writes the other half. Ten trials of community
    /// translations left ten files on a player's disk that no screen mentioned and no action could
    /// reach.
    ///
    /// Three: enough to compare two takes and still step back from both, few enough that the
    /// folder never becomes a thing to manage. ⚠ It also bounds what "Put one back" has to show —
    /// a list of thirty dated files is not a choice, it is an archive.
    /// </summary>
    public const int BackupsKept = 3;

    /// <summary>Drops the oldest copies past <see cref="BackupsKept"/>. Never throws.</summary>
    private static void Prune(string folder)
    {
        try
        {
            var stale = Directory.EnumerateFiles(folder, "translations-*.json")
                                 .OrderByDescending(File.GetLastWriteTimeUtc)
                                 .Skip(BackupsKept);

            foreach (var old in stale) File.Delete(old);
        }
        catch
        {
            // Housekeeping. Failing to tidy must never fail the write that was actually asked for.
        }
    }

    /// <summary>
    /// The replaced translations still on disk, newest first, each described by what it CONTAINS.
    ///
    /// ⚠ A dated file name identifies nothing to a reader. What tells one copy from another is how
    /// many lines it holds and which lineage it belongs to — both are in the file, so they are read
    /// from it rather than guessed from when it was written.
    ///
    /// ⚠ The pair of languages is deliberately NOT among them: it lives in config.json, not in the
    /// translation, so a copy set aside carries no record of it. Showing the game's current
    /// languages beside an old file would label it with something that may not be its own.
    /// </summary>
    public static IReadOnlyList<TranslationBackup> Backups(string gamePath, LoaderDescriptor descriptor)
    {
        var folder = BackupFolder(gamePath, descriptor);
        if (folder is null || !Directory.Exists(folder)) return Array.Empty<TranslationBackup>();

        var found = new List<TranslationBackup>();

        foreach (var path in Directory.EnumerateFiles(folder, "translations-*.json"))
        {
            found.Add(new TranslationBackup(
                path,
                File.GetLastWriteTime(path),
                LocalTranslationProbe.ReadLines(path)?.Count ?? 0,
                UuidIn(path)));
        }

        return found.OrderByDescending(b => b.Replaced).ToList();
    }

    private static string? BackupFolder(string gamePath, LoaderDescriptor descriptor)
    {
        if (string.IsNullOrWhiteSpace(descriptor.UserDataDir)) return null;

        return Path.Combine(gamePath,
            descriptor.UserDataDir.Replace('/', Path.DirectorySeparatorChar), BackupFolderName);
    }

    private static string TargetPath(string gamePath, LoaderDescriptor descriptor) =>
        Path.Combine(gamePath,
            descriptor.UserDataDir.Replace('/', Path.DirectorySeparatorChar),
            LocalTranslationProbe.TranslationFileName);

    /// <summary>The lineage a set-aside file belongs to, so a screen can say whether it is ours.</summary>
    private static string? UuidIn(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var document = System.Text.Json.JsonDocument.Parse(stream);

            return document.RootElement.TryGetProperty("_uuid", out var uuid)
                   && uuid.ValueKind == System.Text.Json.JsonValueKind.String
                ? uuid.GetString()
                : null;
        }
        catch
        {
            // A copy we cannot parse is still a copy somebody may want back.
            return null;
        }
    }

    /// <summary>
    /// Moves this game's translation out of the way, so another can be started.
    ///
    /// ⚠ Set aside, never deleted — same folder, same bound, same "Put one back" as any other
    /// replacement. Removing is only ever a special case of replacing here.
    ///
    /// ⚠ Refuses while the game is open, for the reason every write on this class refuses: the mod
    /// rewrites the file from memory on its own timer, so a file removed now simply reappears.
    /// </summary>
    public TranslationWriteResult Remove(GameInstall game, LoaderDescriptor descriptor)
    {
        if (WhyNotNow(game) is { } refusal) return new TranslationWriteResult(false, null, refusal);

        var target = TargetPath(game.Path, descriptor);
        if (!File.Exists(target))
            return new TranslationWriteResult(false, null, "This game holds no translation.");

        try
        {
            // ⚠ Kept, and named as such: "when the translation was removed" is the one row
            // somebody will look for after realising they removed the wrong thing.
            TranslationBackupStore.TakeAutomatic(game.Path, descriptor, BackupReason.Removed);

            var aside = MoveAside(target);

            // ⚠ The ancestor goes too. It describes the file that just left, so keeping it would
            // have the mod compare the NEXT translation against a snapshot of a different one —
            // every line reading as "changed since sync" on a file nobody has touched.
            var ancestor = Path.Combine(Path.GetDirectoryName(target)!,
                                        LocalTranslationProbe.AncestorFileName);
            if (File.Exists(ancestor)) File.Delete(ancestor);

            return new TranslationWriteResult(true, aside, null);
        }
        catch (Exception ex)
        {
            return new TranslationWriteResult(false, null, ex.Message);
        }
    }

    /// <summary>
    /// Puts a replaced translation back, setting aside whatever is in its place first.
    ///
    /// ⚠ The current file is moved aside rather than deleted, so a restore taken by mistake is
    /// itself undoable. That is the whole reason this folder exists, and it would be strange for
    /// the one action that reads it to be the one that destroys something.
    /// </summary>
    public static TranslationWriteResult Restore(string gamePath, LoaderDescriptor descriptor,
                                                 string backupPath)
    {
        if (string.IsNullOrWhiteSpace(descriptor.UserDataDir))
            return new TranslationWriteResult(false, null, "This game has no place for a translation.");

        var target = TargetPath(gamePath, descriptor);

        if (!File.Exists(backupPath))
            return new TranslationWriteResult(false, null, "That copy is no longer on disk.");

        try
        {
            string? aside = null;
            if (File.Exists(target)) aside = MoveAside(target);

            File.Copy(backupPath, target, overwrite: false);
            File.Delete(backupPath);

            return new TranslationWriteResult(true, aside, null);
        }
        catch (Exception ex)
        {
            return new TranslationWriteResult(false, null, ex.Message);
        }
    }

    /// <summary>
    /// Whether the local file could be fetched again from the server as it stands.
    ///
    /// True only when the mod recorded no local changes AND the file it last synced with is the
    /// one the server still holds. Both come from metadata the mod writes, and either can be
    /// missing — an older file, a hand-edited one — in which case the answer is a plain no.
    ///
    /// ⚠ This decides WORDING only. The backup happens either way: a proof that rests on optional
    /// metadata is not a proof to bet somebody's work on.
    /// </summary>
    public static bool LooksRecoverableOnline(LocalTranslation? local, OnlineTranslation? remote)
    {
        if (local is null || remote is null) return false;
        if (local.LocalChanges > 0) return false;
        if (string.IsNullOrWhiteSpace(local.SourceHash) || string.IsNullOrWhiteSpace(remote.FileHash))
            return false;

        return string.Equals(local.SourceHash, remote.FileHash, StringComparison.OrdinalIgnoreCase);
    }
}
