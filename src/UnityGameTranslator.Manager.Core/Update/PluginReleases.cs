using UnityGameTranslator.Manager.Core.Install;

using UnityGameTranslator.Common;

namespace UnityGameTranslator.Manager.Core.Update;

/// <summary>
/// The newest plugin build published, asked ONCE for the whole machine.
///
/// ⚠ The asking-once is the whole point, not an optimisation. A report is built per game and
/// somebody here has fifty-three of them; a release lookup inside that loop would spend the
/// GitHub allowance (60 requests an hour, unauthenticated) on fifty-three identical answers and
/// then start failing — which the screens would show as "we could not check" on the games that
/// happened to come last. One answer, every game compared against it.
///
/// Kept for the life of the process rather than for a duration: a version that appears while the
/// window is open is not something anyone is waiting on, and inventing a refresh interval here
/// would be the second clock this tool has decided not to have. <see cref="Forget"/> exists so
/// that rescanning — the gesture that already means "look again" — really does look again.
/// </summary>
public sealed class PluginReleases
{
    private readonly GitHubReleaseClient _client;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private ReleaseChannel _answered;
    private PublishedRelease? _release;
    private bool _asked;

    public PluginReleases(GitHubReleaseClient? client = null)
    {
        _client = client ?? GitHubReleaseClient.ForMod();
    }

    /// <summary>
    /// Why the last lookup came back with nothing, or null when it did not fail.
    ///
    /// Kept apart from the answer for the reason every other lookup in this tool keeps them
    /// apart: "there is no newer version" and "we could not find out" are opposite pieces of
    /// news, and a screen that shows the second as the first tells someone they are up to date
    /// on the strength of a blocked request.
    /// </summary>
    public string? LastError { get; private set; }

    /// <summary>Forces the next call to ask again. Called when the user rescans.</summary>
    public void Forget()
    {
        _asked = false;
        _release = null;
        LastError = null;
    }

    /// <summary>
    /// The newest release on that channel, or null when there is none to be had — which covers
    /// both "nothing is published" and "we could not reach GitHub". <see cref="LastError"/> tells
    /// the two apart; callers that show anything to a human must consult it.
    /// </summary>
    public async Task<PublishedRelease?> LatestAsync(ReleaseChannel channel,
                                                     CancellationToken ct = default)
    {
        // Switching channel is a different question, so a stable answer must not be handed to
        // somebody who has since asked for betas.
        if (_asked && _answered == channel) return _release;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_asked && _answered == channel) return _release;

            try
            {
                _release = await _client.GetLatestAsync(channel, ct).ConfigureAwait(false);
                LastError = null;
            }
            catch (OperationCanceledException)
            {
                // Not an answer and not a failure: the caller went away. Left unasked so the next
                // one tries rather than inheriting a verdict nobody reached.
                throw;
            }
            catch (Exception ex)
            {
                _release = null;
                // The short form: this is read inside "could not check for a newer version (…)",
                // where a two-sentence explanation would be unreadable.
                LastError = Connectivity.Summarize(ex);
            }

            _answered = channel;
            _asked = true;
            return _release;
        }
        finally
        {
            _gate.Release();
        }
    }
}
