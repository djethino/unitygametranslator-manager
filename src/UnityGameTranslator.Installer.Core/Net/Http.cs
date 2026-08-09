using System.Net;

namespace UnityGameTranslator.Installer.Core.Net;

/// <summary>
/// How to reach the network. Same four modes and the same names as the mod's `proxy_mode`, so
/// someone who configured one does not have to learn a second vocabulary for the same problem.
/// </summary>
public sealed record ProxySettings(
    string Mode = "default",
    string? Url = null,
    string? Username = null,
    string? Password = null,
    bool BypassLocal = true)
{
    public static readonly ProxySettings Default = new();
}

/// <summary>
/// The one place an HttpClient is made.
///
/// It exists for two reasons that both come from the same place — a user behind a corporate proxy
/// or a firewall:
///
/// 1. **Proxy settings have to apply everywhere.** Nine classes each built their own client, so a
///    proxy configured once would have taken effect in some of them and not others, and the
///    resulting "it works when I search but not when I install" is close to undiagnosable.
///
/// 2. **A blocked call must say so in those words.** Sockets fail with messages like
///    "No connection could be made because the target machine actively refused it", which sends
///    people looking at our servers. <see cref="Describe"/> turns those into a sentence naming the
///    firewall and the proxy, because that is what it usually is.
///
/// ⚠ Nothing here retries on its own. Retrying is the caller's business — it is the caller that
/// knows what state to keep — but every caller MUST offer it. Losing ten minutes of choices to a
/// firewall prompt is the thing this tool must never do.
/// </summary>
public static class Http
{
    /// <summary>
    /// Applied to every client made from here on. Set once at startup from the settings; a
    /// change takes effect for clients created afterwards, which is why long-lived clients are
    /// rebuilt rather than kept.
    /// </summary>
    public static ProxySettings Proxy { get; set; } = ProxySettings.Default;

    public static HttpClient Create(TimeSpan timeout)
    {
        var handler = BuildHandler();
        var client = handler is null ? new HttpClient() : new HttpClient(handler);

        client.Timeout = timeout;
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"UnityGameTranslatorInstaller/{BuildInfo.Version}");

        return client;
    }

    /// <summary>
    /// Null for "default", which leaves HttpClient to its own defaults — the behaviour of every
    /// build before this existed, and the one that works for almost everybody.
    /// </summary>
    private static HttpClientHandler? BuildHandler()
    {
        var mode = (Proxy.Mode ?? "default").Trim().ToLowerInvariant();
        if (mode == "default") return null;

        HttpClientHandler handler;
        try { handler = new HttpClientHandler(); }
        catch { return null; }

        try
        {
            switch (mode)
            {
                case "none":
                    handler.UseProxy = false;
                    return handler;

                case "system":
                    // Read from the system every time rather than trusting a cached default:
                    // that is the point of choosing "system" explicitly.
                    var system = WebRequest.GetSystemWebProxy();
                    if (system is null) return null;
                    handler.UseProxy = true;
                    handler.Proxy = system;
                    return handler;

                case "custom":
                    if (string.IsNullOrWhiteSpace(Proxy.Url)) return null;

                    var proxy = new WebProxy(Proxy.Url.Trim(), Proxy.BypassLocal);
                    if (!string.IsNullOrEmpty(Proxy.Username))
                    {
                        proxy.Credentials = new NetworkCredential(
                            Proxy.Username, Proxy.Password ?? string.Empty);
                    }

                    handler.UseProxy = true;
                    handler.Proxy = proxy;
                    return handler;

                default:
                    return null;
            }
        }
        catch
        {
            // A malformed proxy URL must not stop the tool from running; it falls back to the
            // default route and the failure surfaces as a normal connection error, with the
            // proxy named in the explanation.
            return null;
        }
    }

    /// <summary>
    /// What went wrong, said the way it is usually true.
    ///
    /// The raw exception text points at the machine we tried to reach, which is almost never the
    /// problem. On a home machine it is a firewall prompt that was dismissed or never appeared;
    /// on a work machine it is a proxy. Both are things the person can act on, and neither is
    /// suggested by "the target machine actively refused it".
    /// </summary>
    public static string Describe(Exception exception, string what)
    {
        var reason = exception switch
        {
            TaskCanceledException => "it took too long to answer",
            HttpRequestException { StatusCode: not null } http
                => $"the server answered {(int)http.StatusCode}",
            HttpRequestException => "the connection could not be made",
            _ => exception.Message,
        };

        var route = (Proxy.Mode ?? "default").Trim().ToLowerInvariant() switch
        {
            "custom" => $"Requests go through the proxy you configured ({Proxy.Url}).",
            "none" => "Requests bypass any proxy, as you asked.",
            "system" => "Requests follow the system proxy settings.",
            _ => "If you are behind a company proxy, set it in the network settings.",
        };

        return $"Could not reach {what}: {reason}. "
             + $"A firewall or antivirus blocking this program is the usual cause. {route} "
             + "Nothing was lost — you can try again.";
    }
}
