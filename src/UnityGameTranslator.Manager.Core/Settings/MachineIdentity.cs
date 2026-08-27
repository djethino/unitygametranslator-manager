using System.Security.Cryptography;
using UnityGameTranslator.Manager.Core.Platform;

namespace UnityGameTranslator.Manager.Core.Settings;

/// <summary>
/// A number this machine drew once, so the site can put its accesses in one group.
///
/// ### The problem it answers
///
/// 🔴 Measured in production on 2026-08-27: **thirty-six accesses on one account, thirty-five of
/// them in a single "device not named" heap.** The only thing that grouped anything was the name
/// somebody types when linking — and nobody types it. So the Linked devices page listed everything
/// and helped with nothing, while offering to rename machines it gave no way to tell apart.
///
/// ### 🔴 Drawn, never measured — and that distinction is the whole design
///
/// The obvious source was ready to hand: <c>Secrets.MachineSecret()</c> already derives a stable
/// value from machine name, user name and OS. It is exactly the wrong one. Those have tiny entropy
/// and are frequently a real first name, so a digest of them does not hide anything — anyone
/// holding it can CONFIRM a guess in two tries. This project already knows the trap: the site's
/// visitor fingerprint destroys its salt every night for that precise reason.
///
/// So nothing about the machine is measured. This is 32 random hex characters, written once. It
/// says "the same machine as before" and cannot be made to say anything else, because there is
/// nothing else in it.
///
/// ### Who writes it, and why not the mod
///
/// The Manager, because "several games on one machine" is its remit — the mod's is one game, and it
/// writes nothing outside that game's folder, a property worth keeping. The mod READS this through
/// <c>ManagerLink</c>, on a path it already looks at.
///
/// ⚠ **No Manager, no value, and that is not a hole.** The grouping simply stays manual: each
/// program now shows the code naming its own line, so a machine can be recognised and named once by
/// hand. The mechanism degrades into the one it replaces.
///
/// ⚠ It lives beside the settings rather than in a shared parent folder, so removing the Manager's
/// settings removes it too — "removing the tool removes what belongs to the tool". The cost of that
/// choice is one rename after a reinstall, and it is the honest side of the trade.
/// </summary>
public static class MachineIdentity
{
    public const string FileName = "device.id";

    /// <summary>
    /// The identifier, drawing and saving one the first time.
    ///
    /// ⚠ Returns null rather than throwing when it cannot be written. A read-only profile or a
    /// locked folder must cost the grouping, never the tool: everything else here works without it.
    /// </summary>
    public static string? ReadOrCreate(IPlatform platform)
    {
        var path = Path.Combine(platform.UserDataDirectory, FileName);

        try
        {
            if (File.Exists(path))
            {
                var existing = File.ReadAllText(path).Trim();

                // Shape-checked on the way in as well as out: a file somebody edited, or half
                // written by a machine that lost power, must not become a group of its own for ever.
                if (IsWellFormed(existing)) return existing;
            }

            var drawn = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

            Directory.CreateDirectory(platform.UserDataDirectory);
            File.WriteAllText(path, drawn);

            return drawn;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>What the site will accept, checked here so a bad value never leaves this machine.</summary>
    public static bool IsWellFormed(string? value) =>
        value is { Length: 32 } && value.All(Uri.IsHexDigit);
}
