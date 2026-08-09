using System.Text.Json;
using System.Text.Json.Serialization;
using UnityGameTranslator.Installer.Core.Platform;
using UnityGameTranslator.Installer.Core.Net;

namespace UnityGameTranslator.Installer.Core.Catalog;

/// <summary>
/// How many languages a model's publisher says it handles. Their claim, never our measurement.
///
/// Two numbers because some publishers give two, and collapsing them would mislead exactly the
/// person this is for: a model can be tuned for thirty-five languages and merely have seen a
/// hundred and forty during training. Someone whose language sits in the gap is told the truth —
/// it may work, nobody promised it would.
///
/// ⚠ This is a COUNT, and it must stay one. Turning it into "which languages" — filtering the
/// offer by what somebody is translating into — is the one thing this project does not do
/// anywhere, and a per-language list here would be the first step towards it.
/// </summary>
public sealed class ModelLanguages
{
    /// <summary>Claimed as supported out of the box.</summary>
    [JsonPropertyName("supported")] public int? Supported { get; set; }

    /// <summary>Present in training only, when the publisher distinguishes the two.</summary>
    [JsonPropertyName("pretrained")] public int? Pretrained { get; set; }

    /// <summary>Where the figure was read, so it can be checked rather than believed.</summary>
    [JsonPropertyName("source")] public string? Source { get; set; }

    /// <summary>The claim in one clause, or null when the publisher states nothing.</summary>
    public string? Sentence()
    {
        if (Supported is { } supported && Pretrained is { } pretrained && pretrained > supported)
        {
            return $"the publisher claims {supported} languages out of the box, "
                 + $"and {pretrained} seen during training";
        }

        if (Supported is { } only) return $"the publisher claims {only} languages";
        if (Pretrained is { } trained) return $"the publisher claims {trained} languages seen during training";

        return null;
    }
}

public sealed class ModelNote
{
    /// <summary>Matched as a substring of the model name, case-insensitively.</summary>
    [JsonPropertyName("match")] public string Match { get; set; } = "";

    /// <summary>"ollama", "vllm", "lmstudio"... only used to phrase the line.</summary>
    [JsonPropertyName("runner")] public string? Runner { get; set; }

    /// <summary>"reference" for the one the mod is developed against, "tested" otherwise.</summary>
    [JsonPropertyName("role")] public string Role { get; set; } = "tested";

    [JsonPropertyName("note")] public string Note { get; set; } = "";

    /// <summary>
    /// The exact name to pull, when this is a model we would put in front of someone. Null makes
    /// the entry a note only — right for a family name like "gemma" that matches many things.
    /// </summary>
    [JsonPropertyName("pull")] public string? Pull { get; set; }

    [JsonPropertyName("download_gb")] public double? DownloadGb { get; set; }

    /// <summary>
    /// Card size below which this runs on the processor instead — minutes per line rather than
    /// seconds. Filters what is offered first; never hides the rest.
    /// </summary>
    [JsonPropertyName("min_vram_gb")] public double? MinVramGb { get; set; }

    /// <summary>What the publisher says about language coverage, when they say anything.</summary>
    [JsonPropertyName("languages")] public ModelLanguages? Languages { get; set; }

    [JsonIgnore] public bool IsReference => Role == "reference";

    [JsonIgnore] public bool CanBeInstalled => !string.IsNullOrWhiteSpace(Pull);
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
        _http = http ?? Http.Create(TimeSpan.FromSeconds(10));
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

        // The publisher's claim comes after our own observation, and says whose claim it is. A
        // number this size — two hundred languages — reads as a guarantee unless it is attributed,
        // and we have verified none of them.
        var coverage = note.Languages?.Sentence();
        var claim = coverage is null ? "" : $" On coverage, {coverage}.";

        return $"{lead}{stamp}: {note.Note}{claim}";
    }

    /// <summary>
    /// The models worth offering on this machine, best fit first.
    ///
    /// ⚠ When the card size is unknown, everything is returned with its requirements stated. A
    /// tool that offers nothing because it failed to read a number is worse than one that asks —
    /// and unknown VRAM is common enough (virtual machines, unusual drivers) to be the norm for
    /// somebody.
    ///
    /// ⚠ Ordered by what the machine can run, never by language. Ranking models by "good at X"
    /// would break the rule the whole project rests on.
    /// </summary>
    public static IReadOnlyList<ModelNote> Installable(ModelNotesDocument? document,
                                                       long? videoMemoryBytes)
    {
        if (document is null) return Array.Empty<ModelNote>();

        var offerable = document.Models.Where(note => note.CanBeInstalled).ToList();
        if (videoMemoryBytes is not { } bytes) return offerable;

        var availableGb = bytes / 1024.0 / 1024 / 1024;

        // Fits first, largest of those first — a bigger model that still fits is generally the
        // better translation. What does not fit stays visible, last, with its requirement shown:
        // someone willing to wait is entitled to decide that for themselves.
        return offerable
            .OrderByDescending(note => note.MinVramGb is null || note.MinVramGb <= availableGb)
            .ThenByDescending(note => note.MinVramGb ?? 0)
            .ToList();
    }

    /// <summary>Whether this model fits the card, or null when the card size is unknown.</summary>
    public static bool? Fits(ModelNote note, long? videoMemoryBytes)
    {
        if (videoMemoryBytes is not { } bytes || note.MinVramGb is not { } required) return null;
        return bytes / 1024.0 / 1024 / 1024 >= required;
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
