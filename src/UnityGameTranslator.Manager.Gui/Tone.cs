namespace UnityGameTranslator.Manager.Gui;

/// <summary>
/// What a notice engages — not how loud it is.
///
/// 🔴 **The colour says what it engages, never how important it is.** Rank notices by importance
/// and everything drifts towards red, because everything on screen is there for a reason. Rank them
/// by what the reader is being asked to carry and the four stay apart on their own.
///
/// · <see cref="Error"/> — nothing will work here; no action on this screen can succeed;
/// · <see cref="Warning"/> — it works, but not the way it looks, or work can be lost. A decision;
/// · <see cref="Info"/> — changes what the control beside it will do, with nothing wrong;
/// · <see cref="Success"/> — in place, nothing to do;
/// · <see cref="Neutral"/> — an offer, or a plain row of information. Not a notice at all.
/// </summary>
internal enum Tone
{
    Neutral,
    Info,
    Warning,
    Error,
    Success,
}

/// <summary>
/// The one place a tone becomes two colours.
///
/// ⚠ Written because the two were passed side by side at every call — `Callout(text,
/// "CalloutWarningBg", "StatusWarning")` — which is a pair anybody can mismatch, and which nobody
/// would notice on screen: an amber tint behind a blue rule reads as a colour someone chose. One
/// argument cannot be mismatched.
/// </summary>
internal static class Tones
{
    /// <summary>The tint behind a notice that sits between cards, on the deep surface.</summary>
    public static string CalloutBackground(Tone tone) => tone switch
    {
        Tone.Error => "CalloutErrorBg",
        Tone.Warning => "CalloutWarningBg",
        Tone.Info => "CalloutInfoBg",
        Tone.Success => "CalloutSuccessBg",
        _ => "SurfaceCard",
    };

    /// <summary>
    /// The tint behind a notice in the top strip.
    ///
    /// ⚠ A different set from the callouts, and it has to be: a banner is a raised block on the
    /// darkest background this window has, so tinting it on the deep surface would sink it to the
    /// level of the page behind — coloured, and less visible than it was plain.
    /// </summary>
    public static string BannerBackground(Tone tone) => tone switch
    {
        Tone.Error => "BannerErrorBg",
        Tone.Warning => "BannerWarningBg",
        Tone.Info => "BannerInfoBg",
        Tone.Success => "BannerSuccessBg",
        _ => "SurfaceCard",
    };

    /// <summary>The edge: the callout's left rule, or the banner's outline.</summary>
    public static string Edge(Tone tone) => tone switch
    {
        Tone.Error => "StatusError",
        Tone.Warning => "StatusWarning",
        Tone.Info => "StatusInfo",
        Tone.Success => "StatusSuccess",
        _ => "BorderSubtle",
    };
}
