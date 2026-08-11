using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using UnityGameTranslator.Manager.Core.Detection;
using UnityGameTranslator.Manager.Core.Model;

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
    /// Writes the file, after moving any existing one aside.
    ///
    /// <paramref name="serverHash"/> is written as _source.hash, which is how the mod later tells
    /// "the server moved on" from "I edited this". Without it every downloaded file would look
    /// locally modified from the first launch, and the mod would offer to merge against nothing.
    /// </summary>
    public TranslationWriteResult Install(string gamePath, LoaderDescriptor descriptor,
                                          string json, string? serverHash)
    {
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
