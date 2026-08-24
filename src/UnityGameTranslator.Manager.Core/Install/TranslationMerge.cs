using System.Text.Json;
using System.Text.Json.Nodes;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Manager.Core.Install;

/// <summary>What a merge would do, in figures somebody can judge before agreeing to it.</summary>
/// <param name="TakenFromServer">Lines the published version brings, that nothing here contests.</param>
/// <param name="KeptHere">Lines of yours the published version does not have, or does not outrank.</param>
/// <param name="RemovedHere">
/// Lines this file HAS and the merge would take out of it: the published version dropped them and
/// nothing here had touched them since, so honouring its deletion means deleting them here.
///
/// 🔴 **Counted only when the line is actually in this file.** It used to be every key the shared
/// rule answered <c>Deleted</c> for — which covers three situations, and two of them change nothing
/// here at all (a key gone from both sides, a key only the published version had dropped). Those
/// were reported as "removed on both sides", so a real deletion of somebody's lines was worded as a
/// no-op. On the file that prompted this it was thirteen lines, and the sentence said they were
/// already gone everywhere.
/// </param>
/// <param name="Conflicts">Lines both sides changed, which nobody can settle without being asked.</param>
public sealed record MergeSummary(int TakenFromServer, int KeptHere, int RemovedHere, int Conflicts)
{
    public bool HasConflicts => Conflicts > 0;

    /// <summary>
    /// Nothing of this file's own is at stake: everything it holds either stays as it is or is
    /// unaffected. The act is then taking the published version, not settling two versions — and
    /// calling it a merge asks somebody to arbitrate a disagreement that does not exist.
    ///
    /// ⚠ Removals do NOT make it a merge, and they are the reason this is not simply
    /// <c>KeptHere == 0</c> being read at the call site: they are the published version's decision
    /// being honoured, not a disagreement. They still have to be SAID — they delete lines here.
    /// </summary>
    public bool NothingOfYoursAtStake => KeptHere == 0 && Conflicts == 0;

    /// <summary>Nothing to do: the two agree line for line.</summary>
    public bool Empty => TakenFromServer == 0 && KeptHere == 0 && RemovedHere == 0 && Conflicts == 0;
}

/// <summary>
/// Settling a local translation against the published one, without a game running.
///
/// ⚠ **The verdicts come from <see cref="Merge"/>, the shared rule.** This class holds no opinion
/// about who wins: it reads the three files, asks per key, and assembles the answer. A second
/// opinion here is exactly what the mod's file writer warned against — "a second truth about the
/// same file, and the one that ran last would win".
///
/// ⚠ **Entries are carried over as WRITTEN, never rebuilt.** A line is `{"v":…,"t":…}` or a bare
/// string depending on the file's age, and the server hashes what it is given: rebuilding entries
/// from parsed values would produce a different document for a file nobody edited, which reads as
/// permanently out of sync in both directions.
///
/// ⚠ **Metadata comes from the local file.** _uuid, _game and anything this tool has never heard of
/// belong to the copy being kept, not to the one being merged in.
/// </summary>
public sealed class TranslationMerge
{
    private readonly JsonObject _local;
    private readonly JsonObject _remote;
    private readonly JsonObject? _ancestor;
    private readonly Dictionary<string, MergeDecision> _decisions = new(StringComparer.Ordinal);

    private TranslationMerge(JsonObject local, JsonObject remote, JsonObject? ancestor)
    {
        _local = local;
        _remote = remote;
        _ancestor = ancestor;
    }

    /// <summary>
    /// Work out what merging would do. Returns null when a file could not be read at all — which
    /// is not "nothing to do", and callers must keep the two apart.
    /// </summary>
    /// <param name="ancestorJson">
    /// The snapshot from the last sync. ⚠ Null is allowed and changes the outcome: without it,
    /// every line the two sides disagree about is a conflict, because there is no way to tell who
    /// moved. That is the honest answer, not a failure.
    /// </param>
    public static TranslationMerge? Build(string localJson, string remoteJson, string? ancestorJson)
    {
        var local = Parse(localJson);
        var remote = Parse(remoteJson);
        if (local is null || remote is null) return null;

        var merge = new TranslationMerge(local, remote, ancestorJson is null ? null : Parse(ancestorJson));
        merge.Decide();
        return merge;
    }

    /// <summary>The keys nobody can settle without being asked.</summary>
    public IReadOnlyList<string> ConflictKeys { get; private set; } = Array.Empty<string>();

    public MergeSummary Summary { get; private set; } = new(0, 0, 0, 0);

