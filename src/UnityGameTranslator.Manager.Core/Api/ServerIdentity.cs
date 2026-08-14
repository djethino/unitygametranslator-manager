using UnityGameTranslator.Common;
using UnityGameTranslator.Manager.Core.Model;

namespace UnityGameTranslator.Manager.Core.Api;

/// <summary>Why this account may — or may not — act on the server for a given game.</summary>
public enum ServerStandingKind
{
    /// <summary>Nobody is signed in here. Nothing can be published under no name.</summary>
    SignedOut,

    /// <summary>The game is linked to no account. Whoever is signed in may take it up.</summary>
    Unlinked,

    /// <summary>The game is linked to the account signed in here. Ordinary case.</summary>
    Mine,

    /// <summary>
    /// The game is linked to a DIFFERENT account on this same site. Not an error and not a
    /// suspicion — one machine legitimately holds games belonging to several people.
    /// </summary>
    OtherAccount,

    /// <summary>The game is linked to a different server entirely (self-hosted instance).</summary>
    OtherServer,
}

/// <summary>
/// Whether the account signed into THIS tool is entitled to act on the server for THAT game.
///
/// ⚠ **An account is a property of the game, not of the machine.** The link lives in the game's own
/// config.json, written by the mod when somebody signed in from inside that game — so one computer
/// routinely carries games belonging to different people, and the tool has no business assuming its
/// own account speaks for all of them.
///
/// ⚠ **And the game folder is shared.** Several operating-system accounts install games in the same
/// place; the translation, the config and the plugin sitting there belong to whoever set them up,
/// not to whoever happens to be logged in now. Acting under the wrong name would not merely be
/// wrong: on a lineage somebody else leads it would file the work as a CONTRIBUTION to their
/// translation, under a name they never chose, with no way for them to see it coming.
///
/// ⚠ **The game's own token is deliberately not read.** It is in that config, encrypted, and
/// <see cref="UnityGameTranslator.Common.Secrets"/> is shared, so this tool could technically
/// decrypt it on the machine that wrote it. Not doing so is a decision: a credential belongs to the
/// program its owner handed it to. Borrowing it would also fail across operating-system accounts —
/// the key is bound to the user name as well as the machine — which means the only situation where
/// it would "work" is the one where it is least defensible.
///
/// The answer here is therefore never "act as somebody else". It is "act as me, or say plainly whose
/// name is missing".
/// </summary>
public sealed record ServerStanding(ServerStandingKind Kind, string? GameAccount, string? SignedInAs)
{
    /// <summary>True when a server action may be attempted at all.</summary>
    public bool CanAct => Kind is ServerStandingKind.Unlinked or ServerStandingKind.Mine;

    /// <summary>
    /// This standing said in the shared vocabulary, so the RULE about it lives in one place.
    ///
    /// ⚠ The reading is the manager's — only it looks at another program's config — but what
    /// follows from the reading is <see cref="Standings"/>'s, because the mod answers the same
    /// question about itself and the two must not diverge.
    /// </summary>
    public AccountStanding Standing => Kind switch
    {
        ServerStandingKind.Mine => AccountStanding.Ours,
        ServerStandingKind.Unlinked => AccountStanding.Ours,
        ServerStandingKind.OtherAccount => AccountStanding.SomebodyElses,
        ServerStandingKind.OtherServer => AccountStanding.SomebodyElses,

        // Nobody signed in HERE. Whose game it is decides: a game linked to somebody is theirs
        // whether or not this window has a name; a game linked to nobody is anybody's to set up.
        _ => GameAccount is null ? AccountStanding.Anonymous : AccountStanding.SomebodyElses,
    };

    /// <summary>
    /// 🔴 May this window change the translation FILE in that game folder?
    ///
    /// Editing in a browser and merging write only the local file — no server is involved — which is
    /// exactly why they were never guarded. They still must be: one must not break, by inattention,
    /// the setup another user of this computer put in place. Somebody who launches the game and
    /// switches account there has made a deliberate choice, and that is between the users of that
    /// machine; a stray click from a tool that lists everybody's games is not.
    /// </summary>
    public bool CanWriteLocally => Standings.MayWriteLocally(Standing);

