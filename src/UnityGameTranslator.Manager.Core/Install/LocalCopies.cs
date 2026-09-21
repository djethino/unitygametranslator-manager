namespace UnityGameTranslator.Manager.Core.Install;

/// <summary>
/// What is said whenever files are copied from another game on this computer — ONE wording, for the
/// card, the one-click's confirmation and the command line.
///
/// 🔴 **The user's decision (2026-09-21)**: another game may be offered as a source, but never
/// without saying that this tool does not host those files and is not responsible for them, and
/// that only a game the person trusts should be used — a pirated game can carry anything. And what
/// can be checked is said to be checked: Unity's signature on the engine modules; the .NET
/// libraries have none.
///
/// ⚠ Plain international English, in short sentences: this is read in a fourth language.
/// </summary>
public static class LocalCopies
{
    /// <summary>The warning for copies taken from another game.</summary>
    /// <param name="signed">Engine modules (Unity's signature checked) rather than .NET libraries (no signature).</param>
    public static string Disclaimer(bool signed) =>
        "Copied from another game on this computer. UnityGameTranslator does not host these files and is not "
        + "responsible for them. Only use a game you trust. "
        + (signed ? "Each engine module is checked for Unity's signature." : ".NET libraries have no signature, so they cannot be checked.");

    /// <summary>
    /// Whether this computer can go online at all — a local reading, no traffic. What decides whether
    /// Unity's server is offered as a source; a download that then fails says so on its own.
    /// </summary>
    public static bool NetworkAvailable()
    {
        try { return System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable(); }
        catch (System.Net.NetworkInformation.NetworkInformationException) { return true; }
    }

    /// <summary>Said with the first download from Unity — the tool is a third party to Unity.</summary>
    public const string NotAffiliated = "UnityGameTranslator is not affiliated with Unity Technologies.";

    // ── Said in full once per machine ──────────────────────────────────────────────────────────

    /// <summary>
    /// The plan, told which of these notices were already read on this machine.
    ///
    /// 🔴 **In full the first time, then the source is simply named** (user's decision, 2026-09-21:
    /// « on le propose une seule fois ? la première fois qu'on fait cette action, qu'elle vienne d'un
    /// Apply ou d'un one-click »). The warning and Unity's terms are read once per kind of source —
    /// a copy from another game, a download from Unity — whichever screen shows them first; every
    /// later confirmation still names where the files come from.
    /// </summary>
    public static InstallPlan WithNoticesRead(this InstallPlan plan, Model.InstallerSettings settings) =>
        plan with { UnityNoticeRead = settings.UnityDownloadNoticeRead, LocalCopyNoticeRead = settings.LocalCopyNoticeRead };

    /// <summary>Records, once the plan's confirmation was accepted, the notices it showed. True when something changed.</summary>
    public static bool RecordNoticesRead(Model.InstallerSettings settings, InstallPlan plan)
    {
        var changed = false;

        if (plan.DownloadsFromUnity && !settings.UnityDownloadNoticeRead)
        {
            settings.UnityDownloadNoticeRead = true;
            changed = true;
        }

        if (plan.CopiesFromAnotherGame && !settings.LocalCopyNoticeRead)
        {
            settings.LocalCopyNoticeRead = true;
            changed = true;
        }

        return changed;
    }

    /// <summary>What to do when nothing on this computer can serve and it is offline.</summary>
    public const string GoOnline =
        "No copy was found on this computer. Connect to the internet: they can then be downloaded from Unity's server.";
}
