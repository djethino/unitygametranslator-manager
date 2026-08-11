using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace UnityGameTranslator.Manager.Gui;

/// <summary>
/// Looking a colour up by name, and refusing to do it quietly when the name is wrong.
///
/// ⚠ Why this exists rather than a one-line FindResource in each window: a key that does not exist
/// resolves to null, and a null brush is not an error — the control simply inherits whatever colour
/// its parent had. So a typo produces text in the wrong colour and nothing else, which is precisely
/// the failure that cannot be seen by looking.
///
/// It happened: a session's worth of new messages were written against "StatusOk" and "StatusWarn"
/// while the theme calls them StatusSuccess and StatusWarning. Eleven places, including the line
/// that says a setting was saved and the ones that warn — every one of them uncoloured, and the
/// screen looked plausible throughout. The bug was only found because someone said a failure did
/// not look like a failure.
///
/// So an unknown key comes back in a colour nobody chose for anything: it is meant to be seen,
/// found, and fixed. Falling back to something sensible would be hiding the same mistake again.
/// </summary>
public static class Palette
{
    /// <summary>Not a colour used anywhere in the theme. That is the point.</summary>
    private static readonly IBrush Wrong = new SolidColorBrush(Color.FromRgb(0xFF, 0x00, 0xFF));

    public static IBrush? Of(string key)
    {
        var found = Application.Current?.FindResource(key);

        // Before the application exists — a window built in a test, say — there is nothing to look
        // in, and that is not a mistake to shout about.
        if (Application.Current is null) return null;

        return found as IBrush ?? Wrong;
    }
}
