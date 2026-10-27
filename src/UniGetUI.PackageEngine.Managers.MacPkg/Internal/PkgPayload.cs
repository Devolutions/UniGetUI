using System.IO.Compression;
using System.Text;

namespace UniGetUI.PackageEngine.Managers.MacPkgManager.Internal;

/// <summary>
/// Reads a component's Payload, the gzip-compressed cpio (odc) archive of the files it installs,
/// in one forward pass, keeping only the app's Info.plist and the .icns files of its Resources.
/// </summary>
internal static class PkgPayload
{
    private const int HeaderSize = 76;
    private const int MaxKeptFileBytes = 8 * 1024 * 1024;
    // Icons are kept until the plist names one, so a payload of many icons must not fill memory.
    private const int MaxKeptIconBytes = 32 * 1024 * 1024;
    private const int MaxKeptIcons = 32;
    private const int MaxNameBytes = 4096;

    /// <summary>Saves, as a PNG, the icon the app's Info.plist names, else AppIcon.icns, else any it carries.</summary>
    public static bool SaveIcon(IReadOnlyDictionary<string, string>? plist, IReadOnlyDictionary<string, byte[]> icons,
        string destinationPng)
    {
        string? named = plist is null ? null : AppBundle.IconFileName(plist);
        byte[]? icon = (named is not null ? icons.GetValueOrDefault(named) : null)
            ?? icons.GetValueOrDefault("AppIcon.icns")
            ?? icons.Values.FirstOrDefault();
        if (icon is null)
            return false;
        string temporary = Path.Combine(Path.GetTempPath(), $"unigetui-icon-{Guid.NewGuid():N}.icns");
        try
        {
            File.WriteAllBytes(temporary, icon);
            return AppBundle.ConvertIcon(temporary, destinationPng);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    /// <summary>
    /// The app's Info.plist and its Contents/Resources/*.icns by name ("." when the payload is the app). Stops early once the plist and
    /// the icon it names have both been seen; a payload that is not gzip'd odc cpio yields nothing. Without
    /// <paramref name="keepIcons"/>, only the plist is read. Beyond a few icons or bytes, further icons are skipped.
    /// </summary>
    internal static (byte[]? InfoPlist, Dictionary<string, byte[]> Icons) FindAppResources(Stream payload, string appPath,
        bool keepIcons = true)
    {
        long keptIconBytes = 0;
        string app = Normalize(appPath);
        string root = app.Length == 0 ? "" : app + "/";
        string plistName = root + "Contents/Info.plist";
        string resources = root + "Contents/Resources/";
        byte[]? plist = null;
        string? wanted = null;
        var icons = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        Span<byte> magic = stackalloc byte[2];
        if (payload.ReadAtLeast(magic, 2, throwOnEndOfStream: false) < 2 || magic[0] != 0x1F || magic[1] != 0x8B)
            return (null, icons);
        using var cpio = new GZipStream(new PrefixedStream(magic.ToArray(), payload), CompressionMode.Decompress);
        byte[] header = new byte[HeaderSize];
        while (true)
        {
            if (cpio.ReadAtLeast(header, HeaderSize, throwOnEndOfStream: false) < HeaderSize
                || Encoding.ASCII.GetString(header, 0, 6) != "070707")
                break;
            int nameSize = (int)Octal(header, 59, 6);
            long fileSize = Octal(header, 65, 11);
            if (nameSize is <= 0 or > MaxNameBytes || fileSize < 0)
                break;
            byte[] nameBytes = new byte[nameSize];
            cpio.ReadExactly(nameBytes);
            string name = Normalize(Encoding.UTF8.GetString(nameBytes, 0, nameSize - 1));
            if (name == "TRAILER!!!")
                break;

            bool isPlist = name == plistName;
            bool isIcon = keepIcons
                && name.StartsWith(resources, StringComparison.Ordinal)
                && name.EndsWith(".icns", StringComparison.OrdinalIgnoreCase)
                && !name[resources.Length..].Contains('/')
                && (name[resources.Length..] == wanted
                    || (icons.Count < MaxKeptIcons && keptIconBytes + fileSize <= MaxKeptIconBytes));
            if ((isPlist || isIcon) && fileSize <= MaxKeptFileBytes)
            {
                byte[] data = new byte[fileSize];
                cpio.ReadExactly(data);
                if (isPlist)
                {
                    plist = data;
                    wanted = AppBundle.IconFileName(AppBundle.ParsePlist(plist));
                }
                else
                {
                    icons[name[resources.Length..]] = data;
                    keptIconBytes += data.Length;
                }
                if (isPlist && !keepIcons)
                    break;
                if (wanted is not null && icons.ContainsKey(wanted))
                    break;
            }
            else
                Skip(cpio, fileSize);
        }
        return (plist, icons);
    }

    private static string Normalize(string path) => path.Trim().TrimStart('.').Trim('/');

    private static long Octal(byte[] header, int offset, int length)
    {
        long value = 0;
        for (int i = offset; i < offset + length; i++)
        {
            if (header[i] is < (byte)'0' or > (byte)'7')
                return -1;
            value = value * 8 + (header[i] - '0');
        }
        return value;
    }

    private static void Skip(Stream stream, long count)
    {
        byte[] buffer = new byte[81920];
        while (count > 0)
        {
            int read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
            if (read == 0)
                throw new EndOfStreamException();
            count -= read;
        }
    }

    // Puts back the bytes read to sniff the format, since the payload stream cannot seek.
    private sealed class PrefixedStream(byte[] prefix, Stream inner) : Stream
    {
        private int _prefixRead;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_prefixRead < prefix.Length)
            {
                int n = Math.Min(count, prefix.Length - _prefixRead);
                Array.Copy(prefix, _prefixRead, buffer, offset, n);
                _prefixRead += n;
                return n;
            }
            return inner.Read(buffer, offset, count);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
