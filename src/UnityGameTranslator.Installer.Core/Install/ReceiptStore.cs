using System.Text.Json;
using UnityGameTranslator.Installer.Core.Model;

namespace UnityGameTranslator.Installer.Core.Install;

/// <summary>
/// Reads and writes the install receipt.
///
/// It lives in the game folder, which means it travels with the game if it is moved and is found
/// again without any registry or index. A copy is also kept in the tool's own data directory so
/// every install can be listed without rescanning the machine — but the one in the game folder
/// is the authority, because it is the one that matches what is actually on disk.
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
