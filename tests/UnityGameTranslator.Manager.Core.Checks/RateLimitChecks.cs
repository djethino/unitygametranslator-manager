using System.Net;
using UnityGameTranslator.Manager.Core.Net;

namespace UnityGameTranslator.Manager.Core.Checks;

/// <summary>GitHub's hourly limit, told as such — never as a network fault.</summary>
internal static class RateLimitChecks
{
    /// <summary>A server that answers whatever it was told to, without a network.</summary>
    private sealed class Answer(HttpStatusCode status, string? remaining, string? reset) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = new HttpResponseMessage(status);
            if (remaining is not null) response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", remaining);
            if (reset is not null) response.Headers.TryAddWithoutValidation("X-RateLimit-Reset", reset);
            return Task.FromResult(response);
        }
    }

    private static Exception? Send(string url, HttpStatusCode status, string? remaining, string? reset)
    {
        using var client = new HttpClient(new GitHubRateLimit(new Answer(status, remaining, reset)));
        try
        {
            client.GetAsync(url).GetAwaiter().GetResult().Dispose();
            return null;
        }
        catch (Exception e)
        {
            return e;
        }
    }

    internal static void WhenGitHubSaysWait()
    {
        Program.Section("GitHub's hourly limit");

        const string api = "https://api.github.com/repos/o/r/releases/latest";
        var reset = new DateTimeOffset(2026, 9, 28, 15, 11, 0, TimeSpan.Zero);

        var limited = Send(api, HttpStatusCode.Forbidden, "0", reset.ToUnixTimeSeconds().ToString());
        Program.Check(limited is GitHubRateLimitException { ResetsAt: { } at } e
                      && at == reset && e.Message.Contains(reset.ToLocalTime().ToString("HH:mm")),
            "the limit is named, with the time it lifts",
            "it read \"403 (rate limit exceeded)\" beside advice about firewalls (2026-09-28)");

        Program.Check(Send(api, HttpStatusCode.Forbidden, "12", null) is null
                      && Send("https://example.com/x", HttpStatusCode.Forbidden, "0", null) is null
                      && Send(api, HttpStatusCode.OK, "0", null) is null,
            "any other answer passes through untouched",
            "a refusal that is not the limit, or another host, is the caller's to read");
    }
}
