using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Manager.Gui;

/// <summary>
/// The palette, put into the application's resources from the shared library.
///
/// ⚠ It used to be written out in Theme.axaml, as thirty-odd literal hexadecimals. Two things were
/// wrong with that, and only measuring found them:
///
///  · the values were **Tailwind v3** while the website has moved to **v4**, whose palette was
///    rebuilt in oklch — purple-600 #9333EA against #9810FA, orange-500 #F97316 against #FF6900.
///    So the tool that was written to look like the site had drifted away from it;
///  · the surfaces had been sampled from a SCREENSHOT of the running site, where the animated
///    coloured blobs in the background lighten everything one picks off them. That is why this
///    window was a shade paler than both the site and the mod.
///
/// Now there is one palette (Common.Theme), read out of the site's own CSS custom properties and
/// guarded by the socle's check project. A colour changes there, both products follow.
///
/// ⚠ Posted BEFORE the first window is built (App.Initialize), because a `{DynamicResource}` is
/// resolved when the style is applied. The keys keep the names they had, so no XAML changes.
/// </summary>
internal static class ThemeResources
{
    public static void Apply(Application app)
    {
        var r = app.Resources;

        // ── Surfaces ──────────────────────────────────────────────────────────────────────────
        r["SurfaceBase"] = Brush(Theme.SurfaceBase);          // the page
        r["SurfaceBar"] = Brush(Theme.SurfaceCard);           // nav bar, status bar
        r["SurfaceCard"] = Brush(Theme.SurfaceCard);
        r["SurfaceCardHover"] = Brush(Theme.SurfaceRaised);
        // A form field inside a card is RECESSED — darker than what holds it. A search field sits
        // ON a surface and has to read as something you act on, so it is lighter. Same distinction
        // the site makes, and the reason these two are not one key.
        r["SurfaceInput"] = Brush(Theme.SurfaceDeep);
        r["SurfaceControl"] = Brush(Theme.SurfaceRaised);

        // ── Edges ─────────────────────────────────────────────────────────────────────────────
        r["BorderSubtle"] = Brush(Theme.BorderSubtle);
        r["BorderStrong"] = Brush(Theme.BorderStrong);

        // ── Text ──────────────────────────────────────────────────────────────────────────────
        r["TextPrimary"] = Brush(Theme.TextPrimary);
        r["TextSecondary"] = Brush(Theme.TextSecondary);
        r["TextMuted"] = Brush(Theme.TextMuted);

        // ── Accent ────────────────────────────────────────────────────────────────────────────
        r["Accent"] = Brush(Theme.Accent);
        r["AccentEdge"] = Brush(Theme.AccentEdge);
        r["AccentDeep"] = Brush(Theme.AccentDeep);
        r["AccentSoft"] = Brush(Theme.AccentSoft);
        r["AccentSelected"] = Brush(Theme.RowSelected);
        r["AccentGradient"] = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Colour(Theme.AccentDeep), 0),
                new GradientStop(Colour(Theme.AccentEdge), 1),
            },
        };

        // ── Status ────────────────────────────────────────────────────────────────────────────
        r["StatusSuccess"] = Brush(Theme.StatusSuccess);
        r["StatusWarning"] = Brush(Theme.StatusWarning);
        r["StatusError"] = Brush(Theme.StatusError);
        r["StatusInfo"] = Brush(Theme.StatusInfo);
        r["StatusNeutral"] = Brush(Theme.StatusNeutral);

        // ── What a translation is made of ─────────────────────────────────────────────────────
        // Five keys of their own, never the status colours: see the note in Common.Theme for the
        // divergence that cost — the AI band was amber in the mod and orange everywhere else.
        r["QualityHuman"] = Brush(Theme.QualityHuman);
        r["QualityValidated"] = Brush(Theme.QualityValidated);
        r["QualityAi"] = Brush(Theme.QualityAi);
        r["QualityKept"] = Brush(Theme.QualityKept);
        r["QualityCapture"] = Brush(Theme.QualityCapture);
        r["QualityTrack"] = Brush(Theme.QualityTrack);

        // ── Callouts ──────────────────────────────────────────────────────────────────────────
        r["CalloutErrorBg"] = Brush(Theme.CalloutError);
        r["CalloutWarningBg"] = Brush(Theme.CalloutWarning);
        r["CalloutInfoBg"] = Brush(Theme.CalloutInfo);
        r["CalloutSuccessBg"] = Brush(Theme.CalloutSuccess);

        // Computed here rather than in the socle: it dresses ONE control in ONE product — the play
        // button hovered — where the callout tints above are a shared language the mod and the
        // site speak too. Same green, same surface, simply held a little stronger.
        r["CalloutSuccessHoverBg"] = Brush(Theme.StatusSuccess.Over(Theme.SurfaceDeep, 0.30));

        ApplyFluentOverrides(r);
    }

    /// <summary>
    /// Fluent paints its fields and pickers through named theme resources of its own, NOT through
    /// the control's Background — overriding the template border is silently ignored and the field
    /// stays near-black. These are the knobs that actually work.
    ///
    /// ⚠ They used to live in `Style.Resources` blocks in App.axaml, which was already known to be
    /// a trap: those resources are poured into the surrounding scope rather than scoped to the
    /// controls the selector matches, so a `TextBox.recessed` block quietly restyled every field in
    /// the window. Declared here they are application-wide on purpose, which is what was happening
    /// anyway — now it is said out loud.
    /// </summary>
    private static void ApplyFluentOverrides(IResourceDictionary r)
    {
        // Fields — the search shade, since that is what these windows have.
        r["TextControlBackground"] = Brush(Theme.SurfaceRaised);
        r["TextControlBackgroundPointerOver"] = Brush(Theme.SurfaceHover);
        r["TextControlBackgroundFocused"] = Brush(Theme.SurfaceHover);
        r["TextControlBorderBrush"] = Brush(Theme.BorderStrong);
        r["TextControlBorderBrushPointerOver"] = Brush(Theme.SurfaceHover);
        r["TextControlBorderBrushFocused"] = Brush(Theme.AccentEdge);
        r["TextControlForeground"] = Brush(Theme.TextPrimary);
        r["TextControlForegroundPointerOver"] = Brush(Theme.TextPrimary);
        r["TextControlForegroundFocused"] = Brush(Theme.TextPrimary);
        r["TextControlPlaceholderForeground"] = Brush(Theme.TextMuted);
        r["TextControlPlaceholderForegroundPointerOver"] = Brush(Theme.TextMuted);
        r["TextControlPlaceholderForegroundFocused"] = Brush(Theme.TextMuted);

        // The picker sits in the same toolbar and answers the same kind of question, so it wears
        // the same shade.
        r["ComboBoxBackground"] = Brush(Theme.SurfaceRaised);
        r["ComboBoxBackgroundPointerOver"] = Brush(Theme.SurfaceHover);
        r["ComboBoxBackgroundPressed"] = Brush(Theme.SurfaceHover);
        r["ComboBoxBackgroundFocused"] = Brush(Theme.SurfaceHover);
        r["ComboBoxBorderBrush"] = Brush(Theme.BorderStrong);
        r["ComboBoxBorderBrushPointerOver"] = Brush(Theme.SurfaceHover);
        r["ComboBoxBorderBrushPressed"] = Brush(Theme.AccentEdge);
        r["ComboBoxBorderBrushFocused"] = Brush(Theme.AccentEdge);
        r["ComboBoxForeground"] = Brush(Theme.TextPrimary);
        r["ComboBoxForegroundPointerOver"] = Brush(Theme.TextPrimary);
        r["ComboBoxForegroundPressed"] = Brush(Theme.TextPrimary);
        r["ComboBoxForegroundFocused"] = Brush(Theme.TextPrimary);
        r["ComboBoxDropDownBackground"] = Brush(Theme.SurfaceCard);
        r["ComboBoxDropDownBorderBrush"] = Brush(Theme.BorderSubtle);
        r["ComboBoxDropDownForeground"] = Brush(Theme.TextPrimary);
        r["ComboBoxItemBackgroundPointerOver"] = Brush(Theme.SurfaceRaised);
        r["ComboBoxItemBackgroundSelected"] = Brush(Theme.RowSelected);
        r["ComboBoxItemBackgroundSelectedPointerOver"] = Brush(Theme.RowRelated);
        r["ComboBoxItemForeground"] = Brush(Theme.TextSecondary);
        r["ComboBoxItemForegroundSelected"] = Brush(Theme.TextPrimary);
        r["ComboBoxItemForegroundPointerOver"] = Brush(Theme.TextPrimary);
    }

    private static Color Colour(Rgb c) => Color.FromRgb(c.R, c.G, c.B);

    private static IBrush Brush(Rgb c) => new SolidColorBrush(Colour(c));
}
