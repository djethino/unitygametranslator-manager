using System.Text.Json;
using System.Text.Json.Nodes;
using UnityGameTranslator.Common;
using UnityGameTranslator.Manager.Core.Detection;
using UnityGameTranslator.Manager.Core.Install;
using UnityGameTranslator.Manager.Core.Model;

namespace UnityGameTranslator.Manager.Core.Checks;

/// <summary>
/// The moments of the translation file the Manager takes part in — a download installed from
/// here, a merge written from here, a publication sent from here — replayed on a game folder made for the occasion and held to
/// <c>common/spec/translation-file/moments.json</c>, the same cases the mod's store is held to.
///
/// 🔴 The Manager's stamps (<c>_source.hash</c>, <c>_source.site_id</c>, <c>_local_changes</c>)
/// and the ancestor it writes beside the file are read by the MOD at the game's next launch. A
/// count left as the uploader had it, or an ancestor that is the merged file rather than the
/// published one, is a wrong sync verdict in the game — and nothing here would have said so.
/// </summary>
internal static class MomentsContractChecks
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    internal static void WhatTheFileSaysAfterEachMoment()
    {
        Program.Section("The moments of the translation file, from the Manager's side");

        var casesPath = Find("common", "spec", "translation-file", "moments.json");
        Program.Check(casesPath is not null, "the moments' cases are found",
            "this check reads them; without them, it proves nothing");
        if (casesPath is null) return;

        var doc = JsonNode.Parse(File.ReadAllText(casesPath))!.AsObject();
        var descriptor = new LoaderDescriptor { Id = "bepinex5", UserDataDir = "BepInEx/plugins/UnityGameTranslator" };
        int held = 0, skipped = 0;

        foreach (var node in doc["cases"]!.AsArray())
        {
            var c = node!.AsObject();
            var heldBy = c["held_by"]!.AsArray().Select(t => t!.GetValue<string>()).ToList();
            if (!heldBy.Contains("manager")) { skipped++; continue; }
            held++;

            var id = c["id"]!.GetValue<string>();
            var why = c["why"]!.GetValue<string>();
            var gamePath = Path.Combine(Path.GetTempPath(), "ugt-moments-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(gamePath);
                var failures = Replay(gamePath, descriptor, c["acts"]!.AsArray(), c["expect"]!.AsObject());
                Program.Check(failures.Count == 0, id, failures.Count == 0 ? why : string.Join("; ", failures) + " — " + why);
            }
            catch (Exception e)
            {
                Program.Check(false, id, $"the replay threw {e.GetType().Name}: {e.Message} — {why}");
            }
            finally
            {
                try { Directory.Delete(gamePath, recursive: true); } catch { /* a temp folder left behind proves nothing */ }
            }
        }

        Program.Check(held >= 2, $"{held} cases held here, {skipped} left to the mod",
            "fewer than the contract holds: the file was mis-read");
    }

    private static List<string> Replay(string gamePath, LoaderDescriptor descriptor, JsonArray acts, JsonObject expect)
    {
        var installer = new TranslationInstaller(platform: null);
        var game = new GameInstall { Name = "moments", Path = gamePath };
        var folder = Path.Combine(gamePath, "BepInEx", "plugins", "UnityGameTranslator");
        var file = Path.Combine(folder, LocalTranslationProbe.TranslationFileName);
        var ancestor = Path.Combine(folder, LocalTranslationProbe.AncestorFileName);
        const string uuid = "00000000-0000-4000-8000-000000000001";

        foreach (var node in acts)
        {
            var act = node!.AsObject().First();
            var a = act.Value!.AsObject();
            switch (act.Key)
            {
                case "download":
                {
                    var json = Document(uuid, a["lines"]!.AsObject());
                    if (a["local_changes_in_file"] is JsonNode carried) json["_local_changes"] = carried.GetValue<int>();
                    var result = installer.Install(game, descriptor, json.ToJsonString(Pretty),
                        a["hash"]?.GetValue<string>(), null, a["site_id"]?.GetValue<int?>());
                    if (!result.Written) throw new InvalidOperationException($"download refused: {result.Failure}");
                    break;
                }
                case "edit":
                {
                    // The file changed on this machine: what the mod does at a save, done here by hand.
                    var root = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
                    root[a["key"]!.GetValue<string>()] = new JsonObject
                    {
                        ["v"] = a["v"]!.GetValue<string>(),
                        ["t"] = a["t"]?.GetValue<string>() ?? "A",
                    };
                    File.WriteAllText(file, root.ToJsonString(Pretty));
                    break;
                }
                case "merge":
                {
                    if (a["published"] is not JsonObject publishedLines)
                        throw new InvalidOperationException("the Manager merges against a published file it holds; a merge without one is the mod's case");
                    var remoteJson = Document(uuid, publishedLines).ToJsonString(Pretty);

                    // The merged file keeps the local file's metadata, as the real merge builds it.
                    var local = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
                    var merged = new JsonObject();
                    foreach (var entry in local)
                        if (ContentHash.IsMetadataKey(entry.Key)) merged[entry.Key] = entry.Value?.DeepClone();
                    foreach (var entry in a["merged"]!.AsObject())
                        merged[entry.Key] = entry.Value?.DeepClone();
                    var mergedJson = merged.ToJsonString(Pretty);

                    var ancestorJson = File.Exists(ancestor) ? File.ReadAllText(ancestor) : null;
                    var plan = TranslationMerge.Build(File.ReadAllText(file), remoteJson, ancestorJson)
                               ?? throw new InvalidOperationException("the files could not be read for the merge");
                    int ahead = plan.CountAheadOfServer(mergedJson);

                    var result = installer.WriteMerged(game, descriptor, mergedJson, remoteJson, a["hash"]?.GetValue<string>(), ahead);
                    if (!result.Written) throw new InvalidOperationException($"merge refused: {result.Failure}");
                    break;
                }
                case "upload":
                {
                    // Publishing from here: the file as it stands is what is sent, and the site's
                    // answer is stamped back into it (NotePublished).
                    var sent = File.ReadAllText(file);
                    var result = installer.NotePublished(game, descriptor, sent,
                        a["hash"]?.GetValue<string>(), a["site_id"]?.GetValue<int?>());
                    if (!result.Written) throw new InvalidOperationException($"upload refused: {result.Failure}");
                    break;
                }
                case "write":
                    // The Manager writes the file WITH the moment (Install, WriteMerged): the count
                    // is measured as the merged file is stamped, never carried. Nothing to do here.
                    break;
                default:
                    throw new InvalidOperationException($"act '{act.Key}' is not one the Manager takes part in");
            }
        }

        // ── The facts, read off the files as the mod would ──────────────────
        var failures = new List<string>();
        var written = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        var source = written["_source"] as JsonObject;

        foreach (var e in expect)
        {
            switch (e.Key)
            {
                case "uuid": Same(failures, e.Key, e.Value?.GetValue<string>(), written["_uuid"]?.GetValue<string>()); break;
                case "source_hash": Same(failures, e.Key, e.Value?.GetValue<string>(), source?["hash"]?.GetValue<string>()); break;
                case "site_id": Same(failures, e.Key, e.Value?.GetValue<int?>(), source?["site_id"]?.GetValue<int?>()); break;
                case "main_hash": Same(failures, e.Key, e.Value?.GetValue<string>(), source?["main_hash"]?.GetValue<string>()); break;
                case "local_changes": Same(failures, e.Key, e.Value!.GetValue<int>(), written["_local_changes"]?.GetValue<int>() ?? 0); break;
                case "forked_from":
                    if (e.Value is null ? written["_forked_from"] is not null : written["_forked_from"] is null)
                        failures.Add($"forked_from: expected {(e.Value is null ? "null" : "a block")}, got {(written["_forked_from"] is null ? "null" : "a block")}");
                    break;
                case "ancestor": SameFile(failures, e.Key, e.Value, ancestor); break;
                case "main_ancestor": SameFile(failures, e.Key, e.Value, TranslationFiles.MainAncestorOf(file)); break;
                default: failures.Add($"{e.Key}: not a fact this side derives"); break;
            }
        }
        return failures;
    }

    private static JsonObject Document(string uuid, JsonObject lines)
    {
        var doc = new JsonObject { ["_uuid"] = uuid };
        foreach (var entry in lines) doc[entry.Key] = entry.Value?.DeepClone();
        return doc;
    }

    private static void Same<T>(List<string> failures, string fact, T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            failures.Add($"{fact}: expected {expected?.ToString() ?? "null"}, got {actual?.ToString() ?? "null"}");
    }

    private static void SameFile(List<string> failures, string fact, JsonNode? expected, string path)
    {
        bool exists = File.Exists(path);
        if (expected is JsonValue v && v.TryGetValue<string>(out var word) && word == "absent")
        {
            if (exists) failures.Add($"{fact}: expected no file, found one");
            return;
        }
        if (!exists) { failures.Add($"{fact}: expected lines, found no file"); return; }

        var parsed = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var actual = new JsonObject();
        foreach (var p in parsed)
            if (!p.Key.StartsWith('_')) actual[p.Key] = p.Value?.DeepClone();
        if (!JsonNode.DeepEquals(actual, expected))
            failures.Add($"{fact}: expected {expected?.ToJsonString()}, got {actual.ToJsonString()}");
    }

    private static string? Find(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
