using System.Diagnostics;
using System.Text;
using System.Text.Json;
using UnityGameTranslator.Installer.Core.Net;

namespace UnityGameTranslator.Installer.Core.Api;

/// <summary>A local AI server that answered, with what it offers.</summary>
public sealed record AiServer(string Url, string Product, IReadOnlyList<string> Models)
{
    public override string ToString() =>
        $"{Product} at {Url} ({Models.Count} model(s))";
}

/// <summary>How a translation attempt went, in the terms that matter to a player.</summary>
public sealed record AiTrial(
    bool Succeeded,
    string? Output,
    TimeSpan Elapsed,
    bool? OnGpu,
    string? Detail)
{
    /// <summary>
    /// Time for the same line once the model is already loaded, when it was measured twice.
    ///
    /// Reporting only the first attempt is misleading in the alarming direction: a cold run
    /// carries the model load, seventeen seconds against roughly one warm. Someone shown the
    /// cold figure alone concludes their machine cannot do this, when in play only the very
    /// first line pays that price.
    /// </summary>
    public TimeSpan? WarmElapsed { get; init; }

    /// <summary>
    /// Whether the first measurement actually paid for loading the model, established by asking
    /// the server beforehand rather than assumed. A tool that labels a warm 0.6s as "includes
    /// loading the model" is telling the user something untrue about their own machine.
    /// </summary>
    public bool FirstRunWasCold { get; init; }

    /// <summary>
    /// Whether every placeholder came back untouched, in the same order.
    ///
    /// The mod wraps line breaks, tags and variables as [!nl], [!t*0], [!v*0], [!STR*0] and asks
    /// the model to leave them alone.
    ///
    /// It does NOT ship what comes back blindly: it validates these markers, retries up to three
    /// times, repairs a trailing line break itself, and if the model still fails it leaves the
    /// line untranslated rather than caching a mangled one. So the cost of a model that misses is
    /// up to three times the calls — time and GPU taken from the game — and, when it persists,
    /// text left in its original language. Visible, not silent. Null when the check could not run.
    /// </summary>
    public bool? KeptPlaceholders { get; init; }

    /// <summary>
    /// Whether the answer was the translation alone. Models that add "Sure! Here is the
    /// translation:" are unusable here: the mod displays what comes back, verbatim.
    /// </summary>
    public bool? AnsweredWithTranslationOnly { get; init; }

    /// <summary>Video memory the model actually occupies, in bytes, when the server says so.</summary>
    public long? VramBytes { get; init; }

    /// <summary>
    /// How many answers were judged, and how many of them held.
    ///
    /// The verdicts above used to come from ONE answer, while three more were requested for
    /// timing and thrown away. That is how the same model could be told "keeps placeholders" by
    /// the suite and "does not" by the comparison, minutes apart, with neither being wrong: these
    /// models are sampled, not deterministic, and one draw says almost nothing.
    ///
    /// It matters more here than in most tests: a model that mangles one line in four corrupts a
    /// game's text, and a single green tick is exactly how that gets shipped.
    /// </summary>
    public int Runs { get; init; } = 1;

    public int RunsKeepingPlaceholders { get; init; }
    public int RunsAnsweringOnly { get; init; }

    public string VramText => VramBytes is { } bytes
        ? $"{bytes / 1024.0 / 1024 / 1024:F1} GB"
        : "unknown";
}

/// <summary>
/// Finds a local AI server, and measures whether it is actually usable for playing.
///
/// The mod does not know Ollama or LM Studio: it knows an OpenAI-compatible URL, which it tests
/// with /v1/models. So the job here is not "is Ollama installed" but "what answers" — which also
/// covers servers we never thought of, and a server running on another machine.
/// </summary>
public sealed class AiServerProbe
{
    /// <summary>
    /// Ports worth trying, with the product that usually owns them. Only a starting point: the
    /// settings always accept a URL typed by hand, because someone may run their server on
    /// another port, or on another machine entirely.
    /// </summary>
    private static readonly (int Port, string Product)[] KnownPorts =
    {
        (11434, "Ollama"),
        (1234, "LM Studio"),
        (8000, "vLLM"),
        (8080, "llama.cpp / LocalAI"),
        (1337, "Jan"),
        (5000, "text-generation-webui"),
    };

