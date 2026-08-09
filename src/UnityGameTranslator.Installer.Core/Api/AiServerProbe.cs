using System.Diagnostics;
using System.Text;
using System.Text.Json;

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
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
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
    public async Task<IReadOnlyList<string>?> ListModelsAsync(string baseUrl, CancellationToken ct = default)
    {
        try
        {
            var json = await _http.GetStringAsync(Join(baseUrl, "v1/models"), ct).ConfigureAwait(false);

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

        var again = await TryTranslateAsync(baseUrl, model, ct).ConfigureAwait(false);

        return first with
        {
            WarmElapsed = again.Succeeded ? again.Elapsed : null,
            FirstRunWasCold = !alreadyLoaded,
        };
    }

    public async Task<AiTrial> TryTranslateAsync(string baseUrl, string model,
                                                 CancellationToken ct = default)
    {
        // A neutral sentence, not tied to any language pair: the point is to measure the round
        // trip, not to judge the model's translation quality.
        const string prompt = "Translate to French, answer with the translation only: Start Game";

        var payload = JsonSerializer.Serialize(new
        {
            model,
            messages = new[] { new { role = "user", content = prompt } },
            stream = false,
            max_tokens = 32,
        });

        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };

            var response = await client.PostAsync(Join(baseUrl, "v1/chat/completions"), content, ct)
                                       .ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            stopwatch.Stop();

            if (!response.IsSuccessStatusCode)
                return new AiTrial(false, null, stopwatch.Elapsed, null, $"HTTP {(int)response.StatusCode}");

            var text = ReadFirstChoice(body);
            var onGpu = await IsResidentOnGpuAsync(baseUrl, model, ct).ConfigureAwait(false);

            return new AiTrial(text is not null, text, stopwatch.Elapsed, onGpu, null);
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
            var json = await _http.GetStringAsync(Join(baseUrl, "api/ps"), ct).ConfigureAwait(false);

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

    private static string Join(string baseUrl, string path) =>
        $"{baseUrl.TrimEnd('/')}/{path}";
}
