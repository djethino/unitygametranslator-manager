using System.Text.Json.Serialization;

namespace UnityGameTranslator.Installer.Core.Model;

/// <summary>A mod loader found already installed in a game folder.</summary>
public sealed class DetectedLoader
{
    public required string Id { get; init; }
    public required string Display { get; init; }

    /// <summary>Version read from the loader's own files, or null when unreadable.</summary>
    public string? Version { get; init; }

    /// <summary>Where our plugin goes for this loader, relative to the game root.</summary>
    public required string PluginDir { get; init; }

    /// <summary>
    /// True when our receipt says we installed it. Anything else is the user's, and we leave
    /// it strictly alone.
    /// </summary>
    public bool InstalledByUs { get; set; }

    /// <summary>Other plugins/mods sitting next to ours. Blocks removing the loader.</summary>
    public int ForeignPluginCount { get; set; }
}

/// <summary>The translation file already present in the game, if any.</summary>
public sealed class LocalTranslation
{
    public required string Path { get; init; }

    /// <summary>Lineage identifier shared by every fork of this translation.</summary>
    public string? Uuid { get; init; }

    public string? GameName { get; init; }
    public string? SteamId { get; init; }

    /// <summary>Number of real translation entries, metadata keys excluded.</summary>
    public int EntryCount { get; init; }

    /// <summary>Entries changed since the last sync, as recorded by the mod.</summary>
    public int LocalChanges { get; init; }

    public DateTimeOffset? LastWrite { get; init; }
}

/// <summary>A translation offered by the community site for this game.</summary>
public sealed class OnlineTranslation
{
    [JsonPropertyName("id")] public int Id { get; set; }

    /// <summary>Lineage id. The API calls it file_uuid; it matches the mod's local _uuid.</summary>
    [JsonPropertyName("file_uuid")] public string? Uuid { get; set; }

    [JsonPropertyName("source_language")] public string? SourceLanguage { get; set; }
    [JsonPropertyName("target_language")] public string? TargetLanguage { get; set; }

    /// <summary>Who published it. The API field is "uploader".</summary>
    [JsonPropertyName("uploader")] public string? Author { get; set; }

    [JsonPropertyName("line_count")] public int LineCount { get; set; }
    [JsonPropertyName("download_count")] public int DownloadCount { get; set; }
    [JsonPropertyName("vote_count")] public int VoteCount { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("notes")] public string? Notes { get; set; }

    /// <summary>How the translation was produced, e.g. "ai_corrected". Helps the user choose.</summary>
    [JsonPropertyName("type")] public string? Type { get; set; }

    [JsonPropertyName("quality_score")] public double? QualityScore { get; set; }

    /// <summary>
    /// Date the *content* changed. Deliberately not updated_at: a vote or a download bumps
    /// updated_at, so it would show a translation as fresher than it is.
    /// </summary>
    [JsonPropertyName("content_updated_at")] public DateTimeOffset? ContentUpdatedAt { get; set; }

    [JsonPropertyName("resources_url")] public string? ResourcesUrl { get; set; }

    public override string ToString()
    {
        var langs = $"{SourceLanguage ?? "?"} -> {TargetLanguage ?? "?"}";
        var details = new List<string> { $"{LineCount} lines" };
        if (Status is not null) details.Add(Status);
        if (DownloadCount > 0) details.Add($"{DownloadCount} downloads");

        // The date shown is content_updated_at, never updated_at: a vote or a download bumps
        // updated_at, so it would make an abandoned translation look freshly maintained.
        if (ContentUpdatedAt is { } date) details.Add(date.ToString("yyyy-MM-dd"));

        return $"{langs} by {Author ?? "unknown"} ({string.Join(", ", details)})";
    }
}

/// <summary>Everything we know about a game, gathered in one place for display and decisions.</summary>
public sealed class GameReport
{
    public required GameInstall Game { get; init; }
    public DetectedLoader? InstalledLoader { get; set; }
    public LocalTranslation? LocalTranslation { get; set; }
    public IReadOnlyList<OnlineTranslation> OnlineTranslations { get; set; } = Array.Empty<OnlineTranslation>();

    /// <summary>
    /// Set when the community search failed rather than came back empty. Without it, a blocked
    /// network and a game nobody has translated look exactly the same to the user.
    /// </summary>
    public string? OnlineSearchError { get; set; }

    /// <summary>Loader we would install if the user accepts the default. Null when none fits.</summary>
    public LoaderDescriptor? RecommendedLoader { get; set; }

    /// <summary>Why that recommendation — always shown, never a silent choice.</summary>
    public string? RecommendationReason { get; set; }

    /// <summary>Our plugin build id matching the loader in use, e.g. "bepinex6-il2cpp".</summary>
    public string? PluginBuildId { get; set; }

    /// <summary>Installed plugin version read from the deployed assembly, or null.</summary>
    public string? InstalledPluginVersion { get; set; }

    /// <summary>
    /// The community entry that is the same lineage as the local file, matched on uuid.
    ///
    /// This is the difference between "3 translations available" and "you already have this
    /// one, and it changed online since". Without it we would happily offer the user the file
    /// they are already using.
    /// </summary>
    public OnlineTranslation? MatchingOnline { get; set; }

    /// <summary>Community entries that are NOT what the user already has locally.</summary>
    public IEnumerable<OnlineTranslation> AlternativeOnline =>
        MatchingOnline is null
            ? OnlineTranslations
            : OnlineTranslations.Where(t => !ReferenceEquals(t, MatchingOnline));

    /// <summary>Blocking prerequisites, e.g. a missing .NET Desktop Runtime.</summary>
    public List<string> Blockers { get; } = new();

    /// <summary>Non-blocking things the user must know before installing.</summary>
    public List<string> Warnings { get; } = new();
}
