using System.Text;
using System.Text.Json;
using UnityGameTranslator.Common;
using UnityGameTranslator.Manager.Core.Net;

namespace UnityGameTranslator.Manager.Core.Ai;

/// <summary>
/// Google Translate and DeepL, asked for one line exactly as a game's mod asks them — what the
/// browser editor's Retranslate is answered with, while the game is closed, for a game set up with
/// one of these services.
///
/// ⚠ **The transport only.** What is sent was prepared by the socle (<see cref="Backends.Prepare"/>)
/// and what comes back is judged by it (<see cref="LineTranslation.CheckServiceAnswer"/>); the
/// addresses (<see cref="Endpoints.GoogleTranslate"/>, <see cref="Endpoints.DeepLTranslate"/>) and
/// the language codes (<see cref="Languages.GoogleCode"/>, <see cref="Languages.DeepLCode"/>) are
/// the socle's too. The request bodies mirror the mod's (TranslateWithGoogle, TranslateWithDeepL):
/// a service answering the game one way and this tool another would be two translations of one line.
///
/// ⚠ Synchronous, like the loop that calls it: run it off the UI thread.
/// </summary>
public sealed class TranslationServices
{
    private readonly HttpClient _http;

    public TranslationServices(TimeSpan timeout, HttpClient? http = null)
    {
        _http = http ?? Http.Create(timeout);
    }

    /// <summary>
    /// The text a service returned, or null when there is none — no key, a language the service
    /// does not know, a refusal, or no answer. Why is written to <see cref="LastError"/>.
    /// </summary>
    public string? Translate(GameAiSettings ai, string text, string? sourceLanguage, string targetLanguage,
                             CancellationToken ct)
    {
        LastError = null;

        return ai.Backend switch
        {
            "google" => Google(ai, text, sourceLanguage, targetLanguage, ct),
            "deepl" => DeepL(ai, text, sourceLanguage, targetLanguage, ct),
            _ => null,
        };
    }

    /// <summary>Why the last request gave nothing, in words for a log. Null after an answer.</summary>
    public string? LastError { get; private set; }

    private string? Google(GameAiSettings ai, string text, string? sourceLanguage, string targetLanguage,
                           CancellationToken ct)
    {
        if (string.IsNullOrEmpty(ai.GoogleApiKey))
        {
            LastError = "No Google key in this game's settings.";
            return null;
        }

        var target = Languages.GoogleCode(targetLanguage);
        if (string.IsNullOrEmpty(target))
        {
            LastError = $"Google Translate does not offer {targetLanguage}.";
            return null;
        }

        var body = new Dictionary<string, object> { ["q"] = text, ["target"] = target, ["format"] = "text" };
        if (!string.IsNullOrEmpty(sourceLanguage) && Languages.GoogleCode(sourceLanguage) is { Length: > 0 } source)
            body["source"] = source;

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoints.GoogleTranslate)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Goog-Api-Key", ai.GoogleApiKey);

        return Send(request, "Google Translate", ct, root =>
            root.TryGetProperty("data", out var data)
            && data.TryGetProperty("translations", out var translations)
            && translations.ValueKind == JsonValueKind.Array && translations.GetArrayLength() > 0
            && translations[0].TryGetProperty("translatedText", out var translated)
                ? translated.GetString()
                : null);
    }

    private string? DeepL(GameAiSettings ai, string text, string? sourceLanguage, string targetLanguage,
                          CancellationToken ct)
    {
        if (string.IsNullOrEmpty(ai.DeeplApiKey))
        {
            LastError = "No DeepL key in this game's settings.";
            return null;
        }

        var target = Languages.DeepLCode(targetLanguage, true);
        if (string.IsNullOrEmpty(target))
        {
            LastError = $"DeepL does not offer {targetLanguage}.";
            return null;
        }

        var body = new Dictionary<string, object> { ["text"] = new[] { text }, ["target_lang"] = target };
        if (!string.IsNullOrEmpty(sourceLanguage) && Languages.DeepLCode(sourceLanguage, false) is { Length: > 0 } source)
            body["source_lang"] = source;

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoints.DeepLTranslate(ai.DeeplUseFree))
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Authorization", $"DeepL-Auth-Key {ai.DeeplApiKey}");

        return Send(request, "DeepL", ct, root =>
            root.TryGetProperty("translations", out var translations)
            && translations.ValueKind == JsonValueKind.Array && translations.GetArrayLength() > 0
            && translations[0].TryGetProperty("text", out var translated)
                ? translated.GetString()
                : null);
    }

    private string? Send(HttpRequestMessage request, string service, CancellationToken ct,
                         Func<JsonElement, string?> read)
    {
        try
        {
            using var response = _http.Send(request, ct);
            var body = response.Content.ReadAsStringAsync(ct).GetAwaiter().GetResult();

            if (!response.IsSuccessStatusCode)
            {
                LastError = $"{service} answered {(int)response.StatusCode}.";
                return null;
            }

            using var document = JsonDocument.Parse(body);
            var text = read(document.RootElement);
            if (text is null) LastError = $"{service} answered without a translation.";
            return text;
        }
        // An abandoned request is not a service that went quiet: it stops the caller.
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LastError = Http.Describe(ex, service);
            return null;
        }
    }
}
