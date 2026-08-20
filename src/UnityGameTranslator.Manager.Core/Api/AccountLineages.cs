using System.Text.Json;
using UnityGameTranslator.Manager.Core.Model;
using UnityGameTranslator.Manager.Core.Net;

namespace UnityGameTranslator.Manager.Core.Api;

/// <summary>
/// Where the signed-in account stands in each translation lineage it takes part in.
///
/// One call to /me/translations covers the whole library, and that is deliberate: check-uuid
/// answers the same question for a single uuid — the right shape before an upload, the wrong one
/// for a tool looking at fifty games at once. Fifty calls against a sixty-per-minute budget would
/// starve every other thing the account does, and half of them would come back throttled, which
/// reads to a user as "the site does not know about my translation".
///
/// Fetched once and kept for the life of the window. Signing in or out drops it: the answer is
/// about a person, and the person changed.
/// </summary>
public sealed class AccountLineages
{
    private readonly HttpClient _http;
    private Dictionary<string, LineagePosition>? _index;
    private string? _fetchedFor;

    public AccountLineages(HttpClient? http = null)
    {
        _http = http ?? Http.Create(TimeSpan.FromSeconds(10));
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(
                $"UnityGameTranslatorManager/{BuildInfo.Version}");
        }
    }

    /// <summary>
    /// Why the last fetch came back with nothing, when the reason was a failure rather than an
    /// account with no translations. The two must never look alike: one of them is our problem.
    /// </summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// True once an answer has been read, whatever it contained.
    ///
    /// The interface says nothing at all about a role until this is true — an account whose
    /// lineages have not been read yet looks exactly like an account that owns none, and
    /// announcing "not yours" on that basis would be a guess presented as a fact.
    /// </summary>
    public bool Known => _index is not null;

    /// <summary>Reads the account's translations once, then answers from memory.</summary>
    public async Task EnsureAsync(string? apiToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiToken))
        {
            // Signed out: nothing to know, and nothing to complain about either.
            _index = null;
            _fetchedFor = null;
            LastError = null;
            return;
        }

        if (_index is not null && _fetchedFor == apiToken) return;

        LastError = null;

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"{BuildInfo.ApiBaseUrl}/me/translations");

            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiToken);

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                LastError = "The site no longer accepts this token. Signing in again will fix it.";
                return;
            }

            if (!response.IsSuccessStatusCode)
            {
                LastError = $"The server answered {(int)response.StatusCode}.";
                return;
            }

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            _index = Parse(json, out var parseError);
            _fetchedFor = apiToken;

            if (parseError is not null)
            {
                LastError = parseError;
                _index = null;
                _fetchedFor = null;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LastError = Http.Describe(ex, "the community site");
        }
    }

    /// <summary>
    /// The account's position in one lineage, or null when it holds none — which is the ordinary
    /// case for a translation somebody else wrote and this player merely downloaded.
    /// </summary>
    public LineagePosition? For(string? uuid)
    {
        if (_index is null || string.IsNullOrWhiteSpace(uuid)) return null;
        return _index.TryGetValue(uuid, out var position) ? position : null;
    }

    /// <summary>Everything the account holds. Used to answer "how many await me", not per game.</summary>
    public IEnumerable<LineagePosition> All => _index?.Values ?? Enumerable.Empty<LineagePosition>();

    /// <summary>Drops what was read, so the next Ensure asks again.</summary>
    public void Forget()
    {
        _index = null;
        _fetchedFor = null;
        LastError = null;
    }

    /// <summary>
    /// Reads the lineage fields, and ignores an entry that carries none.
    ///
    /// A site older than these fields answers with the same envelope minus file_uuid and role, and
    /// the result is an empty index — so nothing is claimed anywhere. That is the correct outcome:
    /// an installer that inferred a role from a game and a language would eventually award someone
    /// a Main they do not own.
    /// </summary>
    private static Dictionary<string, LineagePosition>? Parse(string json, out string? error)
    {
        error = null;

        try
        {
            using var document = JsonDocument.Parse(json);

            if (!document.RootElement.TryGetProperty("translations", out var array)
                || array.ValueKind != JsonValueKind.Array)
            {
                error = "unexpected response shape (no translations array)";
                return null;
            }

            var index = new Dictionary<string, LineagePosition>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in array.EnumerateArray())
            {
                var uuid = Text(entry, "file_uuid");
                var role = Text(entry, "role");

                if (string.IsNullOrWhiteSpace(uuid) || string.IsNullOrWhiteSpace(role)) continue;

                // One row per lineage and per user is a server-side rule, but a duplicate must not
                // take the window down over a display detail.
                index[uuid] = new LineagePosition
                {
                    Uuid = uuid,
                    IsMain = string.Equals(role, "main", StringComparison.OrdinalIgnoreCase),
                    BranchesCount = Number(entry, "branches_count"),

                    // ⚠ What is actually WAITING: contributions not been through, holding
                    // something. Null on a server too old to say, and null is unknown — a screen
                    // falls back to the raw count rather than announcing that nothing waits.
                    BranchesWithWork = Number(entry, "branches_with_work"),
                    LinesAvailable = Number(entry, "lines_available"),

                    MainMissing = Flag(entry, "main_missing"),
                    AcceptsBranches = Flag(entry, "accepts_branches"),
                    BranchFrozen = Flag(entry, "branch_frozen"),
                    SiteId = Number(entry, "id") ?? 0,
                    TargetLanguage = Text(entry, "target_language"),
                    SourceLanguage = Text(entry, "source_language"),
                    GameName = entry.TryGetProperty("game", out var game)
                               && game.ValueKind == JsonValueKind.Object
                        ? Text(game, "name")
                        : null,
                };
            }

            return index;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : null;

    private static bool? Flag(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            }
            : null;
}
