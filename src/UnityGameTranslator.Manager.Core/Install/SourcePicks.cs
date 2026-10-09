using System.Collections.Concurrent;

namespace UnityGameTranslator.Manager.Core.Install;

/// <summary>
/// The library sources picked on a game's card and not yet acted on — held for this session only.
///
/// 🔴 **A choice is kept across sessions only once an act has used it** (user's rule, restated
/// 2026-09-21: « on a dit qu'on n'enregistrait pas les choix non validés entre les sessions »). The
/// card's radios used to write the per-game preference as they were clicked, so a source picked and
/// never installed was still selected after a restart. Picked here, it lasts until the window closes;
/// the install that uses it writes it into the game's preferences (<see cref="Model.GamePreference"/>)
/// and clears it from here.
///
/// ⚠ Static: the window rebuilds its inventory whenever the settings change, and a session outlives
/// that — the same reason as the session overrules in GameOverrides.
/// </summary>
public static class SourcePicks
{
    private static readonly ConcurrentDictionary<string, string> Libraries = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> Modules = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The .NET source picked this session for this game, or null.</summary>
    public static string? LibrariesFor(string gamePath) => Libraries.TryGetValue(Key(gamePath), out var id) ? id : null;

    /// <summary>The engine modules' source picked this session for this game, or null.</summary>
    public static string? ModulesFor(string gamePath) => Modules.TryGetValue(Key(gamePath), out var id) ? id : null;

    /// <summary>
    /// Holds a .NET source — only while it differs from <paramref name="decidedId"/>, the source used
    /// with nothing picked (2026-10-09: a pick equal to it kept Undo lit over nothing, the rule of
    /// `.claude/rules/manager-ui.md` §1).
    /// </summary>
    public static void PickLibraries(string gamePath, string id, string? decidedId) => Hold(Libraries, gamePath, id, decidedId);

    /// <summary>Holds an engine modules' source, on the same terms as <see cref="PickLibraries"/>.</summary>
    public static void PickModules(string gamePath, string id, string? decidedId) => Hold(Modules, gamePath, id, decidedId);

    /// <summary>Whether anything is held for this game — what lights Undo.</summary>
    public static bool AnyFor(string gamePath) =>
        Libraries.ContainsKey(Key(gamePath)) || Modules.ContainsKey(Key(gamePath));

    /// <summary>Forgets this game's picks — once an act has written them into its preferences, or on Undo.</summary>
    public static void Settled(string gamePath)
    {
        Libraries.TryRemove(Key(gamePath), out _);
        Modules.TryRemove(Key(gamePath), out _);
    }

    private static void Hold(ConcurrentDictionary<string, string> held, string gamePath, string id, string? decidedId)
    {
        if (string.Equals(id, decidedId, StringComparison.Ordinal)) held.TryRemove(Key(gamePath), out _);
        else held[Key(gamePath)] = id;
    }

    private static string Key(string gamePath) => Path.GetFullPath(gamePath);
}
