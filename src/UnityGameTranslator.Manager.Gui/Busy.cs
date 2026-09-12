using Avalonia.Controls;
using Avalonia.Layout;

namespace UnityGameTranslator.Manager.Gui;

/// <summary>
/// A button that says it is working, for as long as it is.
///
/// 🔴 **Because "did that do anything?" is a question the program was letting people answer by
/// clicking again.** Reported in those terms: *"j'ai cliqué 4 fois avant de voir que ça
/// marchait"* — on Refresh, which does have a spinner, sixteen pixels wide, beside the field
/// rather than on the button just pressed. The indicator was not missing; it was not where the
/// eye was, and nothing stopped a second press.
///
/// ⚠ **Disabling is the part that matters, and it is not politeness — it is correctness.** Four
/// presses on Refresh are four requests, four replies, and four refills of a list somebody may
/// meanwhile have chosen from. An action that cannot be started twice cannot do that, and it takes
/// no vigilance from whoever writes the next screen.
///
/// ⚠ **The width is frozen before the gear goes in.** A button that shrinks to its spinner drags
/// everything on its row sideways and back, which reads as the layout breaking at the exact moment
/// the reader is waiting to find out whether something worked.
///
/// ⚠ This does NOT make a synchronous operation asynchronous. A window that is frozen draws
/// nothing, so a spinner on a button whose work runs on the UI thread shows nothing at all — see
/// analyse/attente-et-asynchrone.md, which is the trap this file does not fix on its own.
/// </summary>
public static class Busy
{
    /// <summary>
    /// Runs <paramref name="work"/> with <paramref name="button"/> held busy: disabled, with the
    /// gear in place of its content, restored whatever happens.
    /// </summary>
    public static async Task While(Button button, Func<Task> work)
    {
        // Already working. Pressing a busy button is not a second request, it is the same one —
        // and the button being disabled makes this unreachable by pointer anyway.
        if (!button.IsEnabled) return;

        var content = button.Content;
        var width = button.Bounds.Width;
        var enabled = button.IsEnabled;

        if (width > 0) button.MinWidth = width;

        button.IsEnabled = false;
        button.Content = new SpinningGear(string.Empty, size: 14)
        {
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        try
        {
            await work();
        }
        finally
        {
            // ⚠ In a finally, and the whole point: a button left spinning after a failure says the
            // program is still working when it has given up, which is worse than never having
            // shown anything. The same rule the model list already followed on its own gear.
            button.Content = content;
            button.IsEnabled = enabled;
        }
    }

    /// <summary>
    /// Wires a button's click to <paramref name="work"/>, held busy for its duration.
    ///
    /// ⚠ The shape to prefer: it puts the guard at the point where the action is DECLARED, so a
    /// screen cannot forget it at one call site out of five. `While` stays public for a handler
    /// that has something of its own to do first — a confirmation, a validity check.
    /// </summary>
    public static void OnClick(Button button, Func<Task> work) =>
        button.Click += async (_, _) => await While(button, work);
}
