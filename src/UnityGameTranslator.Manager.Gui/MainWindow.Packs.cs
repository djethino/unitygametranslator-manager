using Avalonia.Controls;
using UnityGameTranslator.Manager.Core.Install;
using UnityGameTranslator.Manager.Core.Model;

namespace UnityGameTranslator.Manager.Gui;

/// <summary>
/// A .ugtpack double-clicked in the file explorer (PackFileType): the game it names, or the one the
/// person picks (PackGameWindow), opened on its Assets tab with the pack in it.
///
/// 🔴 **No second way to install a pack.** Everything here ends in HoldAssetFilesAsync — the very
/// call a pack dropped on the tab makes — so the plan, the "Made for…" warning, the refusals and
/// the Apply (N) that writes are the tab's own (user, 2026-09-28).
/// </summary>
public partial class MainWindow
{
    private bool _openingPacks;

    private void OnPackArrived() => _ = OpenArrivedPacksAsync();

    /// <summary>
    /// Opens what is waiting, one pack after the other. Nothing before the first search for
    /// games: there would be no game to offer, and the end of that search calls this again.
    /// </summary>
    private async Task OpenArrivedPacksAsync()
    {
        if (!_scannedOnce || _openingPacks) return;

        _openingPacks = true;
        try
        {
            while (PackInbox.TryTake(out var path)) await OpenPackAsync(path);
        }
        finally
        {
            _openingPacks = false;
        }
    }

    private async Task OpenPackAsync(string path)
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();

        var name = Path.GetFileName(path);
        var (manifest, refusal) = await Task.Run(() => (GameAssets.ReadManifest(path, out var why), why));

        if (manifest is null)
        {
            await ConfirmationWindow.TellAsync(this, $"{name} cannot be opened", refusal);
            return;
        }

        var games = _games.Where(g => g.IsModdable).ToList();
        var target = PackTargets.For(manifest, games);

        var game = target.Found
                   ?? await PackGameWindow.ChooseAsync(this, name, manifest.GameName, target.Likely, games);
        if (game is null) return;

        await ShowOnAssetsTabAsync(game);

        if (_shownReport is not { } report || !string.Equals(report.Game.Path, game.Path, StringComparison.OrdinalIgnoreCase))
            return;

        // The tab exists once UGT Mod is in the game: before that there is no folder for the pack.
        // Said, and the card left on the tab that installs it — not a pack silently dropped.
        if (!AssetsOffered(report))
        {
            _gameTab = GameTab.Setup;
            await ShowSelectedAsync();
            await ConfirmationWindow.TellAsync(this, $"UGT Mod is not installed in {game.Name}",
                "Install it from the Set up tab, then open the pack again.");
            return;
        }

        await HoldAssetFilesAsync(report, [path]);
    }

    /// <summary>
    /// The game's card, on its Assets tab. A click in the list would reset the tab to Home, so the
    /// list is moved under a guard and the card drawn from here.
    /// </summary>
    private async Task ShowOnAssetsTabAsync(GameInstall game)
    {
        var another = _selected is null || !string.Equals(_selected.Path, game.Path, StringComparison.OrdinalIgnoreCase);

        _restoringSelection = true;
        GameList.SelectedItem = null;
        SelectByPath(game.Path);
        _restoringSelection = false;

        // Another game's card goes first, so this one opens as a new page rather than being
        // redrawn "in place" over it. A game hidden by the list's filter is still shown: the card
        // follows _selected when the list has no selection.
        if (another) ClearDetail();
        _selected = game;

        _gameTab = GameTab.Assets;
        _openBlocks.Clear();
        await ShowSelectedAsync();
    }
}
