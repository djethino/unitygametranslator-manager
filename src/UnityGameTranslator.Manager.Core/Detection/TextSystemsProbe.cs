using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using UnityGameTranslator.Common;
using UnityGameTranslator.Manager.Core.Install;
using UnityGameTranslator.Manager.Core.Model;

namespace UnityGameTranslator.Manager.Core.Detection;

/// <summary>What UGT Mod recorded that this game SHOWED (spec/texts-seen).</summary>
/// <param name="Unknown">Words a newer mod wrote, named as they are.</param>
public sealed record TextsSeen(IReadOnlyList<TextSystem> Systems, IReadOnlyList<string> Other,
                               IReadOnlyList<string> Unknown, string? ModVersion)
{
    /// <summary>The line both the card and the command line print — see TextSystems.Describe.</summary>
    public string Line => TextSystems.Describe(Systems, Other, Unknown);
}

/// <summary>
/// Which text systems a game uses, the two ways that can be known — see
/// <see cref="TextSystems"/> for why they are never one answer:
///
/// - <see cref="ReadSeen"/>: what the game SHOWED while UGT Mod ran, from the file the mod writes.
///   Exact, and absent until the game has run with a mod that records it;
/// - <see cref="ReadContained"/>: what the game's files CONTAIN, read without running anything.
///   A shipped library is whole, so this says what could appear — never that it does, and never
///   anything about input fields: TextMesh Pro always carries its own.
/// </summary>
public static class TextSystemsProbe
{
    /// <summary>
    /// The mod's record for this game, or null when there is none — never run with a mod that
    /// writes it, or a file that cannot be read (which is said the same way: nothing recorded).
    /// </summary>
    public static TextsSeen? ReadSeen(string gamePath, LoaderDescriptor descriptor)
    {
        var folder = UserDataInventory.FolderFor(gamePath, descriptor);
        if (folder is null) return null;
        var path = Path.Combine(folder, TextSystems.FileName);
        if (!File.Exists(path)) return null;

        try { return Parse(File.ReadAllText(path)); }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// A texts-seen document read the lenient way the contract asks of a reader: a missing
    /// <c>format</c> is not a refusal, and an unknown word is kept by name rather than dropped.
    /// </summary>
    public static TextsSeen? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;

        var systems = new List<TextSystem>();
        var unknown = new List<string>();
        if (root.TryGetProperty("systems", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) continue;
                var word = item.GetString()!;
                if (TextSystems.TryParse(word, out var s)) { if (!systems.Contains(s)) systems.Add(s); }
                else if (word.Length > 0 && !unknown.Contains(word)) unknown.Add(word);
            }
        }

