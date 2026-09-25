using System.Text.Json;
using UnityGameTranslator.Manager.Core.Platform;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Manager.Core.Install;

/// <summary>What the imported interface file holds, read from the file itself.</summary>
/// <param name="Language">Its `_target_language` — the language of the interface it translates.</param>
/// <param name="Lines">How many interface lines it carries.</param>
public sealed record ModUiFile(string Language, int Lines, DateTime ImportedUtc);

/// <summary>
/// The interface file as one game's Set up shows it: what the game holds, what Mod defaults keeps,
/// and whether the kept one could go there.
/// </summary>
/// <param name="InGame">The game's own file, or null when it has none.</param>
/// <param name="Kept">The file kept in Mod defaults, or null when none was imported.</param>
/// <param name="KeptFits">Whether the kept file is in the language this game translates into.</param>
/// <param name="Same">Whether the game's file is byte for byte the kept one — nothing to replace.</param>
/// <param name="TargetLanguage">The language this game translates into.</param>
public sealed record ModUiView(ModUiFile? InGame, ModUiFile? Kept, bool KeptFits, bool Same, string TargetLanguage)
{
    /// <summary>Whether Replace means anything here: both files, the kept one fits, and they differ.</summary>
    public bool Replaceable => InGame is not null && KeptFits && !Same;
}

/// <summary>
/// The ONE translation of UGT Mod's own interface kept by UGT Manager (<see cref="ModUi.FileName"/>),
/// placed into games from Mod defaults.
///
/// 🔴 **One version, imported by the person, never downloaded** (user's decision, 2026-09-25). Only
/// one is kept: somebody has no reason to change the interface's language from game to game, but may
/// improve it inside a game and import that game's file again. The file is never fetched from
/// anywhere — an interface translation written by a stranger can make the mod's own buttons lie
/// (analyse/modui-translate-file.md §6), which is why the mod keeps this file local and so does this.
///
/// 🔴 **Replacing a game's own file is always somebody's act, never a side effect** (user, 2026-09-25).
/// Two acts replace it: applying Mod defaults to that game, which lists the replacement among its
/// differences first, and the game's own Replace button. A game's own Apply and an install only
/// FILL a game that has none. And only a game translating into the file's language gets it — the
/// mod sets aside an interface file in another language at launch.
/// </summary>
public sealed class ModUiLibrary
{
    private readonly string _path;

    public ModUiLibrary(IPlatform platform) : this(platform.UserDataDirectory) { }

    /// <summary>Kept in <paramref name="dataDirectory"/> — the tool's data folder, or a test's.</summary>
    public ModUiLibrary(string dataDirectory) =>
        _path = Path.Combine(dataDirectory, ModUi.FileName);

    /// <summary>The file kept, or null when none was imported (or it can no longer be read).</summary>
    public ModUiFile? Current => File.Exists(_path) ? Describe(_path, File.GetLastWriteTimeUtc(_path)).File : null;

    /// <summary>
    /// Keeps a copy of <paramref name="source"/> as the one version, replacing the previous one.
    /// Returns null when it was kept, or why it was refused.
    ///
    /// ⚠ Refused unless it states its language: without it, nothing can say which games it fits,
    /// and the mod would set it aside in every one of them.
    /// </summary>
    public string? Import(string source)
    {
        var (file, refusal) = Describe(source, DateTime.UtcNow);
        if (file is null) return refusal;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

            // Beside, then moved into place: a half-copied file must never be the one kept.
            var temp = _path + ".tmp";
            File.Copy(source, temp, overwrite: true);
            File.Move(temp, _path, overwrite: true);
            return null;
        }
        catch (Exception ex)
        {
            return $"The file could not be kept ({ex.Message}).";
        }
    }

    /// <summary>Forgets the kept file. What was already placed in games stays there.</summary>
    public void Remove()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    /// <summary>Whether the kept file is in this language (a name or a code).</summary>
    public bool Fits(string? targetLanguage) =>
        Current is { } file && targetLanguage is { Length: > 0 }
        && string.Equals(Languages.Canonical(file.Language), Languages.Canonical(targetLanguage),
                         StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a game's data folder already has an interface file of its own.</summary>
    public static bool GameHasOne(string dataFolder) => File.Exists(Path.Combine(dataFolder, ModUi.FileName));

    /// <summary>
    /// The interface file a game holds, read the same way as an imported one — or null when it has
    /// none or it cannot be read. Its language is empty when the file does not state one.
    /// </summary>
    public static ModUiFile? InGame(string dataFolder)
    {
        var path = Path.Combine(dataFolder, ModUi.FileName);
        if (!File.Exists(path)) return null;

        var (file, _) = Describe(path, File.GetLastWriteTimeUtc(path), requireLanguage: false);
        return file;
    }

    /// <summary>What one game's Set up shows about the file, read in one place.</summary>
    public ModUiView ViewOf(string dataFolder, string targetLanguage) =>
        new(InGame(dataFolder), Current, Fits(targetLanguage), SameAs(dataFolder), targetLanguage);

    /// <summary>Whether the game's file is byte for byte the one kept here — nothing to replace then.</summary>
    public bool SameAs(string dataFolder)
    {
        var game = Path.Combine(dataFolder, ModUi.FileName);
        if (!File.Exists(game) || !File.Exists(_path)) return false;

        try
        {
            return File.ReadAllBytes(game).AsSpan().SequenceEqual(File.ReadAllBytes(_path));
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Places the kept file in a game's data folder when it fits. Without <paramref name="replace"/>
    /// only into a game that has none; with it, over the game's own. Returns whether it was placed.
    /// </summary>
    public bool PlaceInto(string dataFolder, string? targetLanguage, bool replace)
    {
        if (!Fits(targetLanguage)) return false;
        if (GameHasOne(dataFolder) && (!replace || SameAs(dataFolder))) return false;

        Directory.CreateDirectory(dataFolder);

        // Beside, then moved into place: a half-copied file must never be the one the mod reads.
        var target = Path.Combine(dataFolder, ModUi.FileName);
        var temp = target + ".tmp";
        File.Copy(_path, temp, overwrite: true);
        File.Move(temp, target, overwrite: replace);
        return true;
    }

    /// <summary>
    /// Reads a candidate: a JSON object stating its language, and how many lines it has. A game's
    /// own file is read without <paramref name="requireLanguage"/>: it is described, not kept.
    /// </summary>
    private static (ModUiFile? File, string? Refusal) Describe(string path, DateTime whenUtc,
                                                               bool requireLanguage = true)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
                return (null, "This is not a UGT Mod interface file.");

            var language = root.TryGetProperty("_target_language", out var value)
                           && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(language) && requireLanguage)
            {
                return (null, "This file does not say which language it is in. "
                              + $"Use the {ModUi.FileName} from a game where UGT Mod translated its interface.");
            }

            var lines = root.EnumerateObject().Count(p => !p.Name.StartsWith('_'));
            return (new ModUiFile(language ?? "", lines, whenUtc), null);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return (null, $"This file could not be read ({ex.Message}).");
        }
    }
}
