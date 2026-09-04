using System.Text.Json;
using UnityGameTranslator.Manager.Core.Model;

namespace UnityGameTranslator.Manager.Core.Install;

/// <summary>
/// Reads and writes the install receipt.
///
/// It lives in the game folder, which means it travels with the game if it is moved and is found
/// again without any registry or index. It is also the AUTHORITY: it is the one record that matches
/// what is actually on disk, so every action — above all uninstall — reads this and nothing else.
///
/// ⚠ **This class writes in the game folder and nowhere else.** Its summary used to claim a copy
/// was kept in the tool's own data directory; there was none, and on 2026-09-04 that sentence sent
/// somebody looking for a file that did not exist while trying to work out what had happened to a
/// game. <see cref="InstallLedger"/> now keeps that summary — for MEMORY, deliberately not as a
/// second source of truth — and the engines write to it beside their calls here.
/// </summary>
public sealed class ReceiptStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static string PathFor(string gameRoot) =>
        Path.Combine(gameRoot, Receipt.FileName);

    public static Receipt? Read(string gameRoot)
    {
        var path = PathFor(gameRoot);
        if (!File.Exists(path)) return null;

        try
        {
            return JsonSerializer.Deserialize<Receipt>(File.ReadAllText(path), JsonOptions);
        }
        catch
        {
            // A corrupt receipt is worse than none: acting on it could delete the wrong files.
            // Reporting "not installed by us" makes the tool refuse to remove anything.
            return null;
        }
    }

    public static void Write(string gameRoot, Receipt receipt)
    {
        var path = PathFor(gameRoot);
        var json = JsonSerializer.Serialize(receipt, JsonOptions);

        // Write beside the target then move into place: a receipt half-written by a crash would
        // describe an install that does not exist.
        var temp = path + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, path, overwrite: true);
    }

    public static void Delete(string gameRoot)
    {
        try
        {
            var path = PathFor(gameRoot);
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Leaving a stale receipt is harmless: it only ever authorises removing files whose
            // hash still matches, and those are gone.
        }
    }
}