        var other = new List<string>();
        if (root.TryGetProperty("other", out var others) && others.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in others.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } name && !other.Contains(name))
                    other.Add(name);
        }

        string? version = root.TryGetProperty("mod_version", out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

        return new TextsSeen(systems, other, unknown, version);
    }

    // What the files say, by game — read again only when one of the files read has changed.
    private static readonly ConcurrentDictionary<string, (string Stamp, IReadOnlyList<TextSystem> Systems)> Contained = new();

    // ⚠ Measured on 60 games (2026-09-25), and why neither reading is a plain name search:
    //  · as substrings, NGUI and 2D Toolkit came out "contained" in a third of them — a
    //    localisation plugin's adapter classes carry those names inside their own;
    //  · as whole names, 2D Toolkit still came out in eleven: DOTween's enum of what it can tween
    //    has a VALUE named tk2dTextMesh. A name table cannot tell a class from an enum value.
    // Hence, on Mono, the type DEFINITIONS themselves (System.Reflection.Metadata); on IL2CPP,
    // whose type table changes layout between Unity versions, several names internal to the
    // system, all present — no enum carries its font-data class along with its text class.

    // Mono: a system is contained when an assembly DEFINES this type (namespace, name).
    private static readonly (TextSystem System, string Namespace, string Name)[] Definitions =
    {
        (TextSystem.Tmp, "TMPro", "TextMeshProUGUI"),
        (TextSystem.TmpLegacy, "TMProOld", "TextMeshProUGUI"),
        (TextSystem.UiText, "UnityEngine.UI", "Text"),
        (TextSystem.Ngui, "", "UILabel"),
        (TextSystem.Tk2d, "", "tk2dTextMesh"),
    };

    // IL2CPP: a system is contained when EVERY one of these names is in the string table, whole.
    private static readonly (TextSystem System, string[] Names)[] Il2CppNames =
    {
        (TextSystem.Tmp, new[] { "TextMeshProUGUI", "TMP_FontAsset", "TMPro" }),
        (TextSystem.TmpLegacy, new[] { "TMProOld", "TextMeshProUGUI" }),
        (TextSystem.UiText, new[] { "FontData", "UnityEngine.UI" }),
        (TextSystem.Ngui, new[] { "UILabel", "UIWidget", "UIPanel", "UIFont" }),
        (TextSystem.Tk2d, new[] { "tk2dTextMesh", "tk2dFontData" }),
    };

    /// <summary>
    /// What this game's files contain: TextMesh Pro, its legacy form, UI Text, NGUI, 2D Toolkit —
    /// the systems whose presence says something. TextMesh and UI Toolkit ship with every Unity
    /// and are not listed: "contains" would be true everywhere and tell nothing.
    ///
    /// Mono: the types the assemblies in Managed/ define. IL2CPP: global-metadata.dat, where the
    /// linker has already dropped what the game never reaches — the closer of the two to what is
    /// used, and still not the same thing.
    ///
    /// ⚠ Reads tens of megabytes on an IL2CPP game: for the card being opened and the command
    /// line, never for the rows of the list. Remembered against the files' size and date.
    /// </summary>
    public static IReadOnlyList<TextSystem> ReadContained(GameInstall game)
    {
        var files = FilesToRead(game);
        if (files.Count == 0) return Array.Empty<TextSystem>();

        var stamp = string.Join("|", files.Select(f => { var i = new FileInfo(f); return $"{f}:{i.Length}:{i.LastWriteTimeUtc.Ticks}"; }));
        if (Contained.TryGetValue(game.Path, out var known) && known.Stamp == stamp) return known.Systems;

        var found = new HashSet<TextSystem>();
        foreach (var file in files)
        {
            if (game.Runtime == UnityRuntime.Il2Cpp) found.UnionWith(SearchNames(file));
            else found.UnionWith(DefinedIn(file));
        }

        // In the socle's order, whatever order the files were read in.
        var systems = Enum.GetValues<TextSystem>().Where(found.Contains).ToList();
        Contained[game.Path] = (stamp, systems);
        return systems;
    }

    /// <summary>The files that hold the game's code: the metadata on IL2CPP, the game's own assemblies on Mono.</summary>
    private static List<string> FilesToRead(GameInstall game)
    {
        var files = new List<string>();
        if (game.DataDirectory is not { } data) return files;

        if (game.Runtime == UnityRuntime.Il2Cpp)
        {
            var metadata = Path.Combine(data, "il2cpp_data", "Metadata", "global-metadata.dat");
            if (File.Exists(metadata)) files.Add(metadata);
            return files;
        }

        var managed = Path.Combine(data, "Managed");
        if (!Directory.Exists(managed)) return files;
        foreach (var dll in Directory.EnumerateFiles(managed, "*.dll"))
        {
            var name = Path.GetFileName(dll);
            // The runtime and the engine's own modules declare none of these types — reading them
            // is time for nothing. The two libraries that define some of them stay.
            if (name.Equals("Unity.TextMeshPro.dll", StringComparison.OrdinalIgnoreCase)
                || name.Equals("UnityEngine.UI.dll", StringComparison.OrdinalIgnoreCase))
            {
                files.Add(dll);
                continue;
            }
            if (name.StartsWith("System", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Mono.", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("UnityEngine", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Unity.", StringComparison.OrdinalIgnoreCase)
                || name.Equals("mscorlib.dll", StringComparison.OrdinalIgnoreCase)
                || name.Equals("netstandard.dll", StringComparison.OrdinalIgnoreCase))
                continue;
            files.Add(dll);
        }
        return files;
    }

    /// <summary>The systems whose defining type this .NET assembly declares — nothing for a file that is not one.</summary>
    private static IEnumerable<TextSystem> DefinedIn(string file)
    {
        var hits = new HashSet<TextSystem>();
        try
        {
            using var stream = File.OpenRead(file);
            using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
            if (!pe.HasMetadata) return hits;
            var md = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
            foreach (var handle in md.TypeDefinitions)
            {
                var type = md.GetTypeDefinition(handle);
                // Only a name that could be one of ours is decoded: most types are not.
                var name = md.GetString(type.Name);
                foreach (var d in Definitions)
                {
                    if (d.Name != name || hits.Contains(d.System)) continue;
                    if (md.GetString(type.Namespace) == d.Namespace) hits.Add(d.System);
                }
            }
        }
        catch (BadImageFormatException) { }
        catch (InvalidOperationException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return hits;
    }

    /// <summary>
    /// The systems ALL of whose names the file holds WHOLE — between two NULs, which is how the
    /// IL2CPP string table stores every identifier, so a name inside a longer one
    /// ("LocalizeTarget_NGUI_UILabel") is not a match. Read in blocks that overlap by the longest name.
    /// </summary>
    private static IEnumerable<TextSystem> SearchNames(string file)
    {
        var names = Il2CppNames.SelectMany(s => s.Names).Distinct().ToList();
        var patterns = names.Select(n => (Name: n, Bytes: Encoding.ASCII.GetBytes("\0" + n + "\0"))).ToList();
        int overlap = patterns.Max(p => p.Bytes.Length) - 1;
        var present = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            using var stream = File.OpenRead(file);
            var buffer = new byte[1 << 20];
            int carried = 0;
            while (present.Count < patterns.Count)
            {
                int read = stream.Read(buffer, carried, buffer.Length - carried);
                if (read <= 0) break;
                var window = new ReadOnlySpan<byte>(buffer, 0, carried + read);
                foreach (var p in patterns)
                    if (!present.Contains(p.Name) && window.IndexOf(p.Bytes) >= 0) present.Add(p.Name);

                carried = Math.Min(overlap, window.Length);
                window[^carried..].CopyTo(buffer);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return Il2CppNames.Where(s => s.Names.All(present.Contains)).Select(s => s.System);
    }
}
