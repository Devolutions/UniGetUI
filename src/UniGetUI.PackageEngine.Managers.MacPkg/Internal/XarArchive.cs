using System.Buffers.Binary;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace UniGetUI.PackageEngine.Managers.MacPkgManager.Internal;

/// <summary>
/// Reads named files out of a xar archive, the container format of a flat macOS .pkg. Only the
/// table of contents and the small metadata files are read; the payload is never touched.
/// </summary>
internal sealed class XarArchive
{
    private const uint Magic = 0x78617221; // "xar!"
    private const int MinHeaderSize = 28;
    internal const int MaxTocBytes = 16 * 1024 * 1024;

    private readonly record struct Entry(long Offset, long Length, long Size, string Encoding);

    private readonly Stream _stream;
    private readonly long _heapStart;
    private readonly Dictionary<string, Entry> _files = new(StringComparer.Ordinal);

    private XarArchive(Stream stream, long heapStart)
    {
        _stream = stream;
        _heapStart = heapStart;
    }

    public IEnumerable<string> FilePaths => _files.Keys;

    public bool Contains(string path) => _files.ContainsKey(path);

    /// <summary>Parses the archive header and table of contents. The stream must be seekable.</summary>
    public static XarArchive Open(Stream stream)
    {
        Span<byte> header = stackalloc byte[MinHeaderSize];
        stream.Seek(0, SeekOrigin.Begin);
        if (stream.ReadAtLeast(header, MinHeaderSize, throwOnEndOfStream: false) < MinHeaderSize
            || BinaryPrimitives.ReadUInt32BigEndian(header) != Magic)
            throw new InvalidDataException("The file is not a flat installer package (xar archive).");
        ushort headerSize = BinaryPrimitives.ReadUInt16BigEndian(header[4..]);
        ulong tocCompressed = BinaryPrimitives.ReadUInt64BigEndian(header[8..]);
        ulong tocUncompressed = BinaryPrimitives.ReadUInt64BigEndian(header[16..]);
        if (headerSize < MinHeaderSize || tocCompressed is 0 or > MaxTocBytes || tocUncompressed > MaxTocBytes)
            throw new InvalidDataException("The installer package has an invalid table of contents.");

        stream.Seek(headerSize, SeekOrigin.Begin);
        byte[] compressed = new byte[(int)tocCompressed];
        stream.ReadExactly(compressed);
        byte[] toc = Inflate(compressed, MaxTocBytes);

        var archive = new XarArchive(stream, headerSize + (long)tocCompressed);
        XElement root = ParseXml(toc).Root?.Element("toc")
            ?? throw new InvalidDataException("The installer package has no table of contents.");
        archive.AddFiles(root, "", 0);
        return archive;
    }

    // Real packages nest a few folders deep; a crafted one must not exhaust the stack before it is checked.
    private const int MaxDirectoryDepth = 64;

    private void AddFiles(XElement parent, string prefix, int depth)
    {
        if (depth > MaxDirectoryDepth)
            throw new InvalidDataException("The installer package's folders are nested too deeply.");
        foreach (XElement file in parent.Elements("file"))
        {
            string? name = file.Element("name")?.Value;
            if (string.IsNullOrEmpty(name) || name.Contains('/') || name is "." or "..")
                continue;
            string path = prefix + name;
            if (file.Element("type")?.Value == "directory")
            {
                AddFiles(file, path + "/", depth + 1);
                continue;
            }
            if (file.Element("data") is not { } data)
                continue;
            if (!long.TryParse(data.Element("offset")?.Value, out long offset)
                || !long.TryParse(data.Element("length")?.Value, out long length)
                || !long.TryParse(data.Element("size")?.Value, out long size)
                || offset < 0 || length < 0 || size < 0)
                continue;
            string encoding = data.Element("encoding")?.Attribute("style")?.Value ?? "application/octet-stream";
            _files.TryAdd(path, new Entry(offset, length, size, encoding));
        }
    }

    /// <summary>Returns the decoded contents of a file, or null when the archive has no such file.</summary>
    public byte[]? ReadFile(string path, int maxBytes)
    {
        if (!_files.TryGetValue(path, out Entry entry))
            return null;
        if (entry.Size > maxBytes || entry.Length > maxBytes)
            throw new InvalidDataException($"{path} in the installer package is larger than {maxBytes} bytes.");

        _stream.Seek(_heapStart + entry.Offset, SeekOrigin.Begin);
        byte[] raw = new byte[(int)entry.Length];
        _stream.ReadExactly(raw);
        return entry.Encoding switch
        {
            "application/octet-stream" => raw,
            "application/x-gzip" => Inflate(raw, maxBytes),
            _ => throw new InvalidDataException($"{path} in the installer package uses the unsupported encoding {entry.Encoding}."),
        };
    }

    /// <summary>
    /// Streams a file without loading it, for the payload, which can weigh gigabytes. Null when the
    /// archive has no such file or stores it in an encoding this reader does not decode.
    /// </summary>
    public Stream? OpenFile(string path)
    {
        if (!_files.TryGetValue(path, out Entry entry))
            return null;
        Stream raw = new BoundedStream(_stream, _heapStart + entry.Offset, entry.Length);
        return entry.Encoding switch
        {
            "application/octet-stream" => raw,
            "application/x-gzip" => new ZLibStream(raw, CompressionMode.Decompress),
            _ => null,
        };
    }

    private sealed class BoundedStream(Stream inner, long start, long length) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= length)
                return 0;
            inner.Seek(start + _position, SeekOrigin.Begin);
            int read = inner.Read(buffer, offset, (int)Math.Min(count, length - _position));
            _position += read;
            return read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // xar labels zlib streams "application/x-gzip"; the declared size is checked again while
    // inflating, because it can lie.
    private static byte[] Inflate(byte[] compressed, int maxBytes)
    {
        using var input = new ZLibStream(new MemoryStream(compressed), CompressionMode.Decompress);
        using var output = new MemoryStream();
        byte[] buffer = new byte[81920];
        int read;
        while ((read = input.Read(buffer)) > 0)
        {
            if (output.Length + read > maxBytes)
                throw new InvalidDataException($"A file in the installer package inflates beyond {maxBytes} bytes.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    internal static XDocument ParseXml(byte[] bytes)
    {
        var settings = new XmlReaderSettings
        {
            // Ignore rather than prohibit: some vendors' Distribution files carry a DOCTYPE.
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            MaxCharactersInDocument = MaxTocBytes,
        };
        using var reader = XmlReader.Create(new MemoryStream(bytes), settings);
        return XDocument.Load(reader);
    }
}