    private readonly HttpClient _http;

    public AiServerProbe(HttpClient? http = null)
    {
        // Short timeout: this runs against a closed port most of the time, and a settings screen
        // that takes ten seconds to say "nothing found" is a settings screen nobody opens twice.
        _http = http ?? Http.Create(TimeSpan.FromSeconds(2));
    }

    /// <summary>Every local server that answers, probed in parallel.</summary>
    public async Task<IReadOnlyList<AiServer>> DiscoverAsync(CancellationToken ct = default)
    {
        var attempts = KnownPorts.Select(async known =>
        {
            var url = $"http://localhost:{known.Port}";
            var models = await ListModelsAsync(url, ct).ConfigureAwait(false);
            return models is null ? null : new AiServer(url, known.Product, models);
        });

        var results = await Task.WhenAll(attempts).ConfigureAwait(false);
        return results.Where(server => server is not null).Select(server => server!).ToList();
    }

    /// <summary>
    /// Models offered by a server, or null when nothing answers. Uses the OpenAI-compatible
    /// endpoint the mod itself tests, so a server that works here works there.
    /// </summary>
    public async Task<IReadOnlyList<string>?> ListModelsAsync(string baseUrl, CancellationToken ct = default) =>
        await ListModelsAsync(baseUrl, null, ct).ConfigureAwait(false);

