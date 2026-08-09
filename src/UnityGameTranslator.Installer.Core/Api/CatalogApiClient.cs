using System.Text.Json;
using UnityGameTranslator.Installer.Core.Model;

namespace UnityGameTranslator.Installer.Core.Api;

/// <summary>
/// Talks to the community site, anonymously and read-only.
///
/// This is the part no other installer can offer: Steam hands us the app id for free, and the
/// public search endpoint turns it into "this game already has a French translation by someone".
/// No token, no account, no identifier of any kind is sent — the endpoint is public and rate
/// limited, and a search that fails simply means we show nothing.
/// </summary>
public sealed class CatalogApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;

    public CatalogApiClient(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(
                $"UnityGameTranslatorInstaller/{BuildInfo.Version}");
        }
    }

    /// <summary>
    /// Why the last search returned nothing, when the reason was a failure rather than an empty
    /// result. "No translation found" and "the search broke" look identical to a user, and only
    /// one of them is our fault — so the difference is kept instead of swallowed.
    /// </summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// Community translations for a Steam app id. Never throws: a failed search is a missing
    /// convenience, not a reason to block an install. It is recorded in <see cref="LastError"/>.
    /// </summary>
    public async Task<IReadOnlyList<OnlineTranslation>> SearchBySteamIdAsync(
        string steamAppId, CancellationToken ct = default)
    {
        LastError = null;
        if (string.IsNullOrWhiteSpace(steamAppId)) return Array.Empty<OnlineTranslation>();

        var url = $"{BuildInfo.ApiBaseUrl}/translations?steam_id={Uri.EscapeDataString(steamAppId)}";

        try
        {
            var json = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
            var results = Parse(json, out var parseError);
            if (parseError is not null) LastError = parseError;
            return results;
        }
        catch (Exception ex)
        {
            LastError = $"{ex.GetType().Name}: {ex.Message}";
            return Array.Empty<OnlineTranslation>();
        }
    }

    /// <summary>
    /// Community translations for a game we have no Steam id for — Epic, GOG, anything installed
    /// by hand. Without this most of a library is invisible to the catalog: the id only exists
    /// for Steam, so games bought elsewhere would read "no translation yet" for ever.
    ///
    /// Uses q=, the same parameter the mod uses (ApiClient, the branch taken when there is no
    /// Steam id). That matters more than it looks: the endpoint offers three different matchers
    /// — steam_id exact, game= exact on the slug, q= a LIKE on the name — and picking a
    /// different one would make the installer and the mod disagree about the very same game.
    /// </summary>
    public async Task<IReadOnlyList<OnlineTranslation>> SearchByNameAsync(
        string gameName, CancellationToken ct = default)
    {
        LastError = null;
        var name = gameName.Trim();
        if (name.Length < 2) return Array.Empty<OnlineTranslation>();

        var url = $"{BuildInfo.ApiBaseUrl}/translations?q={Uri.EscapeDataString(name)}";

        try
        {
            var json = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
            var results = Parse(json, out var parseError);
            if (parseError is not null) LastError = parseError;
            return results;
        }
        catch (Exception ex)
        {
            LastError = $"{ex.GetType().Name}: {ex.Message}";
            return Array.Empty<OnlineTranslation>();
        }
    }

    /// <summary>
    /// The endpoint wraps its results in a "translations" envelope alongside a count. A bare
    /// array and a "data" envelope are also accepted, so a future pagination change does not
    /// silently reduce every search to zero results.
    /// </summary>
    private static IReadOnlyList<OnlineTranslation> Parse(string json, out string? error)
    {
        error = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var array = root.ValueKind switch
            {
                JsonValueKind.Array => root,
                JsonValueKind.Object when root.TryGetProperty("translations", out var t) => t,
                JsonValueKind.Object when root.TryGetProperty("data", out var data) => data,
                _ => default,
            };

            if (array.ValueKind != JsonValueKind.Array)
            {
                error = "unexpected response shape (no translations array)";
                return Array.Empty<OnlineTranslation>();
            }

            var results = new List<OnlineTranslation>();
            foreach (var element in array.EnumerateArray())
            {
                var item = element.Deserialize<OnlineTranslation>(JsonOptions);
                if (item is null) continue;

                // The uploader is a plain string today but an object in some older payloads.
                // Normalising here keeps every caller from wondering which it got.
                if (item.Author is null && element.TryGetProperty("uploader", out var uploader))
                {
                    item.Author = uploader.ValueKind switch
                    {
                        JsonValueKind.String => uploader.GetString(),
                        JsonValueKind.Object when uploader.TryGetProperty("name", out var name)
                            => name.GetString(),
                        _ => null,
                    };
                }

                results.Add(item);
            }
            return results;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return Array.Empty<OnlineTranslation>();
        }
    }
}
