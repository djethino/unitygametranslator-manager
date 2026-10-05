using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Manager.Gui;

/// <summary>
/// A paste into any box of one line keeps the words of what was copied, without its line breaks
/// (<see cref="PastedText.OneLine"/>).
///
/// 🔴 **Because Avalonia keeps only what precedes the first line break** (TextBox.SanitizeInputText,
/// 11.3), a copy that BEGINS with one pastes nothing at all, with no sign of why. Seen on
/// 2026-10-05: a game title copied with the line above it, into the game search, gave an empty box.
///
/// ⚠ Installed once for every TextBox of the program, as a class handler: a call per box is
/// something a window forgets, and a box added next year would have the defect again. A box that
/// takes several lines (AcceptsReturn) is left to Avalonia — its breaks are content there.
/// </summary>
public static class SingleLinePaste
{
    private static bool _installed;

    public static void Install()
    {
        if (_installed) return;
        _installed = true;

        TextBox.PastingFromClipboardEvent.AddClassHandler<TextBox>(OnPasting);
    }

    private static async void OnPasting(TextBox box, RoutedEventArgs e)
    {
        if (box.AcceptsReturn || box.IsReadOnly) return;

        var clipboard = TopLevel.GetTopLevel(box)?.Clipboard;
        if (clipboard is null) return;

        // Taken over before the first await: Avalonia reads `Handled` as soon as the event returns.
        e.Handled = true;

        string? copied;
        try
        {
            copied = await clipboard.TryGetTextAsync();
        }
        catch (TimeoutException)
        {
            // What Avalonia's own paste does with a clipboard that does not answer: nothing pasted.
            return;
        }

        var text = PastedText.OneLine(copied ?? "");
        if (text.Length == 0) return;

        // Through the ordinary typing path, as Avalonia's own paste does (HandleTextInput): the
        // selection is replaced, MaxLength holds, and the box's own events fire as for any input.
        box.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = text });
    }
}