    /// <summary>
    /// What to tell the user, in terms they can act on. Null when there is nothing to say — the
    /// ordinary case, where saying anything would be noise.
    /// </summary>
    public string? Reason => Kind switch
    {
        ServerStandingKind.SignedOut =>
            "Sign in to the community site from this window first: publishing, contributing and "
            + "being credited all happen under a name.",

        ServerStandingKind.OtherAccount =>
            $"This game is linked to the account \"{GameAccount}\", and this window is signed in as "
            + $"\"{SignedInAs}\". Nothing will be sent under the wrong name — sign in as "
            + $"\"{GameAccount}\" to act on this game's translation, or publish it from inside the "
            + "game, which uses that account by itself.",

        ServerStandingKind.OtherServer =>
            $"This game is linked to a different site ({GameAccount}). Its translation belongs "
            + "there, and this window talks to another server.",

        _ => null,
    };
}

/// <summary>
/// Reads a game's link and this tool's own, and says whether they are the same person.
///
/// Deliberately NOT shared with the mod: the mod runs inside one game and holds that game's own
/// credential by construction, so the question never arises there. Putting it in the shared library
/// would be sharing a rule with nobody — and the library is for what the two must not disagree
/// about, not for everything either of them happens to know.
/// </summary>
public static class ServerIdentity
{
    /// <summary>
    /// Where the signed-in account stands with respect to one game.
    /// </summary>
    /// <param name="settings">This tool's settings, holding its own account, if any.</param>
    /// <param name="gameAccount">
    /// What the game's config says: the account name it was linked with, and the server that issued
    /// the token. Both null when the game was never linked, which is the common case.
    /// </param>
    /// <param name="apiBaseUrl">The site this build talks to.</param>
    public static ServerStanding For(InstallerSettings? settings,
                                     (string? User, string? Server) gameAccount,
                                     string? apiBaseUrl)
    {
        var signedInAs = Trimmed(settings?.ApiUser);
        var hasToken = !string.IsNullOrWhiteSpace(settings?.ApiToken);

        // ⚠ A name without a token is not being signed in. The token can be dropped on load — when
        // the tool is pointed at another server — while the name stays in the file, and treating
        // that as an account would produce a refusal-free path to a request nobody can authorise.
        var gameUser = Trimmed(gameAccount.User);
        var gameServer = Trimmed(gameAccount.Server);

        // ⚠ The game's account travels even when nobody is signed in HERE. Without it, a window
        // signed out could not tell a fresh machine — where everything is allowed — from somebody
        // else's game, where the local translation must not be touched either. That was the case
        // the rule missed: "not signed in here" is not the same as "nobody's game".
        if (!hasToken || signedInAs is null)
            return new ServerStanding(ServerStandingKind.SignedOut, gameUser, null);

        // Never linked: there is no other owner to step on.
        if (gameUser is null) return new ServerStanding(ServerStandingKind.Unlinked, null, signedInAs);

        // ⚠ Checked BEFORE the names. Two servers can carry the same user name and mean two
        // different people, so comparing names across instances would be the one mistake here that
        // reads as a match.
        if (gameServer is not null && !string.IsNullOrWhiteSpace(apiBaseUrl)
            && !SameServer(gameServer, apiBaseUrl))
        {
            return new ServerStanding(ServerStandingKind.OtherServer, gameServer, signedInAs);
        }

        return string.Equals(gameUser, signedInAs, StringComparison.OrdinalIgnoreCase)
            ? new ServerStanding(ServerStandingKind.Mine, gameUser, signedInAs)
            : new ServerStanding(ServerStandingKind.OtherAccount, gameUser, signedInAs);
    }

    /// <summary>
    /// Two addresses for the same site. Compared on host and scheme rather than as text: a trailing
    /// slash or a "/api/v1" suffix is a spelling, not a different server, and refusing on one would
    /// send people hunting for an account problem they do not have.
    /// </summary>
    private static bool SameServer(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;

        return Uri.TryCreate(a, UriKind.Absolute, out var left)
               && Uri.TryCreate(b, UriKind.Absolute, out var right)
               && string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase)
               && left.Port == right.Port;
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
