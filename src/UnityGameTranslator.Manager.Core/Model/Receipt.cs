using System.Text.Json.Serialization;

namespace UnityGameTranslator.Manager.Core.Model;

/// <summary>
/// What we actually wrote, where, and whether it was already there.
///
/// This is the piece that makes "uninstall without breaking anything" mechanically true instead
/// of merely promised. Uninstall never works from a hardcoded list of files a loader is supposed
/// to have — loaders change layout — it works from this record of what happened.
/// </summary>
public sealed class Receipt
{
    public const string FileName = ".ugt-manager-receipt.json";

    [JsonPropertyName("schema")] public int Schema { get; set; } = 1;
    [JsonPropertyName("tool_version")] public string ToolVersion { get; set; } = "";
    [JsonPropertyName("installed_at")] public DateTimeOffset InstalledAt { get; set; }
    [JsonPropertyName("updated_at")] public DateTimeOffset? UpdatedAt { get; set; }

    [JsonPropertyName("game")] public ReceiptGame Game { get; set; } = new();
    [JsonPropertyName("loader")] public ReceiptLoader? Loader { get; set; }
    [JsonPropertyName("plugin")] public ReceiptPlugin? Plugin { get; set; }

    [JsonPropertyName("steam_launch_options")] public ReceiptLaunchOptions? LaunchOptions { get; set; }

    /// <summary>"none" | "reused_existing" | "started_existing" | "installed_official" | "installed_portable".</summary>
    [JsonPropertyName("ollama_action")] public string OllamaAction { get; set; } = "none";

    /// <summary>Where this tool installed itself, if the user accepted. Lets the mod find it.</summary>
    [JsonPropertyName("tool_path")] public string? ToolPath { get; set; }
}

public sealed class ReceiptGame
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("steam_id")] public string? SteamId { get; set; }
    [JsonPropertyName("runtime")] public string Runtime { get; set; } = "";
    [JsonPropertyName("unity")] public string? Unity { get; set; }
}

public sealed class ReceiptLoader
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";

    /// <summary>
    /// False when the loader was already installed. Then we never touch it — not to update it,
    /// not to remove it. It is not ours.
    /// </summary>
    [JsonPropertyName("installed_by_us")] public bool InstalledByUs { get; set; }

    [JsonPropertyName("files")] public List<ReceiptFile> Files { get; set; } = new();

    /// <summary>Directories we created, deepest last. Only ever removed if still empty.</summary>
    [JsonPropertyName("dirs_created")] public List<string> DirsCreated { get; set; } = new();
}

public sealed class ReceiptPlugin
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";

    /// <summary>Loader id this build targets, e.g. "bepinex5".</summary>
    [JsonPropertyName("build")] public string Build { get; set; } = "";

    [JsonPropertyName("files")] public List<ReceiptFile> Files { get; set; } = new();
    [JsonPropertyName("dirs_created")] public List<string> DirsCreated { get; set; } = new();
}

public sealed class ReceiptFile
{
    /// <summary>Path relative to the game root, with forward slashes.</summary>
    [JsonPropertyName("path")] public string Path { get; set; } = "";

    /// <summary>SHA-256 as written by us. If it differs at uninstall time, the user edited it.</summary>
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";

    /// <summary>True when the file already existed and we overwrote it (backup kept).</summary>
    [JsonPropertyName("pre_existing")] public bool PreExisting { get; set; }

    /// <summary>Backup of the previous content, relative to the game root.</summary>
    [JsonPropertyName("backup")] public string? Backup { get; set; }
}

public sealed class ReceiptLaunchOptions
{
    [JsonPropertyName("written")] public bool Written { get; set; }
    [JsonPropertyName("value")] public string Value { get; set; } = "";
}
