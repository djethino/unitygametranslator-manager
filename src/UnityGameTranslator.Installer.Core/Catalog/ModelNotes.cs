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

/// <summary>
/// What the model tester found, as figures rather than as a sentence.
///
/// These used to live only inside the note's prose, which meant no screen could sort on them,
/// compare them or put the interesting ones first — it could only stack paragraphs. A measurement
/// worth taking is worth storing in a form something else can read.
/// </summary>
public sealed class ModelMeasurements
{
    /// <summary>Video memory the model actually held, in GB, read from the server.</summary>
    [JsonPropertyName("vram_gb")] public double? VramGb { get; set; }

    /// <summary>Instructions of the suite followed, out of how many.</summary>
    [JsonPropertyName("suite")] public int? Suite { get; set; }
    [JsonPropertyName("suite_of")] public int? SuiteOf { get; set; }

    /// <summary>
    /// What a line costs in waiting, in seconds: the usual one, and the worst of the run.
    ///
    /// Both matter and neither replaces the other. The usual figure is what playing feels like;
    /// the worst is the line somebody notices and remembers. Measured end to end, retries
    /// included, because that is the delay before the original text is replaced on screen.
    /// </summary>
    [JsonPropertyName("typical_s")] public double? TypicalSeconds { get; set; }
    [JsonPropertyName("worst_s")] public double? WorstSeconds { get; set; }

    /// <summary>
    /// Lines the mod had to ask for again, and lines it gave up on, out of how many it tried.
    ///
    /// A retry is not a failure — the mod corrects most of them — but it spends the time and the
    /// graphics card two or three times over, while the game is running. A refusal is text left in
    /// its original language.
    /// </summary>
    [JsonPropertyName("retried")] public int? Retried { get; set; }
    [JsonPropertyName("refused")] public int? Refused { get; set; }
    [JsonPropertyName("lines")] public int? Lines { get; set; }

    /// <summary>Whether it followed the experimental source-language rule in BOTH of its cases.</summary>
    [JsonPropertyName("strict_source")] public bool? StrictSource { get; set; }

    /// <summary>
    /// Followed every instruction and gave up on nothing.
    ///
    /// Retries are deliberately not counted here: needing one is a cost, not a fault, and the mod
    /// exists to absorb exactly that. What disqualifies is a line the model never got right.
    /// </summary>
    [JsonIgnore]
    public bool Flawless => Suite is { } suite && SuiteOf is { } of && suite == of
                            && Refused is null or 0;
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

    /// <summary>What we measured, when we measured it.</summary>
    [JsonPropertyName("measured")] public ModelMeasurements? Measured { get; set; }

    [JsonIgnore] public bool IsReference => Role == "reference";

    [JsonIgnore] public bool CanBeInstalled => !string.IsNullOrWhiteSpace(Pull);
}

public sealed class ModelNotesDocument
{
    [JsonPropertyName("schema")] public int Schema { get; set; }

    /// <summary>The day this file was last touched, shown to the user verbatim.</summary>
    [JsonPropertyName("updated")] public string? Updated { get; set; }

    /// <summary>
    /// The language everything below was measured translating INTO.
    ///
    /// It has to be said, because it bounds every figure in the file: a model can hold its markers
    /// in one language and lose them in another — the text is tokenised differently, comes out at
    /// a different length, and may not even run in the same direction. Nothing here transfers
    /// automatically to somebody translating into Japanese.
    ///
    /// This is also the answer to it: the tester runs in the reader's OWN language, so the file is
    /// a starting point and the button is the verdict.
    /// </summary>
    [JsonPropertyName("measured_in")] public string? MeasuredIn { get; set; }

    /// <summary>
    /// The graphics card the figures were taken on, and the reason it has to be said.
    ///
    /// Memory held is a fact about the model and travels; whether it FITS is a fact about the
    /// card. On a large one everything sits on the card and answers in tenths of a second. On a
    /// smaller one the same model is split with the processor and takes seconds a line — the
    /// answers stay right, they simply arrive too late to read while playing.
    ///
    /// So the figure to compare is "held" against one's own card, and the tester says which of the
    /// two happened on the machine in front of it.
    /// </summary>
    [JsonPropertyName("measured_on")] public string? MeasuredOn { get; set; }

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

        // Nothing is withheld for scoring badly. Every one of these was run and every figure is
        // shown, so a reader can weigh a model that misses one rare shape against one that misses
        // nothing and wants three times the memory. Hiding the imperfect ones sounded prudent
        // until the measurements came in: it left the smallest cards with no choice at all, on the
        // grounds that we knew better than the person who owns the machine.
        static int Rank(ModelNote note, bool fits) =>
            !fits ? 3
            : note.IsReference ? 0
            : note.Measured is { Flawless: true } ? 1
            : 2;

        if (videoMemoryBytes is not { } bytes)
        {
            return offerable
                .OrderBy(note => Rank(note, true))
                .ThenByDescending(note => note.MinVramGb ?? 0)
                .ToList();
        }

        var availableGb = bytes / 1024.0 / 1024 / 1024;

        // What fits comes first whatever its standing: a model that spills out of this card is not
        // a recommendation, it is a trap — it falls back to the processor and takes minutes a
        // line. What does not fit stays visible, last, with its requirement shown, because someone
        // willing to wait is entitled to decide that themselves.
        return offerable
            .OrderBy(note => Rank(note, note.MinVramGb is null || note.MinVramGb <= availableGb))
            .ThenByDescending(note => note.MinVramGb ?? 0)
            .ToList();
    }

    /// <summary>
    /// Why an entry is shown first, in the words the reader gets. Null for everything else —
    /// a badge on every row is a badge on none.
    ///
    /// "Missed nothing" is a statement about a suite of fifteen sentences and four repetitions of
    /// one line, not a certificate. It is worded as a past observation for that reason.
    /// </summary>
    public static string? Standout(ModelNote note) =>
        note.IsReference ? "What we develop against"
        : note.Measured is { Flawless: true } ? "Missed nothing"
        : null;

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
