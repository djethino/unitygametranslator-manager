using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using UnityGameTranslator.Common;
using UnityGameTranslator.Manager.Core;
using UnityGameTranslator.Manager.Core.Install;
using UnityGameTranslator.Manager.Core.Model;
using static UnityGameTranslator.Manager.Gui.Ui;

namespace UnityGameTranslator.Manager.Gui;

/// <summary>
/// The Assets tab: fonts and replacement images for one game — added by dropping files or a
/// `.ugtpack`, and exported as one. Design and the user's decisions: analyse/manager-onglet-assets.md.
///
/// 🔴 **Dropping is choosing, not acting** (.claude/rules/manager-ui.md §1). What is dropped becomes
/// a list held for the session — what would arrive, what would replace a file already in the game —
/// and `Apply (N)` writes it, like every other block of the card. The action bar counts it too: its
/// Undo drops it and the one-click carries it out. A replacement is never taken by default: the
/// file already there may be a picture somebody retouched by hand.
/// </summary>
public partial class MainWindow
{
    /// <summary>Files handed to one game and not written yet, with the replacements somebody accepted.</summary>
    private sealed class HeldAssets
    {
        public List<string> Paths { get; } = new();
        public AssetPlan Plan { get; set; } = AssetPlan.Empty;

        /// <summary>Offer keys whose replacement was ticked. Everything else that already exists stays.</summary>
        public HashSet<string> Replace { get; } = new(StringComparer.Ordinal);

        /// <summary>What Apply writes: every new offer, and the replacements ticked.</summary>
        public List<AssetOffer> Accepted =>
            Plan.Offers.Where(o => o.Change == AssetChange.Add
                                   || (o.Change == AssetChange.Replace && Replace.Contains(o.Key)))
                       .ToList();
    }

    /// <summary>By game path. ⚠ Session only — a drop nobody applied does not survive the window.</summary>
    private readonly Dictionary<string, HeldAssets> _pendingAssets = new(StringComparer.OrdinalIgnoreCase);

    private int PendingAssetCount(string gamePath) =>
        _pendingAssets.TryGetValue(gamePath, out var held) ? held.Accepted.Count : 0;

    /// <summary>
    /// Whether the tab is offered: UGT Mod is in this game, so there is a folder the mod reads fonts
    /// and images from. Before that, there is nothing for them to belong to.
    /// </summary>
    private bool AssetsOffered(GameReport report) =>
        report.InstalledPluginVersion is not null && InstalledDescriptor(report) is not null;

    // ── The page ─────────────────────────────────────────────────────────────────────────────

    private IEnumerable<Control> GameAssetsPage(GameReport report)
    {
        var descriptor = InstalledDescriptor(report);
        if (descriptor is null) yield break;

        var state = GameAssets.Read(report.Game.Path, descriptor);

        // What somebody comes here to do first, first. What is already there follows, then sharing.
        yield return Card(AddAssetsBlock(report));
        yield return Card(FontsBlock(state));
        yield return Card(ImagesBlock(state));
        yield return Card(ExportBlock(report, descriptor, state));
    }

    private Control AddAssetsBlock(GameReport report)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(SectionTitle("Add fonts and images"));
        panel.Children.Add(Intro("Fonts become available in UGT Mod. Images replace pictures in the game."));

        // One answer for every write into this game: running, or set up under another account.
        var refusal = WriteRefusal(report);

        var add = new Button { Content = "Add files...", IsEnabled = refusal is null };
        add.Click += async (_, _) => await PickAssetFilesAsync(report);

        panel.Children.Add(DropZone(report, add, refusal is null));

        if (refusal is not null) panel.Children.Add(Note(refusal, Tone.Warning));

        if (_pendingAssets.TryGetValue(report.Game.Path, out var held))
        {
            foreach (var control in HeldAssetsView(report, held, refusal)) panel.Children.Add(control);
        }

