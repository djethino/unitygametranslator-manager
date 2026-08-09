using UnityGameTranslator.Installer.Core.Model;

namespace UnityGameTranslator.Installer.Core.Detection;

/// <summary>
/// Decides whether we are willing to touch a game at all.
///
/// The anti-cheat check is not a nicety. Injecting a mod loader into a protected game can get
/// the player's account banned — a cost they pay and we do not. Default is refusal, and the
/// refusal always states its reason so the user knows it is not a bug in our tool.
/// </summary>
public static class ModdabilityProbe
{
    private static readonly string[] AntiCheatDirectories =
    {
        "EasyAntiCheat",
        "EasyAntiCheat_EOS",
        "BattlEye",
        "BEService",
    };

    private static readonly string[] AntiCheatFilePatterns =
    {
        "EasyAntiCheat*.exe",
        "EasyAntiCheat*.dll",
        "BEService*.exe",
        "BEClient*.dll",
        "start_protected_game.exe",
    };

    public static void Evaluate(GameInstall game)
    {
        if (!game.IsUnity)
        {
            game.Verdict = ModdabilityVerdict.NotUnity;
            return;
        }

        var antiCheat = FindAntiCheat(game.Path);
        if (antiCheat is not null)
        {
            game.Verdict = ModdabilityVerdict.AntiCheat;
            game.VerdictDetail = antiCheat;
            return;
        }

        if (IsMicrosoftStore(game.Path))
        {
            game.Verdict = ModdabilityVerdict.StoreProtected;
            game.VerdictDetail = "Microsoft Store / Game Pass";
            return;
        }

        if (game.Runtime == UnityRuntime.Unknown)
        {
            game.Verdict = ModdabilityVerdict.RuntimeUnknown;
            game.VerdictDetail = "Could not tell Mono from IL2CPP";
            return;
        }

        // Only Mono games have a managed corlib to strip; IL2CPP compiles it away entirely.
        if (game.Runtime == UnityRuntime.Mono)
        {
            var corlib = CorlibProbe.Check(game.DataDirectory);
            if (corlib.IsStripped)
            {
                game.Verdict = ModdabilityVerdict.StrippedRuntime;
                game.VerdictDetail = corlib.MissingMember;
                return;
            }
        }

        game.Verdict = ModdabilityVerdict.Ok;
        game.VerdictDetail = null;
    }

    /// <summary>Returns the name of the anti-cheat found, or null.</summary>
    public static string? FindAntiCheat(string gamePath)
    {
        try
        {
            foreach (var dir in AntiCheatDirectories)
            {
                if (Directory.Exists(Path.Combine(gamePath, dir)))
                    return Describe(dir);
            }

            foreach (var pattern in AntiCheatFilePatterns)
            {
                var match = Directory.EnumerateFiles(gamePath, pattern, SearchOption.TopDirectoryOnly)
                                     .FirstOrDefault();
                if (match is not null) return Describe(Path.GetFileName(match));
            }
        }
        catch
        {
            // An unreadable folder is not evidence of safety, but it is not evidence of an
            // anti-cheat either. The caller will fail later on the write attempt, with a clearer
            // message than anything we could invent here.
        }
        return null;
    }

    private static string Describe(string marker)
    {
        var lower = marker.ToLowerInvariant();
        if (lower.Contains("easyanticheat")) return "EasyAntiCheat";
        if (lower.Contains("be")) return "BattlEye";
        if (lower.Contains("protected_game")) return "Anti-cheat launcher";
        return marker;
    }

    /// <summary>
    /// Microsoft Store / Game Pass games live under WindowsApps with a locked-down ACL and
    /// encrypted binaries. Nothing we can do, and worth saying rather than failing obscurely.
    /// </summary>
    public static bool IsMicrosoftStore(string gamePath) =>
        gamePath.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase);

    public static string Explain(GameInstall game) => game.Verdict switch
    {
        ModdabilityVerdict.Ok => "Ready to install.",
        ModdabilityVerdict.AntiCheat =>
            $"Refused: this game ships {game.VerdictDetail}. Modding a protected game can get " +
            "your account banned. This is not a limitation of the tool.",
        ModdabilityVerdict.StoreProtected =>
            "Refused: Microsoft Store / Game Pass builds cannot be modified (locked folder, " +
            "encrypted binaries).",
        ModdabilityVerdict.RuntimeUnknown =>
            "Refused: could not tell whether this game uses Mono or IL2CPP. Installing the " +
            "wrong loader would stop the game from starting.",
        ModdabilityVerdict.StrippedRuntime =>
            $"Refused: this game was built with its runtime library stripped, and " +
            $"{game.VerdictDetail} is missing from it — " +
            $"{CorlibProbe.NeededBy(game.VerdictDetail ?? "")} calls it before any mod runs. " +
            "No mod loader can start here. This is how the game was built, not a limitation of " +
            "the tool or of the mod.",
        ModdabilityVerdict.NotUnity => "Not a Unity game.",
        _ => "Unknown state.",
    };
}
