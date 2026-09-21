using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace UnityGameTranslator.Manager.Core.Install;

/// <summary>
/// Reads Unity's engine modules out of Unity's own "Windows Build Support (Mono)" package, as the
/// download server hands it over — and stops as soon as it has them.
///
/// ⚠ **The format, as read on 2026-09-21** (2021.3.6f1, `…-Windows-Mono-Support-for-Editor-….pkg`):
///   · a xar archive: "xar!", a big-endian header, a zlib-compressed XML table of contents, then a
///     heap holding each file at the offset the table gives;
///   · the file that matters is `Payload`, stored as it is — a gzip stream of a cpio archive in the
///     portable ("odc", `070707`) format;
///   · `./Variations/mono/Managed/UnityEngine*.dll` comes FIRST in it, within the first ~8 MB of a
///     550 MB package. Same modules as the player variations (same IL, measured byte for byte
///     outside the build stamp and the signature), signed by Unity.
///
/// ⚠ **Nothing in the archive decides where a file is written.** Only an engine module's own file
/// name is taken from an entry, matched against a strict pattern; a path, a link or anything else is
/// read past. The files are then held to the same checks as any other source (signature included)
/// before one goes near a game.
/// </summary>
public static class UnityPackage
{
    private static readonly Regex ModuleFile = new(@"^UnityEngine(\.[A-Za-z0-9]+Module)?\.dll$", RegexOptions.CultureInvariant);

    /// <summary>
    /// The folders the modules may be taken from. The first one met in the stream is the one used,
    /// whole. Read on real packages (2026-09-21): `Managed/` at the top in 2018 (Windows and Linux
    /// alike, the same bytes), `Variations/mono/Managed/` first in 2021's Windows package, and the
    /// player's own `…_player_nondevelopment_mono/Data/Managed/` in 2021's Linux one — which has no
    /// shared folder, its modules being built per platform.
    /// </summary>
    private static readonly Regex ModuleFolder = new(
        @"^(Managed|Variations/(mono/Managed|(win(32|64)|linux(32|64))_player_nondevelopment_mono/Data/Managed))$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// Reads <paramref name="package"/> until the engine modules have gone by, writing each one
    /// into <paramref name="destination"/>. Returns their names ("UnityEngine.CoreModule").
    /// </summary>
    public static IReadOnlyList<string> ExtractEngineModules(Stream package, string destination)
    {
        Directory.CreateDirectory(destination);

        // ── xar header and table of contents ──
        var header = ReadExactly(package, 28);
        if (Encoding.ASCII.GetString(header, 0, 4) != "xar!")
            throw new InvalidDataException("Unity's package is not in the format this tool reads (no xar header).");

        var headerSize = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
        var tocCompressed = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8));
        if (headerSize < 28 || tocCompressed > 64 * 1024 * 1024)
            throw new InvalidDataException("Unity's package has a table of contents this tool cannot read.");

        Skip(package, headerSize - 28);
        var toc = ReadExactly(package, (int)tocCompressed);
        long position = headerSize + (long)tocCompressed;
        var heap = position;

        var payload = FindPayload(toc)
            ?? throw new InvalidDataException("Unity's package holds no Payload where this tool expects one.");

        // ── the Payload: skip to it, then read it as it streams ──
        var start = heap + payload.Offset;
        if (start < position) throw new InvalidDataException("Unity's package places its Payload before its own table of contents.");
        Skip(package, start - position);

        using var slice = new Slice(package, payload.Length);
        using Stream stored = payload.ZlibEncoded ? new ZLibStream(slice, CompressionMode.Decompress) : slice;
        using var cpio = new GZipStream(stored, CompressionMode.Decompress);

