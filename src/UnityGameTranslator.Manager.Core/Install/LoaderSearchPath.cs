namespace UnityGameTranslator.Manager.Core.Install;

/// <summary>
/// Where a mod loader is told to look for class libraries before the game's own folder — and the
/// edits that add our folder to that setting, or take it back out.
///
/// 🔴 **Compiled in, never read from the catalogue.** This setting makes the runtime load code
/// from a folder of our choosing, ahead of the game's. Which file and which key do that is the same
/// kind of fact as <see cref="Catalog.LoaderOrigins"/>: a decision that must not travel silently
/// to every installation with the next catalogue fetch.
///
/// ⚠ **Read from the loaders' sources, 2026-09-21:**
///   · BepInEx 5 and 6 (Unity Doorstop 4): `doorstop_config.ini`, `[UnityMono]`,
///     `dll_search_path_override`, `;`-separated, relative to the game folder. BepInEx 6 already
///     puts `"BepInEx\core"` there, so the value is ADDED to, never replaced.
///   · MelonLoader from 0.7.1: `UserData/Loader.cfg` (TOML), `[unityengine]`,
///     `mono_search_path_override`, separated like the system's paths, relative to the game
///     folder. 0.7.0 has no such key. A partial file is read, completed with defaults and written
///     back whole (`LoaderConfig.Initialize`), so a file this tool creates keeps our entry.
///   · IL2CPP builds have no class library to complete.
/// </summary>
public static class LoaderSearchPath
{
    public enum Syntax { Ini, Toml }

    /// <param name="File">Relative to the game folder, forward slashes.</param>
    /// <param name="MinimumLoaderVersion">The first loader version that honours the key, or null.</param>
    public sealed record Setting(string File, string Section, string Key, Syntax Syntax, char Separator,
                                 string? MinimumLoaderVersion);

    /// <summary>
    /// The folder the libraries go in, at the root of the game.
    ///
    /// ⚠ Relative in the setting, so a game folder that moves keeps working. Named after us so that
    /// somebody finding it knows who put it there.
    /// </summary>
    public const string Folder = "ugt-runtime-libraries";

    /// <summary>The setting for a loader, or null when it has none this tool knows how to write.</summary>
    /// <param name="windowsBuild">Whether the game is a Windows build — MelonLoader separates as the system does.</param>
    public static Setting? For(string loaderId, bool windowsBuild) => loaderId.ToLowerInvariant() switch
    {
        "bepinex5" or "bepinex6-mono" =>
            new Setting("doorstop_config.ini", "UnityMono", "dll_search_path_override", Syntax.Ini, ';', null),
        "melonloader" =>
            new Setting("UserData/Loader.cfg", "unityengine", "mono_search_path_override", Syntax.Toml,
                        windowsBuild ? ';' : ':', "0.7.1"),
        _ => null,
    };

    /// <summary>
    /// The setting a recorded configuration file holds — how a removal finds its way back without
    /// depending on which loader is detected today.
    /// </summary>
    public static Setting? ForFile(string configFile, bool windowsBuild)
    {
        var file = configFile.Replace('\\', '/');

        return new[] { For("bepinex5", windowsBuild), For("melonloader", windowsBuild) }
            .FirstOrDefault(s => s is not null && s.File.Equals(file, StringComparison.OrdinalIgnoreCase));
    }

    // ── Reading ───────────────────────────────────────────────────────────────────────────────

    /// <summary>The entries the setting currently lists, as written. Empty when absent.</summary>
    public static IReadOnlyList<string> Entries(string? text, Setting setting)
    {
        if (text is null || Find(Lines(text), setting) is not { KeyLine: { } line } found) return Array.Empty<string>();

        return Split(ValueOf(found.Lines[line]).Value, setting);
    }

    /// <summary>Whether the setting lists this entry (compared as a path, case-insensitive).</summary>
    public static bool Lists(string? text, Setting setting, string entry) =>
        Entries(text, setting).Any(e => SamePath(e, entry));

