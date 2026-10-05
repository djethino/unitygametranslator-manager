using Avalonia.Controls;
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

    /// <summary>
    /// The published update is already in this file, and this file can update the installed copy —
    /// so the Settings card offers Update installed copy (a copy of this file) and no download.
    /// </summary>
    public static bool HoldsOffer(SelfInstaller installer, SelfUpdateOffer offer) =>
        !Versions.IsNewer(SelfUpdater.CurrentVersion, offer.NewVersion)
        && installer.Installed() is { } installed
        && !installer.RunningTheInstalledCopy()
        && !installer.Inspect().NeedsRepair
        && CanUpdateFromHere(installer, installed);

    /// <summary>The toolbar's update button: its label, its tooltip, primary or warning.</summary>
    public sealed record Notice(string Label, string Tip, bool Primary);

    /// <summary>
    /// What the toolbar says about this tool's updates, naming WHICH copy is behind. Null when
    /// nothing is to be said.
    ///
    /// 🔴 User, 2026-10-05: "the button at the top is good because it is visible, but its message is
    /// misleading: it must tell the portable from the installed copy, and say to update the installed
    /// one or that the portable is out of date". An update is measured against the installed copy
    /// (SelfUpdater.Target), and the toolbar said "Update available: 0.5.0" in a window running
    /// 0.5.0 — the installed copy was 0.4.1, and nothing said so.
    /// </summary>
    public static Notice? For(SelfInstaller installer, SelfUpdateCheck result)
    {
        var running = SelfUpdater.CurrentVersion;
        var other = installer.Installed() is { } installed && !installer.RunningTheInstalledCopy()
                    && !installer.Inspect().NeedsRepair
            ? installed
            : null;

        switch (result.State)
        {
            case SelfUpdateState.Available when result.Offer is { } offer:
                if (other is null)
                    return new Notice($"Update available: {offer.NewVersion}",
                        "Open Settings to see what changed and install it.", Primary: true);

                // This file is the new version: the installed copy is updated from it, no download.
                if (HoldsOffer(installer, offer))
                    return new Notice($"Update installed copy to {offer.NewVersion}",
                        $"This copy is {running}. The installed copy is {other.Version}, in "
                        + $"{other.Directory}.\n\nOpen Settings to update it.", Primary: true);

                return new Notice($"Update available: {offer.NewVersion} (installed copy)",
                    $"The installed copy ({other.Version}) is out of date. This copy ({running}) is "
                    + $"out of date too.\n\nOpen Settings to update the installed copy.", Primary: true);

            // The installed copy is current; this file may not be.
            case SelfUpdateState.UpToDate when other is not null && result.Latest is { } latest
                                               && Versions.IsNewer(running, latest):
                return new Notice($"This copy is out of date ({running})",
                    $"The installed copy is {other.Version}, the latest version. It is in "
                    + $"{other.Directory}.\n\nOpen Settings to open it.", Primary: false);

            default:
                return null;
        }
    }

    /// <summary>The button's word for whichever of the two acts applies.</summary>
    public static string Verb(bool canUpdate) => canUpdate ? "Update installed copy" : "Open installed copy";

    /// <summary>
    /// Starts the installed copy and ends this one — two windows would share one settings file.
    /// Null when it started; the reason otherwise.
    /// </summary>
    public static string? Open(ToolInstallation installed) =>
        Relaunch.Start(installed.Executable, installed.Directory);

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
