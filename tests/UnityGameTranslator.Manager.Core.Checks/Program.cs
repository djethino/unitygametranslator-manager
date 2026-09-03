namespace UnityGameTranslator.Manager.Core.Checks;

/// <summary>
/// Runs the Manager's own decisions against the answers they are supposed to give.
///
/// 🔴 **Why this exists at all.** Three of the four products carry checks — the mod's
/// Core.Checks, the socle's Common.Checks, the site's 635 tests — and the Manager carried none.
/// That is not a coincidence to note in passing: on 2026-09-02, four regressions in a row were
/// introduced here in one session, each one a rule that lived in a comment two hundred lines from
/// the code that had to obey it. The site was edited the same day and broke nothing.
///
/// ⚠ **What belongs here, and what cannot.** A rule that can be asked a question and answers
/// without a window, a disk or a network. Everything of that shape should end up in Core and get
/// its cases here; a decision left inside MainWindow is a decision no check can reach, which is
/// the argument for moving it, not for skipping it.
///
/// Run with `dotnet run` from this folder. The exit code is what a script should read.
/// </summary>
internal static class Program
{
    private static int _failures;

    private static int Main()
    {
        SituationChecks.SituationsAGameCanBeIn();
        SituationChecks.WhatASecondLineSays();
        StandingChecks.WhereThisAccountStands();
        TranslationChoiceChecks.WhichTranslationAGameWouldGet();
        TranslationChoiceChecks.WhenNothingIsWaiting();

        Console.WriteLine();
        if (_failures == 0)
        {
            Console.WriteLine("All checks passed.");
            return 0;
        }

        Console.WriteLine($"{_failures} check(s) FAILED.");
        return 1;
    }

    internal static void Check(bool passed, string what, string why)
    {
        if (!passed) _failures++;
        Console.WriteLine($"  {(passed ? "ok  " : "FAIL")}  {what,-56}  {why}");
    }

    internal static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine(title);
        Console.WriteLine(new string('-', title.Length));
    }
}
