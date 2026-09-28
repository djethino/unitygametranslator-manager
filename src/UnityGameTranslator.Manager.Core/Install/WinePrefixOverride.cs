using UnityGameTranslator.Manager.Core.Model;

namespace UnityGameTranslator.Manager.Core.Install;

/// <summary>
/// The Wine DLL override a loader needs, written into the game's own Wine prefix — what
/// <c>winecfg</c> → Libraries does — instead of a launch option somebody has to type.
///
/// 🔴 **Why here and not in Steam's launch options** (user, 2026-09-28: "a Bazzite or Steam Deck
/// user is not an advanced user"). A launch option means typing a line into Steam, or the tool
/// editing Steam's own config, which Steam rewrites on exit, so Steam would have to be closed. The
/// prefix registry belongs to the game alone, is read when the game starts, and is the same for
/// every launcher that gives a game a prefix. BepInEx's own documentation offers it as the
/// alternative to the launch option.
///
/// ⚠ **The prefix exists only after the game's first launch.** Before that there is nothing to
/// write into, and the install says so and falls back to the launch option.
///
/// ⚠ **Never while the game runs**: wineserver writes user.reg when the prefix shuts down and would
/// put back what it read at start. Every install and uninstall already refuses a running game.
/// </summary>
public static class WinePrefixOverride
{
    private const string Section = "[Software\\\\Wine\\\\DllOverrides]";
    private const string Value = "native,builtin";

    /// <summary>The registry file of this game's prefix, or null when there is none to write into.</summary>
    public static string? UserRegistry(GameInstall game)
    {
        if (game.ProtonPrefix is not { } compatData) return null;
        var file = Path.Combine(compatData, "pfx", "user.reg");
        return File.Exists(file) ? file : null;
    }

    /// <summary>True when the prefix already loads this DLL from the game folder first.</summary>
    public static bool IsSet(string userReg, string dll) =>
        Current(File.ReadAllLines(userReg), dll) is { } value && value.StartsWith("native", StringComparison.Ordinal);

    /// <summary>
    /// Writes the override and returns what the entry held before (null: there was none), for the
    /// receipt — uninstall puts it back.
    /// </summary>
    public static ReceiptWineOverride Set(string userReg, string dll)
    {
        var lines = File.ReadAllLines(userReg).ToList();
        var previous = Current(lines, dll);
        var entry = Entry(dll, Value);

        var start = SectionStart(lines);
        if (start < 0)
        {
            // Wine stamps each section with its modification time; any integer is accepted.
            if (lines.Count > 0 && lines[^1].Length > 0) lines.Add("");
            lines.Add($"{Section} {DateTimeOffset.UtcNow.ToUnixTimeSeconds()}");
            lines.Add(entry);
        }
        else if (EntryIndex(lines, start, dll) is var at and >= 0)
        {
            lines[at] = entry;
        }
        else
        {
            lines.Insert(start + 1, entry);
        }

        Write(userReg, lines);
        return new ReceiptWineOverride { File = userReg, Dll = dll, Previous = previous };
    }

    /// <summary>Puts back what was there before the install: the old value, or no entry at all.</summary>
    public static void Restore(ReceiptWineOverride recorded)
    {
        if (!File.Exists(recorded.File)) return;

        var lines = File.ReadAllLines(recorded.File).ToList();
        var start = SectionStart(lines);
        if (start < 0) return;

        var at = EntryIndex(lines, start, recorded.Dll);
        if (at < 0) return;

        if (recorded.Previous is { } previous) lines[at] = Entry(recorded.Dll, previous);
        else lines.RemoveAt(at);

        Write(recorded.File, lines);
    }

    private static string Entry(string dll, string value) => $"\"{dll}\"=\"{value}\"";

    private static string? Current(IReadOnlyList<string> lines, string dll)
    {
        var start = SectionStart(lines);
        if (start < 0) return null;

        var at = EntryIndex(lines, start, dll);
        if (at < 0) return null;

        var line = lines[at];
        var equals = line.IndexOf("=\"", StringComparison.Ordinal);
        return equals < 0 ? null : line.Substring(equals + 2).TrimEnd().TrimEnd('"');
    }

    private static int SectionStart(IReadOnlyList<string> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].StartsWith(Section, StringComparison.OrdinalIgnoreCase)) return i;
        }

        return -1;
    }

    /// <summary>The entry's line inside the section, or -1. The section ends at a blank line or the next header.</summary>
    private static int EntryIndex(IReadOnlyList<string> lines, int start, string dll)
    {
        var key = $"\"{dll}\"=";
        for (var i = start + 1; i < lines.Count && lines[i].Length > 0 && !lines[i].StartsWith('['); i++)
        {
            if (lines[i].StartsWith(key, StringComparison.OrdinalIgnoreCase)) return i;
        }

        return -1;
    }

    /// <summary>LF, as Wine writes it; through a temporary file so a failure never leaves half a registry.</summary>
    private static void Write(string userReg, IReadOnlyList<string> lines)
    {
        var temporary = userReg + ".ugt-tmp";
        File.WriteAllText(temporary, string.Join("\n", lines) + "\n");
        File.Move(temporary, userReg, overwrite: true);
    }
}
