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

    /// <summary>The .NET libraries put beside the game, when it lacked some. Null otherwise.</summary>
    [JsonPropertyName("runtime_libraries")] public ReceiptRuntimeLibraries? RuntimeLibraries { get; set; }

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

/// <summary>
/// The class libraries this tool put beside a game that lacked them, and the one line it added to
/// the loader's configuration so the runtime finds them.
///
/// ⚠ The configuration file is NOT among <see cref="Files"/>: it is the loader's, and removing our
/// libraries takes our entry back out of it rather than deleting it. Taken out, the file is again
/// exactly what the loader shipped, so the loader's own receipt still recognises it.
/// </summary>
public sealed class ReceiptRuntimeLibraries
{
    /// <summary>The Unity version the .NET copies were chosen for ("2018.4.36"); empty when none were needed.</summary>
    [JsonPropertyName("unity")] public string Unity { get; set; } = "";

    /// <summary>
    /// Where the .NET copies came from, as it was named to the person — "the Unity 2021.3.6f1 editor
    /// on this computer", Unity's editor package. (An address, for the copies of an earlier release
    /// of this tool, which took them from BepInEx's archive.)
    /// </summary>
    [JsonPropertyName("source")] public string Source { get; set; } = "";

    /// <summary>The source's id (Install.ClassLibrarySource.Id); empty for copies from before sources had one.</summary>
    [JsonPropertyName("source_id")] public string SourceId { get; set; } = "";

    /// <summary>The .NET libraries written.</summary>
    [JsonPropertyName("files")] public List<ReceiptFile> Files { get; set; } = new();

    /// <summary>The engine modules written, when the game's build had stripped them. Null otherwise.</summary>
    [JsonPropertyName("modules")] public ReceiptEngineModules? Modules { get; set; }
    [JsonPropertyName("dirs_created")] public List<string> DirsCreated { get; set; } = new();

    /// <summary>The loader configuration file holding the search path, relative to the game.</summary>
    [JsonPropertyName("config_file")] public string ConfigFile { get; set; } = "";

    /// <summary>The entry added to it — our folder.</summary>
    [JsonPropertyName("config_entry")] public string ConfigEntry { get; set; } = "";

    /// <summary>True when the configuration file did not exist and we created it.</summary>
    [JsonPropertyName("config_created")] public bool ConfigCreated { get; set; }
}

/// <summary>
/// Unity's engine modules put beside a game whose build stripped them — the whole set, and where
/// it came from, so the card can say it and a game update can be told from an install.
/// </summary>
public sealed class ReceiptEngineModules
{
    /// <summary>The Unity release of the copies ("2021.3.6f1") — an older one of the branch when that was the source.</summary>
    [JsonPropertyName("unity")] public string Unity { get; set; } = "";

    /// <summary>
    /// The game's own release when they were written. What tells a game update (the game moved on,
    /// the copies no longer fit) from an older source chosen on purpose (the copies never matched).
    /// </summary>
    [JsonPropertyName("game_unity")] public string GameUnity { get; set; } = "";

    /// <summary>"editor", "game" or "unity" — see Install.EngineModuleSourceKind.</summary>
    [JsonPropertyName("source_kind")] public string SourceKind { get; set; } = "";

    /// <summary>The source's id, as a person's choice names it (Install.EngineModuleSource.Id).</summary>
    [JsonPropertyName("source_id")] public string SourceId { get; set; } = "";

    /// <summary>The source, as it was named to the person when they were written.</summary>
    [JsonPropertyName("source")] public string Source { get; set; } = "";

    [JsonPropertyName("files")] public List<ReceiptFile> Files { get; set; } = new();
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
