using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using UnityGameTranslator.Manager.Core.Net;

namespace UnityGameTranslator.Manager.Core.Api;

/// <summary>A session opened for editing one translation file in a browser.</summary>
public sealed record EditSession(string ModKey, string Url, DateTimeOffset? ExpiresAt);

/// <summary>
/// What is going on in a session, without moving the file — a few dozen bytes, safe to poll.
/// </summary>
/// <param name="ContentHash">
/// The identity of the file as the session holds it. A change means the browser saved something.
/// </param>
/// <param name="BrowserLeft">The page said it was going away. Nobody is editing any more.</param>
/// <param name="BrowserSeenSecondsAgo">Null when the page has never been seen at all.</param>
/// <param name="PendingChanges">Edits saved in the browser that this side has not fetched.</param>
public sealed record EditSessionState(string? ContentHash, bool BrowserLeft,
                                      int? BrowserSeenSecondsAgo, int PendingChanges);

/// <summary>
/// The browser editor, driven from this tool instead of from inside a game.
///
/// ⚠ **Same protocol as the mod, on purpose and to the letter.** The editor on the site is one
/// screen serving one contract; a second dialect would be a second thing to keep working every time
/// that screen changes. What differs is only who plays the game's part — the mod while playing,
/// this tool while not — and the site is told nothing about which, because nothing there depends on
/// it.
///
/// ⚠ **The session key is a credential.** Sixty-four unguessable characters that authorise reading
/// and rewriting one translation for as long as the session lives. It is kept in this tool's own
/// per-user data directory and NEVER written into the game folder: game folders are shared between
/// the operating-system accounts of one machine, and a key left there would hand the next person a
/// live handle on somebody else's file.
///
/// ⚠ **Nothing here is a listening surface.** Every exchange is this tool calling out over HTTPS;
/// no port is opened, nothing local is exposed, and the browser is merely sent to a URL. The only
/// inbound direction is a response to a request we made.
/// </summary>
public sealed class EditSessionClient
{
    /// <summary>
    /// Files run to tens of megabytes on a large game, so the body is compressed — the site's
    /// DecodeGzipRequest middleware exists for exactly this, and the mod already uses it.
    /// </summary>
    private const string GzipEncoding = "gzip";

    private readonly HttpClient _http;

    public EditSessionClient(HttpClient? http = null)
    {
        // Generous timeout: this uploads and downloads whole translation files, and a slow link on
        // a large game is not an error.
        _http = http ?? Http.Create(TimeSpan.FromSeconds(60));
    }