    /// <summary>
    /// Same, with a key. Online providers need one; a local server ignores it. The single
    /// endpoint the mod tests, so a server that answers here answers there.
    /// </summary>
    public async Task<IReadOnlyList<string>?> ListModelsAsync(string baseUrl, string? apiKey,
                                                              CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, AiEndpoint.Models(baseUrl));
            if (!string.IsNullOrWhiteSpace(apiKey))
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }

            return data.EnumerateArray()
                       .Select(item => item.TryGetProperty("id", out var id) ? id.GetString() : null)
                       .Where(id => id is not null)
                       .Select(id => id!)
                       .ToList();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Translates one short sentence for real, and reports how long it took.
    ///
    /// A latency ping would measure almost nothing: what a player feels is the delay before a
    /// line appears, which depends on the model, the quantisation and the backend. So the test
    /// is an actual translation.
    ///
    /// ⚠ The number is optimistic by construction: this runs with no game on screen, while in
    /// play the model shares the GPU with the rendering. Whoever shows this figure must say so.
    /// </summary>
    public async Task<AiTrial> MeasureAsync(string baseUrl, string model,
                                            CancellationToken ct = default)
    {
        // Whether the first run pays for loading the model is not ours to assume: it depends on
        // what the server already has in memory. Asked before measuring, rather than asserted
        // afterwards — a "includes loading the model" printed next to 0.6s is simply false.
        var alreadyLoaded = await IsResidentOnGpuAsync(baseUrl, model, ct).ConfigureAwait(false) == true;

        var first = await TryTranslateAsync(baseUrl, model, ct).ConfigureAwait(false);
        if (!first.Succeeded) return first;

        // Several warm runs, and the BEST is kept.
        //
        // A single second run reported 61s for a model that answers the very same prompt in
        // 0.6s. Comparing models back to back is exactly what causes it: each new model evicts
        // the previous one, and a run that lands during an eviction measures the memory shuffle
        // rather than the model. Averaging would carry the noise; the best run is the only one
        // guaranteed not to include someone else's loading.
        TimeSpan? bestWarm = null;

        // The answers are judged too, not only timed. They were already paid for, and throwing
        // them away is what let one lucky draw pass for a verdict.
        var runs = 1;
        var kept = first.KeptPlaceholders == true ? 1 : 0;
        var bare = first.AnsweredWithTranslationOnly == true ? 1 : 0;

        for (var run = 0; run < WarmRuns; run++)
        {
            var attempt = await TryTranslateAsync(baseUrl, model, ct).ConfigureAwait(false);
            if (!attempt.Succeeded) continue;
            if (bestWarm is null || attempt.Elapsed < bestWarm) bestWarm = attempt.Elapsed;

            runs++;
            if (attempt.KeptPlaceholders == true) kept++;
            if (attempt.AnsweredWithTranslationOnly == true) bare++;
        }

        return first with
        {
            WarmElapsed = bestWarm,
            FirstRunWasCold = !alreadyLoaded,
            Runs = runs,
            RunsKeepingPlaceholders = kept,
            RunsAnsweringOnly = bare,

            // "Yes" now means every answer held, not that one did. For this criterion nothing
            // less is worth saying: the failure it guards against is silent corruption of text.
            KeptPlaceholders = kept == runs,
            AnsweredWithTranslationOnly = bare == runs,
        };
    }

    /// <summary>How many warm runs to take the best of. Three is enough to shake off one hiccup.</summary>
    private const int WarmRuns = 3;

    /// <summary>
    /// Runs the whole suite against a model and reports every answer, not only the verdicts.
    ///
    /// The checks are heuristics on free text: they will get some calls wrong in both
    /// directions. Showing what the model actually said is what lets a human notice that, and
    /// overrule us. We measure; the user decides whether to use the model.
    /// </summary>
    /// <param name="gameContext">
    /// What the mod's "describe this game" setting would hold. Passed through so the suite can be
    /// run both ways: it is the one line of the prompt a player writes themselves, and whether it
    /// helps or hurts was until now a matter of opinion.
    /// </param>
    public async Task<IReadOnlyList<ModelTestResult>> RunSuiteAsync(
        string baseUrl, string model, string targetLanguage,
        Action<ModelTestResult>? onResult = null, CancellationToken ct = default,
        string? gameContext = null, string? sourceCode = null)
    {
        var results = new List<ModelTestResult>();

        foreach (var test in ModelTestSuite.Build(targetLanguage, gameContext, sourceCode))
        {
            var answer = await AskAsync(baseUrl, model, test.Rule, test.Source, ct)
                .ConfigureAwait(false);

            ModelTestResult result;

            if (answer is null)
            {
                result = new ModelTestResult(test, null, false, "no answer");
            }
            else
            {
                // Judged on the translation, not on the whole answer. A model that repeats the
                // rules makes every structural check lie in both directions at once, so the echo
                // is reported as its own failure and the check is run on what it actually
                // translated.
                var echoed = ModelTestSuite.LooksLikeEchoedInstructions(answer);
                var translation = echoed ? ModelTestSuite.ExtractTranslation(answer) : answer;

                result = new ModelTestResult(test, answer, test.Check(test.Source, translation), null)
                {
                    EchoedInstructions = echoed,
                    Translation = translation,
                };
            }

            results.Add(result);
            onResult?.Invoke(result);
        }

        return results;
    }

    /// <summary>Reasoning budgets to try, best first — the same ladder the mod walks.</summary>
    private static readonly string?[] ReasoningEffortLadder = { "none", "low", null };

    public async Task<AiTrial> TryTranslateAsync(string baseUrl, string model,
                                                 CancellationToken ct = default)
    {
        // Walk the ladder until the server accepts one: a rejected parameter is answered with an
        // error, not with a translation, and giving up on the first rung would report a perfectly
        // good model as broken.
        AiTrial? last = null;

        foreach (var effort in ReasoningEffortLadder)
        {
            var attempt = await TryOnceAsync(baseUrl, model, effort, ct).ConfigureAwait(false);
            if (attempt.Succeeded) return attempt;
            last = attempt;
        }

        return last!;
    }

    /// <summary>
    /// Sends one instruction and returns the answer, walking the reasoning ladder like the mod.
    /// </summary>
    public async Task<string?> AskAsync(string baseUrl, string model, string systemPrompt,
                                        string? userContent = null, CancellationToken ct = default)
    {
        foreach (var effort in ReasoningEffortLadder)
        {
            var answer = await AskOnceAsync(baseUrl, model, systemPrompt, userContent, effort, ct)
                .ConfigureAwait(false);
            if (answer is not null) return answer;
        }
        return null;
    }

    /// <summary>
    /// One request, one answer, or null when the server refused it. The single place a chat
    /// request is built: the timing wrapper and the test suite both go through here, so they
    /// cannot drift into asking the model two different things.
    /// </summary>
    private async Task<string?> AskOnceAsync(string baseUrl, string model, string systemPrompt,
                                             string? userContent, string? effort,
                                             CancellationToken ct)
    {
        // Two messages, split exactly as the mod splits them: the rules go in the system turn
        // and only the text to translate goes in the user turn.
        //
        // Piling everything into one user message was the first shape here, and it changed the
        // answers: rules delivered as user content read as text to work on, and some models
        // dutifully repeated them before translating. Testing a model on a request the mod never
        // sends measures a situation that will never happen.
        var messages = userContent is null
            ? new[] { new { role = "user", content = systemPrompt } }
            : new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userContent },
            };

        // The mod's own formula: twice the source length, never below 200. A first attempt here
        // capped it at 32 and every reasoning model came back blank — the budget was spent before
        // a single word of the answer.
        var maxTokens = Math.Max(200, (userContent ?? systemPrompt).Length * 2);

        var payload = JsonSerializer.Serialize(new
        {
            model,
            messages,
            stream = false,
            temperature = 0.0,
            max_tokens = maxTokens,
            reasoning_effort = effort,
        }, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        });

        try
        {
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var client = Http.Create(TimeSpan.FromMinutes(2));

            var response = await client.PostAsync(AiEndpoint.Chat(baseUrl), content, ct)
                                       .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ReadFirstChoice(body);
        }
        catch
        {
            return null;
        }
    }

    private async Task<AiTrial> TryOnceAsync(string baseUrl, string model, string? effort,
                                             CancellationToken ct)
    {
        // The rules the mod itself sends, in the same words, around a line carrying the
        // placeholders it actually uses. Testing with a bare sentence would measure a job the
        // model is never asked to do.
        const string rules = """
            === TRANSLATION RULES ===
            - Output the translation only, no explanation
            - Keep it concise for UI
            - Keep technical terms unchanged: API, URL, UUID, JSON, AI
            - IMPORTANT: Keep [!nl] placeholders exactly where they are, do not remove or move them
            - IMPORTANT: Keep [!v*0], [!v*1], etc. placeholders exactly as-is, do not modify them

            Now, translate this to French:
            """;

        const string line = "Press [!v*0] to save[!nl]Your API key is required";

        var stopwatch = Stopwatch.StartNew();

        try
        {
            // The request itself is built in exactly one place, so the timed path and the
            // instruction suite cannot drift into asking the model two different things.
            var text = await AskOnceAsync(baseUrl, model, rules, line, effort, ct)
                .ConfigureAwait(false);
            stopwatch.Stop();

            if (text is null)
                return new AiTrial(false, null, stopwatch.Elapsed, null, "no answer");
            var onGpu = await IsResidentOnGpuAsync(baseUrl, model, ct).ConfigureAwait(false);
            var vram = await VramBytesAsync(baseUrl, model, ct).ConfigureAwait(false);

            return new AiTrial(text is not null, text, stopwatch.Elapsed, onGpu, null)
            {
                VramBytes = vram,
                KeptPlaceholders = text is null ? null : KeepsPlaceholders(text),
                AnsweredWithTranslationOnly = text is null ? null : IsBareTranslation(text),
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new AiTrial(false, null, stopwatch.Elapsed, null, ex.GetType().Name);
        }
    }

    /// <summary>
    /// Whether the model is actually resident in video memory.
    ///
    /// This is the question that separates "your machine is slow" from "your GPU is not being
    /// used at all" — the second is fixable and the first is not, and they feel identical. A
    /// known trap produces exactly this: a driver stack too old for the card makes Ollama find
    /// the GPU, stall, and quietly fall back to the processor.
    ///
    /// ⚠ Ollama-specific: /api/ps is not part of the OpenAI-compatible surface. Other servers
    /// return null here, and the caller must say "unknown" rather than "no".
    /// </summary>
    public async Task<bool?> IsResidentOnGpuAsync(string baseUrl, string model,
                                                  CancellationToken ct = default)
    {
        try
        {
            var json = await _http.GetStringAsync(OllamaRoot(baseUrl) + "/api/ps", ct).ConfigureAwait(false);

            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("models", out var models)
                || models.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var entry in models.EnumerateArray())
            {
                var name = entry.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (name is null || !name.StartsWith(model, StringComparison.OrdinalIgnoreCase)) continue;

                var size = entry.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                var vram = entry.TryGetProperty("size_vram", out var v) ? v.GetInt64() : 0;

                // Partially offloaded counts as "not really on the GPU": the slowest part sets
                // the pace, and a mostly-CPU split feels like CPU.
                return size > 0 && vram >= size * 0.9;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Every placeholder present, once each, in the order they were sent.
    ///
    /// Order matters as much as presence: a model that keeps both tokens but swaps them still
    /// puts the line break and the key in the wrong places on screen.
    /// </summary>
    private static bool KeepsPlaceholders(string answer)
    {
        string[] expected = { "[!v*0]", "[!nl]" };

        var position = -1;
        foreach (var token in expected)
        {
            var index = answer.IndexOf(token, StringComparison.Ordinal);
            if (index <= position) return false;                       // missing, or out of order
            if (answer.IndexOf(token, index + 1, StringComparison.Ordinal) >= 0) return false; // duplicated
            position = index;
        }
        return true;
    }

    /// <summary>
    /// The answer is the translation and nothing else. The mod displays what comes back
    /// verbatim, so a preamble ends up on the player's screen.
    /// </summary>
    private static bool IsBareTranslation(string answer)
    {
        var trimmed = answer.Trim();

        // A conversational opener, a code fence, or several paragraphs all mean the model
        // answered about the task instead of performing it.
        string[] tells = { "here is", "here's", "sure", "translation:", "```", "certainly" };
        if (tells.Any(tell => trimmed.StartsWith(tell, StringComparison.OrdinalIgnoreCase))) return false;

        return trimmed.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length <= 2;
    }

    /// <summary>Video memory the model occupies right now, when the server reports it.</summary>
    public async Task<long?> VramBytesAsync(string baseUrl, string model, CancellationToken ct = default)
    {
        try
        {
            var json = await _http.GetStringAsync(OllamaRoot(baseUrl) + "/api/ps", ct).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("models", out var models)) return null;

            foreach (var entry in models.EnumerateArray())
            {
                var name = entry.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (name is null || !name.StartsWith(model, StringComparison.OrdinalIgnoreCase)) continue;
                if (entry.TryGetProperty("size_vram", out var vram)) return vram.GetInt64();
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadFirstChoice(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0)
            {
                return null;
            }

            return choices[0].TryGetProperty("message", out var message)
                   && message.TryGetProperty("content", out var text)
                ? text.GetString()?.Trim()
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The server root, for the few calls that are not OpenAI-compatible.
    ///
    /// /api/ps is Ollama's own, so it hangs off the host rather than off the chat path: someone
    /// who pasted ".../v1/chat/completions" would otherwise be asked for
    /// ".../v1/chat/completions/api/ps", and the video-memory reading would silently come back
    /// as "unknown" on exactly the setups that can report it.
    /// </summary>
    private static string OllamaRoot(string baseUrl)
    {
        var url = baseUrl.TrimEnd('/');

        foreach (var marker in new[] { "/v1/", "/v1" })
        {
            var index = url.LastIndexOf(marker, StringComparison.Ordinal);
            if (index >= 0) return url[..index];
        }

        var chat = url.LastIndexOf("/chat/completions", StringComparison.Ordinal);
        return chat >= 0 ? url[..chat] : url;
    }
}
