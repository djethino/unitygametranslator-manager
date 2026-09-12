namespace UnityGameTranslator.Manager.Gui;

/// <summary>
/// One entry in a dropdown: the value that gets written, and the words somebody reads.
///
/// 🔴 **Data, never a Control**, and that is the whole reason this type exists. Every list in this
/// program used to be filled with `ComboBoxItem`s — a control per entry — and a control belongs to
/// exactly one place in the visual tree. A dropdown draws its selected entry a SECOND time, on its
/// closed face, so the same control was being asked to appear twice: it showed in one and vanished
/// from the other. The language lists carried a note about it and their own type; every other list
/// carried the bug quietly, because a plain word does not look wrong when it is drawn once.
///
/// ⚠ <see cref="ToString"/> is the label on purpose: a picker with no template falls back to it, so
/// an ordinary list of words needs no template at all. A list that shows a flag or an icon sets
/// <see cref="SearchPicker.ItemTemplate"/> and this stays exactly as useful — the template reads
/// <see cref="Label"/> itself.
/// </summary>
/// <param name="Tag">What the settings file stores. Never shown.</param>
/// <param name="Label">What is read on screen. Never stored.</param>
public sealed record Choice(string Tag, string Label)
{
    public override string ToString() => Label;
}
