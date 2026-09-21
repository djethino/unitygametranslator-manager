using System.Text.Json;

namespace UnityGameTranslator.Manager.Core.Detection;

/// <summary>
/// The Unity editors installed on this computer — the most trustworthy local source of both things a
/// stripped game can lack (user's judgement, 2026-09-21): the .NET class libraries of every system,
/// and the Windows player's engine modules.
///
/// ⚠ Found where the Hub installs them — its default folder, and the one the person moved it to
/// (the Hub writes that path to `secondaryInstallPath.json`, as a JSON string).
///
/// ⚠ On Windows they sit under Program Files, which a game cannot write to without being run as an
/// administrator; on Linux the Hub puts them in the person's own folder. The .NET libraries carry
/// no signature, so that difference is the whole of what makes a copy trustworthy on each system.
/// </summary>
public static class UnityEditors
{
    /// <summary>Each installed editor's version and its root folder (the one holding `Editor/`).</summary>
    public static IEnumerable<(UnityVersion Version, string Root)> Installed()
    {
        foreach (var hub in HubEditorFolders())
        {
            if (!Directory.Exists(hub)) continue;

            IEnumerable<string> editors;
            try { editors = Directory.EnumerateDirectories(hub).ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }

            foreach (var editor in editors)
            {
                if (UnityVersions.Parse(Path.GetFileName(editor)) is { } version && Directory.Exists(Path.Combine(editor, "Editor")))
                    yield return (version, editor);
            }
        }
    }

    /// <summary>An editor's .NET profile folder (`…/MonoBleedingEdge/lib/mono/unityjit-win32`), or null when it has none.</summary>
    public static string? ClassLibraries(string root, string profile)
    {
        var folder = Path.Combine(root, "Editor", "Data", "MonoBleedingEdge", "lib", "mono", profile);
        return File.Exists(Path.Combine(folder, "mscorlib.dll")) ? folder : null;
    }

    /// <summary>A playback engine's folder, matched without regard to case (Linux editors spell it their own way).</summary>
    public static string? PlaybackEngine(string root, string name)
    {
        var engines = Path.Combine(root, "Editor", "Data", "PlaybackEngines");
        if (!Directory.Exists(engines)) return null;

        try
        {
            return Directory.EnumerateDirectories(engines)
                            .FirstOrDefault(d => Path.GetFileName(d).Equals(name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IEnumerable<string> HubEditorFolders()
    {
        if (OperatingSystem.IsWindows())
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Unity", "Hub", "Editor");
        else
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Unity", "Hub", "Editor");

        var settings = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UnityHub", "secondaryInstallPath.json")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "UnityHub", "secondaryInstallPath.json");

        string? secondary = null;
        try
        {
            if (File.Exists(settings)) secondary = JsonSerializer.Deserialize<string>(File.ReadAllText(settings));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // The Hub's own file, in a state this tool did not produce: its moved folder is then
            // not searched. Nothing is refused on that — the default folder still is, and the card
            // lists what was found.
        }

        if (!string.IsNullOrWhiteSpace(secondary)) yield return secondary;
    }
}
