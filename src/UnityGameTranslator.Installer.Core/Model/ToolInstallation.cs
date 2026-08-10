using System.Text.Json.Serialization;

namespace UnityGameTranslator.Installer.Core.Model;

/// <summary>Where a launcher can be put, when the system has such a place at all.</summary>
public enum LauncherKind
{
    /// <summary>Start menu on Windows, applications menu on Linux.</summary>
    Menu,

    /// <summary>The desktop. Offered, never ticked by default — people feel strongly both ways.</summary>
    Desktop,
}

/// <summary>
/// What the tool wrote when it installed itself, so that removing it reads the real thing.
///
/// The same principle as the receipt left in a game folder, for the same reason: a removal driven
/// by a list of what we THINK is there deletes what it should not the day the layout changes.
///
/// ⚠ It lives in the settings folder, not in the installation folder — removing the tool deletes
/// that folder, and a record you delete halfway through the job you were using it for is no record
/// at all. Which is also why "remove the settings" is asked last and acted on last.
/// </summary>
public sealed class ToolInstallation
{
    public const string FileName = "installation.json";

    [JsonPropertyName("schema")] public int Schema { get; set; } = 1;

    [JsonPropertyName("version")] public string Version { get; set; } = "";

    [JsonPropertyName("installed_at")] public DateTimeOffset InstalledAt { get; set; }

    [JsonPropertyName("updated_at")] public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>The folder the tool was copied into.</summary>
    [JsonPropertyName("directory")] public string Directory { get; set; } = "";

    /// <summary>The installed executable, which is what a launcher points at.</summary>
    [JsonPropertyName("executable")] public string Executable { get; set; } = "";

    /// <summary>Every file written into the installation folder.</summary>
    [JsonPropertyName("files")] public List<string> Files { get; set; } = new();

    /// <summary>Shortcuts and menu entries, wherever the system keeps them.</summary>
    [JsonPropertyName("launchers")] public List<string> Launchers { get; set; } = new();

    /// <summary>
    /// How the system's own list of installed applications knows about us — a registry key on
    /// Windows. Null when we did not register, which is a fact to keep rather than to infer: a
    /// removal must not go hunting for a key it never wrote.
    /// </summary>
    [JsonPropertyName("registration")] public string? Registration { get; set; }

    /// <summary>
    /// True when the installation folder did not exist before us. Only then may removal delete the
    /// folder itself rather than just the files it holds.
    /// </summary>
    [JsonPropertyName("created_directory")] public bool CreatedDirectory { get; set; }
}
