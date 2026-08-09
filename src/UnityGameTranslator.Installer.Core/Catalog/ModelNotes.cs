using System.Text.Json;
using System.Text.Json.Serialization;
using UnityGameTranslator.Installer.Core.Platform;

namespace UnityGameTranslator.Installer.Core.Catalog;

public sealed class ModelNote
{
    /// <summary>Matched as a substring of the model name, case-insensitively.</summary>
    [JsonPropertyName("match")] public string Match { get; set; } = "";

    /// <summary>"ollama", "vllm", "lmstudio"... only used to phrase the line.</summary>
    [JsonPropertyName("runner")] public string? Runner { get; set; }

    /// <summary>"reference" for the one the mod is developed against, "tested" otherwise.</summary>
    [JsonPropertyName("role")] public string Role { get; set; } = "tested";

    [JsonPropertyName("note")] public string Note { get; set; } = "";

    [JsonIgnore] public bool IsReference => Role == "reference";
}

public sealed class ModelNotesDocument
{
    [JsonPropertyName("schema")] public int Schema { get; set; }

    /// <summary>The day this file was last touched, shown to the user verbatim.</summary>
    [JsonPropertyName("updated")] public string? Updated { get; set; }

    [JsonPropertyName("models")] public List<ModelNote> Models { get; set; } = new();
}

/// <summary>
/// What we have actually run, fetched at runtime.
///
/// Deliberately not compiled in: models age in weeks, the installer does not update itself, and
/// a name baked into the binary would keep being shown long after it stopped being a sensible
/// starting point. Nothing here is a recommendation — the suite is a heuristic on free text and
/// the machine matters as much as the model, so this only says where someone might start.
///
/// Unlike the loader catalog, a missing file costs nothing: no note is shown, and every screen
/// works exactly as before. So there is no embedded fallback and no error surfaced.
/// </summary>
public sealed class ModelNotesProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly IPlatform _platform;
    private readonly HttpClient _http;
    private ModelNotesDocument? _loaded;

    public ModelNotesProvider(IPlatform platform, HttpClient? http = null)
    {
        _platform = platform;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    private string CachePath => Path.Combine(_platform.UserDataDirectory, "models.cache.json");

    public async Task<ModelNotesDocument?> GetAsync(bool offline = false,
                                                    CancellationToken ct = default)
    {
        if (_loaded is not null) return _loaded;

        if (!offline)
        {
            foreach (var url in new[]
                     {
                         BuildInfo.CatalogPrimaryBase + "/models.json",
                         BuildInfo.CatalogMirrorBase + "/models.json",
                     })
            {
                try
                {
                    var json = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
                    var document = Deserialize(json);
                    if (document is null) continue;

                    TryWriteCache(json);
                    return _loaded = document;
                }
                catch
                {
                    // Next source, then the cache. A note nobody sees is not worth a message.
                }
            }
        }

        try
        {
            if (File.Exists(CachePath))
                return _loaded = Deserialize(File.ReadAllText(CachePath));
        }
        catch
        {
            // Unreadable cache: same as no cache.
        }

        return null;
    }

    /// <summary>
    /// The note for a model name, or null. Longest match wins, so "translategemma" is answered
    /// by its own line rather than by the broader "gemma" one.
    /// </summary>
    public static ModelNote? For(ModelNotesDocument? document, string modelName) =>
        document?.Models
                 .Where(note => !string.IsNullOrWhiteSpace(note.Match)
                                && modelName.Contains(note.Match, StringComparison.OrdinalIgnoreCase))
                 .OrderByDescending(note => note.Match.Length)
                 .FirstOrDefault();

    /// <summary>
    /// The line shown beside a model, dated on purpose: "tested in August 2026" reads as an
    /// observation with an age, where a bare sentence would read as a standing promise.
    /// </summary>
    public static string? Describe(ModelNotesDocument? document, string modelName)
    {
        var note = For(document, modelName);
        if (note is null) return null;

        var lead = note.IsReference ? "Reference model" : "Tested";
        var stamp = string.IsNullOrWhiteSpace(document?.Updated) ? "" : $" (as of {document!.Updated})";

        return $"{lead}{stamp}: {note.Note}";
    }

    private static ModelNotesDocument? Deserialize(string json)
    {
        try
        {
            var document = JsonSerializer.Deserialize<ModelNotesDocument>(json, JsonOptions);
            return document is { Models.Count: > 0 } ? document : null;
        }
        catch
        {
            return null;
        }
    }

    private void TryWriteCache(string json)
    {
        try
        {
            Directory.CreateDirectory(_platform.UserDataDirectory);
            File.WriteAllText(CachePath, json);
        }
        catch
        {
            // A read-only disk must not break a fetch that already succeeded.
        }
    }
}
