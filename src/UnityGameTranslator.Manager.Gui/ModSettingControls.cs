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
    /// <summary>The value behind the selected row, or null when nothing is selected.</summary>
    public static string? Tag(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string;

    /// <summary>
    /// Selects the row carrying this value, falling back to the first rather than to nothing.
    ///
    /// An empty selection reads as "not set" while the file says otherwise, and it is what once
    /// made a settings screen claim a pending change on a form nobody had touched.
    /// </summary>
    public static void Select(ComboBox box, string? value)
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, value, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedItem = item;
                return;
            }
        }

        box.SelectedItem ??= box.Items.OfType<ComboBoxItem>().FirstOrDefault();
    }

    /// <summary>
    /// Every language the ecosystem knows, plus "follow the system" naming what that resolves to.
    ///
    /// ⚠ The list comes from the shared catalogue, never from a literal here — it is the same table
    /// the mod compiles in and the site publishes under, and the NAME is the upload contract.
    /// </summary>
    public static ComboBox LanguagePicker(IPlatform platform, double width, bool followSystem = true)
    {
        var box = new ComboBox { Width = width };

        if (followSystem)
        {
            var detected = Languages.FromLocale(platform.SystemLanguage());

            box.Items.Add(new ComboBoxItem
            {
                Content = detected is not null
                    ? $"Follow the system ({Languages.NameOf(detected)})"
                    : "Follow the system",
                Tag = "auto",
            });
        }

        foreach (var (code, name) in Languages.All())
            box.Items.Add(new ComboBoxItem { Content = name, Tag = code });

        return box;
    }

    /// <summary>
    /// How lines get translated: the mod's own two-level shape, one choice of kind then a provider.
    ///
    /// ⚠ "llm", never "ai". The mod matches on "llm"; a tool that wrote its own screen wording into
    /// the file produced games that translated nothing and said nothing about why.
    /// </summary>
    public static ComboBox BackendPicker(double width)
    {
        var box = new ComboBox { Width = width };
        box.Items.Add(new ComboBoxItem { Content = "Community translations only", Tag = "none" });
        box.Items.Add(new ComboBoxItem { Content = "AI (local or cloud)", Tag = "llm" });
        box.Items.Add(new ComboBoxItem { Content = "Google / DeepL", Tag = "google" });
        return box;
    }

    /// <summary>One choice on screen, two values in the file — exactly as the mod stores it.</summary>
    public static ComboBox ProviderPicker(double width)
    {
        var box = new ComboBox { Width = width };
        box.Items.Add(new ComboBoxItem { Content = "Google Translate", Tag = "google" });
        box.Items.Add(new ComboBoxItem { Content = "DeepL", Tag = "deepl" });
        return box;
    }

    /// <summary>What the mod does when a translation and somebody's own edits both moved.</summary>
    public static ComboBox MergeStrategyPicker(double width)
    {
        var box = new ComboBox { Width = width };
        box.Items.Add(new ComboBoxItem { Content = "Ask me every time", Tag = "ask" });
        box.Items.Add(new ComboBoxItem { Content = "Keep my own version", Tag = "local" });
        box.Items.Add(new ComboBoxItem { Content = "Take the newer one", Tag = "remote" });
        return box;
    }

    public static ComboBox NoticePositionPicker(double width)
    {
        var box = new ComboBox { Width = width };

        foreach (var (tag, label) in new[]
                 {
                     ("top-right", "Top right"), ("top-left", "Top left"),
                     ("bottom-right", "Bottom right"), ("bottom-left", "Bottom left"),
                 })
        {
            box.Items.Add(new ComboBoxItem { Content = label, Tag = tag });
        }

        return box;
    }

    /// <summary>Which plugin builds get installed, and what the mod announces from inside a game.</summary>
    public static ComboBox ChannelPicker(double width)
    {
        var box = new ComboBox { Width = width };
        box.Items.Add(new ComboBoxItem { Content = "Stable", Tag = "stable" });
        box.Items.Add(new ComboBoxItem { Content = "Beta (test releases)", Tag = "beta" });
        return box;
    }

    /// <summary>The one sentence explaining why so few keys are accepted here.</summary>
    public const string HotkeyAdvice =
        "Click the key button, then press the key you want. Only keys every game detects the same "
        + "way are accepted here: F1 to F15, the keypad, Insert/Delete/Home/End/Page, the arrows, "
        + "Escape, Tab, Space and Enter. In the game itself the mod accepts far more - any key the "
        + "game does not already use - because there it reads your actual keyboard. A key that "
        + "prints a character is detected differently from one game to the next, so it cannot be "
        + "sent from here.";
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
            Refuse("The mod cannot use that key: Unity has no name for its position, so it would "
                   + "never respond. Your previous key was kept.");
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
