using UnityGameTranslator.Common;
using UnityGameTranslator.Manager.Core.Detection;
using UnityGameTranslator.Manager.Core.Install;
using UnityGameTranslator.Manager.Core.Model;
using UnityGameTranslator.Manager.Core.Platform;

namespace UnityGameTranslator.Manager.Core.Api;

// ⚠ EditSessionStage lived here and moved to the socle: the mod names the same five moments, and
// two lists of states for one session is how they come to describe it differently.

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
/// ⚠ **The session key IS written into the game folder now**, encrypted, and that reverses what was
/// written here before. It said the key must never go there because game folders are shared between
/// the operating-system accounts of one machine — a real risk, and the mod had already answered it:
/// <see cref="Secrets"/> derives from the machine AND the user, so another account reads bytes it
/// cannot decrypt. What the old rule cost was the only thing that can stop two browser editors from
/// erasing each other on one file. See <see cref="EditSessionMarkers"/>.
/// </summary>
public sealed class EditSessionRunner
{
    /// <summary>
    /// How often the session is asked whether anything happened. Short enough that a save feels
    /// immediate, long enough that a whole afternoon of editing costs a few hundred requests.
    /// </summary>
    private static readonly TimeSpan PollInterval =
        TimeSpan.FromSeconds(UnityGameTranslator.Common.EditSessions.PollSeconds);

    /// <summary>
    /// How often we say we are still here. The site ends an idle session on a sliding window, and
    /// somebody reading a long file without saving is not idle.
    /// </summary>
    // From the socle, which derives it from the server's own TTL. It was five minutes here and
    // ten in the mod — two answers to one question, neither aware of the other nor of the fifteen
    // minutes the site actually grants a fresh session.
    private static readonly TimeSpan KeepAliveInterval =
        TimeSpan.FromSeconds(UnityGameTranslator.Common.EditSessions.KeepAliveSeconds);

    /// <summary>
    /// How long to keep following after the page says it is leaving.
    ///
    /// ⚠ Not zero, on purpose: a refresh and a navigation both announce a departure, and ending the
    /// session on the first one would close the editor under somebody who pressed F5.
    /// </summary>
    // Ninety seconds, not forty-five: the mod's figure won, because ending too early leaves
    // somebody's next save with nowhere to land, while waiting too long only holds a slot.
    private static readonly TimeSpan DepartureGrace =
        TimeSpan.FromSeconds(UnityGameTranslator.Common.EditSessions.BrowserGraceSeconds);

    private readonly EditSessionClient _client;
    private readonly TranslationInstaller _installer;

    public EditSessionRunner(IPlatform platform, EditSessionClient? client = null)
    {
        _client = client ?? new EditSessionClient();
        _installer = new TranslationInstaller(platform);
    }

    /// <summary>The session currently being followed, or null.</summary>
    public EditSession? Current { get; private set; }

    /// <summary>
    /// Open a session for the translation of one game and hand back where to send a browser.
    ///
    /// Returns null on failure, with the reason in <see cref="EditSessionClient.LastError"/> —
    /// surfaced through <see cref="LastError"/>.
    /// </summary>
    public async Task<EditSession?> OpenAsync(GameInstall game, LoaderDescriptor descriptor,
                                              string? sourceLanguage, string? targetLanguage,
                                              CancellationToken ct = default)
    {
        LastError = null;

        // ⚠ Asked HERE as well as at the write, and the duplication is the point: a session opened
        // over a running game would be edited for twenty minutes before the first save is refused.
        // The door stays on the write — that is what protects the file — and this one exists so
        // nobody spends that twenty minutes.
        if (_installer.WhyNotNow(game) is { } refusal)
        {
            LastError = refusal + " While it is open, the mod's own live editor is the one that "
                      + "can change this translation.";
            return null;
        }

        var gamePath = game.Path;
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
        var session = await _client.OpenAsync(sent, game.Name, sourceLanguage, targetLanguage,
                                              aiAvailable: false, aiModel: null, ct)
            .ConfigureAwait(false);

        if (session is null)
        {
            LastError = _client.LastError;
            return null;
        }

        Current = session;
        _sentJson = sent;
        _game = game;
        _descriptor = descriptor;

        // Beside the translation, so the mod finds it. ⚠ After the session exists, never before: a
        // marker pointing at a session that failed to open would refuse the next attempt on behalf
        // of nothing at all.
        if (EditSessionMarkers.Write(gamePath, descriptor, session.ModKey) is { } failure)
        {
            // Said, not swallowed: the session works, but the game will not know it is there, and
            // that is the one guarantee this file exists to give.
            LastError = "The session is open, but this game's folder could not be marked as being "
                      + $"edited ({failure}). The mod will not know about it, so do not open a "
                      + "second editor from inside the game — the last one to save would erase the "
                      + "other.";
        }

        return session;
    }

    /// <summary>
    /// Somebody else's window is already editing this translation — or this one is, from a run that
    /// did not end cleanly.
    /// </summary>
    /// <param name="Question">Worded by the socle, identical to what the mod would ask.</param>
    /// <param name="ModKey">Null when the session belongs to another account of this computer.</param>
    /// <param name="Ours">
    /// This tool's own session, left behind. Not a conflict — an offer to pick it back up.
    /// </param>
    public sealed record Blocking(string Question, string? ModKey, bool Ours);

