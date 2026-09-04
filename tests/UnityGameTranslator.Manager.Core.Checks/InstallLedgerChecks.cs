using UnityGameTranslator.Manager.Core.Install;
using UnityGameTranslator.Manager.Core.Model;

namespace UnityGameTranslator.Manager.Core.Checks;

/// <summary>
/// What this tool remembers doing to a game folder, once the folder can no longer say.
///
/// 🔴 **These run on a real temporary folder, not on pure values.** The whole point of this class
/// is a MOMENT — a record that must outlive the thing it describes — and a moment only exists in a
/// sequence. The same reason the mod's own checks replay ModUiStore against real files: a right
/// answer at the wrong time is precisely what pure cases cannot see.
///
/// ⚠ The defect that led here: on 2026-09-04 a game was found holding an inert plugin, and settling
/// what had happened took file creation timestamps and an old report quoted in a conversation. The
/// receipt in the game dies with the uninstall, and nothing else had recorded anything.
/// </summary>
internal static class InstallLedgerChecks
{
    private const string GamePath = @"C:\games\a-game";

    private static Receipt Receipt(string plugin = "0.12.1", bool loaderOurs = true) => new()
    {
        ToolVersion = "0.1.1",
        InstalledAt = new DateTimeOffset(2026, 8, 16, 21, 47, 0, TimeSpan.Zero),
        Game = new ReceiptGame { Path = GamePath, SteamId = "963000", Runtime = "Il2Cpp" },
        Loader = new ReceiptLoader { Id = "bepinex6-il2cpp", Version = "6.0.0.0", InstalledByUs = loaderOurs },
        Plugin = new ReceiptPlugin { Version = plugin, Build = "bepinex6-il2cpp" },
    };

    internal static void WhatTheToolRemembersDoing()
    {
        Program.Section("What this tool remembers doing to a game");

        var folder = Path.Combine(Path.GetTempPath(), "ugt-ledger-" + Guid.NewGuid().ToString("N"));

        try
        {
            var ledger = new InstallLedger(folder);

            // Nothing done, nothing claimed — an empty memory is not an install.
            Program.Check(ledger.For(GamePath) is null,
                "a game never touched is not remembered", "silence, not an empty entry");

            ledger.Remember(Receipt());
            var installed = ledger.For(GamePath);

            Program.Check(installed is not null && installed.RemovedAt is null,
                "an install is remembered", "and reads as still standing");
            Program.Check(installed!.LoaderId == "bepinex6-il2cpp" && installed.PluginVersion == "0.12.1",
                "with what was put there", "the answer to \"what was installed here\"");
            Program.Check(installed.SteamId == "963000" && installed.ToolVersion == "0.1.1",
                "and by which version of this tool", "an install is dated AND attributed");

            // 🔴 The line the whole file exists for: the record survives the uninstall.
            ledger.RememberRemoval(GamePath);
            var removed = ledger.For(GamePath);

            Program.Check(removed is not null,
                "a removal does not erase the memory", "the receipt dies with the folder; this does not");
            Program.Check(removed!.RemovedAt is not null,
                "and it is dated", "\"when did this stop being installed\" now has an answer");
            Program.Check(removed.InstalledAt == new DateTimeOffset(2026, 8, 16, 21, 47, 0, TimeSpan.Zero),
                "while the install date is kept", "both ends of the story or neither");

            // ⚠ An update is not a new installation. Flattening the two would lose how long a game
            // has been set up — which is half of what somebody asks when something looks wrong.
            var update = Receipt(plugin: "0.13.0");
            update.InstalledAt = new DateTimeOffset(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);
            update.UpdatedAt = new DateTimeOffset(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);
            ledger.Remember(update);

            var reinstalled = ledger.For(GamePath);
            Program.Check(reinstalled!.InstalledAt == new DateTimeOffset(2026, 8, 16, 21, 47, 0, TimeSpan.Zero),
                "an update keeps the first install date", "an update is not a new installation");
            Program.Check(reinstalled.PluginVersion == "0.13.0" && reinstalled.RemovedAt is null,
                "and puts the entry back to installed", "a tombstone that is installed again is not one");

            // Two games do not share one memory.
            ledger.Remember(new Receipt
            {
                ToolVersion = "0.1.1",
                InstalledAt = DateTimeOffset.UtcNow,
                Game = new ReceiptGame { Path = @"C:\games\other", Runtime = "Mono" },
                Plugin = new ReceiptPlugin { Version = "0.13.0", Build = "bepinex5" },
            });
            Program.Check(ledger.For(GamePath)!.PluginVersion == "0.13.0"
                          && ledger.For(@"C:\games\other") is not null,
                "each game keeps its own entry", "keyed on the path");

            // ⚠ The same folder written two ways is the same game. Windows paths arrive from a
            // scan, from a receipt and from a person typing — casing and trailing slashes differ.
            Program.Check(ledger.For(@"c:\games\A-GAME\") is not null,
                "case and a trailing slash do not make a second game", "one folder, one entry");

            // 🔴 A memory nobody can write must never fail an install. The folder is correct either
            // way, and the receipt inside it is what every later action reads.
            var unwritable = new InstallLedger(Path.Combine(folder, "a-file-not-a-folder", "deeper"));
            File.WriteAllText(Path.Combine(folder, "a-file-not-a-folder"), "x");

            var threw = false;
            try { unwritable.Remember(Receipt()); }
            catch { threw = true; }

            Program.Check(!threw && unwritable.For(GamePath) is null,
                "a memory that cannot be written is not an error", "an install must not fail over a note");
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch { /* a temp folder, not a result */ }
        }
    }
}
