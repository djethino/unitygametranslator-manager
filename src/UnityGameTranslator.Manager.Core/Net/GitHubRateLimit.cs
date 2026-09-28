using System.Globalization;
using System.Net;

namespace UnityGameTranslator.Manager.Core.Net;

/// <summary>
/// GitHub refused because this network asked too often — not a network problem.
///
/// 🔴 **Why it has a type of its own** (2026-09-28). GitHub's API allows 60 requests an hour per
/// address without an account, and every check, report and install asks it. The refusal arrived
/// as "403 (rate limit exceeded)", and the update notice added "a firewall, antivirus or proxy may
/// be blocking UGT Manager" — sending somebody to look for a problem that did not exist, when the
/// only answer is to wait until a time GitHub states.
/// </summary>
public sealed class GitHubRateLimitException : HttpRequestException
{
    public GitHubRateLimitException(DateTimeOffset? resetsAt, HttpStatusCode status)
        : base(Describe(resetsAt), null, status)
    {
        ResetsAt = resetsAt;
    }

    /// <summary>When GitHub says it will answer again, if it said.</summary>
    public DateTimeOffset? ResetsAt { get; }

    private static string Describe(DateTimeOffset? resetsAt) =>
        "GitHub limits how often one network can ask it (60 times an hour without an account)."
        + (resetsAt is { } at
            ? $" It answers again at {at.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)}."
            : " It answers again within the hour.");
}

/// <summary>
/// Turns GitHub's "rate limit reached" answer into <see cref="GitHubRateLimitException"/>, for
/// requests to its API only. Every other answer passes through untouched.
/// </summary>
public sealed class GitHubRateLimit(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (request.RequestUri is { } uri
            && uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase)
            && response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
            && Header(response, "X-RateLimit-Remaining") == "0")
        {
            DateTimeOffset? resets = long.TryParse(Header(response, "X-RateLimit-Reset"),
                                                   NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch)
                ? DateTimeOffset.FromUnixTimeSeconds(epoch)
                : null;

            var status = response.StatusCode;
            response.Dispose();
            throw new GitHubRateLimitException(resets, status);
        }

        return response;
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
}
