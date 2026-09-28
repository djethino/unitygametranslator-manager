using System.Runtime.InteropServices;
using UnityGameTranslator.Manager.Core.Model;

namespace UnityGameTranslator.Manager.Core.Platform;

/// <summary>
/// Linux, including SteamOS / Steam Deck.
///
/// Two things make Linux different from Windows and both are handled here rather than leaking
/// into Core: Steam lives in several possible places (native, Flatpak, SD card), and most games
/// are Windows builds running through Proton — which need a Wine DLL override to load anything.
/// </summary>
public sealed class LinuxPlatform : IPlatform
{
    public string OsId => "linux";

    public GameArchitecture HostArchitecture => RuntimeInformation.OSArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.X64 => GameArchitecture.X64,
        System.Runtime.InteropServices.Architecture.X86 => GameArchitecture.X86,
        System.Runtime.InteropServices.Architecture.Arm64 => GameArchitecture.Arm64,
        _ => GameArchitecture.Unknown,
    };

    private static string Home =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public IEnumerable<string> SteamRoots()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var candidates = new List<string>
        {
            Path.Combine(Home, ".steam", "steam"),
            Path.Combine(Home, ".steam", "root"),
            Path.Combine(Home, ".local", "share", "Steam"),
            // Flatpak Steam keeps its own tree.
            Path.Combine(Home, ".var", "app", "com.valvesoftware.Steam", "data", "Steam"),
        };

        foreach (var path in candidates)
        {
            if (!seen.Add(path)) continue;
            if (Directory.Exists(Path.Combine(path, "steamapps"))) yield return path;
        }

        // Steam Deck: SD cards and external drives are mounted here and hold a full library.
        foreach (var mountRoot in new[] { "/run/media", "/media" })
        {
            if (!Directory.Exists(mountRoot)) continue;

            IEnumerable<string> mounts;
            try { mounts = Directory.EnumerateDirectories(mountRoot); }
            catch { continue; }

            foreach (var mount in mounts)
            {
                // /run/media/<user>/<card> on some builds, /run/media/<card> on others.
                foreach (var candidate in new[] { mount }.Concat(SafeDirectories(mount)))
                {
                    if (!seen.Add(candidate)) continue;
                    if (Directory.Exists(Path.Combine(candidate, "steamapps"))) yield return candidate;
                }
            }
        }
    }

    private static IEnumerable<string> SafeDirectories(string path)
    {
        try { return Directory.EnumerateDirectories(path); }
        catch { return Array.Empty<string>(); }
    }

    public IEnumerable<GameRootHint> ExtraGameRoots()
    {
        // Heroic is how Epic and GOG games usually land on Linux, and it installs into a known
        // default folder. Nothing beyond launcher defaults is guessed: a "~/Games" folder is a
        // personal habit, not a convention, and anything else is added explicitly by the user
        // and remembered (see CustomFolders).
        var heroicDefault = Path.Combine(Home, "Games", "Heroic");
        if (Directory.Exists(heroicDefault))
            yield return new GameRootHint(heroicDefault, GameStore.Manual);

        var lutris = Path.Combine(Home, "Games");
        if (Directory.Exists(Path.Combine(lutris, "lutris")))
            yield return new GameRootHint(Path.Combine(lutris, "lutris"), GameStore.Manual);
    }

    public string UserDataDirectory
    {
        get
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            var baseDir = string.IsNullOrEmpty(xdg) ? Path.Combine(Home, ".local", "share") : xdg;
            return Path.Combine(baseDir, "unitygametranslator-manager");
        }
    }

    /// <summary>
    /// $XDG_RUNTIME_DIR, which is the place a freedesktop system sets aside for exactly this:
    /// per-user, on a memory filesystem, and emptied when the session ends.
    ///
    /// ⚠ The fallback is /tmp, which is NOT per-user — it is shared by everyone on the machine. So
    /// anything we put there carries the user name, or one person opening the tool would stop
    /// another from opening it at all, with a message about a file they cannot even read.
    /// </summary>
    public string RuntimeStateDirectory
    {
        get
        {
            var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            return string.IsNullOrEmpty(runtime) ? Path.GetTempPath() : runtime;
        }
    }

    /// <summary>
    /// SteamOS keeps / read-only, so the tool can only ever live in the user's home. That is
    /// also the right place on any other distribution for a single-user tool.
    /// </summary>
    public string SelfInstallDirectory =>
        Path.Combine(Home, ".local", "share", "unitygametranslator-manager", "bin");

    public string ExecutableFileName => "unitygametranslator-manager";

    public IReadOnlyList<LauncherKind> LauncherKinds => [LauncherKind.Menu, LauncherKind.Desktop];

    /// <summary>
    /// A .desktop entry, which is what both the applications menu and the desktop actually are on
    /// a freedesktop system — the same file, in two folders.
    ///
    /// ⚠ It has to be executable to be honoured on the desktop of most environments, which is the
    /// kind of thing that fails silently: the file is there, it looks right, and double-clicking it
    /// offers to open it in a text editor.
    /// </summary>
    public IReadOnlyList<string> CreateLauncher(LauncherKind kind, string executable)
    {
        var folder = kind == LauncherKind.Desktop
            ? Path.Combine(Home, "Desktop")
            : Path.Combine(Home, ".local", "share", "applications");

        var path = Path.Combine(folder, "unitygametranslator-manager.desktop");

        var entry = string.Join('\n',
            "[Desktop Entry]",
            "Type=Application",
            "Name=UnityGameTranslator Manager",
            "Comment=Set up UnityGameTranslator in your Unity games",
            $"Exec=\"{executable}\"",
            $"Path={Path.GetDirectoryName(executable)}",
            "Terminal=false",
            "Categories=Game;Utility;",
            "");

        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(path, entry);

            // Guarded rather than assumed: this class only ever runs on Linux, but the compiler
            // cannot know that from here, and the check costs nothing next to a file write.
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            }

            return [path];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Nothing to register. A desktop system's list of applications IS the .desktop file written
    /// above, so claiming a second registration would mean inventing a place to clean up later.
    /// </summary>
    public string? RegisterInstalled(ToolInstallation installation) => null;

    public void UnregisterInstalled(string registration)
    {
    }

    /// <summary>Nothing was registered, so nothing can be missing. See RegisterInstalled.</summary>
    public bool IsRegistered(string registration) => true;

    private static string DataHome
    {
        get
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            return string.IsNullOrEmpty(xdg) ? Path.Combine(Home, ".local", "share") : xdg;
        }
    }

    private static string MimePackage => Path.Combine(DataHome, "mime", "packages", "unitygametranslator-manager.xml");

    /// <summary>
    /// A desktop entry of its own, hidden from the menu: it is what the association names, and it
    /// must exist whether or not somebody asked for a menu launcher.
    /// </summary>
    private const string PackDesktopFile = "unitygametranslator-manager-ugtpack.desktop";

    private static string PackDesktopEntry => Path.Combine(DataHome, "applications", PackDesktopFile);

    private static string IconFile(int size) =>
        Path.Combine(DataHome, "icons", "hicolor", $"{size}x{size}", "mimetypes", PackFileType.IconName + ".png");

    /// <summary>
    /// The freedesktop way, all in the user's data folder: a MIME package declaring the type by its
    /// extension (a sub-class of zip, which it is), the icon in the hicolor theme under the name the
    /// type asks for, a hidden desktop entry that opens it, and that entry made the default.
    ///
    /// ⚠ The databases are rebuilt by the system's own tools. Where one is missing the files are
    /// still in place and a desktop that rebuilds on its own (KDE does) still picks them up — so a
    /// missing tool is not a failure; a file that could not be written is.
    /// </summary>
    public IReadOnlyList<string>? RegisterPackType(ToolInstallation installation)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MimePackage)!);
            File.WriteAllText(MimePackage, string.Join('\n',
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>",
                "<mime-info xmlns=\"http://www.freedesktop.org/standards/shared-mime-info\">",
                $"  <mime-type type=\"{PackFileType.MimeType}\">",
                $"    <comment>{PackFileType.Description}</comment>",
                "    <sub-class-of type=\"application/zip\"/>",
                $"    <glob pattern=\"*{UnityGameTranslator.Common.AssetPacks.Extension}\"/>",
                $"    <icon name=\"{PackFileType.IconName}\"/>",
                "  </mime-type>",
                "</mime-info>",
                ""));

            foreach (var size in PackFileType.IconSizes)
            {
                var icon = IconFile(size);
                Directory.CreateDirectory(Path.GetDirectoryName(icon)!);
                File.WriteAllBytes(icon, PackFileType.Resource(PackFileType.PngFile(size)));
            }

            Directory.CreateDirectory(Path.GetDirectoryName(PackDesktopEntry)!);
            File.WriteAllText(PackDesktopEntry, string.Join('\n',
                "[Desktop Entry]",
                "Type=Application",
                "Name=UnityGameTranslator Manager",
                $"Comment=Open {PackFileType.Description}s",
                $"Exec=\"{installation.Executable}\" %f",
                $"MimeType={PackFileType.MimeType};",
                "NoDisplay=true",
                "Terminal=false",
                ""));

            RunTool("update-mime-database", Path.Combine(DataHome, "mime"));
            RunTool("update-desktop-database", Path.GetDirectoryName(PackDesktopEntry)!);
            RunTool("xdg-mime", "default", PackDesktopFile, PackFileType.MimeType);

            // Nothing inside the installation folder: every file above is at a fixed place outside
            // it, which UnregisterPackType removes by name.
            return [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void UnregisterPackType()
    {
        var files = new List<string> { MimePackage, PackDesktopEntry };
        files.AddRange(PackFileType.IconSizes.Select(IconFile));

        foreach (var file in files)
        {
            try
            {
                if (File.Exists(file)) File.Delete(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Left where it was; nothing else depends on it once the databases forget it.
            }
        }

        RunTool("update-mime-database", Path.Combine(DataHome, "mime"));
        RunTool("update-desktop-database", Path.GetDirectoryName(PackDesktopEntry)!);
    }

    /// <summary>
    /// From the files themselves. ⚠ Another default chosen by the person lives in mimeapps.list,
    /// in formats that vary by desktop; not read — Absent, Ours or Stale only.
    /// </summary>
    public PackTypeState PackTypeStateFor(ToolInstallation installation)
    {
        try
        {
            if (!File.Exists(MimePackage) || !File.Exists(PackDesktopEntry)) return PackTypeState.Absent;

            var exec = $"Exec=\"{installation.Executable}\" %f";
            return File.ReadLines(PackDesktopEntry).Any(line => line == exec) ? PackTypeState.Ours : PackTypeState.Stale;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return PackTypeState.Absent;
        }
    }

    /// <summary>
    /// Runs one of the desktop's own tools to the end. False when the system does not have it —
    /// see RegisterPackType for why that is not an error.
    /// </summary>
    private static bool RunTool(string tool, params string[] arguments)
    {
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo
            {
                FileName = tool,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);

            using var process = System.Diagnostics.Process.Start(start);
            if (process is null) return false;

            // Read before waiting: a full pipe would otherwise hold the tool, and us with it.
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// The .NET *Desktop* runtime is a Windows-only product. For a Proton game the runtime that
    /// matters lives inside the prefix, which we cannot inspect reliably — so we answer "unknown"
    /// and let the caller warn instead of blocking on a check it cannot make.
    /// </summary>
    public bool? HasDotnetDesktopRuntime(string majorVersion) => null;

    /// <summary>Every Windows build here runs through Wine — Proton, or Heroic, Lutris, Bottles.</summary>
    public bool NeedsDllOverride(GameInstall game) => game.IsWindowsBuild;

    public string? SystemLanguage()
    {
        // The desktop environment sets these; LC_ALL wins, then LANG, then LANGUAGE.
        foreach (var name in new[] { "LC_ALL", "LC_MESSAGES", "LANG", "LANGUAGE" })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value)) continue;

            // Shapes seen in the wild: "fr_FR.UTF-8", "fr_FR", "fr", "fr:en". Handed over whole —
            // Languages.FromLocale knows these shapes and knows which part means something.
            var locale = value.Split(':')[0].Trim();
            if (locale.Length >= 2 && char.IsLetter(locale[0])) return locale;
        }
        return null;
    }

    /// <summary>
    /// Read from sysfs, which covers AMD and Intel without running anything, then from nvidia-smi.
    ///
    /// sysfs first on purpose: a Steam Deck is AMD, and asking the kernel costs nothing where
    /// spawning a process may not even be possible. nvidia-smi is the fallback because NVIDIA
    /// does not expose the total through sysfs.
    /// </summary>
    /// <summary>
    /// Exactly the folder the mod searches on Linux. ⚠ Not fontconfig's other places (~/.fonts,
    /// ~/.local/share/fonts): a font found there is one the game never used, and exporting it would
    /// carry a font nobody saw in the game. Widen both together, or neither.
    /// </summary>
    /// <summary>The socle's one list for Linux — the folders the mod of a native game reads.</summary>
    public IEnumerable<string> FontFolders() =>
        UnityGameTranslator.Common.SystemFontFolders
            .For(UnityGameTranslator.Common.SystemFontFolders.Os.Linux, home: Home,
                 xdgDataHome: Environment.GetEnvironmentVariable("XDG_DATA_HOME"))
            .Where(Directory.Exists);

    /// <summary>Linux keeps no table the mod reads: fonts are found by file name alone.</summary>
    public IEnumerable<(string Name, string Path)> RegisteredFonts() => [];

    public long? VideoMemoryBytes()
    {
        long largest = 0;

        try
        {
            foreach (var card in SafeDirectories("/sys/class/drm"))
            {
                var file = Path.Combine(card, "device", "mem_info_vram_total");
                if (!File.Exists(file)) continue;

                if (long.TryParse(File.ReadAllText(file).Trim(), out var bytes) && bytes > largest)
                    largest = bytes;
            }
        }
        catch
        {
            // No sysfs entry, or no permission: fall through to nvidia-smi.
        }

        if (largest > 0) return largest;

        try
        {
            var start = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "nvidia-smi",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("--query-gpu=memory.total");
            start.ArgumentList.Add("--format=csv,noheader,nounits");

            using var process = System.Diagnostics.Process.Start(start);
            if (process is null) return null;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(3000);

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                // Reported in mebibytes by --nounits.
                if (long.TryParse(line.Trim(), out var mib) && mib * 1024 * 1024 > largest)
                    largest = mib * 1024 * 1024;
            }
        }
        catch
        {
            // No NVIDIA driver installed. Not knowing is a valid answer here.
        }

        return largest > 0 ? largest : null;
    }

    public bool IsGameRunning(GameInstall game)
    {
        if (string.IsNullOrEmpty(game.Path) || !Directory.Exists("/proc")) return false;

        var root = Path.GetFullPath(game.Path).TrimEnd('/') + "/";

        foreach (var dir in SafeDirectories("/proc"))
        {
            var name = Path.GetFileName(dir);
            if (!int.TryParse(name, out _)) continue;

            try
            {
                var exeLink = Path.Combine(dir, "exe");
                var target = File.ResolveLinkTarget(exeLink, returnFinalTarget: true)?.FullName;
                if (target is not null && target.StartsWith(root, StringComparison.Ordinal)) return true;
            }
            catch
            {
                // Other users' processes are unreadable; that is expected.
            }
        }
        return false;
    }
}