    private void Decide()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in _local) if (!ContentHash.IsMetadataKey(entry.Key)) keys.Add(entry.Key);
        foreach (var entry in _remote) if (!ContentHash.IsMetadataKey(entry.Key)) keys.Add(entry.Key);
        if (_ancestor is not null)
            foreach (var entry in _ancestor) if (!ContentHash.IsMetadataKey(entry.Key)) keys.Add(entry.Key);

        var conflicts = new List<string>();
        int fromServer = 0, kept = 0, removed = 0;

        foreach (var key in keys)
        {
            var decision = Merge.Decide(LineOf(_local, key), LineOf(_remote, key), LineOf(_ancestor, key));
            _decisions[key] = decision;

            if (decision.IsConflict)
            {
                conflicts.Add(key);
                continue;
            }

            switch (decision.Reason)
            {
                case MergeReason.RemoteAdded:
                case MergeReason.RemoteUpdated:
                    fromServer++;
                    break;
                case MergeReason.LocalOnly:
                case MergeReason.LocalModified:
                    kept++;
                    break;
                case MergeReason.Deleted:
                    // ⚠ Only when this file actually holds it. The same verdict covers a key that
                    // was already gone from both sides and one that only the published version had
                    // dropped — neither changes anything here, and counting them made a real
                    // deletion of somebody's lines indistinguishable from a no-op.
                    if (_local.ContainsKey(key)) removed++;
                    break;
            }
        }

        conflicts.Sort(StringComparer.Ordinal);
        ConflictKeys = conflicts;
        Summary = new MergeSummary(fromServer, kept, removed, conflicts.Count);
    }

    /// <summary>
    /// The merged file, as text.
    ///
    /// ⚠ Refuses while anything is in conflict. A merge that resolved those itself — by taking one
    /// side, or the newest — would be deciding something only a person can decide, and doing it
    /// silently on somebody's work.
    /// </summary>
    public string? BuildMergedJson()
    {
        if (Summary.HasConflicts) return null;

        // Built from the local file so its metadata survives untouched — including keys this tool
        // has never heard of.
        var merged = new JsonObject();

        foreach (var entry in _local)
        {
            if (ContentHash.IsMetadataKey(entry.Key)) merged[entry.Key] = entry.Value?.DeepClone();
        }

        foreach (var pair in _decisions)
        {
            JsonNode? node = pair.Value.Verdict switch
            {
                MergeVerdict.TakeLocal => Node(_local, pair.Key),
                MergeVerdict.TakeRemote => Node(_remote, pair.Key),
                _ => null,
            };

            if (node is not null) merged[pair.Key] = node.DeepClone();
        }

        return merged.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    /// <summary>
    /// How many merged lines the published version does NOT have — what would need publishing.
    ///
    /// ⚠ This is what _local_changes must say after a merge, and it is counted against the REMOTE
    /// file rather than against the merged one. The ancestor moves to the published content (that
    /// is the version we have now seen), so "changed since the last sync" means "differs from what
    /// is published" from this moment on.
    /// </summary>
    public int CountAheadOfServer(string mergedJson)
    {
        var merged = Parse(mergedJson);
        if (merged is null) return 0;

        var ahead = 0;

        foreach (var entry in merged)
        {
            if (ContentHash.IsMetadataKey(entry.Key)) continue;

            var there = LineOf(_remote, entry.Key);
            if (there is null) { ahead++; continue; }

            if (!Merge.Same(LineOf(merged, entry.Key)!.Value, there.Value)) ahead++;
        }

        return ahead;
    }

    private static JsonObject? Parse(string json)
    {
        try
        {
            return JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            }) as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    private static JsonNode? Node(JsonObject? source, string key) =>
        source is not null && source.TryGetPropertyValue(key, out var node) ? node : null;

    /// <summary>One entry as the shared rule sees it, or nothing when this side has no such key.</summary>
    private static TranslationLine? LineOf(JsonObject? source, string key)
    {
        var node = Node(source, key);
        if (node is null) return null;

        if (node is JsonValue value && value.TryGetValue<string>(out var bare))
            return TranslationLine.Bare(bare);

        if (node is not JsonObject entry) return TranslationLine.Bare(null);

        string? text = entry.TryGetPropertyValue("v", out var v) && v is JsonValue vv
                       && vv.TryGetValue<string>(out var s) ? s : null;

        string? tag = entry.TryGetPropertyValue("t", out var t) && t is JsonValue tv
                      && tv.TryGetValue<string>(out var g) ? g : null;

        return new TranslationLine(text, tag);
    }
}
