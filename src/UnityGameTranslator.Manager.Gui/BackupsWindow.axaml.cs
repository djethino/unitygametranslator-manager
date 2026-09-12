using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using UnityGameTranslator.Common;
using UnityGameTranslator.Manager.Core.Detection;
using UnityGameTranslator.Manager.Core.Install;
using UnityGameTranslator.Manager.Core.Model;

namespace UnityGameTranslator.Manager.Gui;

/// <summary>
/// This game's translation as it stood at earlier moments.
///
/// 🔴 **Built like Settings and Mod defaults, deliberately.** The first attempt was a window of its
/// own invention — a bare dialog with hand-made headings — and it read as a different program:
/// same product, another designer. This one uses the shape those two established, because a person
/// who has opened one of them has already learnt this one:
///
///   · a sentence at the top saying what the window is about;
///   · cards on <c>SurfaceCard</c>, each a subject, each titled and introduced;
///   · a docked bar at the bottom, on <c>SurfaceBar</c>, buttons to the right;
///   · the verb of a block UNDER the block it acts on, never above it.
///
/// 🔴 **Two cards, because there are two lists**, and they do not live equally long: an automatic
/// copy ages out on its own, a saved one stays until somebody removes it. Rows that look alike but
/// do not survive alike is how people lose what they thought was kept.
///
/// ⚠ Everything a row SAYS comes from <see cref="Backups"/> — the same words the mod's own panel
/// uses over the same folder. Only the drawing belongs here.
/// </summary>
public sealed class BackupsWindow : Window
{
    private readonly GameInstall _game;
    private readonly LoaderDescriptor _descriptor;
    private readonly bool _running;

    /// <summary>
    /// Two rows of equal share: both lists grow when the window does, which is what makes
    /// enlarging it worth doing. Stacked with fixed caps they ignored the extra room and left an
    /// empty band under them.
    /// </summary>
    private readonly Grid _cards = new() { RowDefinitions = new RowDefinitions("*,*") };
    private readonly TextBlock _now = new();

    /// <summary>Whether anything was written, so the caller knows to refresh the card behind.</summary>
    public bool Touched { get; private set; }

    public BackupsWindow(GameInstall game, LoaderDescriptor descriptor, bool running)
    {
        _game = game;
        _descriptor = descriptor;
        _running = running;

        Title = $"{Backups.ScreenTitle} — {game.Name}";
        Width = 760;

        // ⚠ 780, the height Settings gives itself. Shorter, the two lists showed less than two
        // entries each — a list you cannot read two rows of is a list, not a choice.
        Height = 780;
        MinWidth = 660;
        MinHeight = 520;

        // 🔴 **A plain size, and no SizeToContent.** Sizing to the content fought every attempt to
        // enlarge the window — the height was recomputed from the content, so dragging it taller
        // did nothing and only shrinking worked. Settings and Mod defaults give themselves a size
        // and let the person change it; this does the same.
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = this.FindResource("SurfaceBase") as IBrush;

        Content = Build();
        Redraw();
    }

    private static IBrush? Brush(string key) =>
        Avalonia.Application.Current?.FindResource(key) as IBrush;

