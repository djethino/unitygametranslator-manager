using System.Reflection;
using System.Text.Json;
using UnityGameTranslator.Manager.Core.Settings;

namespace UnityGameTranslator.Manager.Core.Checks;

/// <summary>
/// Every rule that has to know "which of a game's answers end up in its config.json" is held to
/// ONE list, beside the fields — and a field added to <see cref="GamePreference"/> without being
/// placed fails here.
///
/// 🔴 Written because that knowledge lived in the window as a hand-written test, and was short four
/// times in a row (2026-09-19): each field added to the class and forgotten there kept the
/// one-click grey over a change the card was showing. Nothing failed, nothing logged.
///
/// ⚠ By reflection on purpose: a check that lists the fields by hand is a fifth list to forget.
/// </summary>
internal static class PreferenceFieldsChecks
{
    internal static void WhatAGamesAnswersAreFor()
    {
        Program.Section("Which of a game's answers end up in its config.json");

        var fields = typeof(GamePreference)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite)
            .ToList();

        var names = fields.Select(p => p.Name).ToList();
        var placed = GamePreference.ForTheConfig.Concat(GamePreference.NotForTheConfig).ToList();

        var unplaced = names.Except(placed).ToList();
        Program.Check(unplaced.Count == 0,
            "every field of GamePreference is placed on one side",
            unplaced.Count == 0
                ? "a new field fails here until somebody decides whether it reaches the config"
                : "not placed: " + string.Join(", ", unplaced));

        var twice = placed.GroupBy(n => n).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        var ghosts = placed.Except(names).ToList();
        Program.Check(twice.Count == 0 && ghosts.Count == 0,
            "and on one side only, naming real fields",
            twice.Concat(ghosts).Any()
                ? "wrong: " + string.Join(", ", twice.Concat(ghosts))
                : "a name on both sides, or a renamed field, would say two things");

        Program.Check(!new GamePreference().HasAnswersForTheConfig,
            "a game nobody answered has nothing for its config",
            "or every game would offer a settings step");

        foreach (var name in GamePreference.ForTheConfig)
        {
            var answered = new GamePreference();
            Give(answered, fields.First(p => p.Name == name));

            Program.Check(answered.HasAnswersForTheConfig,
                $"an answer in {name} is material for a write",
                "the one-click asks this; a field it does not see keeps it grey over a real change");
        }

        // Settling a Setup answered in the game hands every one of these answers to the file.
        var settled = new GamePreference();
        foreach (var name in GamePreference.ForTheConfig) Give(settled, fields.First(p => p.Name == name));
        settled.InstalledTranslationId = 42;
        settled.AdoptLoader = true;
        settled.SettleAfterSetup(new GameConfigSnapshot(true, FirstRunCompleted: true, null, new GameModOverrides()));

        Program.Check(!settled.HasAnswersForTheConfig,
            "SettleAfterSetup gives every one of them back to the file",
            "one left behind would be offered back as a change to write over what the Setup answered");
        Program.Check(settled.InstalledTranslationId == 42 && settled.AdoptLoader,
            "and keeps what the file knows nothing about", "the loader, the installed translation");

        // Copy is the third list written by hand in this class.
        var full = new GamePreference();
        foreach (var field in fields) Give(full, field);

        Program.Check(JsonSerializer.Serialize(full.Copy()) == JsonSerializer.Serialize(full),
            "Copy() carries every field",
            "a field missing from it is an answer a confirmation shows and then loses");
    }

    /// <summary>Sets a field to a value that counts as answered — never its fresh default.</summary>
    private static void Give(GamePreference preference, PropertyInfo field)
    {
        object value = field.PropertyType switch
        {
            var t when t == typeof(bool) => !(bool)field.GetValue(new GamePreference())!,
            var t when t == typeof(bool?) => false,
            var t when t == typeof(string) => "an answer",
            var t when t == typeof(int) => 7,
            var t when t == typeof(int?) => 42,
            var t when t == typeof(GameModOverrides) => new GameModOverrides { TargetLanguage = "French" },
            var t => throw new InvalidOperationException(
                $"{field.Name} is a {t.Name}: say here what an answer to it looks like"),
        };

        field.SetValue(preference, value);
    }
}
