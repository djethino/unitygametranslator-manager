using System.Text.Json;
using System.Text.Json.Serialization;
using UnityGameTranslator.Manager.Core.Platform;
using UnityGameTranslator.Manager.Core.Net;

namespace UnityGameTranslator.Manager.Core.Catalog;

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
    /// The wait for the very first line, in seconds: the model being read off the disk and put on
    /// the card.
    ///
    /// ⚠ Paid once per session and paid while a game is starting, which is why it is here rather
    /// than folded into the figures above. It is also the widest spread of anything measured — six
    /// seconds to nearly a minute — and it does not follow the download size the way people expect.
    /// A model can be quick per line and slow to arrive.
    /// </summary>
    [JsonPropertyName("load_s")] public double? LoadSeconds { get; set; }

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
    ///
    /// ⚠ This is NOT a distinction, and treating it as one is how the table stopped helping anyone
    /// decide. Nine of the ten measured models pass it — models improved, the bar did not move —
    /// so the badge it used to carry sat on nine rows out of ten. It is a floor: what it separates
    /// is a model worth listing from one that leaves text untranslated.
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
    /// ⚠ Ordered by what the machine can run, NEVER by language. Sorting on "good at Japanese"
    /// would break the rule the whole project rests on — every part of it stays language-agnostic.
    /// That prohibition is real and absolute; deciding what to show first is not, and belongs to
    /// whoever shows it. The catalogue keeps no rank of its own.
    ///
    /// 🔸 THE SAME ORDER IS APPLIED BY THE WEBSITE — `App\Services\ModelCatalog::installable()` in
    /// the website repository. Change one, change the other. The two cannot share code (PHP and C#,
    /// and the shared library takes no JSON parser), so the rule is written twice on purpose. It is
    /// a ladder of thresholds, compared in this order, and each rung is a cost paid while playing:
    ///
    ///   what fits this card (Manager only) · measured at all · gave up on no line ·
    ///   followed every instruction · never had to be asked twice · video memory held, least first ·
    ///   the strict-source option · the wait before the first line, shortest first
    ///
    /// The first key is the ONLY legitimate difference between the two: a web page has no idea what
    /// card the reader owns, so it simply never demotes anything. Everything after it must match —
    /// the same catalogue presented in two orders by two of our own tools is a bug the reader
    /// experiences as one of them being wrong.
    ///
    /// ⚠ THRESHOLDS, not a weighted score, and that is the decision. A score would let a tenth of a
    /// second of loading buy back a line the model refuses to translate, and the two are not the
    /// same kind of thing: one is a wait, the other is text left in English on screen. So each rung
    /// is asked as a yes-or-no, and memory only decides between models that answered alike.
    ///
    /// ⚠ Retries are a THRESHOLD too, never a count. Four retries out of twenty and five is not a
    /// difference anybody can act on, and ranking on it would put a 7.8 GB model above a 2.8 GB one
    /// over a single line. Above the threshold, what decides is the memory left for the game.
    ///
    /// 🔴 The reference model is NOT forced first any more. It is what this project develops
    /// against — a fact about us, not a measurement — and it carries a mark saying exactly that.
    /// Ranking it first put a 16 GB model at the top of a table people read to find one that fits.
    ///
    /// ⚠ Languages claimed no longer breaks ties. It is the publisher's claim, unverified, and the
    /// catalogue's own rule is that nothing here is ordered by language.
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
        double? availableGb = videoMemoryBytes is { } bytes ? bytes / 1024.0 / 1024 / 1024 : null;

        // What fits comes first whatever its standing: a model that spills out of this card is not
        // a recommendation, it is a trap — it falls back to the processor and takes minutes a
        // line. What does not fit stays visible, last, with its requirement shown, because someone
        // willing to wait is entitled to decide that themselves.
        //
        // With no card reading, nothing is demoted: an unknown size is not a small one, and a tool
        // that quietly buries the good models because it failed to read a number is worse than one
        // that lists them all with their requirements.
        bool Fits(ModelNote note) =>
            availableGb is not { } available
            || note.MinVramGb is null
            || note.MinVramGb <= available;

        return offerable
            .OrderBy(note => Fits(note) ? 0 : 1)
            .ThenBy(note => note.Measured is null ? 1 : 0)
            .ThenBy(note => note.Measured?.Refused > 0 ? 1 : 0)
            .ThenBy(note => Incomplete(note) ? 1 : 0)
            .ThenBy(note => note.Measured?.Retried > 0 ? 1 : 0)
            .ThenBy(Held)
            .ThenBy(note => note.Measured?.StrictSource == true ? 0 : 1)
            .ThenBy(note => note.Measured?.LoadSeconds ?? double.MaxValue)
            .ToList();
    }

    /// <summary>Left at least one instruction of the suite unfollowed.</summary>
    private static bool Incomplete(ModelNote note) =>
        note.Measured is { Suite: { } suite, SuiteOf: { } outOf } && suite < outOf;

    /// <summary>
    /// The memory a model actually held, in GB — what is left for the game while it runs.
    ///
    /// ⚠ The MEASURED figure, never <see cref="ModelNote.MinVramGb"/>. That one is rounded up to
    /// real card sizes, so four models holding 1.7, 2.8, 3.1 and 3.1 GB all read "4 GB" and sorted
    /// as equals — collapsing the very difference this rung exists to expose. The rounded figure
    /// answers "will it fit"; only the measured one answers "how much is left".
    ///
    /// Falls back to the requirement when nothing was measured, then to the end of the list.
    /// </summary>
    private static double Held(ModelNote note) =>
        note.Measured?.VramGb ?? note.MinVramGb ?? double.MaxValue;

    /// <summary>
    /// The mark beside a model, in the words the reader gets, or null — which is the answer for
    /// most rows, and has to be: a mark on every row is a mark on none.
    ///
    /// Two of them, and each answers a DIFFERENT question a reader arrives with:
    ///
    ///   "what do you run yourselves?"  → the reference model
    ///   "I have a small card"          → the lightest that missed nothing
    ///
    /// 🔴 The mark this replaces — "Missed nothing", on anything flawless — answered neither, and
    /// by 2026-09 it landed on nine rows out of ten. It had not changed; the models had. A mark
    /// whose condition the whole field eventually meets stops being a mark, silently, and nothing
    /// about the code says so.
    ///
    /// ⚠ The second mark says LIGHTEST, not best: the model it lands on today needed four retries
    /// out of twenty, and its retry column says so in amber right beside the mark. The mark points,
    /// the columns qualify. Neither is allowed to say the other's part.
    /// </summary>
    /// <param name="among">
    /// The rows being shown, because "lightest" is a fact about a list and not about a model. Pass
    /// the very list on screen: a mark computed over one set and displayed beside another names a
    /// row the reader cannot see.
    /// </param>
    public static string? Standout(ModelNote note, IEnumerable<ModelNote> among)
    {
        if (note.IsReference) return "Used in development";

        // Compared over EVERY row including the reference, and only awarded to a row that is not
        // it. If the reference ever were the lightest, the honest outcome is that nothing else
        // carries this mark — handing it to the second lightest would name the wrong model.
        var lightest = Lightest(among);

        return lightest is not null && lightest.Pull == note.Pull
            ? "Lightest that missed nothing"
            : null;
    }

    /// <summary>The smallest measured footprint among models that gave up on nothing, or null.</summary>
    private static ModelNote? Lightest(IEnumerable<ModelNote> among) =>
        among.Where(note => note.Measured is { Flawless: true, VramGb: not null })
             .OrderBy(note => note.Measured!.VramGb!.Value)
             .FirstOrDefault();

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
