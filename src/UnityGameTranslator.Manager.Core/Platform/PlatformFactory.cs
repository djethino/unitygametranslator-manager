using System.Runtime.InteropServices;

namespace UnityGameTranslator.Manager.Core.Platform;

public static class PlatformFactory
{
    /// <summary>
    /// Resolves the adapter for the running OS.
    ///
    /// macOS deliberately throws rather than falling back to the Linux adapter: its Steam paths,
    /// its .app bundles and its code-signing rules are all different, and a silent wrong answer
    /// would be worse than a clear "not supported yet". See analyse/feature-installer.md §11.
    /// </summary>
    public static IPlatform Create()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return new WindowsPlatform();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return new LinuxPlatform();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            throw new PlatformNotSupportedException(
                "macOS is not supported yet. Only Mono games could ever be handled there " +
                "(IL2CPP modding on macOS is not possible with current tooling).");

        throw new PlatformNotSupportedException(
            $"Unsupported operating system: {RuntimeInformation.OSDescription}");
    }
}
