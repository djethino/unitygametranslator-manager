using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using UnityGameTranslator.Manager.Core.Model;
using UnityGameTranslator.Manager.Core.Platform;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Manager.Gui;

/// <summary>
/// The pickers the mod's settings are chosen with, built in ONE place.
///
/// 🔴 **Two screens ask these questions and they must not answer them differently.** The defaults
/// window sets them for every game; a game's own card overrides them for one. A second hand-written
/// copy of "the backends are none / llm / google" drifts within a release or two, and the drift is
/// silent: the value simply stops meaning the same thing on one of the two screens, and the game
/// gets a backend the mod does not recognise.
///
/// ⚠ **What is NOT here is as deliberate as what is.** Discovering a local server and putting a
/// model through the mod's own test suite are tools for setting a translator up — they belong to
/// the defaults window, once, not to every game's card. A card offers the SETTING; the bench stays
/// where somebody goes to make a decision about their machine.
///
/// ⚠ Rendering — widths, spacing, which row a control sits on — stays with each screen. They are
/// two layouts for two purposes, and forcing one on both is how a compact card inherits a
/// full-window form.
/// </summary>
public static class ModSettingControls
{
    /// <summary>
    /// Every language the ecosystem knows, plus "follow the system" naming what that resolves to.
    ///
    /// ⚠ The list comes from the shared catalogue, never from a literal here — it is the same table
    /// the mod compiles in and the site publishes under, and the NAME is the upload contract.
    /// </summary>
    public static SearchPicker LanguagePicker(IPlatform platform, double width, bool followSystem = true)
    {
        var box = new SearchPicker { Width = width };

        LanguageChoice? follow = null;
        if (followSystem)
        {
            var detected = Languages.FromLocale(platform.SystemLanguage());
            var name = detected is not null ? Languages.NameOf(detected) : null;

            follow = new LanguageChoice("auto", name,
                name is not null ? $"Follow the system ({name})" : "Follow the system");
        }

        // ⚠ Through LanguageMark so the flags come with it — and so no picker in this product is
        // ever built with a Control per item, which neither shape can render twice.
        LanguageMark.Fill(box, Languages.All(), follow);

        return box;
    }

    /// <summary>
    /// The language a game's own text is in: every language, plus "Not set" first — which is what
    /// the mod's "auto" means (it does not know, and does not guess).
    ///
    /// ⚠ No "follow the system" here, unlike <see cref="LanguagePicker"/>: this machine's language
    /// says nothing about the language a game was written in.
    /// </summary>
    public static SearchPicker SourceLanguagePicker(double width)
    {
        var box = new SearchPicker { Width = width };
        LanguageMark.Fill(box, Languages.All(), new LanguageChoice("auto", null, "Not set"));
        return box;
    }

    /// <summary>
    /// The value behind the selected row.
    ///
    /// ⚠ Three shapes, and each is the smallest thing that says what it is. A language is a
    /// LanguageChoice because it carries a flag; an AI model is its own name and nothing else, so
    /// wrapping it would invent a distinction that does not exist; everything else is a
    /// <see cref="Choice"/> — a stored value and the words for it, which differ.
    /// </summary>
    public static string? Tag(SearchPicker box) => box.SelectedItem switch
    {
        LanguageChoice choice => choice.Code,
        Choice choice => choice.Tag,
        string text => text,
        _ => null,
    };

    /// <summary>
    /// Selects the row carrying this value, falling back to the first rather than to nothing.
    ///
    /// ⚠ Reselect, not SelectedItem: putting a list back where it was is not somebody choosing, and
    /// raising a choice here would file an answer nobody gave.
    /// </summary>
    public static void Select(SearchPicker box, string? value)
    {
        foreach (var item in box.Items)
        {
            var code = item switch
            {
                LanguageChoice choice => choice.Code,
                Choice choice => choice.Tag,
                string text => text,
                _ => null,
            };

            if (string.Equals(code, value, StringComparison.OrdinalIgnoreCase))
            {
                box.Reselect(item);
                return;
            }
        }

        if (box.SelectedItem is null && box.Items.Count > 0) box.Reselect(box.Items[0]);
    }

