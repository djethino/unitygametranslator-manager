using UnityGameTranslator.Common;

namespace UnityGameTranslator.Manager.Core.Platform;

/// <summary>
/// The .ugtpack file type as the installed tool declares it to the system: its icon, its name, and
/// UGT Manager as what opens it — so a pack gets its own icon in the file explorer and a
/// double-click lands on the game it was made for.
///
/// ⚠ **Declared by an installed copy only** (SelfInstaller). A portable executable can be moved or
/// deleted, and an association pointing at where it used to be opens nothing. Each system writes
/// it its own way (IPlatform.RegisterPackType); what they share is here.
/// </summary>
/// <summary>Where the .ugtpack declaration stands on this system.</summary>
public enum PackTypeState
{
    /// <summary>Nothing of ours is declared.</summary>
    Absent,

    /// <summary>Declared, and pointing at this installation's executable.</summary>
    Ours,

    /// <summary>Declared, but pointing elsewhere — an older folder, a copy since moved.</summary>
    Stale,

    /// <summary>Ours is declared, but the person chose another program for the type (Windows keeps that choice apart).</summary>
    OverriddenByUser,
}

public static class PackFileType
{
    /// <summary>The name the system shows for the type — the file picker says the same.</summary>
    public const string Description = "UGT asset pack";

    /// <summary>Windows: the class the extension points at, holding the icon and the open command.</summary>
    public const string ProgId = "UnityGameTranslator.AssetPack";

    /// <summary>freedesktop: the type the glob declares, and the icon name the theme looks it up by.</summary>
    public const string MimeType = "application/x-ugtpack";

    public const string IconName = "application-x-ugtpack";

    /// <summary>Written into the installation folder, so it leaves with the tool.</summary>
    public const string WindowsIconFile = "ugtpack.ico";

    /// <summary>The hicolor sizes rendered (PackIcon/make-pack-icon.py) and embedded.</summary>
    public static readonly int[] IconSizes = [16, 22, 24, 32, 48, 64, 128, 256];

    public static string PngFile(int size) => $"ugtpack-{size}.png";

    /// <summary>One of the embedded icon files.</summary>
    public static byte[] Resource(string fileName)
    {
        var name = "UnityGameTranslator.Manager.Core.Resources." + fileName;
        using var stream = typeof(PackFileType).Assembly.GetManifestResourceStream(name)
                           ?? throw new InvalidOperationException(name + " is not embedded in this build.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    /// <summary>
    /// The pack a launch was handed — the file a double-click passes, as a full path — or null.
    ///
    /// ⚠ Only an existing file with the extension. The same test guards what another copy of the
    /// tool hands over (PackHandoff): whatever arrives there is a path to OPEN, never anything else.
    /// </summary>
    public static string? PackIn(IEnumerable<string> args)
    {
        foreach (var arg in args)
        {
            if (string.IsNullOrWhiteSpace(arg)) continue;
            if (!arg.EndsWith(AssetPacks.Extension, StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                var full = Path.GetFullPath(arg);
                if (File.Exists(full)) return full;
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Not a path at all: it is not a pack either.
            }
        }

        return null;
    }
}
