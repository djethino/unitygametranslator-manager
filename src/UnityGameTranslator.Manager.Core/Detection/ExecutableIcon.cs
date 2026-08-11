namespace UnityGameTranslator.Manager.Core.Detection;

/// <summary>
/// An icon lifted out of an executable, ready for whatever draws it.
///
/// Either a PNG as the file stored it, or raw BGRA rows top-down — the two forms Windows icons
/// actually come in. The caller decides how to turn that into an image; this layer draws nothing.
/// </summary>
public sealed record ExecutableIcon(byte[] Data, bool IsPng, int Width, int Height);

/// <summary>
/// Reads the icon a Windows executable carries, by parsing the file rather than asking the system.
///
/// ⚠ This exists so the icon works where System.Drawing cannot go, and that is not a corner case:
/// **most games played on Linux are Windows games running under Proton or Wine**, and their .exe
/// is right there with its icon inside. Same for Wine on macOS — CrossOver, Whisky, Apple's Game
/// Porting Toolkit. Native Linux builds are the minority, and they are the one case with genuinely
/// nothing to read: an ELF holds no icon at all, the desktop keeps it in a .desktop file and a
/// theme.
///
/// Being pure file reading, it also works on Windows, where it can eventually replace the
/// System.Drawing path and leave one code path instead of two.
///
/// What it does NOT do, deliberately:
/// - palette icons (4 and 8 bits per pixel) are refused rather than half-decoded. They belong to
///   executables from another era, and a wrong colour table draws garbage that looks like a bug in
///   the icon itself.
/// - it takes one size and stops. Picking the best available beats decoding all of them for a
///   28-pixel row.
/// </summary>
public static class ExecutableIconReader
{
    private const int ResourceTypeIcon = 3;
    private const int ResourceTypeGroupIcon = 14;

    /// <summary>
    /// The best icon in the file at or below <paramref name="preferredSize"/>, or null.
    ///
    /// Never throws: a packed executable, a truncated file or a format we do not read all mean the
    /// same thing to the caller — no icon — and none of them is worth a message.
    /// </summary>
    public static ExecutableIcon? Read(string path, int preferredSize = 64)
    {
        try
        {
            if (!File.Exists(path)) return null;

            var bytes = File.ReadAllBytes(path);
            return Parse(bytes, preferredSize);
        }
        catch
        {
            return null;
        }
    }

    private static ExecutableIcon? Parse(byte[] pe, int preferredSize)
    {
        // --- DOS header: "MZ", then the PE header's offset at 0x3C ---
        if (pe.Length < 0x40 || pe[0] != 'M' || pe[1] != 'Z') return null;

        var peOffset = BitConverter.ToInt32(pe, 0x3C);
        if (peOffset <= 0 || peOffset + 24 > pe.Length) return null;
        if (BitConverter.ToUInt32(pe, peOffset) != 0x00004550) return null; // "PE\0\0"

        var sectionCount = BitConverter.ToUInt16(pe, peOffset + 6);
        var optionalHeaderSize = BitConverter.ToUInt16(pe, peOffset + 20);
        var optionalHeader = peOffset + 24;
        if (optionalHeader + optionalHeaderSize > pe.Length) return null;

        // PE32 keeps the data directories at +96, PE32+ at +112: the difference is the 64-bit
        // image base and a few fields that grew with it.
        var magic = BitConverter.ToUInt16(pe, optionalHeader);
        var dataDirectories = optionalHeader + magic switch
        {
            0x10B => 96,
            0x20B => 112,
            _ => -1,
        };
        if (dataDirectories < 0) return null;

        // Directory 2 is the resources.
        var resourceRva = BitConverter.ToUInt32(pe, dataDirectories + 2 * 8);
        if (resourceRva == 0) return null;

        var sections = ReadSections(pe, optionalHeader + optionalHeaderSize, sectionCount);
        var resourceRoot = ToFileOffset(sections, resourceRva);
        if (resourceRoot < 0 || resourceRoot >= pe.Length) return null;

        // --- The group tells us which sizes exist and which RT_ICON holds each one ---
        var group = FindResourceData(pe, sections, resourceRoot, ResourceTypeGroupIcon);
        if (group is null) return null;

        var chosen = ChooseFromGroup(pe, group.Value.Offset, group.Value.Size, preferredSize);
        if (chosen is null) return null;

        var icon = FindResourceData(pe, sections, resourceRoot, ResourceTypeIcon, chosen.Value.Id);
        if (icon is null) return null;

        var data = new byte[icon.Value.Size];
        Array.Copy(pe, icon.Value.Offset, data, 0, icon.Value.Size);

        // Icons of 256 pixels and above are stored as PNG since Vista; anything else is a DIB.
        if (data.Length > 8 && data[0] == 0x89 && data[1] == 'P' && data[2] == 'N' && data[3] == 'G')
            return new ExecutableIcon(data, IsPng: true, chosen.Value.Width, chosen.Value.Height);

        return DecodeDib(data);
    }

