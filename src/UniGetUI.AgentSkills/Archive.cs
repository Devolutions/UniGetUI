// Strict zip archive reader (port of archive.ts). Rejects unsafe paths, links,
// encryption, multi-disk archives and inconsistent headers.

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Skills;

internal sealed record ArchiveLimits(long MaxExtractedBytes, long MaxEntries);

/// Security/limit validation failure (surfaced to the user).
internal sealed class ArchiveValidationException(string message) : Exception(message);

/// Malformed archive.
internal sealed class ArchiveInvalidException(string message) : Exception(message);

internal static class Archive
{
    private const uint LocalFileHeader = 0x04034b50;
    private const uint CentralDirectoryHeader = 0x02014b50;
    private const uint EndOfCentralDirectory = 0x06054b50;
    private const uint Zip64EndOfCentralDirectory = 0x06064b50;
    private const uint Zip64Locator = 0x07064b50;
    private const int EndMinSize = 22;
    private const int MaxCommentSize = 0xffff;
    private const long MaxSafeInteger = 9007199254740991;

    private const string Cp437High = "ÇüéâäàåçêëèïîìÄÅÉæÆôöòûùÿÖÜ¢£¥₧ƒáíóúñÑªº¿⌐¬½¼¡«»░▒▓│┤╡╢╖╕╣║╗╝╜╛┐└┴┬├─┼╞╟╚╔╩╦╠═╬╧╨╤╥╙╘╒╓╫╪┘┌█▄▌▐▀αßΓπΣσµτΦΘΩδ∞φε∩≡±≥≤⌠⌡÷≈°∙·√ⁿ²■ ";

    private static ArchiveInvalidException Invalid(string s) => new(s);

    private static void EnsureRange(byte[] buf, long offset, long len, string label)
    {
        if (offset < 0 || len < 0 || offset + len > buf.Length) throw Invalid($"Invalid zip archive: {label} is out of bounds");
    }

