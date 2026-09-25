using System.Text.Json.Nodes;
using UnityGameTranslator.Manager.Core.Detection;

namespace UnityGameTranslator.Manager.Core.Checks;

/// <summary>
/// The Manager's reading of texts-seen.json against the contract: <c>common/spec/texts-seen/cases.json</c>,
/// the <c>manager.line</c> of each case — the line the card and <c>report</c> print. The mod writes the
/// file; a word read differently here would be a game said to show what it never showed.
/// </summary>
internal static class TextsSeenContractChecks
{
    internal static void WhatAGameShowed()
    {
        Program.Section("texts-seen.json against the spec's cases");

        var casesPath = Find("common", "spec", "texts-seen", "cases.json");
        Program.Check(casesPath is not null, "the texts-seen contract's cases are found",
            "this check reads them; without them, it proves nothing");
        if (casesPath is null) return;

        var doc = JsonNode.Parse(File.ReadAllText(casesPath))!.AsObject();
        int held = 0;
        foreach (var node in doc["cases"]!.AsArray())
        {
            var c = node!.AsObject();
            if (c["manager"] is not JsonObject expects || expects["line"] is not JsonNode line) continue;
            held++;

            var id = c["id"]!.GetValue<string>();
            var why = c["why"]!.GetValue<string>();
            var read = TextSystemsProbe.Parse(c["document"]!.ToJsonString());
            var expected = line.GetValue<string>();

            Program.Check(read is not null && read.Line == expected, id,
                read is null ? $"not read at all — {why}" : read.Line == expected ? why : $"read \"{read.Line}\", the contract says \"{expected}\" — {why}");
        }

        Program.Check(held >= 5, $"{held} cases carry a reading for this side",
            "fewer than the contract holds: the file was mis-read");
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
