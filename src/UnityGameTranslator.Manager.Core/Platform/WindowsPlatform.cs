using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using UnityGameTranslator.Manager.Core.Model;

namespace UnityGameTranslator.Manager.Core.Platform;

[SupportedOSPlatform("windows")]
public sealed class WindowsPlatform : IPlatform
{
    public string OsId => "windows";

    public GameArchitecture HostArchitecture => RuntimeInformation.OSArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.X64 => GameArchitecture.X64,
        System.Runtime.InteropServices.Architecture.X86 => GameArchitecture.X86,
        System.Runtime.InteropServices.Architecture.Arm64 => GameArchitecture.Arm64,
        _ => GameArchitecture.Unknown,
    };

    public IEnumerable<string> SteamRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // The registry is authoritative: it survives a Steam installed off the default drive.
        foreach (var key in new[] { @"SOFTWARE\WOW6432Node\Valve\Steam", @"SOFTWARE\Valve\Steam" })
        {
            var path = ReadRegistry(RegistryHive.LocalMachine, key, "InstallPath");
            if (path is not null && seen.Add(path) && Directory.Exists(path)) yield return path;
        }

        var userPath = ReadRegistry(RegistryHive.CurrentUser, @"SOFTWARE\Valve\Steam", "SteamPath");
        if (userPath is not null && seen.Add(userPath) && Directory.Exists(userPath)) yield return userPath;

        // Fallbacks for a broken or absent registry entry.
        foreach (var guess in new[]
                 {
                     Path.Combine(Env(Environment.SpecialFolder.ProgramFilesX86), "Steam"),
                     Path.Combine(Env(Environment.SpecialFolder.ProgramFiles), "Steam"),
                 })
        {
            if (seen.Add(guess) && Directory.Exists(guess)) yield return guess;
        }
    }

    public IEnumerable<GameRootHint> ExtraGameRoots()
    {
        // Epic keeps one JSON manifest per installed game; the scanner reads them, we only
        // point at the folder.
        var epicManifests = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (Directory.Exists(epicManifests))
            yield return new GameRootHint(epicManifests, GameStore.Epic);

        foreach (var gog in GogRoots())
            yield return new GameRootHint(gog, GameStore.Gog);

        // Launchers' own default install locations. These hold games even when the launcher's
        // manifests are missing or the launcher itself has been uninstalled.
        //
        // Nothing beyond these is guessed. Scanning every drive for folders named "Games" was
        // tried and removed: such a folder is someone's personal way of organising a library,
        // not a convention, and a tool that assumes it is right on one machine and wrong on the
        // next. Anything else the user adds explicitly, and it is remembered (see CustomFolders).
        foreach (var baseFolder in new[]
                 {
                     Environment.SpecialFolder.ProgramFiles,
                     Environment.SpecialFolder.ProgramFilesX86,
                 })
        {
            foreach (var name in new[] { "Epic Games", @"GOG Galaxy\Games" })
            {
                var path = Path.Combine(Env(baseFolder), name);
                if (Directory.Exists(path)) yield return new GameRootHint(path, GameStore.Manual);
            }
        }
    }

    private static IEnumerable<string> GogRoots()
    {
        // GOG Galaxy records each game under its own key with a "path" value.
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            RegistryKey? games = null;
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                games = baseKey.OpenSubKey(@"SOFTWARE\GOG.com\Games");
                if (games is null) continue;

                foreach (var id in games.GetSubKeyNames())
                {
                    string? path = null;
                    try
                    {
                        using var game = games.OpenSubKey(id);
                        path = game?.GetValue("path") as string;
                    }
                    catch (Exception ex) when (RegistryFailed(ex))
                    {
                        // A single unreadable key must not stop the enumeration — its game is
                        // missing from the list, and that is said.
                        Faults.Say("WindowsPlatform.GogRoots", ex, id);
                    }
                    if (!string.IsNullOrEmpty(path) && Directory.Exists(path)) yield return path;
                }
            }
            finally
            {
                games?.Dispose();
            }
        }
    }

    /// <summary>What reading or writing the registry may meet: a key we may not open, or one gone.</summary>
    private static bool RegistryFailed(Exception e) =>
        e is System.Security.SecurityException or UnauthorizedAccessException or IOException;

    public string UserDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UnityGameTranslator", "Manager");

    /// <summary>%TEMP%, which Windows already gives each account separately.</summary>
    public string RuntimeStateDirectory => Path.GetTempPath();

    public string SelfInstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "UnityGameTranslator Manager");

    public string ExecutableFileName => "UnityGameTranslatorManager.exe";

    public IReadOnlyList<LauncherKind> LauncherKinds => [LauncherKind.Menu, LauncherKind.Desktop];

    /// <summary>
    /// Writes a .lnk through the shell's own shortcut object.
    ///
    /// A .lnk is a binary format nobody should be writing by hand, and the one component that has
    /// created them correctly on every Windows since forever is the shell itself. Reached by late
    /// binding rather than by a COM interop assembly: it is four calls, and adding a dependency to
    /// a single-file build for four calls is a poor trade.
    /// </summary>
    public IReadOnlyList<string> CreateLauncher(LauncherKind kind, string executable)
    {
        var folder = kind == LauncherKind.Desktop
            ? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                           "Programs");

        if (string.IsNullOrEmpty(folder)) return [];

        var path = Path.Combine(folder, "UnityGameTranslator Manager.lnk");

        try
        {
            Directory.CreateDirectory(folder);

            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return [];

            var shell = Activator.CreateInstance(shellType);
            if (shell is null) return [];

            var shortcut = shellType.InvokeMember("CreateShortcut",
                System.Reflection.BindingFlags.InvokeMethod, null, shell, [path]);
            if (shortcut is null) return [];

            var type = shortcut.GetType();
            void Set(string name, object value) => type.InvokeMember(name,
                System.Reflection.BindingFlags.SetProperty, null, shortcut, [value]);

            Set("TargetPath", executable);
            Set("WorkingDirectory", Path.GetDirectoryName(executable) ?? "");
            Set("Description", "Set up UnityGameTranslator in your Unity games");
            Set("IconLocation", executable + ",0");

            type.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);

            return File.Exists(path) ? [path] : [];
        }
        // COMException: a locked shell or a policy; TargetInvocationException: the shell object's own
        // refusal, through the late binding.
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException
                                       or System.Reflection.TargetInvocationException
                                   || Reading.WriteFailed(ex))
        {
            // A locked shell, a policy, a folder we may not write to. The tool is installed and
            // runnable either way; the caller says which part did not happen — the journal why.
            Faults.Say("WindowsPlatform.CreateLauncher", ex, kind.ToString());
            return [];
        }
    }

    /// <summary>
    /// The per-user uninstall key — the same place Windows reads its "Installed apps" list from.
    ///
    /// ⚠ Per-user (HKCU) because the installation is per-user: writing to the machine-wide list
    /// would need elevation and would claim an installation other accounts cannot actually run.
    ///
    /// One fixed key name, so calling this again updates the entry instead of adding another. That
    /// is not a detail: a tool that appears three times in Windows' list after two updates looks
    /// exactly like something that does not clean up after itself.
    /// </summary>
    public string? RegisterInstalled(ToolInstallation installation)
    {
        const string parent = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
        const string name = "UnityGameTranslatorManager";

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"{parent}\{name}", writable: true);
            if (key is null) return null;

            key.SetValue("DisplayName", "UnityGameTranslator Manager");
            key.SetValue("DisplayVersion", installation.Version);
            key.SetValue("Publisher", "ASymptOmatik Games");
            key.SetValue("InstallLocation", installation.Directory);
            key.SetValue("DisplayIcon", installation.Executable);

            // Opens the removal window rather than deleting behind the person's back: the button
            // in Windows' own settings has to lead to the same three questions as ours.
            key.SetValue("UninstallString", $"\"{installation.Executable}\" --remove");

            // No QuietUninstallString on purpose: there is no silent path, and advertising one we
            // do not have would let Windows remove the tool without a word.
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            key.SetValue("URLInfoAbout", BuildInfo.WebsiteBaseUrl);

            var size = SizeInKilobytes(installation);
            if (size > 0) key.SetValue("EstimatedSize", size, RegistryValueKind.DWord);

            return $@"HKCU\{parent}\{name}";
        }
        catch (Exception ex) when (RegistryFailed(ex))
        {
            // Not in Windows' list of installed apps: the caller says so, the journal why.
            Faults.Say("WindowsPlatform.RegisterInstalled", ex);
            return null;
        }
    }

    public bool IsRegistered(string registration)
    {
        const string prefix = @"HKCU\";
        if (!registration.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(registration[prefix.Length..]);
            return key is not null;
        }
        catch (Exception ex) when (RegistryFailed(ex))
        {
            // Unreadable is not the same as absent, and claiming an installation is broken on the
            // strength of a permissions error would send somebody repairing what is not wrong.
            Journal.Note("WindowsPlatform.IsRegistered", $"unreadable, taken as present ({ex.GetType().Name})");
            return true;
        }
    }

    public void UnregisterInstalled(string registration)
    {
        const string prefix = @"HKCU\";
        if (!registration.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(registration[prefix.Length..], throwOnMissingSubKey: false);
        }
        catch (Exception ex) when (RegistryFailed(ex))
        {
            // Already gone, or not ours to delete. Either way there is nothing left to do — but an
            // entry left in Windows' list is said.
            Faults.Say("WindowsPlatform.UnregisterInstalled", ex);
        }
    }

    private const string Classes = @"Software\Classes";

    /// <summary>
    /// The per-user file classes (HKCU\Software\Classes) — the same no-elevation ground as the
    /// uninstall entry above. Three things: the extension points at our class, the class carries the
    /// icon (written beside the executable) and the open command, and the extension lists the class
    /// among what may open it, so "Open with" offers UGT Manager even where somebody chose another
    /// program by hand — a choice Windows keeps elsewhere, and that this does not override.
    /// </summary>
    public IReadOnlyList<string>? RegisterPackType(ToolInstallation installation)
    {
        try
        {
            var icon = Path.Combine(installation.Directory, PackFileType.WindowsIconFile);
            File.WriteAllBytes(icon, PackFileType.Resource(PackFileType.WindowsIconFile));

            using (var type = Registry.CurrentUser.CreateSubKey($@"{Classes}\{PackFileType.ProgId}", writable: true))
            {
                type.SetValue("", PackFileType.Description);

                using var defaultIcon = type.CreateSubKey("DefaultIcon", writable: true);
                defaultIcon.SetValue("", icon);

                using var command = type.CreateSubKey(@"shell\open\command", writable: true);
                command.SetValue("", $"\"{installation.Executable}\" \"%1\"");
            }

            using (var extension = Registry.CurrentUser.CreateSubKey(
                       $@"{Classes}\{UnityGameTranslator.Common.AssetPacks.Extension}", writable: true))
            {
                extension.SetValue("", PackFileType.ProgId);

                using var openWith = extension.CreateSubKey("OpenWithProgids", writable: true);
                openWith.SetValue(PackFileType.ProgId, Array.Empty<byte>(), RegistryValueKind.None);
            }

            AssociationsChanged();
            return [icon];
        }
        catch (Exception e) when (RegistryFailed(e))
        {
            // Null is "not registered" to the caller; the key that refused is said here.
            Faults.Say("WindowsPlatform.RegisterPackType", e);
            return null;
        }
    }

    public void UnregisterPackType()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"{Classes}\{PackFileType.ProgId}", throwOnMissingSubKey: false);

            // The extension's key may carry what other programs put there: only our two entries go,
            // and the key itself only once nothing else is left in it.
            var extensionPath = $@"{Classes}\{UnityGameTranslator.Common.AssetPacks.Extension}";
            using (var extension = Registry.CurrentUser.OpenSubKey(extensionPath, writable: true))
            {
                if (extension is not null)
                {
                    if (extension.GetValue("") as string == PackFileType.ProgId) extension.DeleteValue("", throwOnMissingValue: false);

                    using (var openWith = extension.OpenSubKey("OpenWithProgids", writable: true))
                    {
                        openWith?.DeleteValue(PackFileType.ProgId, throwOnMissingValue: false);
                        if (openWith is not null && openWith.ValueCount == 0 && openWith.SubKeyCount == 0)
                        {
                            openWith.Dispose();
                            extension.DeleteSubKey("OpenWithProgids", throwOnMissingSubKey: false);
                        }
                    }
                }
            }

            using (var left = Registry.CurrentUser.OpenSubKey(extensionPath))
            {
                if (left is not null && left.ValueCount == 0 && left.SubKeyCount == 0)
                {
                    left.Dispose();
                    Registry.CurrentUser.DeleteSubKey(extensionPath, throwOnMissingSubKey: false);
                }
            }

            ForgetExplorerTraces();
            AssociationsChanged();
        }
        catch (Exception e) when (RegistryFailed(e))
        {
            // Not ours to delete after all. Nothing left to do — but what stays is said.
            Faults.Say("WindowsPlatform.UnregisterPackType", e);
        }
    }

    /// <summary>
    /// What the explorer copied on its own the first time a pack was opened: our class among the
    /// type's "Open with" entries, and our executable in its recent programs. Left behind, "Open
    /// with" would offer a program that no longer exists.
    ///
    /// ⚠ Only our two entries. The rest of that key is the person's, and UserChoice — their
    /// "Always use this app" — is protected by Windows and never touched.
    /// </summary>
    private void ForgetExplorerTraces()
    {
        var root = $@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{UnityGameTranslator.Common.AssetPacks.Extension}";

        using (var progIds = Registry.CurrentUser.OpenSubKey(root + @"\OpenWithProgids", writable: true))
            progIds?.DeleteValue(PackFileType.ProgId, throwOnMissingValue: false);

        using var recent = Registry.CurrentUser.OpenSubKey(root + @"\OpenWithList", writable: true);
        if (recent is null) return;

        // Letters naming programs, and MRUList giving their order: ours goes from both.
        var order = recent.GetValue("MRUList") as string ?? "";
        foreach (var name in recent.GetValueNames().Where(n => n.Length == 1))
        {
            if (!string.Equals(recent.GetValue(name) as string, ExecutableFileName, StringComparison.OrdinalIgnoreCase)) continue;

            recent.DeleteValue(name, throwOnMissingValue: false);
            order = order.Replace(name, "", StringComparison.Ordinal);
        }

        if (order.Length > 0) recent.SetValue("MRUList", order);
        else recent.DeleteValue("MRUList", throwOnMissingValue: false);
    }

    public PackTypeState PackTypeStateFor(ToolInstallation installation)
    {
        try
        {
            var extension = UnityGameTranslator.Common.AssetPacks.Extension;
            using var ext = Registry.CurrentUser.OpenSubKey($@"{Classes}\{extension}");
            using var command = Registry.CurrentUser.OpenSubKey($@"{Classes}\{PackFileType.ProgId}\shell\open\command");

            if (ext?.GetValue("") as string != PackFileType.ProgId || command is null) return PackTypeState.Absent;

            if (!string.Equals(command.GetValue("") as string, $"\"{installation.Executable}\" \"%1\"",
                               StringComparison.OrdinalIgnoreCase))
                return PackTypeState.Stale;

            // The person's own "Always use this app" lives here, protected by Windows: a program may
            // not rewrite it, only tell them how to change it.
            using var choice = Registry.CurrentUser.OpenSubKey(
                $@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{extension}\UserChoice");
            var chosen = choice?.GetValue("ProgId") as string;

            return chosen is not null && !string.Equals(chosen, PackFileType.ProgId, StringComparison.OrdinalIgnoreCase)
                ? PackTypeState.OverriddenByUser
                : PackTypeState.Ours;
        }
        catch (Exception e) when (RegistryFailed(e))
        {
            // Absent is what is offered again; that it could not be read is said.
            Faults.Say("WindowsPlatform.PackTypeStateFor", e);
            return PackTypeState.Absent;
        }
    }

    /// <summary>
    /// Tells the shell the file types changed, so the explorer shows the icon now rather than
    /// after a sign-out.
    /// </summary>
    private static void AssociationsChanged() => SHChangeNotify(SHCNE_ASSOCCHANGED, 0, IntPtr.Zero, IntPtr.Zero);

    private const int SHCNE_ASSOCCHANGED = 0x08000000;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    private static int SizeInKilobytes(ToolInstallation installation)
    {
        try
        {
            var total = installation.Files
                .Where(File.Exists)
                .Sum(file => new FileInfo(file).Length);

            return (int)Math.Min(total / 1024, int.MaxValue);
        }
        catch (Exception ex) when (Reading.Failed(ex))
        {
            // Windows' list then shows no size: noted.
            Journal.Note("WindowsPlatform.SizeInKilobytes", $"size unknown ({ex.GetType().Name})");
            return 0;
        }
    }

    /// <summary>
    /// Which .NET Desktop runtimes this machine carries — asked once per version, then remembered.
    ///
    /// ⚠ **A fact about the machine, not about a game**, and it is now on the drawing path: every
    /// report built for an IL2CPP game asks it, and the game list rebuilds every report each time a
    /// community lookup comes back. Unremembered that is a directory enumeration per IL2CPP game
    /// per answer — quadratic in the size of somebody's library, on the interface thread.
    ///
    /// ⚠ Held for the life of the process, which is the honest lifetime: somebody installing a .NET
    /// runtime while this window is open is doing it BECAUSE this tool asked them to, and the
    /// screen that asked tells them to run it again.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool?> _dotnetRuntimes = new();

    public bool? HasDotnetDesktopRuntime(string majorVersion) =>
        _dotnetRuntimes.GetOrAdd(majorVersion, LookUpDotnetDesktopRuntime);

    private bool? LookUpDotnetDesktopRuntime(string majorVersion)
    {
        // The shared framework folder is the ground truth; `dotnet --list-runtimes` needs the
        // CLI on PATH, which a player machine may not have.
        foreach (var root in new[]
                 {
                     Path.Combine(Env(Environment.SpecialFolder.ProgramFiles), "dotnet", "shared", "Microsoft.WindowsDesktop.App"),
                     Path.Combine(Env(Environment.SpecialFolder.ProgramFilesX86), "dotnet", "shared", "Microsoft.WindowsDesktop.App"),
                 })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var name = Path.GetFileName(dir);
                if (name.StartsWith(majorVersion + ".", StringComparison.Ordinal)) return true;
            }
        }

        // Absent folder is weak evidence, not proof: report "no" only if dotnet exists at all.
        var dotnetPresent =
            Directory.Exists(Path.Combine(Env(Environment.SpecialFolder.ProgramFiles), "dotnet")) ||
            Directory.Exists(Path.Combine(Env(Environment.SpecialFolder.ProgramFilesX86), "dotnet"));
        return dotnetPresent ? false : null;
    }

    /// <summary>Windows loads the proxy DLL natively — no override needed, ever.</summary>
    public bool NeedsDllOverride(GameInstall game) => false;

    public bool IsGameRunning(GameInstall game)
    {
        if (string.IsNullOrEmpty(game.Path)) return false;
        var root = NormalizeRoot(game.Path);

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var file = process.MainModule?.FileName;
                if (file is null) continue;
                if (file.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // Access denied on system processes is expected and irrelevant here — one line for
                // all of them.
                Journal.Note("WindowsPlatform.IsGameRunning", $"some processes could not be asked where they run from ({ex.GetType().Name})");
            }
            finally
            {
                process.Dispose();
            }
        }
        return false;
    }

    private static string NormalizeRoot(string path)
    {
        var full = Path.GetFullPath(path);
        return full.EndsWith(Path.DirectorySeparatorChar) ? full : full + Path.DirectorySeparatorChar;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetUserDefaultLocaleName(
        System.Text.StringBuilder localeName, int capacity);

    public string? SystemLanguage()
    {
        try
        {
            // LOCALE_NAME_MAX_LENGTH is 85; the value looks like "fr-FR".
            var buffer = new System.Text.StringBuilder(85);
            if (GetUserDefaultLocaleName(buffer, buffer.Capacity) == 0) return null;

            var locale = buffer.ToString().Trim();
            return locale.Length >= 2 ? locale : null;
        }
        // The kernel32 entry absent (Wine, an unusual Windows): the language is then asked of the
        // person.
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Journal.Note("WindowsPlatform.SystemLanguage", ex.GetType().Name);
            return null;
        }
    }

    public IEnumerable<string> FontFolders()
    {
        // The socle's one list for Windows — the folders the mod reads.
        return UnityGameTranslator.Common.SystemFontFolders
            .For(UnityGameTranslator.Common.SystemFontFolders.Os.Windows,
                 windowsDir: Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                 localAppData: Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
            .Where(Directory.Exists);
    }

    /// <summary>
    /// The machine's fonts, then this user's — the two places Windows records them. A value holds
    /// a bare file name when the font sits in the Windows folder, a full path otherwise.
    /// </summary>
    public IEnumerable<(string Name, string Path)> RegisteredFonts()
    {
        var found = new List<(string, string)>();
        var windowsFonts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");

        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            try
            {
                using var fonts = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts");
                if (fonts is null) continue;

                foreach (var name in fonts.GetValueNames())
                {
                    if (fonts.GetValue(name) is not string file || file.Length == 0) continue;

                    var path = Path.IsPathRooted(file) ? file : Path.Combine(windowsFonts, file);
                    found.Add((UnityGameTranslator.Common.SystemFontNames.RegisteredName(name), path));
                }
            }
            catch (Exception e) when (RegistryFailed(e))
            {
                // A table we may not read is a table we do not have: the file names are searched
                // next. Noted.
                Journal.Note("WindowsPlatform.RegisteredFonts", $"{hive.Name}: not read ({e.GetType().Name})");
            }
        }

        return found;
    }

    /// <summary>
    /// Read from the display adapter keys in the registry.
    ///
    /// qwMemorySize rather than WMI's Win32_VideoController.AdapterRAM: the WMI value is a 32-bit
    /// field and caps at 4 GB, so every card above that reports 4 GB — which would push someone
    /// with a 16 GB card towards a tiny model for no reason.
    ///
    /// The largest adapter wins. Laptops routinely expose both an integrated chip sharing system
    /// memory and a discrete card; the integrated one comes first in the enumeration and is not
    /// the one that will run the model.
    /// </summary>
    public long? VideoMemoryBytes()
    {
        try
        {
            using var adapters = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (adapters is null) return null;

            long largest = 0;

            foreach (var name in adapters.GetSubKeyNames())
            {
                // Only the numbered device keys hold adapters; siblings like "Configuration" do not.
                if (name.Length != 4 || !name.All(char.IsDigit)) continue;

                using var adapter = adapters.OpenSubKey(name);
                if (adapter?.GetValue("HardwareInformation.qwMemorySize") is not { } raw) continue;

                var bytes = raw switch
                {
                    long value => value,
                    int value => (long)value,
                    byte[] buffer when buffer.Length >= 8 => BitConverter.ToInt64(buffer, 0),
                    _ => 0L,
                };

                if (bytes > largest) largest = bytes;
            }

            return largest > 0 ? largest : null;
        }
        catch (Exception ex) when (RegistryFailed(ex))
        {
            // Unknown video memory: no model is recommended by size. Noted.
            Journal.Note("WindowsPlatform.VideoMemoryBytes", $"adapters not read ({ex.GetType().Name})");
            return null;
        }
    }

    private static string Env(Environment.SpecialFolder folder) =>
        Environment.GetFolderPath(folder);

    private static string? ReadRegistry(RegistryHive hive, string subKey, string name)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
            using var key = baseKey.OpenSubKey(subKey);
            return key?.GetValue(name) as string;
        }
        catch (Exception ex) when (RegistryFailed(ex))
        {
            Journal.Note("WindowsPlatform.ReadRegistry", $@"{hive}\{subKey}\{name}: not read ({ex.GetType().Name})");
            return null;
        }
    }
}
