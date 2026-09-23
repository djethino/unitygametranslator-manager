using UnityGameTranslator.Manager.Core.Install;

namespace UnityGameTranslator.Manager.Core.Checks;

/// <summary>What an uninstall takes away besides what it installed.</summary>
public static class UninstallChecks
{
    public static void WhereTheLoaderTreeIs()
    {
        Program.Section("Uninstall: the loader's own folder");

        // 🔴 The catalog gives the mod its OWN folder under the loader's plugins. The cleanup of the
        // loader's cache and log looked in the parent of that folder — `BepInEx/plugins` — and a
        // complete uninstall left `BepInEx/` behind with both in it (2026-09-23).
        Program.Check(UninstallEngine.LoaderTree("BepInEx/plugins/UnityGameTranslator") == "BepInEx",
            "the loader's tree, from the mod's own plugin folder", "the catalog's current BepInEx 5 and 6 entries");
        Program.Check(UninstallEngine.LoaderTree("BepInEx/plugins") == "BepInEx"
                      && UninstallEngine.LoaderTree(@"BepInEx\plugins\UnityGameTranslator\") == "BepInEx",
            "whatever the depth or the separators", "the shape the catalog had before");
        Program.Check(UninstallEngine.LoaderTree("Mods") is null
                      && UninstallEngine.LoaderTree("../Elsewhere/plugins") is null
                      && UninstallEngine.LoaderTree("") is null,
            "no tree for a folder at the game root, or one that climbs out of it",
            "the cleanup deletes inside that tree: it must never be handed the game itself");

        // 🔴 MelonLoader's plugin folder, Mods/, sits at the game root: the tree has to come from
        // the file that tells the loader's version, or MelonLoader/ and its logs stay (2026-09-23).
        var melon = new Model.LoaderDescriptor
        {
            PluginDir = "Mods",
            PluginDirShared = true,
            Detect = new Model.LoaderDetect { VersionFile = "MelonLoader/net6/MelonLoader.dll" },
        };
        Program.Check(UninstallEngine.LoaderTree(melon) == "MelonLoader",
            "MelonLoader's tree, from its version file", "Mods/ is beside it, not inside it");

        var bepinex = new Model.LoaderDescriptor
        {
            PluginDir = "BepInEx/plugins/UnityGameTranslator",
            Detect = new Model.LoaderDetect { VersionFile = "BepInEx/core/BepInEx.Preloader.dll" },
        };
        Program.Check(UninstallEngine.LoaderTree(bepinex) == "BepInEx"
                      && UninstallEngine.LoaderTree(new Model.LoaderDescriptor { PluginDir = "BepInEx/plugins" }) == "BepInEx"
                      && UninstallEngine.LoaderTree(new Model.LoaderDescriptor { PluginDir = "Mods", PluginDirShared = true }) is null,
            "the plugin folder answers only when no version file does", "and a shared folder at the root never");

        // 🔴 The catalog is fetched: a tree it names that is not one of ours cleans NOTHING. The
        // empty-folder sweep would otherwise walk whatever folder it pointed at.
        Program.Check(UninstallEngine.CleanableTree(melon) == "MelonLoader"
                      && UninstallEngine.CleanableTree(bepinex) == "BepInEx",
            "the two loader trees are cleanable", "compiled in");
        Program.Check(new[] { "premiumbowling_Data/x.dll", "UserData/x.dll", "C:/Windows/x.dll", "../x/y.dll", "Mods/x.dll" }
                          .All(v => UninstallEngine.CleanableTree(new Model.LoaderDescriptor
                          {
                              PluginDir = "Mods",
                              PluginDirShared = true,
                              Detect = new Model.LoaderDetect { VersionFile = v },
                          }) is null),
            "a catalog pointing anywhere else cleans nothing", "the game's own folders, settings, another drive");

        // ⚠ config/ and UserData/ are settings: kept by design, so never in what a loader "produced".
        // Inside a tree everything named is deleted whole; beside it, only while empty.
        var inside = new[] { "BepInEx", "MelonLoader" }.SelectMany(t => UninstallEngine.ProducedBy(t).Inside).ToList();
        var beside = new[] { "BepInEx", "MelonLoader" }.SelectMany(t => UninstallEngine.ProducedBy(t).EmptyBeside).ToList();
        Program.Check(inside.Count > 0
                      && !inside.Concat(beside).Any(n => n.Equals("config", StringComparison.OrdinalIgnoreCase)
                                                         || n.Equals("UserData", StringComparison.OrdinalIgnoreCase)
                                                         || n.Contains('/') || n.Contains('\\') || n.Contains(':')
                                                         || n.Contains(".."))
                      && !inside.Any(n => n.Equals("Mods", StringComparison.OrdinalIgnoreCase)),
            "what a loader produced never names settings or a path", "a configuration is not regenerated");
        Program.Check(UninstallEngine.ProducedBy("MelonLoader").Inside.Contains("Logs")
                      && UninstallEngine.ProducedBy("MelonLoader").EmptyBeside.Contains("UserLibs")
                      && UninstallEngine.ProducedBy("BepInEx").Inside.Contains("cache"),
            "each loader's own leftovers", "what a complete uninstall left on 2026-09-23");
    }
}