        return ReadModules(cpio, destination);
    }

    private sealed record PayloadEntry(long Offset, long Length, bool ZlibEncoded);

    /// <summary>The Payload's place in the heap, from the table of contents.</summary>
    private static PayloadEntry? FindPayload(byte[] compressedToc)
    {
        using var zlib = new ZLibStream(new MemoryStream(compressedToc), CompressionMode.Decompress);

        // ⚠ No DTD, no external entity: this XML came over the network.
        using var reader = XmlReader.Create(zlib, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var document = XDocument.Load(reader);

        foreach (var file in document.Descendants("file"))
        {
            if ((string?)file.Element("name") != "Payload" || file.Element("data") is not { } data) continue;

            var encoding = (string?)data.Element("encoding")?.Attribute("style") ?? "";
            return new PayloadEntry(long.Parse((string)data.Element("offset")!), long.Parse((string)data.Element("length")!),
                                    encoding.Contains("gzip", StringComparison.OrdinalIgnoreCase));
        }

        return null;
    }

    /// <summary>
    /// The cpio entries, up to the end of the first module folder met: its engine modules written
    /// out, everything else read past.
    /// </summary>
    private static IReadOnlyList<string> ReadModules(Stream cpio, string destination)
    {
        var taken = new List<string>();
        string? folder = null;

        while (true)
        {
            var header = ReadExactly(cpio, 76);
            if (Encoding.ASCII.GetString(header, 0, 6) != "070707")
                throw new InvalidDataException("Unity's package holds an archive in a format this tool does not read.");

            var nameSize = Octal(header, 59, 6);
            var fileSize = Octal(header, 65, 11);
            if (nameSize is <= 0 or > 4096 || fileSize < 0)
                throw new InvalidDataException("Unity's package holds an entry this tool cannot read.");

            var name = Encoding.UTF8.GetString(ReadExactly(cpio, (int)nameSize), 0, (int)nameSize - 1);
            if (name == "TRAILER!!!") break;

            var path = name.StartsWith("./", StringComparison.Ordinal) ? name[2..] : name;
            var slash = path.LastIndexOf('/');
            var parent = slash < 0 ? "" : path[..slash];
            var fileName = path[(slash + 1)..];

            // The folder is over once an entry outside it arrives: the modules have all gone by.
            if (folder is not null && !parent.Equals(folder, StringComparison.OrdinalIgnoreCase) && taken.Count > 0) break;

            if (ModuleFile.IsMatch(fileName) && ModuleFolder.IsMatch(parent)
                && (folder is null || parent.Equals(folder, StringComparison.OrdinalIgnoreCase)))
            {
                folder = parent;

                // A module is a few megabytes; an entry claiming more than the package's own table
                // could hold is not one.
                if (fileSize > 256L * 1024 * 1024)
                    throw new InvalidDataException($"Unity's package lists {fileName} at an impossible size.");

                using (var target = File.Create(Path.Combine(destination, fileName)))
                    CopyExactly(cpio, target, fileSize);

                taken.Add(Path.GetFileNameWithoutExtension(fileName));
                continue;
            }

            Skip(cpio, fileSize);
        }

        if (taken.Count == 0)
            throw new InvalidDataException("Unity's package holds no engine modules where this tool expects them.");

        return taken;
    }

    // ── Reading exactly what is asked ─────────────────────────────────────────────────────────

    private static long Octal(byte[] header, int offset, int length)
    {
        long value = 0;
        for (var i = offset; i < offset + length; i++)
        {
            var c = header[i];
            if (c < (byte)'0' || c > (byte)'7') return -1;
            value = value * 8 + (c - '0');
        }

        return value;
    }

    private static byte[] ReadExactly(Stream stream, int count)
    {
        var buffer = new byte[count];
        stream.ReadExactly(buffer);
        return buffer;
    }

    private static void Skip(Stream stream, long count)
    {
        var buffer = new byte[81920];
        while (count > 0)
        {
            var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
            if (read == 0) throw new EndOfStreamException("Unity's package ended before its contents did.");
            count -= read;
        }
    }

    private static void CopyExactly(Stream source, Stream target, long count)
    {
        var buffer = new byte[81920];
        while (count > 0)
        {
            var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
            if (read == 0) throw new EndOfStreamException("Unity's package ended in the middle of a module.");
            target.Write(buffer, 0, read);
            count -= read;
        }
    }

    /// <summary>
    /// The next <c>length</c> bytes of a stream, and not one more. Disposing it leaves the outer
    /// stream open: that one is the caller's, and ending it is ending the download.
    /// </summary>
    private sealed class Slice : Stream
    {
        private readonly Stream _inner;
        private long _left;

        public Slice(Stream inner, long length) { _inner = inner; _left = length; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_left <= 0) return 0;
            var read = _inner.Read(buffer, offset, (int)Math.Min(count, _left));
            _left -= read;
            return read;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