    private readonly record struct Section(uint VirtualAddress, uint VirtualSize, uint RawAddress, uint RawSize);

    private static List<Section> ReadSections(byte[] pe, int offset, int count)
    {
        var sections = new List<Section>(count);

        for (var i = 0; i < count; i++)
        {
            var at = offset + i * 40;
            if (at + 40 > pe.Length) break;

            sections.Add(new Section(
                VirtualAddress: BitConverter.ToUInt32(pe, at + 12),
                VirtualSize: BitConverter.ToUInt32(pe, at + 8),
                RawAddress: BitConverter.ToUInt32(pe, at + 20),
                RawSize: BitConverter.ToUInt32(pe, at + 16)));
        }

        return sections;
    }

    /// <summary>
    /// Turns an address as the loader would see it into a position in the file on disk. The two
    /// differ because sections are padded differently in memory and on disk.
    /// </summary>
    private static int ToFileOffset(List<Section> sections, uint rva)
    {
        foreach (var section in sections)
        {
            if (rva >= section.VirtualAddress && rva < section.VirtualAddress + Math.Max(section.VirtualSize, section.RawSize))
                return (int)(section.RawAddress + (rva - section.VirtualAddress));
        }

        return -1;
    }

    /// <summary>
    /// Walks the three-level resource tree — type, then name or id, then language — and returns
    /// where the first matching leaf's bytes are.
    ///
    /// <paramref name="wantedId"/> selects one entry at the second level; without it the first is
    /// taken, which for a group icon is the application's main one.
    /// </summary>
    private static (int Offset, int Size)? FindResourceData(byte[] pe, List<Section> sections,
                                                            int root, int type, int? wantedId = null)
    {
        var typeEntry = FindEntry(pe, root, root, type);
        if (typeEntry is not { IsDirectory: true }) return null;

        var nameDirectory = root + typeEntry.Value.Offset;
        var nameEntry = wantedId is null
            ? FirstEntry(pe, nameDirectory)
            : FindEntry(pe, root, nameDirectory, wantedId.Value);

        if (nameEntry is not { IsDirectory: true }) return null;

        var languageEntry = FirstEntry(pe, root + nameEntry.Value.Offset);
        if (languageEntry is not { IsDirectory: false }) return null;

        // A leaf points at an IMAGE_RESOURCE_DATA_ENTRY: an RVA and a length.
        var leaf = root + languageEntry.Value.Offset;
        if (leaf + 8 > pe.Length) return null;

        var dataRva = BitConverter.ToUInt32(pe, leaf);
        var size = (int)BitConverter.ToUInt32(pe, leaf + 4);
        var offset = ToFileOffset(sections, dataRva);

        if (offset < 0 || size <= 0 || offset + size > pe.Length) return null;

        return (offset, size);
    }

    private readonly record struct Entry(bool IsDirectory, int Offset);

