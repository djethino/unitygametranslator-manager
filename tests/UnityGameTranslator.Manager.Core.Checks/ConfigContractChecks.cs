using System.Text.Json.Nodes;
using UnityGameTranslator.Manager.Core.Ai;
using UnityGameTranslator.Manager.Core.Detection;
using UnityGameTranslator.Manager.Core.Install;
using UnityGameTranslator.Manager.Core.Model;
using UnityGameTranslator.Manager.Core.Settings;

namespace UnityGameTranslator.Manager.Core.Checks;

/// <summary>
/// The Manager's reading of a game's config.json against the contract:
/// <c>common/spec/config/cases.json</c>, the `manager` verdict of each case.
///
/// 🔴 **The same file the mod is held to.** What this tool shows of a game's setup is read off a
/// file the mod writes (GameConfigWriter.Read, the mirror of what Intended writes); a key read
/// differently here than the mod means it is a screen that lies about a game. The cases say what
/// each file means, once, for both readers.
///
/// ⚠ Read exactly as the tool reads: the document is written into a game folder made for the
/// occasion, under the loader's data folder, and Read is asked about that game.
/// </summary>
internal static class ConfigContractChecks
{
    internal static void WhatAGamesConfigSays()
    {
        Program.Section("config.json against the spec's cases");

        var casesPath = Find("common", "spec", "config", "cases.json");
        Program.Check(casesPath is not null, "the config contract's cases are found",
            "this check reads them; without them, it proves nothing");
        if (casesPath is null) return;

        var doc = JsonNode.Parse(File.ReadAllText(casesPath))!.AsObject();
        var descriptor = new LoaderDescriptor { Id = "bepinex5", UserDataDir = "BepInEx/plugins/UnityGameTranslator" };
        int held = 0;

        foreach (var node in doc["cases"]!.AsArray())
        {
            var c = node!.AsObject();
            var expects = c["manager"] as JsonObject;
            if (expects is null) continue;
            held++;

            var id = c["id"]!.GetValue<string>();
            var why = c["why"]!.GetValue<string>();
            var text = c["raw"] is JsonNode raw
                ? raw.GetValue<string>()
                : c["document"]!.ToJsonString();

            var gamePath = Path.Combine(Path.GetTempPath(), "ugt-config-contract-" + Guid.NewGuid().ToString("N"));
            try
            {
                var folder = Path.Combine(gamePath, "BepInEx", "plugins", "UnityGameTranslator");
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, LocalTranslationProbe.ConfigFileName), text);

                var snapshot = GameConfigWriter.Read(gamePath, descriptor);
                var derived = Derive(snapshot, GameConfigWriter.ReadAi(gamePath, descriptor));

                var failures = new List<string>();
                foreach (var expectation in expects)
                {
                    if (!derived.TryGetValue(expectation.Key, out var actual))
                    {
                        failures.Add($"{expectation.Key}: not a value this side derives");
                        continue;
                    }
                    var detail = Agrees(expectation.Value, actual);
                    if (detail is not null) failures.Add($"{expectation.Key}: {detail}");
                }

                Program.Check(failures.Count == 0, id, failures.Count == 0 ? why : string.Join("; ", failures) + " — " + why);
            }
            finally
            {
                try { Directory.Delete(gamePath, recursive: true); } catch { /* a temp folder left behind proves nothing */ }
            }
        }

        Program.Check(held >= 6, $"{held} cases carry a reading for this side",
            "fewer than the contract holds: the file was mis-read");
    }

    /// <summary>
    /// The other direction: what this tool WRITES must mean, once the mod has loaded it, what was
    /// asked. The mod rewrites `enable_ai: true` + `translation_backend: none` to `llm` (a file
    /// older than the backend choice, spec/config x-migrations) — so a game set to "Community
    /// translations only" beside Mod defaults' AI switched on started its AI at launch.
    /// </summary>
    internal static void WhatThisToolWritesStaysWhatWasAsked()
    {
        Program.Section("config.json as this tool writes it");

        var descriptor = new LoaderDescriptor { Id = "bepinex5", UserDataDir = "BepInEx/plugins/UnityGameTranslator" };

        foreach (var backend in new[] { "none", "capture", "llm", "google", "deepl" })
        {
            var gamePath = Path.Combine(Path.GetTempPath(), "ugt-config-write-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(gamePath, "BepInEx", "plugins", "UnityGameTranslator"));

                // Mod defaults with translation on — the very situation of the defect.
                var settings = new InstallerSettings { TranslationBackend = backend, EnableAi = true };
                var result = new GameConfigWriter().Apply(gamePath, descriptor, settings, "French");

                var written = JsonNode.Parse(File.ReadAllText(Path.Combine(
                    gamePath, "BepInEx", "plugins", "UnityGameTranslator", LocalTranslationProbe.ConfigFileName)))!.AsObject();
                var enableAi = written["enable_ai"]?.GetValue<bool>();
                var writtenBackend = written["translation_backend"]?.GetValue<string>();

                bool noTranslation = backend is "none" or "capture";
                Program.Check(result.Written && writtenBackend is not null
                              && !(enableAi == true && writtenBackend == "none")
                              && (!noTranslation || enableAi == false),
                    $"backend {backend}: the mod reads back what was asked",
                    $"wrote translation_backend={writtenBackend}, enable_ai={enableAi} — "
                    + "the mod turns enable_ai:true + none into llm at load");
            }
            finally
            {
                try { Directory.Delete(gamePath, recursive: true); } catch { /* a temp folder left behind proves nothing */ }
            }
        }
    }

    private static Dictionary<string, object?> Derive(GameConfigSnapshot s, GameAiSettings ai) => new()
    {
        // How the game asks a backend for a line — what the browser editor's Retranslate is
        // answered with while the game is closed, and the same numbers the mod's "read" states.
        ["Ai.IsTranslationEnabled"] = ai.IsTranslationEnabled,
        ["Ai.BackendLabel"] = ai.Label,
        ["Ai.AttemptsAllowed"] = ai.AttemptsAllowed,
        ["Ai.TemperatureNormal"] = ai.TemperatureNormal,
        ["Ai.TemperatureRepair"] = ai.TemperatureRepair,
        ["Ai.TemperatureRetranslate"] = ai.TemperatureRetranslate,
        ["Ai.SeedRetranslate"] = ai.SeedRetranslate,
        ["Ai.StrictSourceLanguage"] = ai.StrictSourceLanguage,

        ["Exists"] = s.Exists,
        ["FirstRunCompleted"] = s.FirstRunCompleted,
        ["InGameHotkey"] = s.InGameHotkey,
        ["AutoTranslate"] = s.AutoTranslate,
        ["IsConfigured"] = s.IsConfigured,
        ["Values.TargetLanguage"] = s.Values.TargetLanguage,
        ["Values.TranslationBackend"] = s.Values.TranslationBackend,
        ["Values.AiUrl"] = s.Values.AiUrl,
        ["Values.AiModel"] = s.Values.AiModel,
        ["Values.AiApiKey"] = s.Values.AiApiKey,
        ["Values.GoogleApiKey"] = s.Values.GoogleApiKey,
        ["Values.DeeplApiKey"] = s.Values.DeeplApiKey,
        ["Values.DeeplUseFree"] = s.Values.DeeplUseFree,
        ["Values.ModOnlineMode"] = s.Values.ModOnlineMode,
        ["Values.AutoDownload"] = s.Values.AutoDownload,
        ["Values.NotifyUpdates"] = s.Values.NotifyUpdates,
        ["Values.CheckModUpdates"] = s.Values.CheckModUpdates,
        ["Values.MergeStrategy"] = s.Values.MergeStrategy,
        ["Values.NotificationsEnabled"] = s.Values.NotificationsEnabled,
        ["Values.NotificationPosition"] = s.Values.NotificationPosition,
        ["Values.Channel"] = s.Values.Channel,
    };

    private static string? Agrees(JsonNode? expected, object? actual)
    {
        if (expected is null) return actual is null ? null : $"expected null, got {Show(actual)}";
        var value = expected.AsValue();
        if (value.TryGetValue<bool>(out var b)) return Equals(actual, b) ? null : $"expected {b}, got {Show(actual)}";
        if (value.TryGetValue<string>(out var str)) return string.Equals(actual as string, str, StringComparison.Ordinal) ? null : $"expected \"{str}\", got {Show(actual)}";
        if (value.TryGetValue<long>(out var n)) return actual is not null && actual is not bool && actual is not string && Convert.ToInt64(actual) == n ? null : $"expected {n}, got {Show(actual)}";
        // A temperature: written 2.0 in the case, compared as a number, never as text.
        if (value.TryGetValue<double>(out var d)) return actual is double or int && Math.Abs(Convert.ToDouble(actual) - d) < 1e-9 ? null : $"expected {d}, got {Show(actual)}";
        return $"unsupported expectation {expected.ToJsonString()}";
    }

    private static string Show(object? value) => value is null ? "null" : $"\"{value}\"";

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
