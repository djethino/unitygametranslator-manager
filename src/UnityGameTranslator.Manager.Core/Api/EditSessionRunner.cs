using UnityGameTranslator.Manager.Core.Detection;
using UnityGameTranslator.Manager.Core.Install;
using UnityGameTranslator.Manager.Core.Model;

namespace UnityGameTranslator.Manager.Core.Api;

/// <summary>What the session is doing, for a screen that shows it.</summary>
public enum EditSessionStage
{
    /// <summary>Uploading the file and asking the site for a page.</summary>
    Opening,

    /// <summary>The page is open somewhere; nothing has been saved yet.</summary>
    Waiting,

    /// <summary>A save came back and was written into the game.</summary>
    Applied,

    /// <summary>The page said it was leaving, or the session ended.</summary>
    Finished,

    /// <summary>Something went wrong; <see cref="EditSessionProgress.Message"/> says what.</summary>
    Failed,
}

/// <param name="AppliedCount">How many times a save has been written into the game so far.</param>
public sealed record EditSessionProgress(EditSessionStage Stage, string Message, int AppliedCount);

/// <summary>
/// Runs a browser edit session for one game from start to finish.
///
/// ⚠ **The part the mod plays while a game runs, played here while it does not.** The site is told
/// nothing about which of the two is on the other end, because nothing there depends on it — one
/// editor, one contract. What differs is only who writes the file at the end.
///
/// ⚠ **The session is followed by polling a few dozen bytes**, never by holding a stream open and
/// never by re-downloading the file to see whether it changed. See the state route on the site for
/// why: a long-lived stream is exactly what fails silently behind the corporate proxies this tool
/// spends its time working around, and the file runs to tens of megabytes.
///
/// ⚠ **The session key never touches the game folder.** It authorises reading and rewriting this
/// translation for as long as the session lives, and game folders are shared between the
/// operating-system accounts of one machine.
/// </summary>
public sealed class EditSessionRunner
{
    /// <summary>
    /// How often the session is asked whether anything happened. Short enough that a save feels
    /// immediate, long enough that a whole afternoon of editing costs a few hundred requests.
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    /// <summary>
    /// How often we say we are still here. The site ends an idle session on a sliding window, and
    /// somebody reading a long file without saving is not idle.
    /// </summary>
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long to keep following after the page says it is leaving.
    ///
    /// ⚠ Not zero, on purpose: a refresh and a navigation both announce a departure, and ending the
    /// session on the first one would close the editor under somebody who pressed F5.
    /// </summary>
    private static readonly TimeSpan DepartureGrace = TimeSpan.FromSeconds(45);

    private readonly EditSessionClient _client;
    private readonly TranslationInstaller _installer;

    public EditSessionRunner(EditSessionClient? client = null, TranslationInstaller? installer = null)
    {
        _client = client ?? new EditSessionClient();
        _installer = installer ?? new TranslationInstaller();
    }

    /// <summary>The session currently being followed, or null.</summary>
    public EditSession? Current { get; private set; }

    /// <summary>
    /// Open a session for the translation of one game and hand back where to send a browser.
    ///
    /// Returns null on failure, with the reason in <see cref="EditSessionClient.LastError"/> —
    /// surfaced through <see cref="LastError"/>.
    /// </summary>
    public async Task<EditSession?> OpenAsync(string gamePath, LoaderDescriptor descriptor,
                                              string? gameName, string? sourceLanguage,
                                              string? targetLanguage, CancellationToken ct = default)
    {
        LastError = null;

        var path = Path.Combine(gamePath,
            descriptor.UserDataDir.Replace('/', Path.DirectorySeparatorChar),
            LocalTranslationProbe.TranslationFileName);

        if (!File.Exists(path))
        {
            LastError = "There is no translation file in this game yet. Take one from the community "
                      + "or play once with the mod to start one.";
            return null;
        }

        string sent;
        try
        {
            sent = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            LastError = $"The translation file here could not be read: {ex.Message}";
            return null;
        }

        // ⚠ ai_available stays false: the per-line Retranslate button in the browser is answered by
        // whatever holds the translation loop, and the game is not running. Promising it would
        // leave somebody waiting on nobody.
        var session = await _client.OpenAsync(sent, gameName, sourceLanguage, targetLanguage,
                                              aiAvailable: false, aiModel: null, ct)
            .ConfigureAwait(false);

        if (session is null)
        {
            LastError = _client.LastError;
            return null;
        }

        Current = session;
        _sentJson = sent;
        _gamePath = gamePath;
        _descriptor = descriptor;
        return session;
    }