    private Control Build()
    {
        // 🔴 **No scrollbar around the whole thing.** With one here AND one inside each list there
        // were three, and the outer one appeared as soon as the content ran a few pixels over —
        // so the window both overflowed and could not be grown. The head takes what it needs, the
        // two cards share everything left, and each list scrolls inside its own share. Nothing
        // ever overflows, so no third scrollbar can appear.
        var head = new StackPanel { Spacing = 16 };

        head.Children.Add(new TextBlock
        {
            // ⚠ One line. Every line spent here is a row nobody can see below.
            Text = Backups.PrivacyNote,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("TextSecondary"),
        });

        // 🔴 The current state, above both lists. Without it no row can be read: a line count is
        // neither more nor less until somebody knows where they stand today.
        _now.FontWeight = FontWeight.SemiBold;
        _now.Foreground = Brush("TextPrimary");
        head.Children.Add(_now);

        var body = new Grid
        {
            Margin = new Avalonia.Thickness(24),
            RowDefinitions = new RowDefinitions("Auto,*"),
        };

        Grid.SetRow(head, 0);
        Grid.SetRow(_cards, 1);

        _cards.Margin = new Avalonia.Thickness(0, 16, 0, 0);

        body.Children.Add(head);
        body.Children.Add(_cards);

        var close = new Button { Content = "Close", IsDefault = true, IsCancel = true,
                                 Classes = { "primary" } };
        close.Click += (_, _) => Close();

        // The same docked bar as Settings and Mod defaults: one place the eye already knows to
        // look for the way out.
        var bar = new Border
        {
            Background = Brush("SurfaceBar"),
            BorderBrush = Brush("BorderSubtle"),
            BorderThickness = new Avalonia.Thickness(0, 1, 0, 0),
            Padding = new Avalonia.Thickness(24, 12),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 12,
                HorizontalAlignment = HorizontalAlignment.Right,
                Children = { close },
            },
        };

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(bar);
        root.Children.Add(body);

