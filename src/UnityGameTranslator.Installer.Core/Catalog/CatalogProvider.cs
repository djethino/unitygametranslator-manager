using System.Reflection;
using System.Text.Json;
using UnityGameTranslator.Installer.Core.Model;
using UnityGameTranslator.Installer.Core.Platform;

namespace UnityGameTranslator.Installer.Core.Catalog;

public enum CatalogSource
{
    Remote,
    Mirror,
    Cache,
    Embedded,
}

public sealed record CatalogResult(LoaderCatalogDocument Document, CatalogSource Source, string? Error);

/// <summary>
/// Gets the loader catalog, in this order: GitHub, our site as a mirror, the local cache, then
/// the copy baked into the binary.
///
/// GitHub is first on purpose. Serving it from our own site would drop an IP into our server
/// logs on every launch, and we have no reason to hold that (see the no-telemetry decision).
/// The embedded copy guarantees the tool works offline and on a locked-down network — it can be
/// out of date, but it can never be missing.
/// </summary>
public sealed class CatalogProvider
{
    private const string EmbeddedResourceName =
        "UnityGameTranslator.Installer.Core.Resources.loaders.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly IPlatform _platform;
    private readonly HttpClient _http;

    public CatalogProvider(IPlatform platform, HttpClient? http = null)
    {
        _platform = platform;
        _http = http ?? CreateHttpClient();
    }

    private static HttpClient CreateHttpClient() => Net.Http.Create(TimeSpan.FromSeconds(10));

    private string CachePath => Path.Combine(_platform.UserDataDirectory, "loaders.cache.json");

    public async Task<CatalogResult> GetAsync(bool offline = false, CancellationToken ct = default)
    {
        string? lastError = null;

        if (!offline)
        {
            foreach (var (url, source) in new[]
                     {
                         (BuildInfo.CatalogPrimaryBase + "/loaders.json", CatalogSource.Remote),
                         (BuildInfo.CatalogMirrorBase + "/loaders.json", CatalogSource.Mirror),
                     })
            {
                try
                {
                    var json = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
                    var document = Deserialize(json);
                    if (document is not null)
                    {
                        TryWriteCache(json);
                        return new CatalogResult(document, source, null);
                    }
                    lastError = $"{source}: malformed catalog";
                }
                catch (Exception ex)
                {
                    lastError = $"{source}: {ex.GetType().Name}";
                }
            }
        }

        try
        {
            if (File.Exists(CachePath))
            {
                var document = Deserialize(File.ReadAllText(CachePath));
                if (document is not null) return new CatalogResult(document, CatalogSource.Cache, lastError);
            }
        }
        catch (Exception ex)
        {
            lastError = $"cache: {ex.GetType().Name}";
        }

        var embedded = LoadEmbedded();
        return new CatalogResult(embedded, CatalogSource.Embedded, lastError);
    }

    /// <summary>Synchronous convenience for the CLI, which has nothing else to do meanwhile.</summary>
    public CatalogResult Get(bool offline = false) =>
        GetAsync(offline).GetAwaiter().GetResult();

    public static LoaderCatalogDocument LoadEmbedded()
    {
        using var stream = Assembly.GetExecutingAssembly()
                                   .GetManifestResourceStream(EmbeddedResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded catalog missing ({EmbeddedResourceName}). This is a build error.");

        using var reader = new StreamReader(stream);
        return Deserialize(reader.ReadToEnd())
            ?? throw new InvalidOperationException("Embedded catalog is malformed. Build error.");
    }

    private static LoaderCatalogDocument? Deserialize(string json)
    {
        try
        {
            var document = JsonSerializer.Deserialize<LoaderCatalogDocument>(json, JsonOptions);
            // A catalog with no loaders is not a catalog: refuse it so we fall through to a
            // source that actually has content, instead of silently finding nothing installable.
            return document is { Loaders.Count: > 0 } ? document : null;
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
            // A read-only or full disk must not break a catalog fetch that already succeeded.
        }
    }
}