    /// <summary>
    /// Follow the session until the page leaves, the caller cancels, or it ends.
    ///
    /// ⚠ Every save is written into the game as it arrives, rather than once at the end. Somebody
    /// who closes the window mid-session keeps what they had already saved — and "saved" is the
    /// word the browser uses, so it has to mean the same thing here.
    /// </summary>
    public async Task FollowAsync(IProgress<EditSessionProgress>? progress, CancellationToken ct)
    {
        var session = Current;
        if (session is null || _gamePath is null || _descriptor is null) return;

        var applied = 0;
        string? lastHash = null;
        var nextKeepAlive = DateTimeOffset.UtcNow + KeepAliveInterval;
        DateTimeOffset? leavingSince = null;

        progress?.Report(new EditSessionProgress(EditSessionStage.Waiting,
            "Waiting for changes from the browser…", applied));

        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(PollInterval, ct).ConfigureAwait(false);

                var state = await _client.PollAsync(session.ModKey, ct).ConfigureAwait(false);

                if (state is null)
                {
                    // A session the site no longer knows is over; anything else is a hiccup worth
                    // retrying on the next tick rather than a reason to abandon the editor.
                    if (_client.LastError?.Contains("expired", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        progress?.Report(new EditSessionProgress(EditSessionStage.Finished,
                            "The edit session has ended.", applied));
                        return;
                    }

                    continue;
                }

                if (lastHash is not null && state.ContentHash is not null && state.ContentHash != lastHash)
                {
                    var written = await ApplyAsync(session.ModKey, ct).ConfigureAwait(false);
                    if (written)
                    {
                        applied++;
                        progress?.Report(new EditSessionProgress(EditSessionStage.Applied,
                            "Changes from the browser were written into the game.", applied));
                    }
                    else
                    {
                        progress?.Report(new EditSessionProgress(EditSessionStage.Failed,
                            LastError ?? "The changes could not be written.", applied));
                    }
                }

                lastHash = state.ContentHash ?? lastHash;

                // A departure is announced by a refresh as well as by a real one, hence the grace.
                if (state.BrowserLeft)
                {
                    leavingSince ??= DateTimeOffset.UtcNow;
                    if (DateTimeOffset.UtcNow - leavingSince > DepartureGrace)
                    {
                        progress?.Report(new EditSessionProgress(EditSessionStage.Finished,
                            "The editor page was closed.", applied));
                        return;
                    }
                }
                else
                {
                    leavingSince = null;
                }

                if (DateTimeOffset.UtcNow >= nextKeepAlive)
                {
                    await _client.KeepAliveAsync(session.ModKey, ct).ConfigureAwait(false);
                    nextKeepAlive = DateTimeOffset.UtcNow + KeepAliveInterval;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Asked to stop: the caller closes the session.
        }
    }

    /// <summary>
    /// Fetch what the session holds and write it into the game.
    ///
    /// ⚠ Fetching is also what tells the site the edits reached this machine, which is why it only
    /// happens when the file is actually going to be written.
    /// </summary>
    private async Task<bool> ApplyAsync(string modKey, CancellationToken ct)
    {
        LastError = null;

        var received = await _client.FetchAsync(modKey, ct).ConfigureAwait(false);
        if (received is null)
        {
            LastError = _client.LastError;
            return false;
        }

        var result = _installer.WriteEditedSession(_gamePath!, _descriptor!, _sentJson ?? "{}", received);
        if (!result.Written)
        {
            LastError = result.Failure;
            return false;
        }

        // What is on disk is now what the session holds: the next comparison must be against this,
        // or every later save would be counted against the file as it was when the session opened.
        _sentJson = received;
        return true;
    }

    /// <summary>
    /// Close the session.
    ///
    /// ⚠ Called on every exit, including a refusal or a cancel. Sessions are a bounded resource on
    /// the site and an abandoned one holds a slot until it expires, multiplied by every user who
    /// closes a window.
    /// </summary>
    public async Task CloseAsync(CancellationToken ct = default)
    {
        var session = Current;
        Current = null;
        if (session is null) return;

        await _client.CloseAsync(session.ModKey, ct).ConfigureAwait(false);
    }

    /// <summary>Why the last step failed, in words a user can act on.</summary>
    public string? LastError { get; private set; }

    // The file as it stood when the session last agreed with disk. Kept so a save can be counted
    // rather than guessed — see TranslationInstaller.WriteEditedSession.
    private string? _sentJson;
    private string? _gamePath;
    private LoaderDescriptor? _descriptor;
}