    /// <summary>
    /// How lines get translated: the mod's own two-level shape, one choice of kind then a provider.
    ///
    /// ⚠ "llm", never "ai". The mod matches on "llm"; a tool that wrote its own screen wording into
    /// the file produced games that translated nothing and said nothing about why.
    /// </summary>
    public static SearchPicker BackendPicker(double width)
    {
        // 🔴 **The order is the product's positioning, not the order they were written.** What
        // costs nothing comes first — community work, an AI on your own machine, writing the lines
        // yourself — and what costs the reader money comes last: Google, DeepL and the online
        // models run on their own key, at their own expense, and are an addition rather than the
        // point. Nothing here depends on the index; Select matches on the tag.
        var box = new SearchPicker { Width = width };
        box.Items.Add(new Choice("none", "Community translations only"));
        box.Items.Add(new Choice("llm", "AI (local or cloud)"));

        // 🔴 **Translating by hand is a CHOICE, and it had no name.** The mod captures the game's
        // text as it meets it and its editor lets somebody write each line — the way every
        // translation with nobody's machine behind it starts. Until now that was not an option but
        // the ABSENCE of one: "community translations only" on a game with no community
        // translation, which reads as a setup that failed rather than a way of working.
        //
        // ⚠ It also lets the rest of this program tell the two apart. A game set up under this
        // answer is complete; one set up on community work that does not exist is not, and the
        // one-click can now say so instead of installing everything for a result nobody wanted.
        box.Items.Add(new Choice("capture", "Captures only (translate by hand)"));

        // Last, and that is the whole point of the order above: these are the reader's own keys and
        // the reader's own money. They work, they are supported, and they are not what this is for.
        box.Items.Add(new Choice("google", "Google / DeepL (your own key)"));
        return box;
    }

    /// <summary>One choice on screen, two values in the file — exactly as the mod stores it.</summary>
    public static SearchPicker ProviderPicker(double width)
    {
        var box = new SearchPicker { Width = width };
        box.Items.Add(new Choice("google", "Google Translate"));
        box.Items.Add(new Choice("deepl", "DeepL"));
        return box;
    }

    /// <summary>What the mod does when a translation and somebody's own edits both moved.</summary>
    public static SearchPicker MergeStrategyPicker(double width)
    {
        var box = new SearchPicker { Width = width };
        box.Items.Add(new Choice("ask", "Ask me every time"));
        box.Items.Add(new Choice("local", "Keep my own version"));
        box.Items.Add(new Choice("remote", "Take the newer one"));
        return box;
    }

    public static SearchPicker NoticePositionPicker(double width)
    {
        var box = new SearchPicker { Width = width };

        foreach (var (tag, label) in new[]
                 {
                     ("top-right", "Top right"), ("top-left", "Top left"),
                     ("bottom-right", "Bottom right"), ("bottom-left", "Bottom left"),
                 })
        {
            box.Items.Add(new Choice(tag, label));
        }

        return box;
    }

    /// <summary>Which plugin builds get installed, and what the mod announces from inside a game.</summary>
    public static SearchPicker ChannelPicker(double width)
    {
        var box = new SearchPicker { Width = width };
        box.Items.Add(new Choice("stable", "Stable"));
        box.Items.Add(new Choice("beta", "Beta (test releases)"));
        return box;
    }

    /// <summary>
    /// Why so few keys are accepted here. The reason (a per-project Unity setting decides what a
    /// character key is called, see analyse/hotkey-keycode-divergence.md) stays in this comment:
    /// the reader only needs the fact and the way out.
    /// </summary>
    public const string HotkeyAdvice =
        "Click the button, then press a key. Allowed here: F1–F15, keypad, arrows, Insert, Delete, "
        + "Home, End, Page Up, Page Down, Escape, Tab, Space, Enter. "
        + "Letters and symbols (², ;, …) are read differently by some games. "
        + "To use one, set it in the game.";
}

/// <summary>
/// The in-game shortcut, captured the way the mod's own HotkeyCapture does it: three modifier boxes,
/// a "+", and one button that takes the base key.
///
/// 🔴 **Written once because getting it wrong locks somebody out of the mod, silently.** What a
/// Unity KeyCode designates depends on a per-project setting no runtime API reports, so a key that
/// prints a character means different things in different games — six of thirteen test games
/// disagreed with the other five about the same physical key. A key like that is refused HERE,
/// while it is still under the finger, rather than dropped later at write time: discovering three
/// screens on that a choice was quietly discarded is how somebody stops trusting a tool. If it were
/// accepted, the mod's panel would simply stop opening, saying nothing, in a game where it used to
/// — and the screen that could fix it is the one behind that key. See
/// analyse/hotkey-keycode-divergence.md.
///
/// ⚠ Modifiers are boxes rather than part of the capture, deliberately: swapping Ctrl for Alt does
/// not then mean redoing the capture.
/// </summary>
public sealed class HotkeyEditor
{
    private readonly CheckBox _ctrl;
    private readonly CheckBox _alt;
    private readonly CheckBox _shift;
    private readonly Button _key;

    private bool _capturing;

    /// <summary>The composed shortcut, e.g. "Ctrl+F10". Never empty.</summary>
    public string Value { get; private set; }

    /// <summary>The row to place: the three boxes, the "+", and the key button.</summary>
    public Control Row { get; }

