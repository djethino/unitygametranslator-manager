using System.Text.Json;
using System.Text.Json.Serialization;
using UnityGameTranslator.Manager.Core.Model;
using UnityGameTranslator.Manager.Core.Platform;

namespace UnityGameTranslator.Manager.Core.Install;

/// <summary>
/// What this tool has done to a game folder, kept where the game folder cannot take it away.
///
/// 🔴 **Written 2026-09-04, because the question "what happened here?" had no answer.** A game was
/// found holding a plugin with no loader — inert — and settling how it got that way took reading
/// file CREATION timestamps and an old report that happened to be quoted in a conversation. The
/// receipt in the game says what IS installed and dies with the uninstall, so the tool forgets it
/// was ever there. Nothing else recorded anything.
///
/// ⚠ <see cref="ReceiptStore"/>'s own summary claimed this file already existed — *"a copy is also
/// kept in the tool's own data directory so every install can be listed without rescanning the
/// machine"*. It did not. A comment describing a mechanism that is absent is worse than no comment:
/// it sends whoever needs it looking for a file, and it reads as documentation.
///
/// ## What it is, and what it is not
///
/// | | |
/// |---|---|
/// | a **summary** per game, kept after removal | the receipt's file lists and hashes — those stay in the game, where the uninstall needs them |
/// | the record of what THIS TOOL did | not a record of what happened to the folder; Steam, a hand, another program leave no trace here |
///
/// 🔴 **The receipt in the game stays the authority.** This is a memory, never a source of truth
/// for an action: uninstall reads the game's own receipt and nothing else, because that one matches
/// what is on disk. A ledger entry saying "installed" about files somebody deleted by hand must
/// never authorise removing anything.
///
/// ⚠ **No new category of data.** It holds game paths, which `game-preferences.json` beside it
/// already holds for every game seen. It never leaves the machine, and `diagnose` — made to be
/// pasted in public — does not read it.
/// </summary>
public sealed class InstallLedger
{
    /// <summary>
    /// ⚠ Not `installation.json`, which sits in the same folder and is the record of the TOOL
    /// installing ITSELF. Two files a letter apart would be read for each other.
    /// </summary>
    public const string FileName = "game-installs.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _directory;

    /// <summary>
    /// ⚠ The folder rather than the platform, because the folder is all this needs — and because
    /// IPlatform carries a dozen members a check would have to fake to exercise three lines. The
    /// overload below keeps every caller reading like its neighbours.
    /// </summary>
    public InstallLedger(string userDataDirectory) => _directory = userDataDirectory;

    public InstallLedger(IPlatform platform) : this(platform.UserDataDirectory) { }

    private string Path => System.IO.Path.Combine(_directory, FileName);

    /// <summary>One game, as this tool last left it.</summary>
    public sealed class Entry
    {
        [JsonPropertyName("game_path")] public string GamePath { get; set; } = "";
        [JsonPropertyName("steam_id")] public string? SteamId { get; set; }
        [JsonPropertyName("tool_version")] public string ToolVersion { get; set; } = "";

        [JsonPropertyName("installed_at")] public DateTimeOffset InstalledAt { get; set; }
        [JsonPropertyName("updated_at")] public DateTimeOffset? UpdatedAt { get; set; }

        /// <summary>
        /// When this tool removed what it had installed. Null while something of ours is there.
        ///
        /// 🔴 **The whole reason this file exists.** Everything else is already knowable from the
        /// game folder while the install stands; this is the one fact that disappears with it.
        /// </summary>
        [JsonPropertyName("removed_at")] public DateTimeOffset? RemovedAt { get; set; }

        [JsonPropertyName("loader_id")] public string? LoaderId { get; set; }
        [JsonPropertyName("loader_version")] public string? LoaderVersion { get; set; }

        /// <summary>
        /// False when the loader was already there. Kept because it decides what this tool is
        /// allowed to touch — and because "the loader vanished" reads differently depending on
        /// whether it was ours to begin with.
        /// </summary>
        [JsonPropertyName("loader_installed_by_us")] public bool LoaderInstalledByUs { get; set; }

        [JsonPropertyName("plugin_version")] public string? PluginVersion { get; set; }
    }

    /// <summary>Everything remembered, keyed by game path in lower case.</summary>
    public Dictionary<string, Entry> Read()
    {
        try
        {
            var path = Path;
            if (!File.Exists(path)) return new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

            return JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path), JsonOptions)
                   ?? new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // ⚠ A memory nobody can read is an empty memory, never a failed operation. This file
            // exists to answer questions afterwards; an install must not fail because of it.
            return new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>What this tool remembers doing to one game, or null.</summary>
    public Entry? For(string gamePath) =>
        Read().TryGetValue(Key(gamePath), out var entry) ? entry : null;

    /// <summary>
    /// Records an install or an update, from the receipt that was just written.
    ///
    /// ⚠ `installed_at` is kept from the first time: an update is not a new installation, and
    /// flattening the two would lose how long this game has been set up.
    /// </summary>
    public void Remember(Receipt receipt)
    {
        Write(receipt.Game.Path, entry =>
        {
            entry.GamePath = receipt.Game.Path;
            entry.SteamId = receipt.Game.SteamId;
            entry.ToolVersion = receipt.ToolVersion;

            if (entry.InstalledAt == default) entry.InstalledAt = receipt.InstalledAt;
            entry.UpdatedAt = receipt.UpdatedAt;

            // Back in place: an entry that had been removed and is installed again is not a
            // tombstone any more.
            entry.RemovedAt = null;

            entry.LoaderId = receipt.Loader?.Id;
            entry.LoaderVersion = receipt.Loader?.Version;
            entry.LoaderInstalledByUs = receipt.Loader?.InstalledByUs ?? false;
            entry.PluginVersion = receipt.Plugin?.Version;
        });
    }

    /// <summary>
    /// Records that what we had installed here has been removed.
    ///
    /// ⚠ Called where the game's receipt is DELETED, which is the moment the folder stops being
    /// able to answer for itself.
    /// </summary>
    public void RememberRemoval(string gamePath)
    {
        Write(gamePath, entry =>
        {
            entry.GamePath = gamePath;
            entry.RemovedAt = DateTimeOffset.UtcNow;
        });
    }

    private void Write(string gamePath, Action<Entry> update)
    {
        try
        {
            var all = Read();
            var key = Key(gamePath);

            if (!all.TryGetValue(key, out var entry))
            {
                entry = new Entry { GamePath = gamePath };
                all[key] = entry;
            }

            update(entry);

            Directory.CreateDirectory(_directory);

            // Beside the target then moved into place, like the receipt: a file half written by a
            // crash would describe an install nobody can trust.
            var temp = Path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(all, JsonOptions));
            File.Move(temp, Path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // 🔴 Never fails the caller. An install that worked must not report failure because a
            // note about it could not be filed — the game folder is correct either way, and the
            // receipt inside it is what any later action reads.
        }
    }

    private static string Key(string gamePath) =>
        gamePath.TrimEnd('/', '\\').ToLowerInvariant();
}
