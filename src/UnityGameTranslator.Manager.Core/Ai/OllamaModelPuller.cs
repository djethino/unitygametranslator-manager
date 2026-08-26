using System.Text;
using System.Text.Json;
using UnityGameTranslator.Manager.Core.Net;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Manager.Core.Ai;

/// <summary>
/// Downloads a model into an Ollama that is already running.
///
/// This is what turns an installed Ollama into something that can actually translate: a fresh
/// install holds no model at all, and stopping at "Ollama is installed" would leave someone with
/// an engine and no fuel, on a screen that claims to have set things up.
///
/// ⚠ /api/pull is Ollama's own endpoint, not part of the OpenAI-compatible surface the rest of
/// this tool speaks. That is deliberate and it is the boundary: everything the MOD does goes
/// through the compatible API, so it works with any server. Only this installer-side convenience
/// knows about Ollama, and only because no standard equivalent exists.
///
/// Nothing here removes or replaces a model. A pull of something already present is a no-op on
/// Ollama's side, which is what makes it safe to offer to someone who is unsure.
/// </summary>
public sealed class OllamaModelPuller
{
    private readonly string _baseUrl;
    private readonly HttpClient _http;

    public OllamaModelPuller(string baseUrl = Endpoints.OllamaDefault, HttpClient? http = null)
    {
        _baseUrl = baseUrl.TrimEnd('/');

        // Long by necessity: several gigabytes over a domestic line. The progress callback is
        // what keeps this from looking frozen, not a shorter timeout.
        _http = http ?? Http.Create(TimeSpan.FromHours(2));
    }

    /// <summary>Status line, bytes done, bytes total when Ollama states one.</summary>
    public event Action<string, long?, long?>? Progress;

    /// <summary>
    /// Whether THIS server can be asked to download a model at all.
    ///
    /// 🔴 **The question is not "is it branded Ollama", it is "will the Download button work".**
    /// Asked of the network, like every other question this tool asks about a server: an Ollama in
    /// a container, on another port, or behind a proxy answers yes, and so does anything else that
    /// implements the same endpoint. A server that does not answer it cannot pull, whatever it
    /// calls itself.
    ///
    /// ⚠ Exists because the list of tested models used to be offered on ANY server that answered
    /// /v1/models — LM Studio, a machine on the network, even a paid online provider if that was
    /// the saved address. Every one of them showed a Download button that could only fail. That is
    /// the "never a dead end" rule: what is known before the click is said before the click.
    ///
    /// Uses /api/tags rather than /api/pull: it is the read-only half of the same private API, so
    /// asking costs nothing and downloads nothing.
    /// </summary>
    public async Task<bool> CanDownloadAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await _http
                .GetAsync(_baseUrl + "/api/tags", HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            return response.IsSuccessStatusCode;
        }
        catch
        {
            // Unreachable, refused, or something that is not Ollama. All three mean the same thing
            // for the caller — do not offer a download — and none of them is worth a message of
            // its own here: the server list above has already said whether anything answers.
            return false;
        }
    }

    /// <summary>Null when the model is there, otherwise what went wrong, in plain words.</summary>
    public async Task<string?> PullAsync(string model, CancellationToken ct = default)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new { model, stream = true });
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");

            using var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/api/pull")
            {
                Content = content,
            };

            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return $"Ollama refused the download ({(int)response.StatusCode}). "
                     + $"The name '{model}' may no longer exist in its library.";
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream);

            string? lastError = null;
            var sawSuccess = false;

            // One JSON object per line, as it happens. Read as a stream rather than buffered:
            // that is the only thing standing between the user and a window that looks hung for
            // twenty minutes.
            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;

                    if (root.TryGetProperty("error", out var error))
                    {
                        lastError = error.GetString();
                        continue;
                    }

                    var status = root.TryGetProperty("status", out var s) ? s.GetString() : null;
                    long? done = root.TryGetProperty("completed", out var c) ? c.GetInt64() : null;
                    long? total = root.TryGetProperty("total", out var t) ? t.GetInt64() : null;

                    if (status is not null)
                    {
                        Progress?.Invoke(status, done, total);
                        if (status.Equals("success", StringComparison.OrdinalIgnoreCase))
                            sawSuccess = true;
                    }
                }
                catch (JsonException)
                {
                    // A truncated line at the end of the stream is not a failed download.
                }
            }

            if (lastError is not null) return lastError;

            return sawSuccess
                ? null
                : "The download stopped before finishing. Running it again resumes where it left "
                + "off — Ollama keeps what it already has.";
        }
        catch (OperationCanceledException)
        {
            return "Cancelled. Ollama keeps what it downloaded, so starting again resumes.";
        }
        catch (Exception ex)
        {
            return Http.Describe(ex, "Ollama");
        }
    }
}