        return panel;
    }

    /// <summary>
    /// Where files are dropped — the whole block, not a target to aim at. The button beside the
    /// words is the same act for somebody who would rather browse.
    /// </summary>
    private Control DropZone(GameReport report, Button add, bool allowed)
    {
        var edge = new Rectangle
        {
            Stroke = Brush("BorderSubtle"),
            StrokeThickness = 1.5,
            StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 4, 3 },
            RadiusX = 6,
            RadiusY = 6,
        };

        var words = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        words.Children.Add(new TextBlock
        {
            Text = "Drop fonts (.ttf, .otf), images (.png) or asset packs (.ugtpack) here",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Foreground = Brush(allowed ? "TextSecondary" : "TextMuted"),
        });
        add.HorizontalAlignment = HorizontalAlignment.Center;
        words.Children.Add(add);

        var zone = new Panel
        {
            MinHeight = 96,
            Background = Brushes.Transparent,   // a transparent background is what makes the whole area a drop target
            Children =
            {
                edge,
                new Border { Padding = new Thickness(16, 14), VerticalAlignment = VerticalAlignment.Center, Child = words },
            },
        };

        if (!allowed) return zone;

        DragDrop.SetAllowDrop(zone, true);

        void Lit(bool on) => edge.Stroke = Brush(on ? "AccentEdge" : "BorderSubtle");

        // ⚠ While dragging, only whether files are coming — which ones is read on the drop, and each
        // file that is not a font, an image or a pack is then said, never silently refused here.
        static bool CarriesFiles(DragEventArgs e) => e.DataTransfer.Formats.Contains(DataFormat.File);

        zone.AddHandler(DragDrop.DragEnterEvent, (_, e) => Lit(CarriesFiles(e)));
        zone.AddHandler(DragDrop.DragLeaveEvent, (_, _) => Lit(false));
        zone.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            e.DragEffects = CarriesFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
        });
        zone.AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            Lit(false);
            var paths = DroppedFiles(e);
            if (paths.Count > 0) await HoldAssetFilesAsync(report, paths);
        });

        return zone;
    }

    private static List<string> DroppedFiles(DragEventArgs e) =>
        e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().Where(File.Exists).ToList() ?? [];

    private async Task PickAssetFilesAsync(GameReport report)
    {
        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add fonts and images",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("Fonts, images and asset packs")
                {
                    Patterns = [.. AssetPacks.FontExtensions.Concat(AssetPacks.ImageExtensions)
                                                 .Append(AssetPacks.Extension).Select(e => "*" + e)],
                },
            ],
        });

        var paths = picked.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count > 0) await HoldAssetFilesAsync(report, paths);
    }

    /// <summary>
    /// Adds dropped files to what is held for this game and plans them again, together — so a font
    /// dropped twice counts once and the latest copy wins. Writes nothing.
    /// </summary>
    private async Task HoldAssetFilesAsync(GameReport report, IReadOnlyList<string> paths)
    {
        var descriptor = InstalledDescriptor(report);
        if (descriptor is null) return;

        if (!_pendingAssets.TryGetValue(report.Game.Path, out var held))
        {
            held = new HeldAssets();
            _pendingAssets[report.Game.Path] = held;
        }

        foreach (var path in paths)
        {
            held.Paths.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            held.Paths.Add(path);
        }

        // ⚠ Off this thread: a pack of a few hundred pictures is read and hashed whole.
        Working("Reading the files...");
        try
        {
            var all = held.Paths.ToList();
            held.Plan = await Task.Run(() => GameAssets.Plan(report.Game, descriptor, all));
        }
        finally
        {
            WorkEnded();
        }

        // Nothing to write and nothing to say: the drop leaves no trace.
        if (held.Plan.Offers.Count == 0 && held.Plan.Refused.Count == 0) _pendingAssets.Remove(report.Game.Path);

        await ShowSelectedAsync();
    }

    /// <summary>What was dropped: a summary with its two buttons, then each row, then what was left out.</summary>
    private IEnumerable<Control> HeldAssetsView(GameReport report, HeldAssets held, string? refusal)
    {
        var plan = held.Plan;
        var adds = plan.Offers.Count(o => o.Change == AssetChange.Add);
        var replaces = plan.Offers.Where(o => o.Change == AssetChange.Replace).ToList();
        var same = plan.Offers.Count(o => o.Change == AssetChange.Same);

        var summary = new List<string>();
        if (adds > 0) summary.Add($"{adds} new");
        if (replaces.Count > 0) summary.Add($"{replaces.Count} already in this game and different");
        if (same > 0) summary.Add($"{same} already in this game");
        if (plan.Refused.Count > 0) summary.Add($"{plan.Refused.Count} left out");

        // The rule above the list, then the decision: on a long pack the buttons stay in view.
        yield return new Border
        {
            Height = 1,
            Background = Brush("BorderSubtle"),
            Margin = new Thickness(0, 4, 0, 0),
        };

        var apply = ScopeMark.Marked(EditSide.Local, "Apply", enabled: false);
        apply.Classes.Add("primary");

        void RefreshApply()
        {
            var count = held.Accepted.Count;
            ScopeMark.SetLabel(apply, count > 0 ? $"Apply ({count})" : "Apply");
            ToolTip.SetTip(apply, count > 0
                ? $"Writes {Composition.Amount(count, "font or image", "fonts and images")} into this game."
                : "Nothing to add.");

            // Last, so the refusal replaces the tooltip above rather than the reverse.
            apply.IsEnabled = count > 0 && refusal is null;
            if (refusal is not null) ToolTip.SetTip(apply, refusal);
        }

        apply.Click += async (_, _) => await ApplyHeldAssetsAsync(report);

        var undo = new Button { Content = "Undo" };
        ToolTip.SetTip(undo, "Clears this list. Nothing in the game is changed.");
        undo.Click += async (_, _) =>
        {
            _pendingAssets.Remove(report.Game.Path);
            await ShowSelectedAsync();
        };

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        head.Children.Add(new TextBlock
        {
            Text = string.Join(" · ", summary),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush("TextSecondary"),
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(undo);
        buttons.Children.Add(apply);
        Grid.SetColumn(buttons, 1);
        head.Children.Add(buttons);
        yield return head;

        // A pack made from another game: said once, above the rows, and never a refusal.
        foreach (var other in plan.MadeFor)
            yield return Callout($"Made for {other}. Check that it is meant for this game.", Tone.Warning);

        // Declining is the default for a replacement; one box for all of them when there are several.
        var rowBoxes = new List<(CheckBox Box, string Key)>();
        if (replaces.Count > 1)
        {
            var all = new CheckBox
            {
                Content = $"Replace the files already in this game ({replaces.Count})",
                FontSize = 12,
                IsChecked = replaces.All(r => held.Replace.Contains(r.Key)),
            };

            all.IsCheckedChanged += (_, _) =>
            {
                var on = all.IsChecked == true;
                foreach (var (box, _) in rowBoxes) box.IsChecked = on;
            };

            yield return all;
        }

        var rows = new StackPanel { Spacing = 6 };
        foreach (var offer in plan.Offers.OrderBy(o => o.Change == AssetChange.Same).ThenBy(o => o.Kind).ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase))
            rows.Children.Add(OfferRow(offer, held, rowBoxes, () =>
            {
                RefreshApply();

                // The bar at the bottom counts these too, and its one-click carries them out.
                ShowActionBar(report);
            }));

        foreach (var left in plan.Refused)
            rows.Children.Add(Note(left.Name.Length > 0 ? $"{left.Name}: {left.Reason}" : left.Reason, Tone.Warning));

        // ⚠ Bounded: a pack can carry hundreds of pictures, and the cards below must stay reachable.
        yield return new ScrollViewer
        {
            MaxHeight = 320,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = rows,
        };

        RefreshApply();
    }

    private Control OfferRow(AssetOffer offer, HeldAssets held, List<(CheckBox, string)> boxes, Action refreshApply)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };

        var text = new StackPanel { Spacing = 1 };
        text.Children.Add(new TextBlock
        {
            Text = offer.Name,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Brush(offer.Change == AssetChange.Same ? "TextMuted" : "TextPrimary"),
        });
        text.Children.Add(new TextBlock
        {
            Text = (offer.Kind == AssetKind.Font ? "Font" : "Image") + " · from " + offer.From,
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Brush("TextMuted"),
        });
        row.Children.Add(text);

        Control state = offer.Change switch
        {
            AssetChange.Add => new TextBlock { Text = "New", FontSize = 11, Foreground = Brush(TextColour(Tone.Success)) },
            AssetChange.Same => new TextBlock { Text = "Already in this game", FontSize = 11, Foreground = Brush("TextMuted") },
            _ => ReplaceBox(offer, held, boxes, refreshApply),
        };

        state.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(state, 1);
        row.Children.Add(state);

        return row;
    }

    private static CheckBox ReplaceBox(AssetOffer offer, HeldAssets held, List<(CheckBox, string)> boxes, Action refreshApply)
    {
        var box = new CheckBox
        {
            Content = "Replace",
            FontSize = 11,
            IsChecked = held.Replace.Contains(offer.Key),
            Foreground = Brush(TextColour(Tone.Warning)),
        };

        ToolTip.SetTip(box, offer.Kind == AssetKind.Font
            ? "This game already has a different font with this name."
            : "This game already has a different picture or setting for this image. The current one is backed up first.");

        box.IsCheckedChanged += (_, _) =>
        {
            if (box.IsChecked == true) held.Replace.Add(offer.Key);
            else held.Replace.Remove(offer.Key);

            // The count on Apply follows the box, and the bar at the bottom counts it too.
            refreshApply();
        };

        boxes.Add((box, offer.Key));
        return box;
    }

    private async Task ApplyHeldAssetsAsync(GameReport report)
    {
        if (!_pendingAssets.TryGetValue(report.Game.Path, out var held)) return;
        var descriptor = InstalledDescriptor(report);
        if (descriptor is null) return;

        var accepted = held.Accepted;
        if (accepted.Count == 0) return;

        AssetWriteResult result;
        Working("Adding fonts and images...");
        try
        {
            result = await Task.Run(() => GameAssets.Apply(_platform, report.Game, descriptor, accepted));
        }
        finally
        {
            WorkEnded();
        }

        if (!result.Done)
        {
            await MessageAsync("Nothing was changed", result.Failure ?? "The files could not be written.");
            return;
        }

        _pendingAssets.Remove(report.Game.Path);
        Status($"Added {Composition.Amount(accepted.Count, "font or image", "fonts and images")} to {report.Game.Name}.");
        await ShowSelectedAsync();
    }

    // ── What the game holds ──────────────────────────────────────────────────────────────────

    private static Control FontsBlock(GameAssetsState state)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(SectionTitle($"Fonts ({state.Fonts.Count})"));

        if (state.Fonts.Count == 0)
        {
            panel.Children.Add(Note("No fonts added to this game."));
            return panel;
        }

        var list = new StackPanel { Spacing = 4 };
        foreach (var font in state.Fonts)
            list.Children.Add(NameAndDetail(font.Name, SizeOf(font.Length), Tone.Neutral));

        panel.Children.Add(Bounded(list));
        panel.Children.Add(Note("Choose which game font each one replaces in UGT Mod: Translation Tools, Fonts tab."));
        return panel;
    }

    private static Control ImagesBlock(GameAssetsState state)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(SectionTitle($"Replacement images ({state.Images.Count})"));

        if (!state.HasTranslation)
        {
            panel.Children.Add(Note("This game has no translation file yet. Play it once with UGT Mod."));
            return panel;
        }

        if (state.Images.Count == 0)
        {
            panel.Children.Add(Note("No replacement images in this game's translation."));
        }
        else
        {
            var list = new StackPanel { Spacing = 4 };
            foreach (var image in state.Images.OrderBy(i => i.SpriteName, StringComparer.OrdinalIgnoreCase))
            {
                list.Children.Add(image.Present
                    ? NameAndDetail(image.SpriteName, image.File ?? "", Tone.Neutral)
                    : NameAndDetail(image.SpriteName, $"{image.File ?? "no file"} is missing", Tone.Warning));
            }

            panel.Children.Add(Bounded(list));
        }

        panel.Children.Add(Note("To make one: pick the picture in UGT Mod's Element Inspector and use Export Original."));
        return panel;
    }

    private Control ExportBlock(GameReport report, LoaderDescriptor descriptor, GameAssetsState state)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(SectionTitle("Export"));
        panel.Children.Add(Intro("Puts this game's fonts and replacement images in one .ugtpack file, to share or keep."));

        var (fonts, images) = GameAssets.Exportable(state);
        var any = fonts + images > 0;

        var export = new Button { Content = "Export...", IsEnabled = any, HorizontalAlignment = HorizontalAlignment.Left };
        ToolTip.SetTip(export, any
            ? $"{Composition.Amount(fonts, "font", "fonts")}, {Composition.Amount(images, "image", "images")}."
            : "This game has no fonts or images to export.");

        export.Click += async (_, _) => await ExportAssetsAsync(report, descriptor);

        panel.Children.Add(Row(any
            ? $"{Composition.Amount(fonts, "font", "fonts")}, {Composition.Amount(images, "image", "images")}"
            : "Nothing to export", export));
        return panel;
    }

    private async Task ExportAssetsAsync(GameReport report, LoaderDescriptor descriptor)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export fonts and images",
            SuggestedFileName = $"{report.Game.Name} assets{AssetPacks.Extension}",
            DefaultExtension = AssetPacks.Extension.TrimStart('.'),
            FileTypeChoices = [new FilePickerFileType("UGT asset pack") { Patterns = ["*" + AssetPacks.Extension] }],
        });

        if (file?.TryGetLocalPath() is not { } destination) return;

        AssetWriteResult result;
        Working("Exporting fonts and images...");
        try
        {
            result = await Task.Run(() => GameAssets.Export(report.Game, descriptor, destination,
                                                             $"UnityGameTranslator Manager {BuildInfo.Version}"));
        }
        finally
        {
            WorkEnded();
        }

        if (!result.Done)
        {
            await MessageAsync("Nothing was exported", result.Failure ?? "The file could not be written.");
            return;
        }

        Status($"Exported {Composition.Amount(result.Written, "file", "files")} to {System.IO.Path.GetFileName(destination)}.");
    }

    // ── Shapes ───────────────────────────────────────────────────────────────────────────────

    private static Control NameAndDetail(string name, string detail, Tone tone)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        row.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Brush("TextPrimary"),
        });

        var right = new TextBlock
        {
            Text = detail,
            FontSize = 11,
            Margin = new Thickness(12, 0, 0, 0),
            Foreground = Brush(TextColour(tone)),
        };
        Grid.SetColumn(right, 1);
        row.Children.Add(right);
        return row;
    }

    /// <summary>A list that scrolls on its own past a few rows, so the cards under it stay in reach.</summary>
    private static Control Bounded(Control list) => new ScrollViewer
    {
        MaxHeight = 240,
        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        Content = list,
    };

    private static string SizeOf(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / 1024d / 1024d:0.#} MB" : $"{Math.Max(1, bytes / 1024)} KB";
}