        return root;
    }

    /// <summary>
    /// Rebuilds both cards from disk.
    ///
    /// 🔴 **In place, never by reopening.** The window used to close and come back after every
    /// act, which showed as a flash and lost the reader's place — a window that blinks each time a
    /// button is pressed reads as something going wrong.
    ///
    /// ⚠ Read from disk each time rather than from what we believe we just did: a restore changes
    /// the line count, and a header describing the state before the act lies for as long as the
    /// window stays open.
    /// </summary>
    private void Redraw()
    {
        var kept = TranslationBackupStore.List(_game.Path, _descriptor);
        var local = LocalTranslationProbe.Read(_game.Path, _descriptor);

        // ⚠ The sentence comes from the socle, so the mod's panel and this window head the same
        // screen identically. Written here, this said "no translation yet" on a MISSING file and
        // "Now: 0 lines" on an empty one — two answers to one question — and "Now: -1 lines" on a
        // file too damaged to count.
        var lines = local?.EntryCount ?? 0;
        _now.Text = Backups.NowLine(lines);

        _cards.Children.Clear();

        var saved = SavedCard(kept, lines);
        var automatic = AutomaticCard(kept);

        ((Border)automatic).Margin = new Avalonia.Thickness(0, 16, 0, 0);

        Grid.SetRow(saved, 0);
        Grid.SetRow(automatic, 1);

        _cards.Children.Add(saved);
        _cards.Children.Add(automatic);

        ShareTheHeight(kept);
    }

    /// <summary>
    /// Gives each card the height it has content for, and the leftover to whichever list is still
    /// scrolling.
    ///
    /// 🔴 **The two rows used to be `*,*` — strictly equal, whatever they held.** Three saved
    /// copies took half a tall window and drew a gap under the third, while the eight automatic
    /// ones beside them were still scrolling. The rule for who deserves what is in the socle
    /// (ListShares), because the mod's panel asks it of the same two lists; only the rows are ours.
    ///
    /// ⚠ `Auto` for a list that fits, a weighted `*` for one that does not, and a final `*` that
    /// collects what neither needs — without that last row the layout hands the spare height back
    /// to the cards, which is the gap again.
    /// </summary>
    private void ShareTheHeight(IReadOnlyList<BackupEntry> kept)
    {
        var savedRows = kept.Count(e => e.IsSaved);
        var autoRows = kept.Count - savedRows;

        var wants = new List<double>();
        if (savedRows > 0) wants.Add(RowSpace * savedRows + CardChrome);
        if (autoRows > 0) wants.Add(RowSpace * autoRows + CardChrome);

        // Not laid out yet: ListShares answers "each asks for its own content", which is what a
        // window that has not been measured should do.
        var room = _cards.Bounds.Height;
        var shares = ListShares.Split(wants, room);

        var rows = new RowDefinitions();
        var next = 0;

        rows.Add(Row(savedRows > 0 ? shares[next++] : default, savedRows > 0));
        rows.Add(Row(autoRows > 0 ? shares[next] : default, autoRows > 0));

        // ⚠ Only when nobody is scrolling: a spare row beside a list that still has rows to show
        // would take room away from the one thing that needs it.
        if (shares.TrueForAll(s => s.Weight <= 0)) rows.Add(new RowDefinition(GridLength.Star));

        _cards.RowDefinitions = rows;
    }

    private static RowDefinition Row(ListShare share, bool hasRows)
    {
        if (!hasRows || share.Weight <= 0) return new RowDefinition(GridLength.Auto);

        return new RowDefinition(new GridLength(share.Weight, GridUnitType.Star));
    }

    /// <summary>What one row comes to, and the card's own heading and padding around the list.</summary>
    private const double RowSpace = 62;

    private const double CardChrome = 96;

    /// <param name="lines">
    /// What the game holds right now. ⚠ It decides whether Backup may be pressed, and the socle
    /// decides that — this window used to carry its own half of the answer (a `hasTranslation` flag
    /// and a sentence of its own) while the mod's panel had none, so the same empty game offered the
    /// button in one product and refused it in the other, in two different sets of words.
    /// </param>
    private Control SavedCard(IReadOnlyList<BackupEntry> kept, int lines)
    {
        var saved = Backups.SavedCount(kept);
        var why = Backups.WhyCannotSave(kept, lines);

        // ⚠ A grid, not a stack: a StackPanel gives each child its natural height, so the list
        // would keep its own and spill out of the card instead of scrolling inside it. The list
        // takes the room, the verb keeps its line under it.
        var body = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };

        var list = Rows(kept, wantSaved: true,
            empty: "No backups yet. Take one before you try something, and you can walk back out "
                 + "of whatever you try.");

        Grid.SetRow(list, 0);
        body.Children.Add(list);

        // 🔴 **Under the list it fills, not above it.** Every verb in this product sits below the
        // zone it acts on and to the right — the Apply of a settings block, the Apply of a hotkey.
        // Above, it read as a heading for the list rather than an act upon it.
        // ⚠ `Backup`, one verb, nothing after it — the window is named "Translation backups", so
        // the subject is already written above and repeating it in the button says it twice.
        var save = ScopeMark.Marked(EditSide.Local, "Backup",
                                    enabled: why is null && !_running);
        save.Classes.Add("primary");

        // ⚠ Never a control that cannot be pressed without words saying which reason applies. The
        // running game comes first: it is the one refusal this window owns, and it outranks the
        // others because nothing can be written at all while the files are locked.
        ToolTip.SetTip(save, _running
            ? $"{_game.Name} is running, so its files are locked."
            : why ?? "Backs up the translation as it stands, with the fonts and images it uses.");

        // ⚠ Off the UI thread and behind a busy button, like the three verbs on each row. Copying a
        // translation with its fonts and images is not instant, and it used to run right here —
        // the window froze for the length of it and could not have drawn an indicator if it had
        // had one.
        Busy.OnClick(save, async () =>
        {
            var made = await Task.Run(() => TranslationBackupStore.SaveCopy(_game.Path, _descriptor));
            if (made is not null) Touched = true;
            Redraw();
        });

        var verb = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Avalonia.Thickness(0, 10, 0, 0),
            Children = { save },
        };

        Grid.SetRow(verb, 1);
        body.Children.Add(verb);

        return Card(Backups.SavedHeading,
                    $"{saved} of {Backups.SavedKept} — these stay until you delete one.", body);
    }

    private Control AutomaticCard(IReadOnlyList<BackupEntry> kept)
    {
        var body = Rows(kept, wantSaved: false,
            empty: "Nothing yet. One is taken whenever something replaces this game's translation.");

        return Card(Backups.AutomaticHeading,
                    $"The last {Backups.AutomaticKept} taken before something replaced this game's "
                    + "translation — the oldest goes as a new one arrives. Keep holds on to one.",
                    body);
    }

    /// <summary>
    /// The rows of one list, scrolling inside whatever room its card was given.
    ///
    /// 🔴 Its own scroll area, never a shared one: ten rows in a single scroller push the second
    /// card below the fold, and scrolling to reach it loses the first — which is the list this
    /// window exists to compare against.
    /// </summary>
    private Control Rows(IReadOnlyList<BackupEntry> kept, bool wantSaved, string empty)
    {
        var rows = new StackPanel { Spacing = 4 };
        var any = false;

        foreach (var entry in kept)
        {
            if (entry.IsSaved != wantSaved) continue;
            any = true;
            rows.Children.Add(Row(entry, kept));
        }

        if (!any)
        {
            rows.Children.Add(new TextBlock
            {
                Text = empty,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("TextMuted"),
            });

            return rows;
        }

        return new ScrollViewer
        {
            Content = rows,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
    }

    /// <summary>
    /// One copy: what it is, why it exists, and what may be done with it.
    ///
    /// 🔴 **Two lines at most, and the verbs share the first one.** Stacked — facts, then reason,
    /// then a row of buttons — a copy took four lines, and the lists showed less than two entries
    /// each. A list you cannot read two rows of is not a list, it is a keyhole.
    /// </summary>
    /// <summary>
    /// What this copy translates, in two flags — or nothing when neither is known.
    ///
    /// 🔴 **Two flags and an arrow, never the names.** A row already carries a date, a count of
    /// lines and sometimes a name; adding "English → French" in words turns a list of ten into a
    /// wall of text, when the question is only "which one of mine is this".
    ///
    /// ⚠ A source that was never settled gets the arrow with nothing before it, because that IS
    /// the fact: the copy was taken before anybody said what it translates from. Writing "Auto"
    /// there would dress an absence up as an answer.
    ///
    /// ⚠ Identical to the mod's row, deliberately — this is one list of one folder shown in two
    /// windows, and whether "auto" counts as a language is answered once, in the socle.
    /// </summary>
    private static Control? Languages(BackupEntry entry)
    {
        bool source = Backups.IsSettledLanguage(entry.SourceLanguage);
        bool target = Backups.IsSettledLanguage(entry.TargetLanguage);

        if (!source && !target) return null;

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (source && LanguageMark.For(entry.SourceLanguage) is { } from) row.Children.Add(from);

        row.Children.Add(new TextBlock
        {
            Text = "→",
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush("TextMuted"),
        });

        if (target && LanguageMark.For(entry.TargetLanguage) is { } into) row.Children.Add(into);

        return row;
    }

    private Control Row(BackupEntry entry, IReadOnlyList<BackupEntry> all)
    {
        // 🔴 **What identifies stays on the first line; what qualifies goes underneath, small.**
        // Everything used to be strung onto one line, which grew past its column and ran under
        // the buttons — the saved list showed it first, carrying three verbs instead of two. Cut
        // with an ellipsis it lost the end; split in two it loses nothing, and the row reads the
        // way the rows below it already did.
        // 🔴 **A NAME identifies; a date only qualifies.** Both products wrote the name in the
        // smallest size and the dimmest colour, on the second line, under the date — so the one
        // thing somebody scans a list of ten dated rows for was the hardest thing on the row to
        // read. The rename help in the mod says it outright: a name is what makes one of them
        // findable.
        //
        // ⚠ The grammar of the row does not change, only what fills each slot. Kept identical to
        // the mod's, deliberately: this is one list of one folder shown in two windows.
        string named = !string.IsNullOrEmpty(entry.Label) ? entry.Label : null;
        var facts = $"{entry.At:dd MMM HH:mm}   {entry.Lines} lines";

        var details = new List<string>();

        // Named, the date joins the qualifiers. Unnamed, the act that caused it takes their first
        // place — an unnamed saved copy says nothing here, since "Saved by you" is the title of
        // the very card it sits in, repeated on every row.
        if (named != null) details.Add(facts);
        else if (!entry.IsSaved) details.Add(Backups.Describe(entry.Reason, entry.By));

        if (entry.ByHand > 0) details.Add($"{entry.ByHand} by hand");
        if (entry.WithAssets) details.Add("with fonts and images");

        // ⚠ **Trimmed, and clipped to its column.** A Grid does not clip its children, and a
        // TextBlock with neither wrapping nor trimming simply paints past the edge — so a long
        // line ran underneath the buttons beside it. It showed on the saved list first, which
        // carries three verbs instead of two and therefore has the least room.
        var text = new StackPanel
        {
            Spacing = 1,
            VerticalAlignment = VerticalAlignment.Center,
            ClipToBounds = true,
            Margin = new Avalonia.Thickness(0, 0, 10, 0),
        };

        text.Children.Add(new TextBlock
        {
            Text = named ?? facts,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Brush("TextPrimary"),
        });

        if (Languages(entry) is { } pair) text.Children.Add(pair);

        // 🔴 The one restore nothing can undo, said where the counts are and not in small print:
        // this copy is a different translation, not an earlier version of the one in place.
        if (Backups.IsAnotherLineage(entry.Uuid, LocalUuid()))
        {
            text.Children.Add(new TextBlock
            {
                Text = Backups.AnotherLineageNote,
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = Brush("StatusWarning"),
            });
        }

        // ⚠ Absent entirely when there is nothing to say, rather than an empty line: a copy
        // somebody took a second ago, unnamed and with no assets, is one line and no more.
        if (details.Count > 0)
        {
            text.Children.Add(new TextBlock
            {
                Text = string.Join(" · ", details),
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = Brush("TextSecondary"),
            });
        }

        // ⚠ **Only Restore carries a scope mark, and that is not an omission.** The mark answers
        // "where does the result land": Restore lands in the game, so it is Local. Rename, Delete
        // and Keep touch nothing but the backup folder, and marking them would announce a change
        // to a game that is not happening.
        var verbs = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var restore = ScopeMark.Marked(EditSide.Local, "Restore", enabled: !_running);
        ToolTip.SetTip(restore, _running
            ? $"{_game.Name} is running, so its files are locked."
            : "Puts this backup into the game. What is there now is backed up first, so this can "
              + "be walked back.");

        // 🔴 **Asked, exactly as the mod asks it.** This window and the mod's panel look at the same
        // folder; one of them removed a copy on the click while the other asked first. Two screens
        // onto one folder disagreeing about whether losing work deserves a question is not a
        // difference of style — and the one that did not ask was the one where the pointer is
        // already moving between rows.
        //
        // ⚠ The words come from `Backups`, not from here. Written twice they drift, and the drift
        // is invisible: nobody has both dialogs open at once to notice.
        restore.Click += async (_, _) =>
        {
            var nowLines = LocalTranslationProbe.Read(_game.Path, _descriptor)?.EntryCount ?? 0;

            if (!await ConfirmationWindow.AskAsync(
                    this, Backups.ConfirmRestoreTitle,
                    Backups.ConfirmRestoreBody(entry.Lines, nowLines,
                                               entry.At.ToString("dd MMM HH:mm"),
                                               Backups.IsAnotherLineage(entry.Uuid, LocalUuid())),
                    Backups.ConfirmRestoreVerb))
            {
                return;
            }

            // ⚠ Busy AFTER the confirmation, not around it: a button held busy while a dialog is
            // open would say the program is working when it is waiting for an answer.
            await Busy.While(restore, () =>
                ActAsync(() => TranslationBackupStore.Restore(_game.Path, _descriptor, entry.Id),
                         "This backup could not be put back"));
        };

        verbs.Children.Add(restore);

        if (entry.IsSaved)
        {
            var rename = new Button { Content = "Rename", FontSize = 12 };
            ToolTip.SetTip(rename, "Ten dated rows are not a choice. A name makes one findable.");
            rename.Click += async (_, _) =>
            {
                if (await AskNameAsync(entry)) Touched = true;
                Redraw();
            };
            verbs.Children.Add(rename);

            var delete = new Button { Content = "Delete", FontSize = 12 };
            ToolTip.SetTip(delete, "Deletes this backup and frees a slot. Nothing else is touched.");
            // ⚠ The one act on this window nothing puts back — the others all leave a way out.
            delete.Click += async (_, _) =>
            {
                if (!await ConfirmationWindow.AskAsync(
                        this, Backups.ConfirmDeleteTitle,
                        Backups.ConfirmDeleteBody(NameFor(entry), entry.Lines),
                        Backups.ConfirmDeleteVerb))
                {
                    return;
                }

                await Busy.While(delete, () =>
                    ActAsync(() => TranslationBackupStore.Delete(_game.Path, _descriptor, entry.Id),
                             "This backup could not be deleted"));
            };
            verbs.Children.Add(delete);
        }
        else
        {
            // ⚠ The gesture that closes the loop between the two cards: recognise the one worth
            // having before it ages out, and it stops ageing.
            // ⚠ Two reasons it may be impossible, and both are known BEFORE the click: the slots
            // are full, or this very copy is already kept. Keeping copies rather than moves now,
            // so the row stays in the automatic list with its button — and a button that can only
            // refuse must say so instead of waiting to be pressed.
            var already = Backups.AlreadyKept(all, entry);

            var keep = new Button
            {
                Content = "Keep",
                FontSize = 12,
                IsEnabled = !already && Backups.CanSaveAnother(all),
            };

            // ⚠ The slot ceiling alone: this duplicates a backup that already holds lines, so how
            // many the game holds today has no say in it — which is why WhyNoRoom is a question of
            // its own and not part of WhyCannotSave.
            ToolTip.SetTip(keep, already
                                 ? Backups.AlreadyKeptHint
                                 : Backups.WhyNoRoom(all)
                                   ?? $"Copies it into {Backups.SavedHeading}, so it stops ageing "
                                      + "out. This one stays where it is.");

            Busy.OnClick(keep, () =>
                ActAsync(() => TranslationBackupStore.Keep(_game.Path, _descriptor, entry.Id),
                         "This backup could not be kept"));

            verbs.Children.Add(keep);
        }

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };

        Grid.SetColumn(text, 0);
        Grid.SetColumn(verbs, 1);
        grid.Children.Add(text);
        grid.Children.Add(verbs);

        return new Border
        {
            Background = Brush("SurfaceInput"),
            BorderBrush = Brush("BorderSubtle"),
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(4),
            Padding = new Avalonia.Thickness(10, 6),
            Child = grid,
        };
    }

    /// <summary>
    /// Run one of the store's writes and report it when it did not happen.
    ///
    /// 🔴 **It used to take an Action, so every one of these returned a bool into nothing.** Keep,
    /// Delete and Restore can all refuse — a file that has moved, a name already taken, a folder
    /// that cannot be written — and the window simply redrew, unchanged, with no word. Somebody
    /// pressed Keep, watched nothing happen, and pressed it on another row to see whether the
    /// button worked at all. Restore is the one that mattered most: believing a file was put back
    /// when it was not is how the next act is taken on the wrong file.
    ///
    /// ⚠ Touched only on success: it is what tells the caller the game has to be re-read, and a
    /// write that did not happen has nothing to re-read.
    /// </summary>
    /// <summary>
    /// Performs one of this window's writes and redraws what it changed.
    ///
    /// 🔴 **The write runs OFF the UI thread, and that is a correction rather than a refinement.**
    /// This method was `async` and its body was entirely synchronous: `write()` copied files on the
    /// thread that draws, so the window froze for the length of every restore, delete and keep.
    /// Reported as "ça freeze plutôt que de me mettre notre loading" — and an indicator would not
    /// have helped, because a frozen window draws nothing at all. That is the trap: the missing
    /// spinner is the visible half of a defect whose other half is that it could not have shown.
    ///
    /// ⚠ What runs over there touches no control: TranslationBackupStore is files and nothing else.
    /// The redraw is back here, after the await, on the thread that owns the window.
    /// </summary>
    private async Task ActAsync(Func<bool> write, string couldNot)
    {
        var done = await Task.Run(write);
        if (done) Touched = true;

        Redraw();

        if (!done)
        {
            await ConfirmationWindow.TellAsync(this, couldNot,
                "Nothing was changed. The backup folder may have been moved or written to by "
                + "something else while this window was open — close it and open it again to see "
                + "what is actually there.");
        }
    }

    private string? LocalUuid() => LocalTranslationProbe.Read(_game.Path, _descriptor)?.Uuid;

    /// <summary>
    /// How a backup is referred to in a question about it.
    ///
    /// ⚠ Its name when it has one, its date otherwise — never "this backup". Somebody holding ten
    /// of them has to recognise WHICH one is about to go, and the dialog is the last place that can
    /// still be told apart from the row underneath the pointer.
    /// </summary>
    private static string NameFor(BackupEntry entry) =>
        string.IsNullOrEmpty(entry.Label)
            ? $"from {entry.At:dd MMM HH:mm}"
            : "\"" + entry.Label + "\"";

    /// <summary>
    /// Asks what to call a backup. True when something was written.
    ///
    /// ⚠ Owned by this window rather than by the main one: a dialog whose owner sits behind
    /// another modal opens behind it on some window managers, which reads as a frozen program.
    /// </summary>
    private async Task<bool> AskNameAsync(BackupEntry entry)
    {
        var field = new TextBox
        {
            Text = entry.Label ?? "",
            Watermark = "What is this one?",
            MinWidth = 320,
        };

        // 🔴 **Enter saves here, where every confirmation in this program has it cancel.** That
        // convention is deliberate and right for a dialog asking a DECISION — nobody should delete
        // a translation by leaning on Enter, and ConfirmationWindow argues it at length. This one
        // asks for a VALUE: somebody types a name and presses Enter, as they do in every program
        // ever written, and it threw the name away.
        //
        // ⚠ The two flags answer two different keys and only one of them was ever in question:
        // Escape still cancels.
        var save = new Button { Content = "Save", Classes = { "primary" }, IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };

        var layout = new StackPanel { Spacing = 14, Margin = new Avalonia.Thickness(24) };

        layout.Children.Add(new TextBlock
        {
            Text = "Name this backup",
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush("TextPrimary"),
        });

        layout.Children.Add(new TextBlock
        {
            Text = $"The backup from {entry.At:dd MMM HH:mm}, {entry.Lines} lines.",
            FontSize = 12,
            Foreground = Brush("TextSecondary"),
        });

        layout.Children.Add(field);
        layout.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { cancel, save },
        });

        var dialog = new Window
        {
            Title = "Name this backup",
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brush("SurfaceBase"),
            Content = layout,
        };

        var written = false;

        save.Click += (_, _) =>
        {
            TranslationBackupStore.Rename(_game.Path, _descriptor, entry.Id, field.Text);
            written = true;
            dialog.Close();
        };

        cancel.Click += (_, _) => dialog.Close();

        await dialog.ShowDialog(this);
        return written;
    }

    /// <summary>
    /// The card Settings and Mod defaults use, to the pixel.
    ///
    /// ⚠ Copied rather than shared because those two keep private copies of it as well — a third
    /// is the point at which it should become one control, and that refactor belongs to all three
    /// at once rather than to whichever window is being written today.
    /// </summary>
    private static Control Card(string title, string? intro, Control content)
    {
        var body = new StackPanel { Spacing = 10 };

        body.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeight.SemiBold,
            FontSize = 14,
            Foreground = Brush("TextPrimary"),
        });

        if (intro is not null)
        {
            body.Children.Add(new TextBlock
            {
                Text = intro,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("TextMuted"),
            });
        }

        // ⚠ The list is the child that grows: the title and the intro take what they need, the
        // rows take the rest. Without this the card stretches and the list keeps its own height,
        // leaving a band of empty card under it.
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };

        var top = new StackPanel { Spacing = 10 };
        while (body.Children.Count > 0)
        {
            var child = body.Children[0];
            body.Children.RemoveAt(0);
            top.Children.Add(child);
        }

        Grid.SetRow(top, 0);
        Grid.SetRow(content, 1);
        grid.Children.Add(top);
        grid.Children.Add(content);

        return new Border
        {
            Background = Brush("SurfaceCard"),
            BorderBrush = Brush("BorderSubtle"),
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(8),
            Padding = new Avalonia.Thickness(16),
            Child = grid,
        };
    }
}
