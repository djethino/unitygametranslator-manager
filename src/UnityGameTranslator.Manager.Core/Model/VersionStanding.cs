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
        && SameStream(Installed!, Available!)
        && Versions.IsNewer(Installed, Available);

    /// <summary>
    /// Whether these two versions come from the same stream, and can therefore be ranked at all.
    ///
    /// 🔴 **Semver cannot rank two streams, and here it ranks them WRONG.** BepInEx publishes
    /// <c>6.0.0-be.785</c> (Bleeding Edge, June 2026) and <c>6.0.0-pre.2</c> (a release from
    /// August 2024). Semver compares the pre-release identifiers as text: "be" &lt; "pre", so it
    /// declares the two-year-old build the newer one. Somebody running Bleeding Edge would be
    /// offered a downgrade, described as an update, every time they opened the window.
    ///
    /// ⚠ Not fixable by teaching an order — there IS none. Bleeding Edge 785 and pre.2 are two
    /// publication lines of the same version, and only their publisher knows which contains what.
    /// So the honest answer is silence: same tag, compare; different tags, say nothing.
    ///
    /// ⚠ A version without a pre-release part (5.4.23.4, 0.7.3) belongs to the plain stream and
    /// compares with any other plain one. That is every loader but this one.
    /// </summary>
    private static bool SameStream(string installed, string available)
        => string.Equals(StreamOf(installed), StreamOf(available), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The pre-release tag without its number: "be" from <c>6.0.0-be.785</c>, "pre" from
    /// <c>6.0.0-pre.2</c>, empty from <c>5.4.23.4</c>. Build metadata is already stripped upstream.
    /// </summary>
    private static string StreamOf(string version)
    {
        var dash = version.IndexOf('-');
        if (dash < 0) return "";

        var tag = version.Substring(dash + 1);

        var dot = tag.IndexOf('.');
        if (dot >= 0) tag = tag.Substring(0, dot);

        // "beta1", "rc2" — the digits are the count, the letters are the stream.
        var end = 0;
        while (end < tag.Length && !char.IsDigit(tag[end])) end++;

        return tag.Substring(0, end);
    }

    /// <summary>
    /// Installed, and nothing newer is published. False while the answer is unknown — this is the
    /// claim that must never be made on a failed lookup.
    /// </summary>
    /// ⚠ Also requires the two to be comparable. Without that, two versions we cannot rank fall
    /// through to "up to date" — the same claim, made on no evidence, and the more damaging of the
    /// two because it is reassuring. Both false is the honest state: it means we do not know.
    public bool UpToDate =>
        IsInstalled
        && !string.IsNullOrWhiteSpace(Available)
        && SameStream(Installed!, Available!)
        && !UpdateAvailable;

    /// <summary>
    /// Installed, something is published, and the two cannot be ranked — different publication
    /// streams. Distinct from a failed lookup: we have both answers, they simply do not compare.
    /// </summary>
    public bool NotComparable =>
        IsInstalled
        && !string.IsNullOrWhiteSpace(Available)
        && !SameStream(Installed!, Available!);
}