    private static ushort U16(byte[] b, long o)
    {
        EnsureRange(b, o, 2, "field");
        return BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan((int)o, 2));
    }

    private static uint U32(byte[] b, long o)
    {
        EnsureRange(b, o, 4, "field");
        return BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan((int)o, 4));
    }

    private static long U64(byte[] b, long o, string label)
    {
        EnsureRange(b, o, 8, label);
        var v = BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan((int)o, 8));
        if (v > MaxSafeInteger) throw Invalid($"Invalid zip archive: {label} exceeds the safe integer range");
        return (long)v;
    }

    private static int FindEnd(byte[] buf)
    {
        if (buf.Length < EndMinSize) return -1;
        var min = Math.Max(0, buf.Length - MaxCommentSize - EndMinSize);
        for (var o = buf.Length - EndMinSize; o >= min; o--)
        {
            if (U32(buf, o) != EndOfCentralDirectory) continue;
            if (o + EndMinSize + U16(buf, o + 20) == buf.Length) return o;
        }
        return -1;
    }

    private sealed record CentralDirectory(long Entries, long Offset, long Size, long TrailerOffset);

    private static CentralDirectory ReadCentralDirectory(byte[] buf, int end)
    {
        var disk = U16(buf, end + 4);
        var cdDisk = U16(buf, end + 6);
        var onDisk = U16(buf, end + 8);
        var total = U16(buf, end + 10);
        var size = U32(buf, end + 12);
        var offset = U32(buf, end + 16);
        var zip64 = onDisk == 0xffff || total == 0xffff || size == 0xffffffff || offset == 0xffffffff;
        if (!zip64)
        {
            if (disk != 0 || cdDisk != 0 || onDisk != total) throw Invalid("Multi-disk zip archives are not supported");
            return new CentralDirectory(total, offset, size, end);
        }
        if (disk != 0 || cdDisk != 0) throw Invalid("Multi-disk zip archives are not supported");
        var locator = end - 20;
        EnsureRange(buf, locator, 20, "zip64 locator");
        if (U32(buf, locator) != Zip64Locator) throw Invalid("Invalid zip64 locator");
        if (U32(buf, locator + 4) != 0 || U32(buf, locator + 16) != 1) throw Invalid("Multi-disk zip archives are not supported");
        var z = U64(buf, locator + 8, "zip64 end offset");
        EnsureRange(buf, z, 56, "zip64 end of central directory");
        if (U32(buf, z) != Zip64EndOfCentralDirectory) throw Invalid("Invalid zip64 end of central directory");
        var recordSize = U64(buf, z + 4, "zip64 end size");
        if (recordSize < 44) throw Invalid("Invalid zip64 end of central directory");
        EnsureRange(buf, z, recordSize + 12, "zip64 end of central directory");
        if (z + recordSize + 12 != locator) throw Invalid("Invalid zip64 end of central directory");
        if (U32(buf, z + 16) != 0 || U32(buf, z + 20) != 0) throw Invalid("Multi-disk zip archives are not supported");
        var entriesOnDisk = U64(buf, z + 24, "zip64 entries on disk");
        var totalEntries = U64(buf, z + 32, "zip64 total entries");
        if (entriesOnDisk != totalEntries) throw Invalid("Multi-disk zip archives are not supported");
        return new CentralDirectory(totalEntries, U64(buf, z + 48, "zip64 central directory offset"), U64(buf, z + 40, "zip64 central directory size"), z);
    }

    private static string? NormalizeArchivePath(string raw)
    {
        if (raw.Length == 0 || raw.Contains('\0')) return null;
        var path = raw.Replace('\\', '/');
        if (path.StartsWith('/') || (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':')) return null;
        var parts = path.Split('/');
        if (parts.Contains("..")) return null;
        var normalized = string.Join("/", parts.Where(p => p.Length > 0 && p != "."));
        if (normalized.Length == 0 && !path.EndsWith('/')) return null;
        return path.EndsWith('/') && normalized.Length > 0 ? normalized + "/" : normalized;
    }

    private static (int Offset, int Length)? FindExtra(byte[] buf, int extraOffset, int extraLength, ushort target)
    {
        var end = extraOffset + extraLength;
        var o = extraOffset;
        while (o < end)
        {
            if (o + 4 > end) throw Invalid("Invalid zip extra field");
            var id = U16(buf, o);
            var size = U16(buf, o + 2);
            var data = o + 4;
            EnsureRange(buf, data, size, "zip extra field");
            if (data + size > end) throw Invalid("Invalid zip extra field");
            if (id == target) return (data, size);
            o = data + size;
        }
        return null;
    }

    private static string DecodeFileName(ReadOnlySpan<byte> bytes, bool utf8, byte[]? unicodeExtra)
    {
        var strict = new UTF8Encoding(false, true);
        try
        {
            if (utf8) return strict.GetString(bytes);
            if (unicodeExtra is { Length: >= 5 } && unicodeExtra[0] == 1 && BinaryPrimitives.ReadUInt32LittleEndian(unicodeExtra.AsSpan(1, 4)) == Crc32.Compute(bytes))
                return strict.GetString(unicodeExtra.AsSpan(5));
        }
        catch (DecoderFallbackException)
        {
            throw Invalid("The encoded data was not valid for encoding utf-8");
        }
        var sb = new StringBuilder();
        foreach (var b in bytes) sb.Append(b < 0x80 ? (char)b : Cp437High[b - 0x80]);
        return sb.ToString();
    }

    /// Read every file entry of a zip archive into memory (insertion-ordered;
    /// a duplicate name replaces the earlier content but keeps its position).
    public static List<(string Path, byte[] Contents)> ReadZip(byte[] buf, ArchiveLimits limits)
    {
        var end = FindEnd(buf);
        if (end < 0) throw Invalid("Invalid zip archive");
        var cd = ReadCentralDirectory(buf, end);
        if (cd.Entries > limits.MaxEntries) throw new ArchiveValidationException($"Archive contains too many files ({cd.Entries}). Maximum is {limits.MaxEntries}.");
        EnsureRange(buf, cd.Offset, cd.Size, "central directory");
        if (cd.Offset + cd.Size > cd.TrailerOffset) throw Invalid("Invalid zip archive: central directory overlaps archive trailer");
        var files = new List<(string Path, byte[] Contents)>();
        long extracted = 0;
        var offset = (int)cd.Offset;
        for (long n = 0; n < cd.Entries; n++)
        {
            EnsureRange(buf, offset, 46, "central directory entry");
            if (U32(buf, offset) != CentralDirectoryHeader) throw Invalid("Invalid zip central directory entry");
            var flags = U16(buf, offset + 8);
            var method = U16(buf, offset + 10);
            var checksum = U32(buf, offset + 16);
            long compressed = U32(buf, offset + 20);
            long uncompressed = U32(buf, offset + 24);
            var nameLen = U16(buf, offset + 28);
            var extraLen = U16(buf, offset + 30);
            var commentLen = U16(buf, offset + 32);
            long diskStart = U16(buf, offset + 34);
            var external = U32(buf, offset + 38);
            long localOffset = U32(buf, offset + 42);
            var variable = nameLen + extraLen + commentLen;
            EnsureRange(buf, offset + 46, variable, "central directory entry data");
            var nameStart = offset + 46;
            var extraOffset = nameStart + nameLen;

            var needsZip64 = uncompressed == 0xffffffff || compressed == 0xffffffff || localOffset == 0xffffffff || diskStart == 0xffff;
            if (!needsZip64)
            {
                if (diskStart != 0) throw Invalid("Multi-disk zip archives are not supported");
            }
            else
            {
                var z = FindExtra(buf, extraOffset, extraLen, 0x0001) ?? throw Invalid("Invalid zip64 extra field");
                var extra = buf.AsSpan(z.Offset, z.Length).ToArray();
                var vo = 0;
                long Next(string label)
                {
                    if (vo + 8 > extra.Length) throw Invalid($"Invalid zip64 extra field: missing {label}");
                    var v = U64(extra, vo, $"zip64 {label}");
                    vo += 8;
                    return v;
                }
                if (uncompressed == 0xffffffff) uncompressed = Next("uncompressed size");
                if (compressed == 0xffffffff) compressed = Next("compressed size");
                if (localOffset == 0xffffffff) localOffset = Next("local header offset");
                if (diskStart == 0xffff)
                {
                    if (vo + 4 > extra.Length) throw Invalid("Invalid zip64 extra field: missing disk start");
                    diskStart = U32(extra, vo);
                }
                if (diskStart != 0) throw Invalid("Multi-disk zip archives are not supported");
            }

            var centralName = buf.AsSpan(nameStart, nameLen).ToArray();
            var unicodeExtra = FindExtra(buf, extraOffset, extraLen, 0x7075) is { } ue ? buf.AsSpan(ue.Offset, ue.Length).ToArray() : null;
            var rawName = DecodeFileName(centralName, (flags & 0x800) != 0, unicodeExtra);
            var fileName = NormalizeArchivePath(rawName) ?? throw new ArchiveValidationException($"Archive contains unsafe path: {rawName}");
            if ((flags & 0x1) != 0) throw new ArchiveValidationException("Encrypted zip entries are not supported");
            var fileType = (external >> 16) & 0xF000; // 0o170000
            if (fileType != 0 && fileType != 0x8000 && fileType != 0x4000) throw new ArchiveValidationException("Archive links are not supported");
            var isDir = rawName.Replace('\\', '/').EndsWith('/') || fileType == 0x4000;
            offset += 46 + variable;

            extracted += uncompressed;
            if (extracted > limits.MaxExtractedBytes) throw new ArchiveValidationException($"Archive extracts to more than {limits.MaxExtractedBytes} bytes.");
            if (isDir) continue;

            EnsureRange(buf, localOffset, 30, "local file header");
            var lo = (int)localOffset;
            if (U32(buf, lo) != LocalFileHeader) throw Invalid("Invalid zip local file header");
            var localFlags = U16(buf, lo + 6);
            var localMethod = U16(buf, lo + 8);
            var localNameLen = U16(buf, lo + 26);
            var localExtraLen = U16(buf, lo + 28);
            EnsureRange(buf, lo + 30, localNameLen + localExtraLen, "local file header data");
            if (localFlags != flags || localMethod != method || !buf.AsSpan(lo + 30, localNameLen).SequenceEqual(centralName))
                throw Invalid("Zip local header does not match central directory");
            long dataOffset = lo + 30 + localNameLen + localExtraLen;
            EnsureRange(buf, dataOffset, compressed, "file data");
            if (dataOffset + compressed > cd.Offset) throw Invalid("Invalid zip archive: file data overlaps central directory");
            var compressedBytes = buf.AsSpan((int)dataOffset, (int)compressed).ToArray();
            byte[] contents;
            switch (method)
            {
                case 0:
                    contents = compressedBytes;
                    break;
                case 8:
                {
                    using var input = new MemoryStream(compressedBytes);
                    using var deflate = new DeflateStream(input, CompressionMode.Decompress);
                    using var ms = new MemoryStream();
                    var buffer = new byte[81920];
                    int read;
                    try
                    {
                        while ((read = deflate.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            ms.Write(buffer, 0, read);
                            if (ms.Length > uncompressed + 1) break;
                        }
                    }
                    catch (InvalidDataException e)
                    {
                        throw Invalid(e.Message);
                    }
                    contents = ms.ToArray();
                    break;
                }
                default:
                    throw Invalid($"Unsupported zip compression method: {method}");
            }
            if (contents.Length != uncompressed) throw Invalid("Zip entry size mismatch");
            if (Crc32.Compute(contents) != checksum) throw Invalid("Zip entry checksum mismatch");
            var idx = files.FindIndex(f => f.Path == fileName);
            if (idx >= 0) files[idx] = (fileName, contents);
            else files.Add((fileName, contents));
        }
        if (offset != cd.Offset + cd.Size) throw Invalid("Invalid zip central directory size");
        return files;
    }
}

internal static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }
}
