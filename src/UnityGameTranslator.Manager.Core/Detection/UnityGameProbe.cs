using System.Text;
using System.Text.RegularExpressions;
using UnityGameTranslator.Manager.Core.Model;

namespace UnityGameTranslator.Manager.Core.Detection;

/// <summary>
/// Answers, for one folder: is this a Unity game, which scripting backend, which Unity version,
/// which architecture.
///
/// Every answer is allowed to be "unknown". That is the whole point — a wrong runtime means the
/// wrong loader, and the wrong loader means a game that will not start.
/// </summary>
public static partial class UnityGameProbe
{
    /// <summary>
    /// A Unity version string as Unity writes it: 2021.3.16f1, 5.6.0f3, 2023.1.0a16.
    /// The check is strict on purpose: BepInEx's own parser is loose enough to return a bogus
    /// "3.0.0f1" from encrypted or garbage bytes, and a plausible-but-wrong version is worse
    /// than none.
    /// </summary>
    [GeneratedRegex(@"^\d{1,4}\.\d+\.\d+[abfpx]\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex UnityVersionPattern();

    /// <summary>Probes a folder. Returns null when it holds no Unity game.</summary>
    public static GameInstall? Probe(string folder, string? displayName = null,
                                     GameStore store = GameStore.Unknown, string? steamAppId = null)
    {
        if (!Directory.Exists(folder)) return null;

        var dataDir = FindDataDirectory(folder);
        var executable = FindExecutable(folder, dataDir);

        // UnityPlayer.dll alone is enough on Windows; a *_Data folder is the portable signal.
        var hasUnityPlayer = File.Exists(Path.Combine(folder, "UnityPlayer.dll"))
                             || File.Exists(Path.Combine(folder, "UnityPlayer.so"));

        if (dataDir is null && !hasUnityPlayer) return null;

        var game = new GameInstall
        {
            Name = displayName ?? DeriveName(folder, dataDir),
            Path = Path.GetFullPath(folder),
            Store = store,

            // The store scanner's id when there is one, the game's own file otherwise.
            //
            // steam_appid.txt is what the MOD reads first, and it is written by the game itself,
            // so it survives being copied out of a Steam library — which is exactly when the
            // scanners have nothing to say. Measured here: two of six games sitting outside a
            // detected library carry one, and without it they are searched by name, on a title
            // that may not even be in Latin script.
            SteamAppId = steamAppId ?? ReadSteamAppId(folder),
            DataDirectory = dataDir,
            ExecutablePath = executable,
        };

        // What the game states about itself, kept whatever the display name ended up being: the
        // site records this pair, and it is what lets another machine find the same game.
        var (company, product) = ReadAppInfo(dataDir);
        game.ProductName = product;
        game.CompanyName = company;

        game.Runtime = DetectRuntime(folder, dataDir);
        game.UnityVersion = DetectUnityVersion(folder, dataDir);
        game.Architecture = DetectArchitecture(folder, executable);

        return game;
    }

    /// <summary>
    /// Folder names that say nothing about which game this is. Repacked releases very often put
    /// the real game in a "game" subfolder, which would otherwise leave the user staring at
    /// several identical rows named "game".
    /// </summary>
    private static readonly HashSet<string> GenericFolderNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "game", "games", "bin", "app", "build", "data", "release",
            "win", "win32", "win64", "x64", "x86", "client",
        };

    /// <summary>
    /// A name a human can recognise, when no store gave us one.
    ///
    /// ⚠ The product name Unity itself recorded comes FIRST, and that matters far beyond tidiness:
    /// the mod publishes translations under Application.productName, so any other spelling makes
    /// the community lookup miss. Measured on real installs, 2026-08-09:
    ///
    ///   folder "LONESTARuBxQC"      → product "LONESTAR"
    ///   folder "HyperEchelon6vYY3"  → product "Hyper Echelon"
    ///
    /// Both games had a published translation and both were reported as having none. Repacks and
    /// store installers add suffixes, and folder names drop the spaces a title actually has —
    /// neither can be searched for.
    ///
    /// The folder name stays as the fallback: it carries the release name, which is what a person
    /// recognises when Unity recorded nothing usable.
    /// </summary>
    private static string DeriveName(string folder, string? dataDir)
    {
        if (ReadProductName(dataDir) is { } product) return product;

        var folderName = new DirectoryInfo(folder).Name;
        if (!GenericFolderNames.Contains(folderName)) return folderName;

        if (dataDir is not null)
        {
            var stem = Path.GetFileName(dataDir);
            stem = stem[..^"_Data".Length];
            if (stem.Length > 2 && !GenericFolderNames.Contains(stem)) return stem;
        }

        var parent = Directory.GetParent(Path.GetFullPath(folder))?.Name;
        return string.IsNullOrEmpty(parent) ? folderName : parent;
    }

    /// <summary>
    /// The product name as Unity wrote it, from &lt;Game&gt;_Data/app.info.
    ///
    /// The file holds two lines: company, then product. It is plain UTF-8 and Unity has written it
    /// for years, but it is not guaranteed — so a missing or odd file means "no answer" and the
    /// caller falls back, never an exception.
    /// </summary>
    private static string? ReadProductName(string? dataDir) => ReadAppInfo(dataDir).Product;

    /// <summary>
    /// Both lines of &lt;Game&gt;_Data/app.info: the company, then the product.
    ///
    /// ⚠ The product was already read here to name the game; the company was thrown away. It is
    /// what turns a weak product name into an identity — the site keeps the pair, so two machines
    /// looking at the same game agree without anybody typing anything.
    /// </summary>
    private static (string? Company, string? Product) ReadAppInfo(string? dataDir)
    {
        if (dataDir is null) return (null, null);

        try
        {
            var path = Path.Combine(dataDir, "app.info");
            if (!File.Exists(path)) return (null, null);

            var lines = File.ReadAllLines(path);
            if (lines.Length < 2) return (null, null);

            var company = lines[0].Trim();
            var product = lines[1].Trim();

            // A blank or absurd value is worse than the folder name: it would show as an empty row
            // and search for nothing.
            return (company.Length is > 1 and < 120 ? company : null,
                    product.Length is > 1 and < 120 ? product : null);
        }
        catch
        {
            return (null, null);
        }
    }

    /// <summary>
    /// The app id the game states about itself, or null.
    ///
    /// Validated as digits rather than trusted: the file is written by whoever built the game, it
    /// is sometimes left at Steamworks' own placeholder, and an id that is not a number would be
    /// sent to the API as a search that can only fail.
    /// </summary>
    private static string? ReadSteamAppId(string folder)
    {
        try
        {
            var path = Path.Combine(folder, "steam_appid.txt");
            if (!File.Exists(path)) return null;

            var value = File.ReadAllText(path).Trim().TrimStart('﻿');

            // 480 is Steam's public test app ("Spacewar"), shipped by mistake often enough to be
            // worth naming: it identifies no real game and would match another game's catalogue.
            if (value.Length is 0 or > 10 || !value.All(char.IsDigit) || value == "480") return null;

            return value;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The &lt;Game&gt;_Data folder. Some games nest it one level down.</summary>
    public static string? FindDataDirectory(string folder)
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(folder, "*_Data"))
            {
                // A real data folder holds either Managed/ (Mono) or il2cpp_data/ (IL2CPP),
                // or at minimum the serialised globals.
                if (Directory.Exists(Path.Combine(dir, "Managed"))
                    || Directory.Exists(Path.Combine(dir, "il2cpp_data"))
                    || File.Exists(Path.Combine(dir, "globalgamemanagers"))
                    || File.Exists(Path.Combine(dir, "data.unity3d"))
                    || File.Exists(Path.Combine(dir, "mainData")))
                {
                    return dir;
                }
            }
        }
        catch
        {
            // Unreadable folder: treated as "not a Unity game here".
        }
        return null;
    }

