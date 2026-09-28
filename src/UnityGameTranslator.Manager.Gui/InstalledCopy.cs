using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using UnityGameTranslator.Common;
using UnityGameTranslator.Manager.Core.Install;
using UnityGameTranslator.Manager.Core.Model;
using UnityGameTranslator.Manager.Core.Update;

namespace UnityGameTranslator.Manager.Gui;

/// <summary>
/// The two acts offered to somebody running a downloaded copy on a computer where UGT Manager is
/// installed: open the installed copy, or — when this file is newer — update it from this file.
///
/// 🔴 **One door, two places.** The overview's banner and the Installation card in Settings both
/// offer them (user, 2026-09-28: "we give the way out right away"); written once here, so the two
/// cannot drift into one that checks and one that does not.
/// </summary>
internal static class InstalledCopy
{
    /// <summary>This file is newer than the installed copy, and a build able to copy itself.</summary>
    public static bool CanUpdateFromHere(SelfInstaller installer, ToolInstallation installed) =>
        Versions.Compare(SelfUpdater.CurrentVersion, installed.Version) > 0 && installer.Plan().Refusal is null;

    /// <summary>The button's word for whichever of the two acts applies.</summary>
    public static string Verb(bool canUpdate) => canUpdate ? "Update installed copy" : "Open installed copy";

    /// <summary>
    /// Starts the installed copy and ends this one — two windows would share one settings file, and
    /// the one-window lock would make the new one close again. Null when it started; the reason
    /// otherwise.
    /// </summary>
    public static string? Open(ToolInstallation installed)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(installed.Executable)
            {
                UseShellExecute = true,
                WorkingDirectory = installed.Directory,
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
                                       or FileNotFoundException)
        {
            return $"Could not start {installed.Executable}: {ex.Message}";
        }

        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        return null;
    }

    /// <summary>
    /// Copies this file over the installed copy, then offers to open it. Null when done (opened or
    /// not); the reason when it failed.
    /// </summary>
    public static async Task<string?> UpdateFromHereAsync(Window owner, SelfInstaller installer)
    {
        ToolInstallation updated;
        try
        {
            updated = installer.UpdateInstalled();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return ex.Message;
        }

        // Offered rather than done: they are still in the downloaded copy, and the point of
        // updating the installed one is to end up in it.
        var across = await ConfirmationWindow.AskAsync(owner,
            $"Open the updated copy ({updated.Version})?",
            $"It is in {updated.Directory}, where your shortcut points. The downloaded file "
            + "stays where it is.",
            "Open");

        return across ? Open(updated) : null;
    }
}