    /// <summary>
    /// Look for a session already open on this game's translation, before anything is uploaded.
    ///
    /// ⚠ Asked BEFORE the upload, because asking after would mean a second session already exists
    /// on the site — the very state this prevents.
    ///
    /// Returns null when the way is clear, which includes a marker the site has forgotten: that one
    /// is removed here rather than left to refuse every future session for ever.
    /// </summary>
    public async Task<Blocking?> FindBlockingAsync(GameInstall game, LoaderDescriptor descriptor,
                                                   CancellationToken ct = default)
    {
        var marker = EditSessionMarkers.Read(game.Path, descriptor);
        if (marker is null) return null;

        var when = marker.OpenedUtc is { } opened
            ? "on " + opened.ToLocalTime().ToString("d MMM, HH:mm")
            : "at an unknown time";

        if (!marker.Endable)
        {
            return new Blocking(
                "A browser editing session for this game was opened from "
                + EditSessions.HolderName(marker.Holder) + " " + when + " by another user of this "
                + "computer, or before this game was moved here. It cannot be ended from your "
                + "account, and two sessions on one translation erase each other's saves. "
                + "Open yours anyway?",
                null, Ours: false);
        }

        var state = await _client.PollAsync(marker.ModKey!, ct).ConfigureAwait(false);

        // ⚠ Gone is the ONLY reading that clears a marker. A network hiccup answers null too, and
        // treating that as "nobody is editing" would open a second session over a live one — the
        // socle spells out why silence counts as alive.
        if (state is null && _client.SessionGone)
        {
            EditSessionMarkers.Clear(game.Path, descriptor);
            return null;
        }

        if (marker.IsOurs)
        {
            return new Blocking(
                "A browser editing session opened from here " + when + " is still running. Your "
                + "browser tab is probably still on it. Pick it back up, so that what you save "
                + "there reaches this game again?",
                marker.ModKey, Ours: true);
        }

        return new Blocking(
            EditSessions.ConflictQuestion(marker.Holder, when, state?.PendingChanges ?? 0),
            marker.ModKey, Ours: false);
    }

    /// <summary>
    /// End a session somebody else's window opened, keeping what its browser had saved.
    ///
    /// ⚠ **Drained first**, exactly as <see cref="CloseAsync"/> drains our own. Saves the browser
    /// made and nobody fetched exist in the session and nowhere else; deleting it first would
    /// destroy work the site told somebody was saved.
    /// </summary>
    public async Task TakeOverAsync(GameInstall game, LoaderDescriptor descriptor, string modKey,
                                    CancellationToken ct = default)
    {
        var path = Path.Combine(game.Path,
            descriptor.UserDataDir.Replace('/', Path.DirectorySeparatorChar),
            LocalTranslationProbe.TranslationFileName);

        var received = await _client.FetchAsync(modKey, ct).ConfigureAwait(false);

        if (received is not null)
        {
            var onDisk = File.Exists(path) ? File.ReadAllText(path) : "{}";
            _installer.WriteEditedSession(game, descriptor, onDisk, received);
        }

        await _client.CloseAsync(modKey, ct).ConfigureAwait(false);
        EditSessionMarkers.Clear(game.Path, descriptor);
    }

    /// <summary>
    /// Pick up a session this tool left open — a crash, a kill, a window closed the hard way.
    ///
    /// ⚠ **The browser is not reopened**, and that is not a shortcut: the URL carried a one-time
    /// token that died when the page first loaded. The tab that is still open stays attached on its
    /// own, and what it saves reaches the game again the moment we start following. Somebody who
    /// closed it can reach the session from the site's own editor page.
    /// </summary>
    public bool Resume(GameInstall game, LoaderDescriptor descriptor, string modKey)
    {
        var path = Path.Combine(game.Path,
            descriptor.UserDataDir.Replace('/', Path.DirectorySeparatorChar),
            LocalTranslationProbe.TranslationFileName);

        if (!File.Exists(path)) return false;

        Current = new EditSession(modKey, BuildInfo.WebsiteBaseUrl + "/edit-session", null);
        // ⚠ The baseline is the file as it stands NOW, not as it stood when the session opened —
        // that one died with the process. It is only used to count what a save changed, so the
        // worst case is a count taken from a later starting point; guessing an older state would
        // instead make lines captured since look like browser deletions.
        _sentJson = File.ReadAllText(path);
        _game = game;
        _descriptor = descriptor;
        return true;
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
        if (session is null || _game is null || _descriptor is null) return;

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
                    if (_client.SessionGone)
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

        var result = _installer.WriteEditedSession(_game!, _descriptor!, _sentJson ?? "{}", received);
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

        // 🔴 **Drained before it is deleted.** Ending a session used to throw away whatever had
        // been saved in the browser since the last tick: somebody clicked Save on the site and
        // closed the editor a second later, and their work was gone with nothing said.
        //
        // ⚠ The inversion this removes is the telling part: a session that survived a CRASH was
        // picked up again at the next start and applied, while one closed properly was deleted
        // with its last save inside. Quitting cleanly was worse than being killed.
        //
        // Failure here is not allowed to prevent the close — a session left open on the site is a
        // slot held until it expires, for everybody.
        try { await ApplyAsync(session.ModKey, ct).ConfigureAwait(false); }
        catch { /* reported through LastError by ApplyAsync; the close must still happen */ }

        await _client.CloseAsync(session.ModKey, ct).ConfigureAwait(false);

        // The marker goes last, and only here: while it exists, the game refuses to open its own
        // editor. Removing it before the session is really gone would let a second one in.
        if (_game is not null && _descriptor is not null)
            EditSessionMarkers.Clear(_game.Path, _descriptor);
    }

    /// <summary>Why the last step failed, in words a user can act on.</summary>
    public string? LastError { get; private set; }

    // The file as it stood when the session last agreed with disk. Kept so a save can be counted
    // rather than guessed — see TranslationInstaller.WriteEditedSession.
    private string? _sentJson;
    private GameInstall? _game;
    private LoaderDescriptor? _descriptor;
}
