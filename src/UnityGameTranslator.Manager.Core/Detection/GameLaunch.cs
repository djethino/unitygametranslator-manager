using System.Diagnostics;
using UnityGameTranslator.Manager.Core.Model;

namespace UnityGameTranslator.Manager.Core.Detection;

/// <summary>How a game is started, and why that route rather than the executable.</summary>
/// <param name="Target">A protocol address or an executable path.</param>
/// <param name="ThroughStore">True when a store is being asked to launch it.</param>
/// <param name="Why">One line for the tooltip, so the choice is never mute.</param>
public sealed record LaunchRoute(string Target, bool ThroughStore, string Why);

/// <summary>
/// Starting a game the tool has found.
///
/// ⚠ **The store's own route wins wherever there is one, and that is a correctness rule rather
/// than a courtesy.** Running the executable directly bypasses everything the store wraps around
/// it — and for this tool, one of those things is load-bearing: the Proton instructions it prints
/// tell people to put `WINEDLLOVERRIDES="winhttp=n,b" %command%` in Steam's LAUNCH OPTIONS. Those
/// options exist only when Steam starts the game. Launch the binary and the loader is never
/// injected, so the mod does nothing and the tool that told them to set it up looks broken.
///
/// On Windows the same route also avoids the other half: many Steam games initialise steam_api at
/// startup and simply exit, or pop "Steam must be running", when started from outside it.
///
/// Everything else runs from its executable, which is the honest general case: a DRM-free GOG
/// title, a game installed by hand, a folder somebody added themselves. There is nothing special
/// to write for any of them — the tool already found the binary and already writes mod files next
/// to it, so starting it is the smaller act of the two.
/// </summary>
public static class GameLaunch
{
    /// <summary>
    /// How this game would be started, or null when we have nothing to start it with.
    ///
    /// Null is a real answer: a folder we identified as a Unity game without pinning down its
    /// executable can be scanned, reported and even modded — the plugin goes beside the data
    /// folder — while still leaving nothing to press Play on.
    /// </summary>
    public static LaunchRoute? RouteFor(GameInstall game)
    {
        // ⚠ Steam first, and by id. steam://rungameid is the only route that applies the launch
        // options — which is where the Proton override lives, and without which the mod never
        // loads. It also works from Linux, where the executable is a Windows binary the desktop
        // has no idea what to do with.
        //
        // ⚠⚠ But ONLY for a game Steam itself told us about. An app id alone is not enough and
        // using it would launch the wrong game: SteamAppId falls back to reading steam_appid.txt
        // out of the folder, and that file travels with any copy of the game — a title moved out
        // of the library, a repack, a folder somebody added by hand. Handing its id to Steam
        // starts whatever Steam has under that id, which is a different copy, or nothing at all
        // with "you do not own this game". The folder in front of us is the one being managed and
        // modded; it is the one that has to start.
        if (game.Store == GameStore.Steam && game.SteamAppId is { Length: > 0 } appId)
        {
            return new LaunchRoute($"steam://rungameid/{appId}", true,
                "Started through Steam, so its launch options apply — the Proton override lives "
                + "there, and starting the binary directly would leave the mod unloaded.");
        }

        // Epic keeps its own id in the manifest we already read. Same reasoning as Steam: the
        // launcher signs the session in, and some titles refuse to start without it.
        if (game.Store == GameStore.Epic && game.StoreAppId is { Length: > 0 } epicId)
        {
            return new LaunchRoute(
                $"com.epicgames.launcher://apps/{epicId}?action=launch&silent=true", true,
                "Started through the Epic launcher, which some titles require.");
        }

        if (game.ExecutablePath is { Length: > 0 } exe && File.Exists(exe))
        {
            return new LaunchRoute(exe, false,
                "Started directly. Nothing here needs a launcher.");
        }

        return null;
    }

    /// <summary>
    /// Starts it, and says what went wrong rather than throwing into an interface.
    ///
    /// ⚠ UseShellExecute for both cases, which is what makes one call serve a protocol address and
    /// an executable alike — and on Linux hands a steam:// address to the desktop rather than
    /// trying to run it. The working directory is set for the executable case: a Unity game
    /// started from elsewhere can fail to find its own data folder.
    /// </summary>
    /// <returns>Null when it started, or a sentence fit to show when it did not.</returns>
    public static string? Start(LaunchRoute route)
    {
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = route.Target,
                UseShellExecute = true,
            };

            if (!route.ThroughStore)
                info.WorkingDirectory = Path.GetDirectoryName(route.Target) ?? "";

            Process.Start(info);
            return null;
        }
        catch (Exception ex)
        {
            // The common one is a store that is not installed, which the message should say
            // plainly rather than reporting a Win32 error nobody can act on.
            return route.ThroughStore
                ? $"The store could not be asked to start it ({ex.Message}). Is it installed?"
                : $"It could not be started ({ex.Message}).";
        }
    }
}
