using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using UnityGameTranslator.Manager.Core.Detection;
using UnityGameTranslator.Manager.Core.Model;
using UnityGameTranslator.Manager.Core.Platform;

namespace UnityGameTranslator.Manager.Core.Install;

/// <summary>What happened, and where the previous file went.</summary>
public sealed record TranslationWriteResult(
    bool Written,
    string? BackupPath,
    string? Failure);

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
    public TranslationWriteResult Install(GameInstall game, LoaderDescriptor descriptor,
                                          string json, string? serverHash)
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

            string? backup = null;
            if (File.Exists(target)) backup = MoveAside(target);

            // Written beside then moved: a file half-written by a crash or a full disk would take
            // the place of a translation that was working a second earlier.
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
        return backup;
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