    private static Entry? FindEntry(byte[] pe, int root, int directory, int id)
    {
        if (directory + 16 > pe.Length) return null;

        // Named entries come first, then the ones keyed by id; only the latter interest us.
        var named = BitConverter.ToUInt16(pe, directory + 12);
        var byId = BitConverter.ToUInt16(pe, directory + 14);
        var first = directory + 16 + named * 8;

        for (var i = 0; i < byId; i++)
        {
            var at = first + i * 8;
            if (at + 8 > pe.Length) break;

            if (BitConverter.ToUInt32(pe, at) != (uint)id) continue;

            var raw = BitConverter.ToUInt32(pe, at + 4);
            return new Entry((raw & 0x80000000) != 0, (int)(raw & 0x7FFFFFFF));
        }

        return null;
    }

    private static Entry? FirstEntry(byte[] pe, int directory)
    {
        if (directory + 16 > pe.Length) return null;

        var total = BitConverter.ToUInt16(pe, directory + 12) + BitConverter.ToUInt16(pe, directory + 14);
        if (total == 0 || directory + 24 > pe.Length) return null;

        var raw = BitConverter.ToUInt32(pe, directory + 16 + 4);
        return new Entry((raw & 0x80000000) != 0, (int)(raw & 0x7FFFFFFF));
    }

    /// <summary>
    /// Picks a size out of the group directory: the largest that still fits the request, or the
    /// smallest available when every one of them is bigger.
    /// </summary>
    private static (int Id, int Width, int Height)? ChooseFromGroup(byte[] pe, int offset, int size,
                                                                   int preferredSize)
    {
        if (size < 6) return null;

        var count = BitConverter.ToUInt16(pe, offset + 4);
        (int Id, int Width, int Height)? best = null;

        for (var i = 0; i < count; i++)
        {
            var at = offset + 6 + i * 14;
            if (at + 14 > pe.Length || at + 14 > offset + size) break;

            // A stored 0 means 256: the field is one byte and the format predates that size.
            int width = pe[at] == 0 ? 256 : pe[at];
            int height = pe[at + 1] == 0 ? 256 : pe[at + 1];
            int id = BitConverter.ToUInt16(pe, at + 12);

            if (best is null)
            {
                best = (id, width, height);
                continue;
            }

            var fits = width <= preferredSize;
            var bestFits = best.Value.Width <= preferredSize;

            // Bigger is better while it fits; once nothing fits, smaller is the lesser evil —
            // scaling a 256px icon down to 28 costs memory for nothing.
            var better = (fits, bestFits) switch
            {
                (true, false) => true,
                (true, true) => width > best.Value.Width,
                (false, false) => width < best.Value.Width,
                _ => false,
            };

            if (better) best = (id, width, height);
        }

        return best;
    }

    /// <summary>
    /// Decodes the 32-bit form of an icon bitmap into top-down BGRA.
    ///
    /// Two quirks of the format, both load-bearing: the header claims **twice** the real height
    /// because a monochrome mask used to follow the colours, and the rows are stored bottom-up.
    /// Reading either at face value gives an icon that is half its size and upside down.
    ///
    /// Palette depths are refused rather than approximated — see the class note.
    /// </summary>
    private static ExecutableIcon? DecodeDib(byte[] dib)
    {
        if (dib.Length < 40) return null;

        var headerSize = BitConverter.ToInt32(dib, 0);
        var width = BitConverter.ToInt32(dib, 4);
        var storedHeight = BitConverter.ToInt32(dib, 8);
        var bitCount = BitConverter.ToUInt16(dib, 14);

        if (headerSize < 40 || bitCount != 32) return null;

        var height = storedHeight / 2;
        if (width <= 0 || height <= 0 || width > 512 || height > 512) return null;

        var pixels = headerSize;
        var needed = width * height * 4;
        if (pixels + needed > dib.Length) return null;

        var bgra = new byte[needed];

        for (var y = 0; y < height; y++)
        {
            var source = pixels + (height - 1 - y) * width * 4;
            Array.Copy(dib, source, bgra, y * width * 4, width * 4);
        }

        return new ExecutableIcon(bgra, IsPng: false, width, height);
    }
}