    /// <summary>
    /// Why the key on screen cannot be used, or hidden when there is nothing to say.
    ///
    /// Exposed rather than placed, because the two screens put it in different company — and it is
    /// raised on ARRIVAL as well as on capture: a key chosen before this tool learned that character
    /// keys do not travel is still sitting in the settings, and is now skipped when writing to
    /// games. A setting silently without effect is the one thing a screen must never leave behind.
    /// </summary>
    public TextBlock Problem { get; }

    /// <summary>Raised whenever <see cref="Value"/> actually changes, never on a refused capture.</summary>
    public event Action? Changed;

    /// <param name="warnOnArrival">
    /// Whether a starting key that cannot travel between games is flagged straight away.
    ///
    /// 🔴 **True for Mod defaults, false for a game's own key, and the difference is not cosmetic.**
    /// A key sitting in Mod defaults exists to be pushed into games, so one that cannot travel is a
    /// setting silently without effect — exactly what a screen must never leave behind. A key
    /// sitting in a GAME is not a defect at all: it was captured there, against the keyboard as
    /// that game reads it, and it works perfectly where it lives. Flagging it tells the player
    /// their own good choice is broken, about the one setting this tool has no business judging.
    /// </param>
    public HotkeyEditor(string? initial, IBrush? muted, IBrush? warning, bool warnOnArrival = true)
    {
        Value = string.IsNullOrWhiteSpace(initial) ? BindableKeys.Default : initial;

        _ctrl = new CheckBox { Content = "Ctrl", IsChecked = Value.Contains("Ctrl+") };
        _alt = new CheckBox { Content = "Alt", IsChecked = Value.Contains("Alt+") };
        _shift = new CheckBox { Content = "Shift", IsChecked = Value.Contains("Shift+") };

        _key = new Button { Content = BindableKeys.BaseKeyOf(Value), MinWidth = 110, FontSize = 12 };

        Problem = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
            Foreground = warning,
        };

        if (warnOnArrival && BindableKeys.ExplainNotUniversal(Value) is { } carriedOver)
        {
            Problem.Text = carriedOver;
            Problem.IsVisible = true;
        }

        _ctrl.IsCheckedChanged += (_, _) => Recompose();
        _alt.IsCheckedChanged += (_, _) => Recompose();
        _shift.IsCheckedChanged += (_, _) => Recompose();

        _key.Click += (_, _) =>
        {
            _capturing = true;
            _key.Content = "Press a key...";
            Problem.IsVisible = false;
            _key.Focus();
        };

        _key.KeyDown += OnKeyDown;

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };

        row.Children.Add(_ctrl);
        row.Children.Add(_alt);
        row.Children.Add(_shift);
        row.Children.Add(new TextBlock
        {
            Text = "+",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = muted,
        });
        row.Children.Add(_key);

        Row = row;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_capturing) return;
        e.Handled = true;

        // Modifiers have their own boxes here, so pressing one alone is not an answer.
        if (e.PhysicalKey is PhysicalKey.ControlLeft or PhysicalKey.ControlRight
            or PhysicalKey.AltLeft or PhysicalKey.AltRight
            or PhysicalKey.ShiftLeft or PhysicalKey.ShiftRight
            or PhysicalKey.MetaLeft or PhysicalKey.MetaRight)
        {
            return;
        }

        _capturing = false;

        // The physical position, turned into the name Unity gives it. ⚠ That name only means the
        // same thing in every game for keys that print nothing — see BindableKeys.
        var unityName = BindableKeys.FromPhysicalKey(e.PhysicalKey.ToString());

        if (unityName is null)
        {
            // Said, never worked around. Substituting another key silently would leave somebody
            // pressing the one they chose and concluding the mod is broken.
            // Unity has no KeyCode for this position (ISO "<>", JIS/Korean IME keys…).
            Refuse("Games cannot detect this key. The previous key was kept.");
            return;
        }

        if (BindableKeys.ExplainNotUniversal(unityName) is { } notUniversal)
        {
            Refuse(notUniversal);
            return;
        }

        _key.Content = unityName;
        Problem.IsVisible = false;
        Recompose();
    }

    private void Refuse(string why)
    {
        _key.Content = BindableKeys.BaseKeyOf(Value);
        Problem.Text = why;
        Problem.IsVisible = true;
    }

    private void Recompose()
    {
        var composed = (_ctrl.IsChecked == true ? "Ctrl+" : "")
                     + (_alt.IsChecked == true ? "Alt+" : "")
                     + (_shift.IsChecked == true ? "Shift+" : "")
                     + _key.Content;

        if (composed == Value) return;

        Value = composed;
        Changed?.Invoke();
    }
}
