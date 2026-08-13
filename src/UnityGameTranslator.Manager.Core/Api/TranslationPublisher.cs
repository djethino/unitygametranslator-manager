using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using UnityGameTranslator.Manager.Core.Net;

namespace UnityGameTranslator.Manager.Core.Api;

/// <summary>What publishing this file would do, decided by the server and never guessed here.</summary>
public enum PublishOutcome
{
    /// <summary>Nobody has this lineage. It becomes a translation of its own, led by this account.</summary>
    NewTranslation,

    /// <summary>This account already owns this lineage. Its published file is replaced.</summary>
    UpdateMine,

    /// <summary>
    /// Somebody else leads this lineage. The upload becomes a CONTRIBUTION to their translation,
    /// for them to review — it does not replace anything of theirs.
    /// </summary>
    ContributeToTheirs,
}

/// <summary>
/// Where a file stands in its lineage, as the server sees it, before anything is sent.
/// </summary>
/// <param name="MainOwner">
/// Who leads the lineage, when it is not this account. The one fact that turns "publish" into
/// "propose to somebody", and the reason this is asked BEFORE uploading rather than discovered
/// after.
/// </param>
/// <param name="BranchesCount">Contributions waiting on this account's own Main, when it has one.</param>
/// <param name="ServerFileHash">The published file's hash, when this account owns it.</param>
public sealed record LineageStanding(PublishOutcome Outcome, string? MainOwner,
                                     int? BranchesCount, string? ServerFileHash)
{
    /// <summary>
    /// What will happen, said before it happens.
    ///
    /// ⚠ The third case is the one that must never be a surprise: uploading into somebody else's
    /// lineage files the work as a contribution under their translation. That is a perfectly good
    /// thing to do on purpose and a bad thing to discover afterwards.
    /// </summary>
    public string Describe() => Outcome switch
    {
        PublishOutcome.NewTranslation =>
            "Nobody has published this translation yet. It will become yours, and you will lead it.",

        PublishOutcome.UpdateMine => BranchesCount is > 0
            ? $"This replaces your published version. {BranchesCount} contribution"
              + (BranchesCount == 1 ? " is" : "s are") + " waiting for your review."
            : "This replaces your published version.",

        _ => $"This translation is led by {MainOwner ?? "somebody else"}. What you send becomes a "
             + "contribution for them to review — nothing of theirs is replaced, and nothing is "
             + "published under your name until they take it.",
    };
}

/// <summary>
/// Publishing a translation from this tool, under the account signed in HERE.
///
/// ⚠ **Whether this account may act at all is decided before anything is sent**, by
/// <see cref="ServerIdentity"/>. One machine holds games belonging to different people, and the
/// game folder is shared between operating-system accounts — so "I am signed in" is never the same
/// question as "this game is mine".
///
/// ⚠ **What an upload BECOMES is decided by the server, never here.** The client asks check-uuid
/// and reports the answer; the site's own ownership rules do the rest. A client that decided for
/// itself would eventually decide differently from the site, and the case where it mattered would
/// be somebody's translation being replaced.
/// </summary>
public sealed class TranslationPublisher
{
    private readonly HttpClient _http;

    public TranslationPublisher(HttpClient? http = null)
    {
        // Uploads whole translation files; a slow link on a large game is not an error.
        _http = http ?? Http.Create(TimeSpan.FromSeconds(60));
    }

