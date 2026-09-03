using UnityGameTranslator.Common;

namespace UnityGameTranslator.Manager.Core.Checks;

/// <summary>
/// How the mod's files are grouped when somebody is asked what to remove.
///
/// 🔴 **The stake is a deletion nobody can undo.** The screen groups by kind so a person decides
/// per kind rather than all-or-nothing, and each group carries what is actually lost. A file that
/// falls in the wrong group is offered under the wrong consequence — the "Other files" label says
/// "not recognised, judge them yourself", which is what somebody clearing clutter reads before
/// throwing away work.
///
/// ⚠ The grouping itself walks a real folder, so what is checked here is the QUESTION it asks of
/// each name — the socle's answer to "is this the mod's interface". That question is what the mod
/// (which writes the files) and this tool (which lists them) must not spell two ways.
/// </summary>
internal static class UserDataChecks
{
    internal static void WhichFilesAreTheModsInterface()
    {
        Program.Section("Recognising the mod's interface files");

        Program.Check(ModUi.IsOurs(ModUi.FileName),
            "the interface file is recognised",
            "unrecognised, it is offered for deletion under a warning written for stray files");

        Program.Check(ModUi.IsOurs(ModUi.SetAsideFileName("French")),
            "a language set aside is recognised too",
            "the mod puts one away whenever the target language changes; it is the same work");

        Program.Check(!ModUi.IsOurs("translations.json")
                      && !ModUi.IsOurs("translations.json.ancestor")
                      && !ModUi.IsOurs("translations.json.backup"),
            "the game's translation is not the mod's interface",
            "grouped together, 'remove my translation' would take a pass of the translator unasked");

        Program.Check(!ModUi.IsOurs("config.json") && !ModUi.IsOurs("UnityGameTranslator.dll"),
            "the settings and the plugin are not either",
            "the plugin is not data at all, and the settings have their own consequence");

        // A shared folder is the case that matters: under BepInEx the mod keeps its files beside
        // its own assembly, among every other mod's. Claiming a name on a prefix alone would move
        // somebody else's file during a repair.
        Program.Check(!ModUi.IsOurs("modui-translate") && !ModUi.IsOurs("modui-translate.json.bak"),
            "a near miss is not claimed",
            "in a shared plugins folder, a wrong yes moves another mod's file");
    }
}
