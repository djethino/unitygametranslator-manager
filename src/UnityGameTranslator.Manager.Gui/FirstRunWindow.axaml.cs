using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace UnityGameTranslator.Manager.Gui;

/// <summary>
/// What this program is about to do, before it does any of it.
///
/// 🔴 **Until 2026-08-26 the first launch read the machine and then told the site which games were
/// on it — before any window had asked anything.** The mod has asked since its first version: its
/// wizard puts this question second, right after the welcome, before the language and before the
/// AI. The Manager sees a whole library where the mod sees one game, and it was the one not asking.
///
/// ⚠ **Not a licence, and not a consent form.** Nothing here is signed and nothing is accepted:
/// the program is free software and using it agrees to nothing. This asks one question — may it
/// use the internet — and states plainly what happens either way.
///
/// ### Why the two answers are equals
///
/// Both buttons are real answers, so neither is "cancel" and neither is styled as the mistake.
/// ConfirmationWindow could not carry this: its second button says Cancel by design, because it
/// exists for acts one might regret. Nobody regrets staying offline.
///
/// ⚠ **Closing the window is not an answer.** It leaves OnlineAsked false, so nothing goes out and
/// the question comes back next time. A window dismissed with Escape must never be read as a yes.
///
/// ### Why the local scan is described but not offered as a choice
///
/// Finding the games is what this program IS; there is no version of it that does not look. And it
/// sends nothing — so there is nothing to consent to, only something to be told, which is why it is
/// stated first and without a switch. What people fear is a program trawling their disks, and that
/// is precisely what this one does not do.
/// </summary>
public sealed class FirstRunWindow : Window
{
    private bool _answered;
    private bool _online;

    private FirstRunWindow()
    {
        Title = "UnityGameTranslator Manager";
        Width = 620;
        SizeToContent = SizeToContent.Height;
        MinHeight = 260;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;
        Background = this.FindResource("SurfaceBase") as IBrush;

        var layout = new StackPanel { Spacing = 14, Margin = new Thickness(24) };

        layout.Children.Add(Heading("Before this program starts"));

        // ⚠ The two halves are separated on purpose, and in this order: what stays here, then what
        // leaves. Somebody worried about a tool that reads their machine gets their answer in the
        // first line instead of hunting for it under a heading about the internet.
        layout.Children.Add(Section(
            "On this machine",
            "It finds Unity games in the folders used by Steam, Epic and GOG, and in folders you "
            + "add yourself. It does not search your disks, and it reads nothing outside those "
            + "folders."));

        layout.Children.Add(Section(
            "On the internet, if you allow it",
            "It asks the site if a translation exists for the games it found, sending their names "
            + "or Steam ids. It also checks which mod loaders and versions have been published. "
            + "Nothing else leaves this machine, and it never sends what is inside your games."));

        layout.Children.Add(new TextBlock
        {
            // Named, not implied: "you can change this later" is worth nothing without the place.
            // The wording is the one the switch itself carries under Tool settings > Network, so
            // somebody looking for it later recognises what they are looking at.
            Text = "Offline, everything else still works: it finds your games, installs the mod and "
                 + "manages what is already on this machine. You can change this at any time with "
                 + "\"Work online\" in the tool's settings, and the bar at the bottom of the window "
                 + "always says which one you are in.",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = this.FindResource("TextMuted") as IBrush,
        });

        var offline = new Button { Content = "Stay offline" };
        offline.Click += (_, _) => Answer(false);

        // ⚠ Primary, and it is the honest emphasis rather than a nudge: finding translations is
        // what somebody installed this for. The other answer sits beside it at the same size, and
        // the paragraph above says what it costs — which is little.
        //
        // 🔴 The verb is the one every program has used for thirty years. A first attempt said
        // "Look things up": a phrasal verb, on a button read in a fourth language, which is the
        // plain-English rule broken in the act of trying to follow it.
        var online = new Button { Content = "Work online", Classes = { "primary" }, IsDefault = true };
        online.Click += (_, _) => Answer(true);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(offline);
        buttons.Children.Add(online);
        layout.Children.Add(buttons);

        Content = layout;
    }

    private void Answer(bool online)
    {
        _online = online;
        _answered = true;
        Close();
    }

    private TextBlock Heading(string text) => new()
    {
        Text = text,
        FontSize = 15,
        FontWeight = FontWeight.SemiBold,
        TextWrapping = TextWrapping.Wrap,
        Foreground = this.FindResource("TextPrimary") as IBrush,
    };

    private Control Section(string title, string body)
    {
        var panel = new StackPanel { Spacing = 3 };

        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = this.FindResource("TextPrimary") as IBrush,
        });

        panel.Children.Add(new TextBlock
        {
            Text = body,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = this.FindResource("TextSecondary") as IBrush,
        });

        return panel;
    }

    /// <summary>
    /// Puts the question, and reports whether it was answered.
    /// </summary>
    /// <returns>
    /// null when the window was closed without choosing — the caller must then stay offline AND
    /// leave the question unanswered, so it is put again next time.
    /// </returns>
    public static async Task<bool?> AskAsync(Window owner)
    {
        var window = new FirstRunWindow();
        await window.ShowDialog(owner);
        return window._answered ? window._online : null;
    }
}
