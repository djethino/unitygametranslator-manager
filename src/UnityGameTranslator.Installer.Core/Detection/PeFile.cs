using UnityGameTranslator.Installer.Core.Model;

namespace UnityGameTranslator.Installer.Core.Detection;

/// <summary>
/// Just enough PE reading to answer "32 or 64 bit?".
///
/// It matters: BepInEx ships separate x86 and x64 archives, and there are still 32-bit Unity
/// games in the wild. Picking the wrong one produces a game that silently starts without the
/// loader — the most confusing failure we could hand a user.
/// </summary>
public static class PeFile
{
    private const ushort ImageFileMachineI386 = 0x014c;
    private const ushort ImageFileMachineAmd64 = 0x8664;
    private const ushort ImageFileMachineArm64 = 0xAA64;

    public static GameArchitecture ReadArchitecture(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);

            if (stream.Length < 0x40) return GameArchitecture.Unknown;
            if (reader.ReadUInt16() != 0x5A4D) return GameArchitecture.Unknown; // "MZ"

            stream.Position = 0x3C;
            var peHeaderOffset = reader.ReadInt32();
            if (peHeaderOffset <= 0 || peHeaderOffset + 6 > stream.Length) return GameArchitecture.Unknown;

            stream.Position = peHeaderOffset;
            if (reader.ReadUInt32() != 0x00004550) return GameArchitecture.Unknown; // "PE\0\0"

            return reader.ReadUInt16() switch
            {
                ImageFileMachineI386 => GameArchitecture.X86,
                ImageFileMachineAmd64 => GameArchitecture.X64,
                ImageFileMachineArm64 => GameArchitecture.Arm64,
                _ => GameArchitecture.Unknown,
            };
        }
        catch
        {
            return GameArchitecture.Unknown;
        }
    }

    /// <summary>
    /// Version resource of a PE file. Used as a fallback for the Unity version and to read an
    /// installed loader's version. Returns null rather than a partial guess.
    /// </summary>
    public static string? ReadFileVersion(string path)
    {
        try
        {
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
            var version = info.FileVersion;
            return string.IsNullOrWhiteSpace(version) ? null : version.Trim();
        }
        catch
        {
            return null;
        }
    }
}
