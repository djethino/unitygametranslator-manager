using UnityGameTranslator.Common;

namespace UnityGameTranslator.Manager.Core.Model;

/// <summary>
/// What is installed in a game against what could be, for one part of the install.
///
/// The same shape serves the mod loader and the plugin, which look alike on screen and are
/// nothing alike underneath — the loader's answer comes from the catalog and works offline, the
/// plugin's comes from the release list and needs the network. Reading them through one type is
/// what lets the card treat them as two rows of the same table instead of two special cases.
///
/// ⚠ Three states, never two. "Nothing newer" and "we could not find out" are opposite pieces of
/// news: <see cref="Available"/> is null for the second, and <see cref="CheckFailed"/> says why.
/// A screen that folds them together tells someone they are up to date on the strength of a
/// request a firewall ate.
/// </summary>
/// <param name="Installed">The version in the game, or null when this part is not installed.</param>
/// <param name="Available">What is published, or null when that could not be established.</param>
/// <param name="CheckFailed">Why <paramref name="Available"/> is unknown, in words fit to show.</param>
public sealed record VersionStanding(string? Installed, string? Available, string? CheckFailed = null)
{
    public bool IsInstalled => !string.IsNullOrWhiteSpace(Installed);

    /// <summary>
    /// Something newer exists and is worth offering.
    ///
    /// ⚠ Strictly newer, never merely different. A version we cannot rank above the installed one
    /// leaves this false, which is the safe direction: the case it protects is a build carrying a
    /// finer version than the one published — a loader read from its own PE header as 5.4.23.2
    /// where the catalog says 5.4.23, or a pre-release someone installed by hand. Offering to
    /// "update" those would replace something newer with something older and call it an update.
    /// </summary>
    public bool UpdateAvailable =>
        IsInstalled
        && !string.IsNullOrWhiteSpace(Available)
        && Versions.IsNewer(Installed, Available);

    /// <summary>
    /// Installed, and nothing newer is published. False while the answer is unknown — this is the
    /// claim that must never be made on a failed lookup.
    /// </summary>
    public bool UpToDate =>
        IsInstalled && !string.IsNullOrWhiteSpace(Available) && !UpdateAvailable;
}