    // ── Writing ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The file's text with <paramref name="entry"/> listed first — or unchanged when it already is.
    ///
    /// ⚠ First, because the search stops at the first folder holding a library of that name, and
    /// the whole point is that ours is found before the game's stripped copy. What was there stays
    /// after it, in its order.
    /// </summary>
    public static string Add(string? text, Setting setting, string entry)
    {
        var newline = text is not null && text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

        if (string.IsNullOrEmpty(text))
            return $"[{setting.Section}]{newline}{Render(setting, new[] { entry }, quoted: setting.Syntax == Syntax.Toml)}{newline}";

        var lines = Lines(text);
        var found = Find(lines, setting);

        if (found.KeyLine is { } keyLine)
        {
            var (value, quoted) = ValueOf(lines[keyLine]);
            var entries = Split(value, setting);
            if (entries.Any(e => SamePath(e, entry))) return text;

            lines[keyLine] = Render(setting, new[] { entry }.Concat(entries).ToList(),
                                    quoted || setting.Syntax == Syntax.Toml);
        }
        else if (found.SectionLine is { } sectionLine)
        {
            lines.Insert(sectionLine + 1, Render(setting, new[] { entry }, quoted: setting.Syntax == Syntax.Toml));
        }
        else
        {
            if (lines.Count > 0 && lines[^1].Length > 0) lines.Add("");
            lines.Add($"[{setting.Section}]");
            lines.Add(Render(setting, new[] { entry }, quoted: setting.Syntax == Syntax.Toml));
        }

        return string.Join(newline, lines);
    }

    /// <summary>
    /// The file's text without <paramref name="entry"/>, every other entry kept in its order.
    /// Unchanged when the entry is not listed.
    /// </summary>
    public static string Remove(string text, Setting setting, string entry)
    {
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = Lines(text);

        if (Find(lines, setting) is not { KeyLine: { } keyLine }) return text;

        var (value, quoted) = ValueOf(lines[keyLine]);
        var entries = Split(value, setting);
        var kept = entries.Where(e => !SamePath(e, entry)).ToList();
        if (kept.Count == entries.Count) return text;

        lines[keyLine] = Render(setting, kept, quoted || setting.Syntax == Syntax.Toml);
        return string.Join(newline, lines);
    }

    // ── The file format, as little of it as this needs ────────────────────────────────────────

    private sealed record Located(List<string> Lines, int? SectionLine, int? KeyLine);

    private static List<string> Lines(string text) => text.Replace("\r\n", "\n").Split('\n').ToList();

    /// <summary>The section's header line and the key's line inside it, when present.</summary>
    private static Located Find(List<string> lines, Setting setting)
    {
        int? section = null;
        var inSection = false;

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                inSection = line[1..^1].Trim().Equals(setting.Section, StringComparison.OrdinalIgnoreCase);
                if (inSection) section = i;
                continue;
            }

            if (!inSection || line.StartsWith('#') || line.StartsWith(';')) continue;

            var equals = line.IndexOf('=');
            if (equals <= 0) continue;

            if (line[..equals].Trim().Equals(setting.Key, StringComparison.OrdinalIgnoreCase))
                return new Located(lines, section, i);
        }

        return new Located(lines, section, null);
    }

    /// <summary>The raw value after '=', without its quotes, and whether it had them.</summary>
    private static (string Value, bool Quoted) ValueOf(string line)
    {
        var value = line[(line.IndexOf('=') + 1)..].Trim();

        if (value.StartsWith('"'))
        {
            var close = value.IndexOf('"', 1);
            return (close > 0 ? value[1..close] : value[1..], true);
        }

        // A trailing comment belongs to the file, not to the value.
        var hash = value.IndexOf(" #", StringComparison.Ordinal);
        return (hash >= 0 ? value[..hash].TrimEnd() : value, false);
    }

    private static List<string> Split(string value, Setting setting) =>
        value.Split(setting.Separator).Select(e => e.Trim()).Where(e => e.Length > 0).ToList();

    private static string Render(Setting setting, IReadOnlyList<string> entries, bool quoted)
    {
        var value = string.Join(setting.Separator, entries);
        return quoted ? $"{setting.Key} = \"{value}\"" : $"{setting.Key} = {value}".TrimEnd();
    }

    /// <summary>Two ways of writing the same relative folder: case, slashes and a trailing one.</summary>
    private static bool SamePath(string a, string b) =>
        string.Equals(Normalise(a), Normalise(b), StringComparison.OrdinalIgnoreCase);

    private static string Normalise(string path) => path.Trim().Trim('"').Replace('\\', '/').TrimEnd('/');
}
