using System.Buffers.Binary;
using System.Text;

namespace UniGetUI.PackageEngine.Managers.WinInstallerManager.Internal;

internal sealed record PeInfo(
    ushort Machine,
    IReadOnlyList<string> Sections,
    IReadOnlyDictionary<string, string> Strings,
    string? FixedProductVersion,
    string? FixedFileVersion,
    byte[] OverlayHead,
    byte[]? Icon)
{
    public string? String(string key) =>
        Strings.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
}

internal static class PeReader
{
    public const ushort MachineI386 = 0x014C;
    public const ushort MachineAmd64 = 0x8664;
    public const ushort MachineArm64 = 0xAA64;

    private const int OverlayHeadBytes = 1024 * 1024;
    private const int MaxResourceBytes = 4 * 1024 * 1024;
    private const int MaxResourceEntries = 4096;
    private const int MaxSections = 96;

    private const uint ResourceIcon = 3;
    private const uint ResourceGroupIcon = 14;
    private const uint ResourceVersion = 16;

    private readonly record struct Section(string Name, uint VirtualSize, uint VirtualAddress, uint RawSize, uint RawPointer);
    private readonly record struct Resource(uint Type, uint Id, uint DataRva, uint Size);

    public static PeInfo Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess);
        return Read(stream);
    }

    public static PeInfo Read(Stream stream)
    {
        long length = stream.Length;
        Span<byte> dos = stackalloc byte[64];
        ReadAt(stream, 0, dos, "the DOS header");
        if (BinaryPrimitives.ReadUInt16LittleEndian(dos) != 0x5A4D)
            throw new InvalidDataException("It is not a Windows executable.");
        long peOffset = BinaryPrimitives.ReadUInt32LittleEndian(dos[0x3C..]);

        Span<byte> coff = stackalloc byte[24];
        ReadAt(stream, peOffset, coff, "the PE header");
        if (BinaryPrimitives.ReadUInt32LittleEndian(coff) != 0x00004550)
            throw new InvalidDataException("It is not a Windows executable.");
        ushort machine = BinaryPrimitives.ReadUInt16LittleEndian(coff[4..]);
        int sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(coff[6..]);
        int optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(coff[20..]);
        if (sectionCount > MaxSections)
            throw new InvalidDataException("Its PE header declares too many sections.");

        byte[] optional = new byte[optionalSize];
        ReadAt(stream, peOffset + 24, optional, "the optional header");
        if (optional.Length < 2)
            throw new InvalidDataException("Its PE header has no optional header.");
        ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(optional);
        bool plus = magic switch
        {
            0x20B => true,
            0x10B => false,
            _ => throw new InvalidDataException("Its optional header has an unknown format."),
        };
        int directoriesOffset = plus ? 112 : 96;
        int directoryCount = optional.Length >= directoriesOffset
            ? (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(optional.AsSpan(directoriesOffset - 4)), 16)
            : 0;
        (uint Address, uint Size) Directory(int index) =>
            index < directoryCount && optional.Length >= directoriesOffset + 8 * (index + 1)
                ? (BinaryPrimitives.ReadUInt32LittleEndian(optional.AsSpan(directoriesOffset + 8 * index)),
                    BinaryPrimitives.ReadUInt32LittleEndian(optional.AsSpan(directoriesOffset + 8 * index + 4)))
                : (0, 0);

        byte[] table = new byte[sectionCount * 40];
        ReadAt(stream, peOffset + 24 + optionalSize, table, "the section table");
        var sections = new Section[sectionCount];
        long imageEnd = peOffset + 24 + optionalSize + table.Length;
        for (int i = 0; i < sectionCount; i++)
        {
            var entry = table.AsSpan(i * 40, 40);
            string name = Encoding.ASCII.GetString(entry[..8]).TrimEnd('\0');
            sections[i] = new Section(name,
                BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]),
                BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]),
                BinaryPrimitives.ReadUInt32LittleEndian(entry[16..]),
                BinaryPrimitives.ReadUInt32LittleEndian(entry[20..]));
            if (sections[i].RawSize > 0)
                imageEnd = Math.Max(imageEnd, (long)sections[i].RawPointer + sections[i].RawSize);
        }

        imageEnd = Math.Min(imageEnd, length);
        var (certificateOffset, _) = Directory(4);
        long overlayEnd = certificateOffset >= imageEnd && certificateOffset <= length ? certificateOffset : length;
        byte[] overlay = new byte[(int)Math.Min(OverlayHeadBytes, Math.Max(overlayEnd - imageEnd, 0))];
        ReadAt(stream, imageEnd, overlay, "the installer data");

        var resources = ReadResources(stream, sections, Directory(2).Address);
        var (strings, productVersion, fileVersion) = resources.FirstOrDefault(r => r.Type == ResourceVersion) is { Size: > 0 } version
            ? ParseVersionInfo(ReadResource(stream, sections, version))
            : (new Dictionary<string, string>(), null, null);

        return new PeInfo(machine, sections.Select(s => s.Name).ToArray(), strings, productVersion, fileVersion, overlay,
            ReadIcon(stream, sections, resources));
    }

    private static void ReadAt(Stream stream, long offset, Span<byte> buffer, string what)
    {
        if (offset < 0 || offset + buffer.Length > stream.Length)
            throw new InvalidDataException($"The file ends before {what}.");
        stream.Position = offset;
        stream.ReadExactly(buffer);
    }

    private static long? FileOffset(IReadOnlyList<Section> sections, uint rva, uint size)
    {
        foreach (var section in sections)
        {
            uint span = Math.Max(section.VirtualSize, section.RawSize);
            if (rva >= section.VirtualAddress && rva - section.VirtualAddress < span)
            {
                uint delta = rva - section.VirtualAddress;
                if ((long)delta + size > section.RawSize)
                    return null;
                return (long)section.RawPointer + delta;
            }
        }
        return null;
    }

    private static List<Resource> ReadResources(Stream stream, IReadOnlyList<Section> sections, uint rootRva)
    {
        var resources = new List<Resource>();
        if (rootRva == 0 || FileOffset(sections, rootRva, 16) is null)
            return resources;
        int budget = MaxResourceEntries;

        IEnumerable<(uint Id, uint Offset, bool IsDirectory)> Entries(uint directoryOffset)
        {
            if (FileOffset(sections, rootRva + directoryOffset, 16) is not { } at)
                yield break;
            byte[] header = new byte[16];
            ReadAt(stream, at, header, "a resource directory");
            int count = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(12)) + BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(14));
            if (count > budget)
                throw new InvalidDataException("Its resources are too many to read.");
            budget -= count;
            byte[] entries = new byte[count * 8];
            if (FileOffset(sections, rootRva + directoryOffset + 16, (uint)entries.Length) is not { } entriesAt)
                yield break;
            ReadAt(stream, entriesAt, entries, "a resource directory");
            for (int i = 0; i < count; i++)
            {
                uint name = BinaryPrimitives.ReadUInt32LittleEndian(entries.AsSpan(i * 8));
                uint offset = BinaryPrimitives.ReadUInt32LittleEndian(entries.AsSpan(i * 8 + 4));
                yield return ((name & 0x80000000) != 0 ? uint.MaxValue : name, offset & 0x7FFFFFFF, (offset & 0x80000000) != 0);
            }
        }

        foreach (var type in Entries(0))
        {
            if (!type.IsDirectory || type.Id is not (ResourceIcon or ResourceGroupIcon or ResourceVersion))
                continue;
            foreach (var name in Entries(type.Offset))
            {
                if (!name.IsDirectory)
                    continue;
                foreach (var language in Entries(name.Offset))
                {
                    if (language.IsDirectory || FileOffset(sections, rootRva + language.Offset, 16) is not { } leaf)
                        continue;
                    byte[] data = new byte[16];
                    ReadAt(stream, leaf, data, "a resource");
                    resources.Add(new Resource(type.Id, name.Id, BinaryPrimitives.ReadUInt32LittleEndian(data),
                        BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4))));
                    break;
                }
            }
        }
        return resources;
    }

    private static byte[] ReadResource(Stream stream, IReadOnlyList<Section> sections, Resource resource)
    {
        if (resource.Size > MaxResourceBytes || FileOffset(sections, resource.DataRva, resource.Size) is not { } at)
            throw new InvalidDataException("One of its resources lies outside the file.");
        byte[] data = new byte[resource.Size];
        ReadAt(stream, at, data, "a resource");
        return data;
    }

    private readonly record struct Block(string Key, int ValueOffset, int ValueLength, bool IsText, int ChildrenOffset, int End);

    private static Block? ReadBlock(byte[] data, int offset, int limit)
    {
        if (offset + 6 > limit)
            return null;
        int length = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset));
        int valueLength = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 2));
        bool isText = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 4)) == 1;
        int end = offset + length;
        if (length < 6 || end > limit)
            return null;
        int keyEnd = offset + 6;
        while (keyEnd + 1 < end && (data[keyEnd] != 0 || data[keyEnd + 1] != 0))
            keyEnd += 2;
        string key = Encoding.Unicode.GetString(data, offset + 6, keyEnd - offset - 6);
        int valueOffset = Align(keyEnd + 2);
        int valueBytes = Math.Max(0, Math.Min(isText ? valueLength * 2 : valueLength, end - valueOffset));
        return new Block(key, valueOffset, valueBytes, isText, Math.Min(Align(valueOffset + valueBytes), end), end);
    }

    private static int Align(int offset) => (offset + 3) & ~3;

    private static IEnumerable<Block> Children(byte[] data, Block parent)
    {
        int offset = parent.ChildrenOffset;
        while (ReadBlock(data, offset, parent.End) is { } child)
        {
            yield return child;
            offset = Align(child.End);
        }
    }

    internal static (Dictionary<string, string> Strings, string? ProductVersion, string? FileVersion) ParseVersionInfo(byte[] data)
    {
        var strings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (ReadBlock(data, 0, data.Length) is not { Key: "VS_VERSION_INFO" } root)
            return (strings, null, null);
        string? product = null, file = null;
        if (root.ValueLength >= 52 && BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(root.ValueOffset)) == 0xFEEF04BD)
        {
            static string Format(uint high, uint low) => $"{high >> 16}.{high & 0xFFFF}.{low >> 16}.{low & 0xFFFF}";
            var fixedInfo = data.AsSpan(root.ValueOffset);
            file = Format(BinaryPrimitives.ReadUInt32LittleEndian(fixedInfo[8..]), BinaryPrimitives.ReadUInt32LittleEndian(fixedInfo[12..]));
            product = Format(BinaryPrimitives.ReadUInt32LittleEndian(fixedInfo[16..]), BinaryPrimitives.ReadUInt32LittleEndian(fixedInfo[20..]));
        }
        var tables = Children(data, root).Where(b => b.Key == "StringFileInfo").SelectMany(b => Children(data, b)).ToArray();
        var table = tables.FirstOrDefault(t => t.Key.StartsWith("0409", StringComparison.OrdinalIgnoreCase));
        if (table.Key is null && tables.Length > 0)
            table = tables[0];
        if (table.Key is not null)
        {
            foreach (var entry in Children(data, table))
            {
                if (entry.Key.Length > 0 && entry.ValueLength > 0)
                    strings[entry.Key] = Encoding.Unicode.GetString(data, entry.ValueOffset, entry.ValueLength).TrimEnd('\0');
            }
        }
        return (strings, product, file);
    }

    private static readonly byte[] PngSignature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    public static bool IsPng(byte[] image) => image.AsSpan().StartsWith(PngSignature);

    private static byte[]? ReadIcon(Stream stream, IReadOnlyList<Section> sections, List<Resource> resources)
    {
        if (resources.FirstOrDefault(r => r.Type == ResourceGroupIcon) is not { Size: >= 6 } group)
            return null;
        byte[] directory = ReadResource(stream, sections, group);
        int count = BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(4));
        (int Score, ushort Id, byte[] Entry)? best = null;
        for (int i = 0; i < count && 6 + (i + 1) * 14 <= directory.Length; i++)
        {
            var entry = directory.AsSpan(6 + i * 14, 14);
            int width = entry[0] == 0 ? 256 : entry[0];
            int bits = BinaryPrimitives.ReadUInt16LittleEndian(entry[6..]);
            int score = width * 1000 + bits;
            if (best is null || score > best.Value.Score)
                best = (score, BinaryPrimitives.ReadUInt16LittleEndian(entry[12..]), entry.ToArray());
        }
        if (best is not { } chosen || resources.FirstOrDefault(r => r.Type == ResourceIcon && r.Id == chosen.Id) is not { Size: > 0 } icon)
            return null;
        byte[] image = ReadResource(stream, sections, icon);
        return IsPng(image) ? image : SingleImageIcon(chosen.Entry, image);
    }

    private static byte[] SingleImageIcon(ReadOnlySpan<byte> entry, byte[] image)
    {
        byte[] ico = new byte[6 + 16 + image.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(4), 1);
        entry[..8].CopyTo(ico.AsSpan(6));
        BinaryPrimitives.WriteUInt32LittleEndian(ico.AsSpan(14), (uint)image.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(ico.AsSpan(18), 22);
        image.CopyTo(ico, 22);
        return ico;
    }

    public static byte[]? BestIconInIco(byte[] ico)
    {
        if (ico.Length < 6 || BinaryPrimitives.ReadUInt16LittleEndian(ico) != 0 || BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(2)) != 1)
            return null;
        int count = BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(4));
        (int Score, int Offset, int Size, int Entry)? best = null;
        for (int i = 0; i < count && 6 + (i + 1) * 16 <= ico.Length; i++)
        {
            var entry = ico.AsSpan(6 + i * 16, 16);
            long size = BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
            long offset = BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
            if (size == 0 || offset + size > ico.Length)
                continue;
            int score = (entry[0] == 0 ? 256 : entry[0]) * 1000 + BinaryPrimitives.ReadUInt16LittleEndian(entry[6..]);
            if (best is null || score > best.Value.Score)
                best = (score, (int)offset, (int)size, 6 + i * 16);
        }
        if (best is not { } chosen)
            return null;
        byte[] image = ico.AsSpan(chosen.Offset, chosen.Size).ToArray();
        return IsPng(image) ? image : SingleImageIcon(ico.AsSpan(chosen.Entry, 16), image);
    }

    public static byte[]? IconFromFile(byte[] data)
    {
        if (data.Length < 2 || data[0] != 'M' || data[1] != 'Z')
            return BestIconInIco(data);
        try
        {
            return Read(new MemoryStream(data, writable: false)).Icon;
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            return null;
        }
    }
}
