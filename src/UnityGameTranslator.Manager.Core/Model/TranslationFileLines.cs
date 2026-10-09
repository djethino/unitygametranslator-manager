using System.Text.Json;
using System.Text.Json.Nodes;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Manager.Core.Model;

/// <summary>
/// Is this entry of a translations.json a line of the game's text — the socle's rule
/// (<see cref="TranslationFileKeys.IsLine"/>), the value's shape read in the Manager's JSON.
///
/// 🔴 Every reader of a raw translation file in the Manager asks THIS, never « starts with an
/// underscore » (2026-10-09): a game's text can begin with one, and the prefix rule hid it — not
/// counted, not merged, not hashed — while the mod and the site now read it as a line.
/// </summary>
public static class TranslationFileLines
{
    public static bool IsLine(string name, JsonElement value)
        => TranslationFileKeys.IsLine(name, value.ValueKind == JsonValueKind.Object && value.TryGetProperty("v", out _));

    public static bool IsLine(string name, JsonNode? value)
        => TranslationFileKeys.IsLine(name, value is JsonObject line && line.ContainsKey("v"));
}
