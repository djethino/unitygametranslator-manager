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
/// <param name="OnABranch">
/// This account's own row in the lineage is a CONTRIBUTION, not a translation of its own.
///
/// 🔴 Not the same question as <see cref="PublishOutcome.ContributeToTheirs"/>, and conflating the
/// two is a mistake that shows. That outcome means "you have no row here yet and sending one would
/// make you a contributor"; this flag means "you already are one". An established contributor comes
/// back through <see cref="PublishOutcome.UpdateMine"/> — so reading the outcome alone offered them
/// a "This translation is finished" box that the server discards on arrival, because a branch always
/// inherits its Main's.
/// </param>
/// <param name="Notes">The description carried by this account's row, as the server holds it.</param>
/// <param name="ResourcesUrl">
/// The link carried by this account's row — its OWN, never the Main's it may be borrowing for
/// display. Sending an inherited link back would pin a branch to a copy of it.
/// </param>
/// <param name="Status">"complete" or "in_progress", as published. Never guessed here.</param>
/// <param name="RowId">This account's own row, the one anything written goes to.</param>
/// <param name="AcceptsBranches">
/// Whether this lineage takes contributions — the Main's own decision.
///
/// ⚠ Null on a server that predates the field, and null is NOT "no": announcing that somebody
/// works alone because a server said nothing would put words in their mouth.
/// </param>
/// <param name="BranchFrozen">
/// A branch on a Main that has since closed. Nothing can be done with it as a branch any more —
/// not publishing, not even describing it — and the way on is to publish it as its own translation.
/// </param>
/// <param name="MainMissing">
/// A branch whose Main has been removed by its author. Same wall as <paramref name="BranchFrozen"/>
/// and a different sentence: what they were building on is not published any more, so their copy is
/// the only one left.
/// </param>
/// <param name="MainAbandoned">
/// A branch whose Main is still published and whose owner erased their account. Nobody will ever
/// read a contribution to it — and unlike the two above, nothing about the lineage looks wrong.
/// </param>
public sealed record LineageStanding(PublishOutcome Outcome, string? MainOwner,
                                     int? BranchesCount, string? ServerFileHash,
                                     bool OnABranch = false, string? Notes = null,
                                     string? ResourcesUrl = null, string? Status = null,
                                     int? RowId = null, bool? AcceptsBranches = null,
                                     bool BranchFrozen = false,
                                     bool? MainMissing = null, bool? MainAbandoned = null)
{
    /// <summary>Whether this account has a row here at all — the thing details can be edited on.</summary>
    public bool HasARowOfItsOwn => Outcome == PublishOutcome.UpdateMine;

    /// <summary>Whether the author's "finished" declaration is theirs to make on this row.</summary>
    public bool MayDeclareFinished => HasARowOfItsOwn && !OnABranch;

    /// <summary>
    /// Whether the contributions decision is theirs to make. Same test as the one above, and for
    /// the same reason: both belong to the Main, and a branch shown either would be answering for
    /// somebody else's translation.
    /// </summary>
    public bool MayDecideContributions => MayDeclareFinished;

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

                var mine = root.TryGetProperty("translation", out var block)
                           && block.ValueKind == JsonValueKind.Object
                    ? block
                    : default;

                var has = mine.ValueKind == JsonValueKind.Object;

                return new LineageStanding(
                    PublishOutcome.UpdateMine, null, branches,
                    has ? Text(mine, "file_hash") : null,
                    OnABranch: role == "branch",
                    Notes: has ? Text(mine, "notes") : null,
                    // ⚠ The row's OWN link. "resources_url" beside it is the effective one — a
                    // branch's Main's, when the branch has none — and is for showing, not for
                    // sending back. Falls back on servers older than the distinction.
                    ResourcesUrl: has ? Text(mine, "resources_url_own") ?? Text(mine, "resources_url") : null,
                    Status: has ? Text(mine, "status") : null,
                    RowId: has && mine.TryGetProperty("id", out var rowId) && rowId.TryGetInt32(out var row)
                        ? row
                        : null,
                    AcceptsBranches: Flag(root, "accepts_branches"),
                    BranchFrozen: Flag(root, "branch_frozen") == true,

                    // Read here so the publish path can refuse before sending, the way it already
                    // does for a frozen branch. Null on a server that predates them, and null is
                    // "not asked" — never "the Main is fine".
                    MainMissing: Flag(root, "main_missing"),
                    MainAbandoned: Flag(root, "main_abandoned"));
            }

            // Somebody else's lineage: we would be contributing to it — if they take contributions.
            if (exists && root.TryGetProperty("main", out var main) && main.ValueKind == JsonValueKind.Object)
                return new LineageStanding(PublishOutcome.ContributeToTheirs, Text(main, "uploader"),
                                           null, null,
                                           AcceptsBranches: Flag(root, "accepts_branches"),

                                           // ⚠ Here too, and not only on our own row: somebody
                                           // holding this file and about to contribute for the
                                           // first time meets the same wall, and meets it before
                                           // the work rather than after.
                                           MainAbandoned: Flag(root, "main_abandoned"));

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
    /// <param name="notes">
    /// The description to store. 🔴 <b>Null does NOT mean "keep".</b> The endpoint writes this
    /// field from the request on every update, so an absent one stores null — which is how this
    /// tool erased, on each publish, the description its author had written on the site or in the
    /// game. A caller with nothing new to say must send back what the server already holds
    /// (<see cref="LineageStanding.Notes"/>), not nothing.
    /// </param>
    /// <param name="resourcesUrl">The link to fonts or images, under the same rule as notes.</param>
    /// <param name="status">
    /// "complete" or "in_progress". This one IS kept when omitted — the endpoint reads it as
    /// `?? existing`. The asymmetry is the server's, and it is why the two are documented apart
    /// rather than together.
    /// </param>
    /// <returns>The published translation's id, or null on failure.</returns>
    /// <param name="company">
    /// The studio Unity records beside the product name, when the game states one.
    ///
    /// 🔴 **It is what turns a weak name into an identity.** A product called "Game" or "Prototype"
    /// identifies nothing; with the studio beside it, two machines looking at the same folder agree
    /// without anybody typing anything. The site keeps the pair as `unity_name`/`unity_company` and
    /// resolves with it — see the migration that added them, and what their absence used to cost.
    /// </param>
    public async Task<int?> PublishAsync(string contentJson, string apiToken,
                                         string? steamId, string? gameName,
                                         string sourceLanguage, string targetLanguage,
                                         string? notes = null, string? status = null,
                                         string? resourcesUrl = null,
                                         bool? acceptsBranches = null,
                                         string? company = null,
                                         CancellationToken ct = default)
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

                // ⚠ Sent whenever the game states one. An older site ignores an unknown field, so
                // this costs nothing where it is not understood.
                if (!string.IsNullOrWhiteSpace(company)) writer.WriteString("game_company", company);
                writer.WriteString("source_language", sourceLanguage);
                writer.WriteString("target_language", targetLanguage);
                writer.WriteString("content", contentJson);

                // ⚠ Written whenever the caller STATED something, empty string included — that is
                // how a description or a link gets cleared on purpose. Omitted only when the
                // caller passed null, which by the rule above means "I have nothing to say about
                // this field", and costs the stored value. See the parameter docs.
                if (notes is not null)
                {
                    if (notes.Length == 0) writer.WriteNull("notes");
                    else writer.WriteString("notes", notes);
                }

                // ⚠ Empty rather than "" would be refused: the endpoint validates this as a URL
                // when present, so a blank must go as null to mean "no link".
                if (resourcesUrl is not null)
                {
                    if (resourcesUrl.Length == 0) writer.WriteNull("resources_url");
                    else writer.WriteString("resources_url", resourcesUrl);
                }

                // ⚠ OMITTED rather than defaulted when null. The server then keeps whatever the
                // translation already had — which is how this tool has always behaved, and why it
                // never undid a "complete" the way the mod did. A caller that has no opinion must
                // send nothing at all.
                if (!string.IsNullOrWhiteSpace(status)) writer.WriteString("status", status);

                // Same rule as status: omitted when the caller has no opinion, so the server keeps
                // what it holds. Null is what a branch sends — the decision is its Main's.
                if (acceptsBranches is bool takes) writer.WriteBoolean("accepts_branches", takes);

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
    /// Change what is SAID about a published translation, without sending the file.
    ///
    /// 🔴 A description or a link is not a release. Doing this through an upload would send
    /// whatever else the local file has gained since it was last published — so it goes to its own
    /// endpoint, which touches those three fields and nothing else.
    ///
    /// ⚠ Each argument is sent only when it is non-null: null means "no opinion", empty means
    /// "clear it". The endpoint reads absence the same way, so a caller can fix a link without
    /// restating a description it never read.
    ///
    /// ⚠ <paramref name="status"/> must be null on a branch. The server refuses it rather than
    /// ignoring it — a contribution inherits its Main's — and reporting that refusal as an error
    /// is right: the alternative is a client believing it set something.
    /// </summary>
    /// <returns>True when the change was stored.</returns>
    /// <param name="acceptsBranches">
    /// The Main's decision on contributions, or null when the caller has none to give — a branch,
    /// which may not answer for the lineage it contributes to.
    /// </param>
    public async Task<bool> UpdateDetailsAsync(int translationId, string apiToken,
                                               string? notes = null, string? resourcesUrl = null,
                                               string? status = null,
                                               bool? acceptsBranches = null,
                                               CancellationToken ct = default)
    {
        LastError = null;

        try
        {
            var payload = new MemoryStream();
            using (var writer = new Utf8JsonWriter(payload))
            {
                writer.WriteStartObject();

                if (notes is not null)
                {
                    if (notes.Length == 0) writer.WriteNull("notes");
                    else writer.WriteString("notes", notes);
                }

                if (resourcesUrl is not null)
                {
                    if (resourcesUrl.Length == 0) writer.WriteNull("resources_url");
                    else writer.WriteString("resources_url", resourcesUrl);
                }

                if (!string.IsNullOrWhiteSpace(status)) writer.WriteString("status", status);

                // ⚠ Sent only when there is an answer. Null means "not this caller's to decide" —
                // a branch — and writing false there would close a Main that never asked.
                if (acceptsBranches is bool open) writer.WriteBoolean("accepts_branches", open);

                writer.WriteEndObject();
            }

            var url = $"{BuildInfo.ApiBaseUrl}/translations/{translationId}/details";
            using var request = new HttpRequestMessage(HttpMethod.Patch, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);
            request.Content = new ByteArrayContent(payload.ToArray());
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);

            if (response.IsSuccessStatusCode) return true;

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            // ⚠ 404 is the one worth its own words: this endpoint arrived after the tool did, so a
            // site that has not been updated answers "no such route" — which is not the user
            // having done anything wrong, and must not read as one.
            LastError = (int)response.StatusCode == 404
                ? "This site does not offer editing the details on their own yet. Publishing from "
                  + "the game can still change them."
                : Describe((int)response.StatusCode, body);

            return false;
        }
        catch (Exception ex)
        {
            LastError = Net.Http.Describe(ex, "the community site");
            return false;
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

    /// <summary>
    /// A boolean the server may not have sent. Null is the answer for absent, never false — a
    /// missing field is a server that predates it, not somebody's decision.
    /// </summary>
    private static bool? Flag(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => (bool?) null,
            }
            : null;
}
