using System.Text.RegularExpressions;

namespace UnityGameTranslator.Manager.Core.Diagnostics;

/// <summary>
/// Strips personal detail out of anything the user might paste into a public issue.
///
/// We collect no telemetry, so the only way a problem reaches us is a report the user chooses
/// to send. That makes this class the whole privacy surface of the tool: what it misses ends up
/// on a public issue tracker under the user's name.
/// </summary>
public static partial class Sanitize
{
    [GeneratedRegex(@"[A-Za-z]:\\Users\\[^\\/:*?""<>|\r\n]+", RegexOptions.IgnoreCase)]
    private static partial Regex WindowsHomePattern();

    [GeneratedRegex(@"/(?:home|Users)/[^/\s]+")]
    private static partial Regex UnixHomePattern();

    /// <summary>Replaces the user's home directory with a placeholder, everywhere it appears.</summary>
    public static string Path(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "";

        var result = path;

        // Replace the actual home directory first: it is the only one we can match exactly.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
        {
            result = result.Replace(home, "<home>", StringComparison.OrdinalIgnoreCase);
        }

        // Then the generic shapes, which also catch paths belonging to other accounts.
        result = WindowsHomePattern().Replace(result, "<home>");
        result = UnixHomePattern().Replace(result, "<home>");

        var user = Environment.UserName;
        if (!string.IsNullOrEmpty(user) && user.Length > 2)
        {
            result = result.Replace(user, "<user>", StringComparison.OrdinalIgnoreCase);
        }

        return result;
    }

    /// <summary>
    /// Keeps a URL's shape without its secrets: scheme, host, port and path stay so we can tell
    /// "a local server on 11434" from "a cloud endpoint", the query string goes.
    /// </summary>
    public static string Url(string? url)
    {
        if (string.IsNullOrEmpty(url)) return "";

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "<invalid url>";

        var port = uri.IsDefaultPort ? "" : $":{uri.Port}";
        var query = string.IsNullOrEmpty(uri.Query) ? "" : "?<redacted>";
        return $"{uri.Scheme}://{uri.Host}{port}{uri.AbsolutePath}{query}";
    }

    /// <summary>Machine name and account are identifying on their own — never reported.</summary>
    public static string Text(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";

        var result = Path(text);
        var machine = Environment.MachineName;
        if (!string.IsNullOrEmpty(machine) && machine.Length > 2)
        {
            result = result.Replace(machine, "<machine>", StringComparison.OrdinalIgnoreCase);
        }
        return result;
    }
}
