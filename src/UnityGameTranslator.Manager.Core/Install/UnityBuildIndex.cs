namespace UnityGameTranslator.Manager.Core.Install;

/// <summary>
/// Unity's description of one build's downloads — the `unity-&lt;version&gt;-linux.ini` next to the
/// installers on Unity's server: one section per module, each naming its file, size and md5.
///
/// ⚠ Read for two fields and nothing else. The address it gives is RELATIVE, joined to the build's
/// folder on Unity's own host by <see cref="Catalog.RuntimeLibraryOrigins.UnityBuildFileUrl"/>: an
/// absolute address in it is refused, so the file can never send a download elsewhere.
/// </summary>
public static class UnityBuildIndex
{
    public sealed record Package(string Url, long? Size);

    /// <summary>The package a section names ("Windows-Mono"), or null when there is none.</summary>
    public static Package? Section(string index, string name)
    {
        var inside = false;
        string? url = null;
        long? size = null;

        foreach (var raw in index.Split('\n'))
        {
            var line = raw.Trim();

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                if (inside) break;
                inside = line[1..^1].Trim().Equals(name, StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inside) continue;

            var equals = line.IndexOf('=');
            if (equals <= 0) continue;

            var key = line[..equals].Trim();
            var value = line[(equals + 1)..].Trim();

            if (key.Equals("url", StringComparison.OrdinalIgnoreCase)) url = value;
            else if (key.Equals("size", StringComparison.OrdinalIgnoreCase) && long.TryParse(value, out var bytes)) size = bytes;
        }

        if (url is null || url.Contains("://", StringComparison.Ordinal) || url.Contains("..", StringComparison.Ordinal))
            return null;

        return new Package(url, size);
    }
}
