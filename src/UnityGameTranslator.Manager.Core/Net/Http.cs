using System.Net;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Manager.Core.Net;

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

    /// <summary>
    /// The number this machine drew once, sent so the site can group this account's accesses by
    /// machine. Null until startup sets it, and null for good when it could not be written.
    ///
    /// ⚠ Set here rather than passed to every caller, for the same reason as the proxy: nine
    /// classes build calls, and a value that has to be remembered at each of them is a value that
    /// will be missing from some. See <see cref="Settings.MachineIdentity"/> for what it is — and
    /// above all for what it is NOT: nothing about this machine is measured.
    /// </summary>
    public static string? DeviceId { get; set; }

    public static HttpClient Create(TimeSpan timeout)
    {
        var handler = BuildHandler();

        // 🔴 **Asking for compression and decompressing it are ONE decision, taken here.** This
        // tool asked for neither, which is why it never broke — but the mod set the header by hand
        // and left the handler alone, so a server that took the offer answered gzip it could not
        // read, and every call died in the JSON parser (2026-08-20, see the mod's ApiClient).
        //
        // AutomaticDecompression sends `Accept-Encoding` ITSELF and decompresses what comes back,
        // which is exactly why it is the only correct way to do this: the two halves cannot drift
        // apart. Nothing must ever add that header by hand on top of it.
        //
        // ⚠ Nothing changes today — the site does not compress JSON — and that is the point: the
        // day it does, this tool follows instead of failing.
        if (handler is not null)
        {
            try { handler.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate; }
            catch { /* a handler that refuses it still works, uncompressed */ }
        }

        // 🔴 **The machine header is added when the request LEAVES, never when the client is made.**
        //
        // It was a default header at first, and that was a real defect, found in production on
        // 2026-08-27: `MainWindow` holds `AccountLineages` as a FIELD INITIALISER, so its client is
        // built while the window is being constructed — before the line further down that creates
        // the SettingsStore and therefore before DeviceId exists. That one client carries every
        // authenticated call, so the Manager's own access was the one thing that never declared its
        // machine, and it sat in the "not named" heap looking exactly like a failure of the whole
        // idea.
        //
        // ⚠ Reordering the fields would have worked and would have been wrong: the next long-lived
        // client somebody adds brings the bug straight back, silently. Reading the value at send
        // time makes the order stop mattering.
        //
        // ⚠ Same shape as the note on Proxy above ("clients created afterwards"), which is exactly
        // the trap this avoids: a value snapshotted at construction is a value that can be missing
        // from precisely the client that matters.
        var client = new HttpClient(new DeviceHeader(handler ?? DefaultHandler()));

        client.Timeout = timeout;
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"UnityGameTranslatorManager/{BuildInfo.Version}");

        // What a buffered answer may weigh. Some thirty calls read an answer into a string — the
        // catalogue, GitHub's API, the site, the AI server at whatever address the person typed —
        // and the largest thing any of them legitimately reads back is a translation file, which
        // the site caps at the socle's figure. Files are streamed to disk elsewhere (Download) and
        // are not subject to this. ⚠ The socle's constant and not a number of this file's, so the
        // day the ecosystem's cap moves, this moves with it.
        client.MaxResponseContentBufferSize = Limits.TranslationFileBytes;

        return client;
    }

    /// <summary>
    /// Puts the machine identifier on each request as it goes out.
    /// </summary>
    /// <remarks>
    /// ⚠ Per request, never per client — see the note where this is wired in. And it never replaces
    /// a header a caller set itself: a caller that says something specific about a request knows
    /// more than this does.
    /// </remarks>
    private sealed class DeviceHeader(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        internal const string Name = "X-UGT-Device";

        /// <summary>
        /// The one host the identifier is for. Compiled in, so a build pointed at a local site
        /// sends it there and nowhere else.
        /// </summary>
        private static readonly string? SiteHost =
            Uri.TryCreate(BuildInfo.ApiBaseUrl, UriKind.Absolute, out var site) ? site.Host : null;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (ReadsTheHeader(request)
                && Settings.MachineIdentity.IsWellFormed(DeviceId)
                && !request.Headers.Contains(Name))
            {
                request.Headers.TryAddWithoutValidation(Name, DeviceId);
            }

            return base.SendAsync(request, cancellationToken);
        }

        /// <summary>
        /// Whether whoever answers this request will read the identifier at all.
        ///
        /// 🔴 **Our site, and only the calls where it looks.** This handler sits in the one
        /// factory every client comes from — the catalogue on GitHub, the loader builds, the key
        /// tests at Google and DeepL, the Ollama installer, whatever AI server somebody typed in —
        /// and for a day it put the identifier on all of them. A random number that says nothing
        /// about the machine still says "the same machine as last time", and handing that to four
        /// third parties is the tracking the README promises does not happen. Found by the audit
        /// of 2026-08-27.
        ///
        /// The site reads the header in exactly two places: beside a bearer token, where it groups
        /// this account's accesses, and on `POST /auth/device`, the one anonymous call that has to
        /// name the machine so the per-game cap can act. An anonymous search does not need it, so
        /// it does not get it: somebody who never signed in stays a stranger from one launch to
        /// the next, which is the whole point of not signing in.
        ///
        /// ⚠ Default headers are merged into the request before it reaches a handler, so a token
        /// set on the client is visible here.
        /// </summary>
        private static bool ReadsTheHeader(HttpRequestMessage request)
        {
            if (SiteHost is null || request.RequestUri is not { } uri) return false;
            if (!string.Equals(uri.Host, SiteHost, StringComparison.OrdinalIgnoreCase)) return false;

            return request.Headers.Authorization is not null
                || uri.AbsolutePath.EndsWith("/auth/device", StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The plain handler, carrying decompression and nothing else.
    /// </summary>
    /// <remarks>
    /// ⚠ Proxy settings are deliberately untouched: a <see cref="HttpClientHandler"/> left alone
    /// uses the system proxy, which is what `new HttpClient()` did here before. The only reason
    /// this exists is that decompression has to be set on a handler, and "default" mode had none.
    /// </remarks>
    private static HttpMessageHandler DefaultHandler()
    {
        try
        {
            return new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };
        }
        catch
        {
            return new HttpClientHandler();
        }
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
        // The request DID arrive — the server answered, it simply answered badly. Not a
        // connection problem, so none of the advice below applies to it.
        if (exception is HttpRequestException { StatusCode: not null } http)
            return $"Could not reach {what}: the server answered {(int)http.StatusCode}. "
                 + "Nothing was lost — you can try again.";

        // 🔴 The cause comes from the shared library, not from a guess made here. This used to
        // say "a firewall or antivirus blocking this program is the usual cause" whatever had
        // happened — which is right for a blocked socket and WRONG for a name that does not
        // resolve or a server that is down, the two cases where it sends someone hunting through
        // their antivirus for a problem that is not there. The socket error code says which it is.
        // Same wording as the mod, on purpose: one failure must not read as two different things.
        var problem = Connectivity.Classify(exception);
        var cause = Connectivity.Explain(problem) ?? exception.Message;

        // Only where a proxy could plausibly be in the way. With no network at all it is noise.
        var route = problem == ConnectionProblem.NoNetwork ? null : RouteNote();

        // The guess is still worth making when nothing could be named — that is what a guess is for.
        var hint = problem == ConnectionProblem.Unknown
            ? "A firewall or antivirus blocking this program is the usual cause."
            : null;

        return string.Join(" ", new[]
        {
            $"Could not reach {what}.", cause, hint, route, "Nothing was lost — you can try again.",
        }.Where(part => !string.IsNullOrEmpty(part)));
    }

    /// <summary>Where requests actually go, so the person knows what to check.</summary>
    private static string RouteNote() => (Proxy.Mode ?? "default").Trim().ToLowerInvariant() switch
    {
        "custom" => $"Requests go through the proxy you configured ({Proxy.Url}).",
        "none" => "Requests bypass any proxy, as you asked.",
        "system" => "Requests follow the system proxy settings.",
        _ => "If you are behind a company proxy, set it in the network settings.",
    };
}
