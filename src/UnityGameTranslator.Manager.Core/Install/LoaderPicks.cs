using System.Collections.Concurrent;
using UnityGameTranslator.Manager.Core.Catalog;
using UnityGameTranslator.Manager.Core.Model;

namespace UnityGameTranslator.Manager.Core.Install;

/// <summary>
/// The loader — and the build of it — picked on a game's card and not yet installed, held for this
/// session only. Same rule and same shape as <see cref="SourcePicks"/>.
///
/// 🔴 **Read by the inventory, so the whole card follows it** (2026-10-08). The pick lived in the
/// picker itself, read back by the install alone: the one-click's steps and its confirmation named
/// the loader the catalogue puts first ("install BepInEx") while the click installed MelonLoader;
/// any redraw put the picker back on the first loader without a word; and the closure kept pointing
/// at the previous game's picker when another game was opened on its other tab. Held here and laid
/// over <see cref="GameReport.RecommendedLoader"/>, there is one answer, and the steps, the plan,
/// the mod's card and the translations window all read it.
///
/// ⚠ Kept only while it differs from what would be used anyway — the picker forgets it when the
/// first loader is chosen again, so Undo is never offered over nothing.
/// </summary>
public static class LoaderPicks
{
    private static readonly ConcurrentDictionary<string, string> Loaders = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, (string LoaderId, LoaderBuild Build)> Builds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The loader id picked this session for this game, or null.</summary>
    public static string? LoaderFor(string gamePath) => Loaders.TryGetValue(Key(gamePath), out var id) ? id : null;

    /// <summary>
    /// The build picked this session for this game, for THAT loader — or null.
    ///
    /// ⚠ Asked with the loader, because a build belongs to one: a BepInEx build left over after
    /// switching to MelonLoader must not be handed to MelonLoader's install.
    /// </summary>
    public static LoaderBuild? BuildFor(string gamePath, string loaderId) =>
        Builds.TryGetValue(Key(gamePath), out var held)
        && string.Equals(held.LoaderId, loaderId, StringComparison.OrdinalIgnoreCase)
            ? held.Build
            : null;

    /// <summary>Whether anything is held for this game — a loader or a build.</summary>
    public static bool AnyFor(string gamePath) =>
        Loaders.ContainsKey(Key(gamePath)) || Builds.ContainsKey(Key(gamePath));

    /// <summary>Holds a loader; the build held for another loader goes with the change.</summary>
    public static void PickLoader(string gamePath, string loaderId)
    {
        Loaders[Key(gamePath)] = loaderId;
        if (Builds.TryGetValue(Key(gamePath), out var held)
            && !string.Equals(held.LoaderId, loaderId, StringComparison.OrdinalIgnoreCase))
        {
            Builds.TryRemove(Key(gamePath), out _);
        }
    }

    /// <summary>Back to the loader that is used when nobody picks one.</summary>
    public static void ForgetLoader(string gamePath)
    {
        Loaders.TryRemove(Key(gamePath), out _);
        Builds.TryRemove(Key(gamePath), out _);
    }

    public static void PickBuild(string gamePath, string loaderId, LoaderBuild build) =>
        Builds[Key(gamePath)] = (loaderId, build);

    /// <summary>Back to the newest build.</summary>
    public static void ForgetBuild(string gamePath) => Builds.TryRemove(Key(gamePath), out _);

    /// <summary>Forgets this game's picks — once an install has put the loader in place.</summary>
    public static void Settled(string gamePath) => ForgetLoader(gamePath);

    /// <summary>
    /// The loader to use among those that fit: the one picked, when it is one of them, and
    /// otherwise the first.
    ///
    /// ⚠ A pick never makes a loader fit — the order of <paramref name="candidates"/> is the rule
    /// for that, and a loader that left the list (catalogue refreshed, preference changed) falls
    /// back to the first rather than being installed anyway.
    /// </summary>
    public static LoaderDescriptor? Resolve(IReadOnlyList<LoaderDescriptor> candidates, string? pickedId)
    {
        if (candidates.Count == 0) return null;

        return pickedId is null
            ? candidates[0]
            : candidates.FirstOrDefault(l => string.Equals(l.Id, pickedId, StringComparison.OrdinalIgnoreCase))
              ?? candidates[0];
    }

    private static string Key(string gamePath) => Path.GetFullPath(gamePath);
}
