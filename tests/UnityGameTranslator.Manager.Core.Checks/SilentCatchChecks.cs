using UnityGameTranslator.Checks.Shared;

namespace UnityGameTranslator.Manager.Core.Checks;

/// <summary>
/// No catch in the Manager swallows a failure without a word: it is said through Faults (written to
/// the journal, Diagnostics/Journal), noted as a recognised case (Journal.Note), or let go on.
///
/// 🔴 **Why it is a check** (2026-10-07). The rule had been held in the mod since 2026-09-27; nothing
/// held it here, and 234 silent catches had piled up — one of them let the uninstall screen announce
/// a last backup that had failed, just before deleting the translation. The counter and its rule are
/// common/tests/Shared/SilentCatches.cs, written once for the mod, the Manager and the library; this
/// file only says which folders are the Manager's.
/// </summary>
internal static class SilentCatchChecks
{
    public static void NothingSwallowed()
    {
        Program.Section("Silent catches: none in the Manager");
        SilentCatches.SelfCheck(Program.Check);
        var root = ManagerRoot();
        Program.Check(root is not null, "the Manager's sources are found", "this check reads them; without them, it proves nothing");
        if (root is null) return;
        SilentCatches.NoneUnder(Program.Check, "no silent catch in the Manager", [Path.Combine(root, "src")], root);
    }

    /// <summary>`dotnet run -- silent-list [file]`: where the silent catches are, one per line.</summary>
    public static int List(string? only)
    {
        var root = ManagerRoot();
        if (root is null) { Console.WriteLine("The Manager's sources were not found."); return 1; }
        return SilentCatches.List([Path.Combine(root, "src")], root, only);
    }

    // The repository root: the folder holding src/UnityGameTranslator.Manager.Core.
    private static string? ManagerRoot() => SilentCatches.FolderHolding("src/UnityGameTranslator.Manager.Core");
}
