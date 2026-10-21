using System.Formats.Tar;
using System.IO.Compression;

namespace UniGetUI.PackageEngine.Managers.CargoManager;

internal static class CargoArchive
{
    internal const long ExpandedLimit = 512L * 1024 * 1024;
    internal const int EntryLimit = 10000;

    internal static string? Format(string name) =>
        name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase) ? "tgz"
        : name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? "zip"
        : name.EndsWith(".tar", StringComparison.OrdinalIgnoreCase) ? "tar"
        : null;

    internal static Dictionary<string, byte[]> Read(byte[] bytes, string format,
        Func<string, bool> select, CancellationToken cancellationToken,
        long selectedEntryLimit = ExpandedLimit)
    {
        Dictionary<string, byte[]> selected = new(StringComparer.Ordinal);
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        int count = 0;
        using MemoryStream input = new(bytes, writable: false);

        void Entry(string rawName, Stream? content, long declaredSize, bool directory)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++count > EntryLimit)
                throw new InvalidDataException("Cargo archive contains too many entries.");
            while (rawName.StartsWith("./", StringComparison.Ordinal))
                rawName = rawName[2..];
            if (directory && rawName.TrimEnd('/').Length == 0)
                return;
            string name = ValidateName(rawName.TrimEnd('/'));
            if (!paths.Add(name))
                throw new InvalidDataException($"Duplicate Cargo archive entry: {name}");
            if (declaredSize < 0 || declaredSize > ExpandedLimit - expanded)
                throw new InvalidDataException("Cargo archive expands beyond the allowed size.");
            if (directory || content is null)
                return;
            bool keep = select(name);
            if (keep && declaredSize > selectedEntryLimit)
                throw new InvalidDataException("Selected Cargo archive entry exceeds the allowed size.");
            using MemoryStream? output = keep ? new() : null;
            byte[] buffer = new byte[64 * 1024];
            long size = 0;
            int read;
            while ((read = content.Read(buffer)) != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                size += read;
                expanded += read;
                if (expanded > ExpandedLimit || (keep && size > selectedEntryLimit))
                    throw new InvalidDataException("Cargo archive expands beyond the allowed size.");
                output?.Write(buffer, 0, read);
            }
            if (size != declaredSize)
                throw new InvalidDataException($"Truncated Cargo archive entry: {name}");
            if (output is not null)
                selected.Add(name, output.ToArray());
        }

        if (format == "zip")
        {
            using ZipArchive archive = new(input, ZipArchiveMode.Read);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                int unixType = (entry.ExternalAttributes >> 16) & 0xF000;
                if (unixType is not (0 or 0x8000 or 0x4000)
                    || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Cargo archive contains a link or special file.");
                using Stream content = entry.Open();
                Entry(entry.FullName, content, entry.Length, entry.FullName.EndsWith('/'));
            }
        }
        else if (format is "tgz" or "tar")
        {
            using Stream decompressed = format == "tgz"
                ? new GZipStream(input, CompressionMode.Decompress, leaveOpen: true)
                : input;
            using TarReader reader = new(decompressed);
            TarEntry? entry;
            while ((entry = reader.GetNextEntry()) is not null)
            {
                bool directory = entry.EntryType == TarEntryType.Directory;
                if (!directory && entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                    throw new InvalidDataException("Cargo archive contains a link or special file.");
                Entry(entry.Name, entry.DataStream, entry.Length, directory);
            }
        }
        else
            throw new NotSupportedException($"Unsupported Cargo archive format: {format}");
        return selected;
    }

    internal static string ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains('\\') || name.Contains(':')
            || name.StartsWith('/') || name.Any(c => c < 32)
            || name.Split('/').Any(part => part is "" or "." or ".."
                || part.EndsWith(' ') || part.EndsWith('.') || IsDeviceName(part)))
            throw new InvalidDataException($"Unsafe Cargo archive path: {name}");
        return name;
    }

    internal static bool IsDeviceName(string name)
    {
        string stem = name.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL"
            || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal)
                || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '1' and <= '9');
    }
}
