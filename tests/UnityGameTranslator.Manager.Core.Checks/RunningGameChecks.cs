using UnityGameTranslator.Manager.Core.Detection;

namespace UnityGameTranslator.Manager.Core.Checks;

/// <summary>
/// How a Linux process is recognised as a game: by the files it has mapped, since a Proton game's
/// executable is Wine's loader, and by the name the kernel keeps, cut at 15 bytes.
/// </summary>
internal static class RunningGameChecks
{
    private const string Root = "/home/player/.local/share/Steam/steamapps/common/Tap Ninja/";

    internal static void HowALinuxGameIsSeenRunning()
    {
        Program.Section("How a Linux game is seen running");

        // Lines as /proc/PID/maps writes them for a Proton game (read on a test machine, 2026-10-03).
        var proton = new[]
        {
            "7f2a1c000000-7f2a1c021000 rw-p 00000000 00:00 0 ",
            "7f2a20000000-7f2a20001000 r--p 00000000 00:2a 1183 /home/player/.local/share/Steam/steamapps/common/Proton 11.0/files/lib/wine/x86_64-unix/wine64-preloader",
            "7f2a24000000-7f2a24010000 r--p 00000000 00:2a 5521 /home/player/.local/share/Steam/steamapps/common/Tap Ninja/baselib.dll",
            "7ffd5e1f0000-7ffd5e211000 rw-p 00000000 00:00 0 [stack]",
        };
        Program.Check(LinuxGameProcess.MapsHold(proton, Root), "a Proton game is found by the files it maps",
            "its executable is Wine's loader, outside the game's folder");

        Program.Check(!LinuxGameProcess.MapsHold(proton.Take(2).Concat(proton.Skip(3)), Root),
            "Wine's loader alone is not the game", "the same loader runs every Proton game");

        var neighbour = new[] { "7f2a24000000-7f2a24010000 r--p 00000000 00:2a 77 /home/player/.local/share/Steam/steamapps/common/Tap Ninja 2/baselib.dll" };
        Program.Check(!LinuxGameProcess.MapsHold(neighbour, Root), "a folder that only starts with the same name is another game",
            "the root is compared with its final '/'");

        Program.Check(LinuxGameProcess.CommOf("Tap Ninja.exe") == "Tap Ninja.exe", "a short name is kept whole",
            "the kernel keeps up to 15 bytes");
        Program.Check(LinuxGameProcess.CommOf("The_Haunted_Island.exe") == "The_Haunted_Isl", "a long name is cut at 15 bytes",
            "that is the only name the process list ever shows for it");
        Program.Check(LinuxGameProcess.CommOf("龙胤立志传传传.exe") == "龙胤立志传", "a cut never splits a character",
            "five characters of three bytes fill 15; a sixth would not fit");
    }
}