    /// <summary>Why the last call failed, in words a user can act on. Null after a success.</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// Ask the server what publishing this lineage would do, without sending the file.
    ///
    /// Returns null when the question could not be asked at all — which is NOT the same as "it
    /// would be new", and must never be treated as such: guessing "new" on a failed lookup is how
    /// a contribution turns into a claim over somebody else's lineage.
    /// </summary>
    public async Task<LineageStanding?> CheckAsync(string uuid, string apiToken,
                                                   CancellationToken ct = default)
    {
        LastError = null;

        if (string.IsNullOrWhiteSpace(uuid))
        {
            LastError = "This translation file has no lineage identifier, so it cannot be published "
                      + "from here. Opening it once in the game gives it one.";
            return null;
        }

        try
        {
            var url = $"{BuildInfo.ApiBaseUrl}/translations/check-uuid?uuid={Uri.EscapeDataString(uuid)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                LastError = Describe((int)response.StatusCode, body);
                return null;
            }

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            var exists = root.TryGetProperty("exists", out var e) && e.ValueKind == JsonValueKind.True;
            var role = Text(root, "role");

            // Ours: the answer carries our own row, whatever its role in the lineage.
            if (exists && role is "main" or "fork" or "branch")
            {
                int? branches = root.TryGetProperty("branches_count", out var b)
                                && b.TryGetInt32(out var count) ? count : null;

                string? hash = root.TryGetProperty("translation", out var mine)
                               && mine.ValueKind == JsonValueKind.Object
                    ? Text(mine, "file_hash")
                    : null;

                return new LineageStanding(PublishOutcome.UpdateMine, null, branches, hash);
            }

            // Somebody else's lineage: we would be contributing to it.
            if (exists && root.TryGetProperty("main", out var main) && main.ValueKind == JsonValueKind.Object)
                return new LineageStanding(PublishOutcome.ContributeToTheirs, Text(main, "uploader"), null, null);

            // Exists without either shape: unknown to us, and inventing a reading would be worse
            // than saying so.
            if (exists)
            {
                LastError = "The server answered about this lineage in a way this version does not "
                          + "understand. Publishing from the game will use its own, newer, rules.";
                return null;
            }

            return new LineageStanding(PublishOutcome.NewTranslation, null, null, null);
        }
        catch (Exception ex)
        {
            LastError = Net.Http.Describe(ex, "the community site");
            return null;
        }
    }

    /// <summary>
    /// Send the file.
    ///
    /// ⚠ <paramref name="contentJson"/> goes as TEXT, exactly as it sits on disk. The server parses
    /// and validates it itself, and every key it carries — including ones this tool has never heard
    /// of — has to arrive intact.
    /// </summary>
    /// <returns>The published translation's id, or null on failure.</returns>
    public async Task<int?> PublishAsync(string contentJson, string apiToken,
                                         string? steamId, string? gameName,
                                         string sourceLanguage, string targetLanguage,
                                         string? notes = null, CancellationToken ct = default)
    {
        LastError = null;

        if (string.IsNullOrWhiteSpace(steamId) && string.IsNullOrWhiteSpace(gameName))
        {
            LastError = "This game has neither a Steam id nor a name to publish under.";
            return null;
        }

        // ⚠ Language NAMES, not codes: the endpoint checks them against the catalogue, and a code
        // is refused outright — which is the good outcome compared to publishing under a language
        // nobody searches by.
        if (string.IsNullOrWhiteSpace(sourceLanguage) || string.IsNullOrWhiteSpace(targetLanguage))
        {
            LastError = "Publishing needs to know which language this translates from, and into. "
                      + "Both are set in the game's own settings.";
            return null;
        }

        try
        {
            var payload = new MemoryStream();
            using (var writer = new Utf8JsonWriter(payload))
            {
                writer.WriteStartObject();
                if (!string.IsNullOrWhiteSpace(steamId)) writer.WriteString("steam_id", steamId);
                if (!string.IsNullOrWhiteSpace(gameName)) writer.WriteString("game_name", gameName);
                writer.WriteString("source_language", sourceLanguage);
                writer.WriteString("target_language", targetLanguage);
                writer.WriteString("content", contentJson);
                if (!string.IsNullOrWhiteSpace(notes)) writer.WriteString("notes", notes);
                writer.WriteEndObject();
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{BuildInfo.ApiBaseUrl}/translations");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);
            request.Content = new ByteArrayContent(payload.ToArray());
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                LastError = Describe((int)response.StatusCode, body);
                return null;
            }

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (root.TryGetProperty("translation", out var translation)
                && translation.ValueKind == JsonValueKind.Object
                && translation.TryGetProperty("id", out var id) && id.TryGetInt32(out var value))
            {
                return value;
            }

            // Accepted, and we could not read the id. The work is published either way, so this is
            // reported as a success with nothing to link to rather than as a failure.
            return 0;
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
    /// ⚠ Bounded and taken only from known fields: echoing an arbitrary response body into the
    /// interface would put a remote server in charge of what this window says.
    /// </summary>
    private static string Describe(int status, string? body)
    {
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;

                if (root.ValueKind == JsonValueKind.Object)
                {
                    foreach (var field in new[] { "error", "message" })
                    {
                        if (root.TryGetProperty(field, out var value)
                            && value.ValueKind == JsonValueKind.String
                            && value.GetString() is { Length: > 0 } text)
                        {
                            return text.Length > 300 ? text[..300] + "…" : text;
                        }
                    }
                }
            }
            catch
            {
                // Not JSON, or not shaped as expected: the status code says enough.
            }
        }

        return status switch
        {
            401 => "The site did not accept this account's sign-in. Signing in again from this "
                   + "window usually settles it.",
            413 => "That translation file is larger than the site accepts.",
            422 => "The site refused the file's contents.",
            429 => "The site is asking us to slow down. Try again in a moment.",
            _ => $"The server answered {status}.",
        };
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