    /// <summary>Why the last call failed, in words a user can act on. Null after a success.</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// Open a session for a translation file and get the URL to send a browser to.
    /// </summary>
    /// <param name="contentJson">
    /// The file exactly as it sits on disk. ⚠ Passed as TEXT and re-parsed once here only to be
    /// re-emitted inside the envelope: the session file comes back to replace the local one, so
    /// every key it carries — including ones this tool has never heard of — has to survive the
    /// round trip. Deserialising into a model would quietly drop them.
    /// </param>
    /// <param name="aiAvailable">
    /// Whether an AI backend is configured HERE. It drives the per-line Retranslate button in the
    /// browser. ⚠ False for now from this tool: that button is answered by whatever holds the
    /// translation loop, and the game is not running — promising it would leave the user waiting on
    /// nobody. See analyse/manager-translation-workbench.md.
    /// </param>
    public async Task<EditSession?> OpenAsync(string contentJson, string? gameName,
                                              string? sourceLanguage, string? targetLanguage,
                                              bool aiAvailable = false, string? aiModel = null,
                                              CancellationToken ct = default)
    {
        LastError = null;

        JsonDocument content;
        try
        {
            content = JsonDocument.Parse(contentJson);
        }
        catch (Exception ex)
        {
            LastError = $"This game's translation file is not valid JSON, so it cannot be edited: {ex.Message}";
            return null;
        }

        using (content)
        {
            if (content.RootElement.ValueKind != JsonValueKind.Object)
            {
                LastError = "This game's translation file is not a JSON object. Nothing was sent.";
                return null;
            }

            var payload = new MemoryStream();
            using (var writer = new Utf8JsonWriter(payload))
            {
                writer.WriteStartObject();
                writer.WritePropertyName("content");
                content.RootElement.WriteTo(writer);
                WriteOptional(writer, "game_name", gameName);
                WriteOptional(writer, "source_language", sourceLanguage);
                WriteOptional(writer, "target_language", targetLanguage);
                writer.WriteBoolean("ai_available", aiAvailable);
                WriteOptional(writer, "ai_model", aiModel);
                writer.WriteEndObject();
            }

            var json = await PostAsync($"{BuildInfo.ApiBaseUrl}/edit-session/init", payload.ToArray(), ct)
                .ConfigureAwait(false);
            if (json is null) return null;

            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;

                var modKey = Text(root, "mod_key");
                var url = Text(root, "url");

                if (string.IsNullOrWhiteSpace(modKey) || string.IsNullOrWhiteSpace(url))
                {
                    LastError = "The site opened a session but did not say where to find it.";
                    return null;
                }

                DateTimeOffset? expires = null;
                if (Text(root, "expires_at") is { Length: > 0 } raw
                    && DateTimeOffset.TryParse(raw, out var parsed))
                {
                    expires = parsed;
                }

                return new EditSession(modKey, url, expires);
            }
            catch (Exception ex)
            {
                LastError = $"The site's answer could not be read: {ex.Message}";
                return null;
            }
        }
    }

    /// <summary>
    /// What has happened in the session, cheaply enough to ask every few seconds.
    ///
    /// ⚠ This is what makes following an editor affordable: the file is only fetched once the hash
    /// says it changed. Polling the content route instead would stream the whole translation — tens
    /// of megabytes on a large game — to learn that nothing had happened.
    ///
    /// ⚠ Asking does NOT keep the session alive; <see cref="KeepAliveAsync"/> does, and it is a
    /// separate call on purpose, so a window nobody has looked at since yesterday stops holding a
    /// slot on the site.
    /// </summary>
    /// <summary>
    /// The site no longer has this session: it expired, or somebody closed it there.
    ///
    /// 🔴 **A flag, because the caller used to look for the word "expired" in LastError.** That
    /// message is written for a person to read; matching on it couples the end of a session to a
    /// turn of phrase, and rewording it — a translation, a softer sentence — would silently turn
    /// "the session is over" into "a hiccup, try again in three seconds", for ever.
    /// </summary>
    public bool SessionGone { get; private set; }

    public async Task<EditSessionState?> PollAsync(string modKey, CancellationToken ct = default)
    {
        LastError = null;
        SessionGone = false;

        try
        {
            var url = $"{BuildInfo.ApiBaseUrl}/edit-session/{Uri.EscapeDataString(modKey)}/state";
            using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                SessionGone = response.StatusCode == System.Net.HttpStatusCode.NotFound;
                LastError = SessionGone
                    ? "That edit session has expired or was closed."
                    : $"The server answered {(int)response.StatusCode}.";
                return null;
            }

            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;

            return new EditSessionState(
                Text(root, "content_hash"),
                root.TryGetProperty("browser_left", out var left)
                    && left.ValueKind == JsonValueKind.True,
                root.TryGetProperty("browser_seen_seconds_ago", out var seen)
                    && seen.TryGetInt32(out var seconds) ? seconds : null,
                root.TryGetProperty("pending_changes", out var pending)
                    && pending.TryGetInt32(out var count) ? count : 0);
        }
        catch (Exception ex)
        {
            LastError = Net.Http.Describe(ex, "the community site");
            return null;
        }
    }

    /// <summary>
    /// The session file as it now stands — what the browser has saved.
    ///
    /// ⚠ Returned as TEXT, for the same reason it was sent as text: this is about to become the
    /// file in the game, and it must arrive byte for byte.
    ///
    /// ⚠ Fetching also tells the site the edits reached this machine, which is what clears its
    /// "saved but not applied" counter. Only call it when the file is actually going to be written.
    /// </summary>
    public async Task<string?> FetchAsync(string modKey, CancellationToken ct = default)
    {
        LastError = null;

        try
        {
            var url = $"{BuildInfo.ApiBaseUrl}/edit-session/{Uri.EscapeDataString(modKey)}/content";
            using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                SessionGone = response.StatusCode == System.Net.HttpStatusCode.NotFound;
                LastError = SessionGone
                    ? "That edit session has expired or was closed."
                    : $"The server answered {(int)response.StatusCode}.";
                return null;
            }

            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            // A truncated transfer produces something that is not a JSON object, and writing that
            // over a translation would be worse than not fetching at all.
            if (string.IsNullOrWhiteSpace(text) || !text.TrimStart().StartsWith('{'))
            {
                LastError = "The server sent something that is not a translation file. Nothing was written.";
                return null;
            }

            return text;
        }
        catch (Exception ex)
        {
            LastError = Net.Http.Describe(ex, "the community site");
            return null;
        }
    }

    /// <summary>
    /// Keep the session alive. The site ends an idle session on a sliding window, and somebody
    /// editing for an hour without saving is not idle.
    /// </summary>
    public async Task<bool> KeepAliveAsync(string modKey, CancellationToken ct = default)
    {
        LastError = null;
        var url = $"{BuildInfo.ApiBaseUrl}/edit-session/{Uri.EscapeDataString(modKey)}/keepalive";
        return await PostAsync(url, Array.Empty<byte>(), ct).ConfigureAwait(false) is not null;
    }

    /// <summary>
    /// Close the session.
    ///
    /// ⚠ Called whenever we stop following one, including on a refusal or a crash-free exit.
    /// Sessions are a bounded resource on the site and an abandoned one holds a slot until it
    /// expires — multiplied by every user who closes a window.
    /// </summary>
    public async Task<bool> CloseAsync(string modKey, CancellationToken ct = default)
    {
        LastError = null;

        try
        {
            var url = $"{BuildInfo.ApiBaseUrl}/edit-session/{Uri.EscapeDataString(modKey)}";
            using var response = await _http.DeleteAsync(url, ct).ConfigureAwait(false);

            // A session already gone is the outcome we wanted.
            return response.IsSuccessStatusCode
                   || response.StatusCode == System.Net.HttpStatusCode.NotFound;
        }
        catch (Exception ex)
        {
            LastError = Net.Http.Describe(ex, "the community site");
            return false;
        }
    }

    /// <summary>One POST with a gzipped JSON body, returning the response text or null.</summary>
    private async Task<string?> PostAsync(string url, byte[] body, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);

            if (body.Length > 0)
            {
                var compressed = new MemoryStream();
                using (var gzip = new GZipStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
                {
                    gzip.Write(body, 0, body.Length);
                }

                var content = new ByteArrayContent(compressed.ToArray());
                content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                content.Headers.ContentEncoding.Add(GzipEncoding);
                request.Content = content;
            }
            else
            {
                request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            }

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (response.IsSuccessStatusCode) return text;

            LastError = Describe((int)response.StatusCode, text);
            return null;
        }
        catch (Exception ex)
        {
            LastError = Net.Http.Describe(ex, "the community site");
            return null;
        }
    }

    /// <summary>
    /// The server's own words when it sent any, its status code when it did not.
    ///
    /// ⚠ The body is only read for a JSON "error" field. Echoing an arbitrary response into the
    /// interface would put a remote server in charge of what this window says.
    /// </summary>
    private static string Describe(int status, string? body)
    {
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("error", out var error)
                    && error.ValueKind == JsonValueKind.String
                    && error.GetString() is { Length: > 0 } message)
                {
                    // Bounded: a server is not allowed to fill a window with prose.
                    return message.Length > 300 ? message[..300] + "…" : message;
                }
            }
            catch
            {
                // Not JSON, or not shaped as expected: the status code says enough.
            }
        }

        return status switch
        {
            413 => "That translation file is too large for an edit session.",
            429 => "The site is asking us to slow down. Try again in a moment.",
            404 => "That edit session has expired or was closed.",
            _ => $"The server answered {status}.",
        };
    }

    private static void WriteOptional(Utf8JsonWriter writer, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) writer.WriteNull(name);
        else writer.WriteString(name, value);
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
