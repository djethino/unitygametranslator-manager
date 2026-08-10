using System.Globalization;

namespace UnityGameTranslator.Installer.Core.Platform;

/// <summary>
/// Holds the right to be the running copy of this tool, and says who has it otherwise.
///
/// Why it matters here rather than as a nicety: two windows share one settings file, one folder of
/// receipts and one catalogue cache. Whichever saves last wins, and the other keeps showing values
/// that are no longer true — the same shape of bug as the token that was silently emptied by a
/// window editing a copy of itself. Two copies updating the tool at the same moment is worse
/// still: both rename the running binary, and only one of the two names means anything afterwards.
///
/// ⚠ Deliberately NOT applied to the command line. Asking a question about your games while the
/// window is open must always work; refusing would be surprising and would break the one path
/// support relies on. What is guarded is the window.
///
/// A lock file rather than a named mutex, because a named mutex is not shared between processes on
/// Unix — it would have guarded Windows and quietly done nothing on the Steam Deck. The operating
/// system drops the lock when the process ends, including when it is killed, so there is no stale
/// state to clean up and no "it says it is already running when it is not".
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string FileName = "instance.lock";

    private readonly FileStream? _held;

    private SingleInstance(FileStream? held) => _held = held;

    /// <summary>
    /// The right to run, granted without a lock, for the cases where no lock can be taken: a
    /// system we have no adapter for, a profile we cannot write to. Failing open is the right way
    /// round — the cost of two windows is confusion, the cost of none is a tool that will not open.
    /// </summary>
    public static SingleInstance Unguarded() => new(null);

    /// <summary>The process id written by whoever holds it, when one can be read.</summary>
    public static int? HolderProcessId { get; private set; }

    /// <summary>
    /// Takes the right to run, or returns null when another copy already has it.
    ///
    /// Never throws: a machine where the lock cannot be created at all (a read-only profile, an
    /// exotic filesystem) must still be able to open the tool. Failing open is the right way round
    /// — the cost of two windows is confusion, the cost of none is a tool that will not start.
    /// </summary>
    public static SingleInstance? TryAcquire(IPlatform platform)
    {
        string path;
        try
        {
            Directory.CreateDirectory(platform.UserDataDirectory);
            path = Path.Combine(platform.UserDataDirectory, FileName);
        }
        catch
        {
            return Unguarded();
        }

        try
        {
            // Shared for reading so the second copy can find out who to bring forward; not shared
            // for writing, which is what makes this a lock at all.
            var held = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);

            var id = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
            var bytes = System.Text.Encoding.UTF8.GetBytes(id);
            held.Write(bytes, 0, bytes.Length);
            held.Flush();

            HolderProcessId = Environment.ProcessId;
            return new SingleInstance(held);
        }
        catch (IOException)
        {
            HolderProcessId = ReadHolder(path);
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            // Not ours to lock. Better two windows than none.
            return Unguarded();
        }
    }

    private static int? ReadHolder(string path)
    {
        try
        {
            using var reader = new FileStream(path, FileMode.Open, FileAccess.Read,
                                              FileShare.ReadWrite);
            var buffer = new byte[32];
            var read = reader.Read(buffer, 0, buffer.Length);
            var text = System.Text.Encoding.UTF8.GetString(buffer, 0, read).Trim();

            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
                ? id
                : null;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        try { _held?.Dispose(); } catch { /* the process is ending anyway */ }
    }
}
