using UnityGameTranslator.Manager.Core.Api;
using UnityGameTranslator.Manager.Core.Model;

namespace UnityGameTranslator.Manager.Core.Checks;

/// <summary>
/// Whether the account signed into this tool may act on the server for a given game.
///
/// 🔴 **Checked because this rule was contradicted on screen for weeks.** The list wrote
/// "@somebody (you)" in green, with a tooltip promising the game could publish, while every tab
/// refused every act — because the list compared the game's account BY NAME with this tool's,
/// and ServerIdentity compares the SERVER first. Two sites can carry the same user name and mean
/// two different people.
///
/// The type now forbids the comparison the list was making (the account travels as a pair, so
/// there is no bare name to compare). These cases pin the rule the type cannot express.
/// </summary>
internal static class StandingChecks
{
    private const string Site = "https://unitygametranslator.example/api/v1";

    private static InstallerSettings SignedIn(string user) => new()
    {
        ApiUser = user,
        ApiToken = "ugt_a-token",
    };

    internal static void WhereThisAccountStands()
    {
        Program.Section("Where this account stands on a game");

        // Nothing can be published under no name — and the game's own account still travels, so a
        // signed-out window can tell a fresh machine from somebody else's game.
        var out1 = ServerIdentity.For(null, ("someone", Site), Site);
        Program.Check(out1.Kind == ServerStandingKind.SignedOut && out1.GameAccount == "someone",
            "signed out still reports whose game it is", "not signed in here is not nobody's game");

        // ⚠ A name without a token is not being signed in. The token is dropped when the tool is
        // pointed elsewhere while the name stays in the file; reading that as an account gives a
        // refusal-free path to a request nobody can authorise.
        var nameOnly = ServerIdentity.For(new InstallerSettings { ApiUser = "me" }, (null, null), Site);
        Program.Check(nameOnly.Kind == ServerStandingKind.SignedOut,
            "a name without a token is not signed in", "the token is what authorises");

        var unlinked = ServerIdentity.For(SignedIn("me"), (null, null), Site);
        Program.Check(unlinked.Kind == ServerStandingKind.Unlinked && unlinked.CanAct,
            "an unlinked game may be taken up", "there is no other owner to step on");

        var mine = ServerIdentity.For(SignedIn("me"), ("me", Site), Site);
        Program.Check(mine.Kind == ServerStandingKind.Mine && mine.CanAct,
            "the same account on the same site is mine", "the ordinary case");

        var theirs = ServerIdentity.For(SignedIn("me"), ("someone", Site), Site);
        Program.Check(theirs.Kind == ServerStandingKind.OtherAccount && !theirs.CanAct,
            "another account on the same site refuses", "one machine holds several people's games");

        // 🔴 The case the list got wrong: same NAME, different SERVER. Checked before the names,
        // and it must stay that way — this is the one mistake here that reads as a match.
        var elsewhere = ServerIdentity.For(SignedIn("me"), ("me", "https://someone-elses.example"), Site);
        Program.Check(elsewhere.Kind == ServerStandingKind.OtherServer && !elsewhere.CanAct,
            "the same name on another site is NOT me", "the server is compared first");

        // 🔴 **The LOCAL write, pinned apart from CanAct — because that is the one call sites keep
        // forgetting.** CanAct guards the server; CanWriteLocally guards the translation FILE in a
        // game folder, and editing in a browser, merging, restoring and taking a translation all
        // write only that file, which is exactly why they were once left unguarded.
        //
        // Twice now a control has reached TakeSelectedTranslationAsync with "the game is not
        // running" as its only condition, on a card where every neighbouring button was greyed.
        // The rule is CLAUDE.md's, extended to local writes on 2026-08-14: one must not break, by
        // inattention, the setup another user of this computer put in place.
        Program.Check(!theirs.CanWriteLocally && !elsewhere.CanWriteLocally,
            "another account's game is read-only on disk too",
            "editing and merging write that game's file, and it is not ours");

        Program.Check(mine.CanWriteLocally && unlinked.CanWriteLocally,
            "our own and unclaimed games stay writable",
            "the refusal is about somebody else, never about caution");

        // ⚠ Signed out with nobody's game is NOT a refusal: a machine where nothing is linked and
        // nobody is signed in belongs to whoever is sitting at it. Only a game bearing a name is
        // somebody's.
        var anonymous = ServerIdentity.For(null, (null, null), Site);
        Program.Check(anonymous.CanWriteLocally && !out1.CanWriteLocally,
            "signed out is decided by whose game it is",
            "nobody's game is anybody's; a named one is theirs");

        // ⚠ A trailing slash or an "/api/v1" suffix is a spelling, not a different server. Refusing
        // on one would send people hunting for an account problem they do not have.
        foreach (var spelling in new[]
        {
            "https://unitygametranslator.example",
            "https://unitygametranslator.example/",
            "https://unitygametranslator.example/api/v1",
        })
        {
            var same = ServerIdentity.For(SignedIn("me"), ("me", spelling), Site);
            Program.Check(same.Kind == ServerStandingKind.Mine,
                $"\"{spelling}\" is the same server", "compared on host and port, not as text");
        }

        // Names are people, and people type their own name in whatever case they please.
        var cased = ServerIdentity.For(SignedIn("Me"), ("me", Site), Site);
        Program.Check(cased.Kind == ServerStandingKind.Mine,
            "the account name is compared without case", "a name is not a password");
    }
}
