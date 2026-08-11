using UnityGameTranslator.Manager.Core.Model;

namespace UnityGameTranslator.Manager.Core.Detection;

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

            game.BrokenLoaderFamilies.Clear();
            game.BrokenLoaderFamilies.AddRange(corlib.Broken);

            if (corlib.IsStripped)
            {
                game.Verdict = ModdabilityVerdict.StrippedRuntime;
                game.VerdictDetail = CorlibProbe.Describe(corlib.Broken);
                return;
            }
        }

        // Architecture matters as much as runtime: BepInEx ships separate x86 and x64 archives,
        // and there are still 32-bit Unity games. Guessing from the host means a 64-bit loader in
        // a 32-bit game, which fails by doing nothing at all.
        if (game.Architecture == GameArchitecture.Unknown)
        {
            game.Verdict = ModdabilityVerdict.ArchitectureUnknown;
            game.VerdictDetail = "Could not read 32-bit or 64-bit";
            return;
        }

        game.Verdict = ModdabilityVerdict.Ok;
        game.VerdictDetail = null;
    }

    /// <summary>
    /// Whether a refusal is one the user may overrule.
    ///
    /// The line is reversibility, not confidence. Everything we install is recorded and can be
    /// removed, so a loader that turns out not to work costs time and nothing else — the user is
    /// entitled to try. An anti-cheat is the exception and always will be: the cost there is a
    /// banned account, it is paid by them, and no uninstall undoes it.
    /// </summary>
    public static bool CanBeOverridden(ModdabilityVerdict verdict) => verdict switch
    {
        ModdabilityVerdict.RuntimeUnknown => true,
        ModdabilityVerdict.ArchitectureUnknown => true,
        ModdabilityVerdict.StrippedRuntime => true,
        ModdabilityVerdict.StoreProtected => true,
        _ => false,
    };

    /// <summary>What the user should weigh before overruling a refusal.</summary>
    public static string OverrideCaveat(ModdabilityVerdict verdict) => verdict switch
    {
        ModdabilityVerdict.RuntimeUnknown =>
            "Pick the wrong one and the game starts without the mod, or not at all. Uninstalling puts it back.",
        ModdabilityVerdict.ArchitectureUnknown =>
            "Pick the wrong one and the loader silently never runs. Uninstalling puts it back.",
        ModdabilityVerdict.StrippedRuntime =>
            "This has been tested on a game built the same way: BepInEx 5, BepInEx 6 and MelonLoader all failed, and so did swapping in unstripped runtime libraries. Trying costs a few minutes and nothing else.",
        ModdabilityVerdict.StoreProtected =>
            "The folder is usually read-only, so the install will most likely be refused by the system rather than by us.",
        _ => "",
    };

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
            "Refused: this game was built with its runtime library stripped. Missing: " +
            $"{game.VerdictDetail}. Every loader runs managed code and uses reflection before " +
            "any mod does, and on a game stripped this hard they all fail — BepInEx 5, BepInEx 6 " +
            "and MelonLoader were each tried on such a game, and so was swapping in unstripped " +
            "runtime libraries. This is how the game was built, not a limitation of the tool or " +
            "of the mod.",
        ModdabilityVerdict.ArchitectureUnknown =>
            "Refused: could not read whether this game is 32-bit or 64-bit. A 64-bit loader in a " +
            "32-bit game does not crash, it simply never runs — which looks exactly like a broken mod.",
        ModdabilityVerdict.NotUnity => "Not a Unity game.",
        _ => "Unknown state.",
    };
}