    private static string? FindExecutable(string folder, string? dataDir)
    {
        // The executable is named after the data folder: "Foo_Data" -> "Foo.exe".
        if (dataDir is not null)
        {
            var stem = Path.GetFileName(dataDir);
            stem = stem[..^"_Data".Length];

            foreach (var candidate in new[] { stem + ".exe", stem + ".x86_64", stem + ".x86", stem })
            {
                var path = Path.Combine(folder, candidate);
                if (File.Exists(path)) return path;
            }
        }

        try
        {
            foreach (var exe in Directory.EnumerateFiles(folder, "*.exe"))
            {
                var name = Path.GetFileName(exe);
                // Unity ships these next to the game; neither is the game.
                if (name.StartsWith("UnityCrashHandler", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Equals("UnityPlayer.exe", StringComparison.OrdinalIgnoreCase)) continue;
                return exe;
            }
        }
        catch
        {
            // Ignored: absence of an executable is a valid outcome.
        }
        return null;
    }

    /// <summary>
    /// Mono or IL2CPP. GameAssembly.dll / GameAssembly.so is the compiled IL2CPP output and is
    /// the only signal that cannot be faked by a leftover folder.
    /// </summary>
    public static UnityRuntime DetectRuntime(string folder, string? dataDir)
    {
        if (File.Exists(Path.Combine(folder, "GameAssembly.dll"))
            || File.Exists(Path.Combine(folder, "GameAssembly.so")))
        {
            return UnityRuntime.Il2Cpp;
        }

        if (dataDir is not null)
        {
            if (Directory.Exists(Path.Combine(dataDir, "il2cpp_data"))) return UnityRuntime.Il2Cpp;

            var managed = Path.Combine(dataDir, "Managed");
            if (Directory.Exists(managed))
            {
                // Managed/ with the runtime assemblies present means a real Mono game.
                if (File.Exists(Path.Combine(managed, "Assembly-CSharp.dll"))
                    || File.Exists(Path.Combine(managed, "mscorlib.dll"))
                    || File.Exists(Path.Combine(managed, "UnityEngine.dll")))
                {
                    return UnityRuntime.Mono;
                }
            }
        }

        return UnityRuntime.Unknown;
    }

    /// <summary>
    /// Full Unity version, suffix included.
    ///
    /// Primary source is the serialised header, which carries the exact string. The PE version
    /// resource is only a fallback: it gives 2021.3.16 but never the "f1", so it is returned as
    /// a partial answer rather than pretended to be complete.
    /// </summary>
    public static string? DetectUnityVersion(string folder, string? dataDir)
    {
        if (dataDir is not null)
        {
            foreach (var name in new[] { "globalgamemanagers", "mainData", "data.unity3d" })
            {
                var version = ReadVersionFromSerializedFile(Path.Combine(dataDir, name));
                if (version is not null) return version;
            }
        }

        foreach (var name in new[] { "UnityPlayer.dll", "UnityPlayer.so" })
        {
            var path = Path.Combine(folder, name);
            if (!File.Exists(path)) continue;

            var raw = PeFile.ReadFileVersion(path);
            if (raw is null) continue;

            // "2021.3.16.51829" -> "2021.3.16" (the last field is a build number, not a suffix).
            var parts = raw.Split('.');
            if (parts.Length >= 3) return string.Join('.', parts[0], parts[1], parts[2]);
        }

        return null;
    }

    /// <summary>
    /// Unity writes its version as a NUL-terminated string near the start of the serialised
    /// file — usually at offset 0x14, but not always, and the header layout has moved between
    /// engine generations.
    ///
    /// So we scan the first bytes for whole strings rather than reading a fixed offset. The
    /// "whole" matters: a scan that starts mid-string turns "2022.3.62f2" into "22.3.62f2",
    /// which still satisfies the version pattern and would be reported as fact. A candidate is
    /// therefore only accepted when it begins at a real string boundary — the start of the
    /// window, or just after a NUL.
    /// </summary>
    private static string? ReadVersionFromSerializedFile(string path)
    {
        if (!File.Exists(path)) return null;

        try
        {
            using var stream = File.OpenRead(path);
            var length = (int)Math.Min(stream.Length, 512);
            if (length < 8) return null;

            var buffer = new byte[length];
            var read = stream.Read(buffer, 0, length);
            if (read < 8) return null;

            var start = 0;
            while (start < read)
            {
                // Skip anything that is not the beginning of a printable string.
                if (buffer[start] == 0) { start++; continue; }

                var end = start;
                while (end < read && buffer[end] != 0) end++;
                if (end >= read) break; // unterminated: refuse rather than guess

                if (end - start is >= 5 and <= 24)
                {
                    var candidate = Encoding.ASCII.GetString(buffer, start, end - start);
                    if (UnityVersionPattern().IsMatch(candidate)) return candidate;
                }

                start = end + 1;
            }
        }
        catch
        {
            // Encrypted or unusual asset files simply yield no version.
        }
        return null;
    }

    private static GameArchitecture DetectArchitecture(string folder, string? executable)
    {
        if (executable is not null && executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            var arch = PeFile.ReadArchitecture(executable);
            if (arch != GameArchitecture.Unknown) return arch;
        }

        // UnityPlayer.dll is a reliable second source on Windows builds.
        var player = Path.Combine(folder, "UnityPlayer.dll");
        if (File.Exists(player))
        {
            var arch = PeFile.ReadArchitecture(player);
            if (arch != GameArchitecture.Unknown) return arch;
        }

        // Native Linux builds name themselves by architecture.
        if (executable is not null)
        {
            if (executable.EndsWith(".x86_64", StringComparison.OrdinalIgnoreCase)) return GameArchitecture.X64;
            if (executable.EndsWith(".x86", StringComparison.OrdinalIgnoreCase)) return GameArchitecture.X86;
        }

        return GameArchitecture.Unknown;
    }
}
