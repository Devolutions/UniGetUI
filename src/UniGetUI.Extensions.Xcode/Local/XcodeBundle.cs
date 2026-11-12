using System.Buffers.Binary;
using System.Xml;
using System.Xml.Linq;

namespace UniGetUI.Extensions.Xcode.Local;

/// <summary>Runs a macOS tool and returns what it printed; a test stands in for the real tools.</summary>
internal delegate Task<XcodesResult> XcodeToolRunner(
    string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken);

/// <summary>What an Xcode bundle says about itself, read without starting it.</summary>
/// <param name="ShortVersion">CFBundleShortVersionString, such as <c>26.4</c>; it never names a beta.</param>
internal sealed record XcodeBundleInfo(
    string Path, string ShortVersion, string Build, bool IsBeta, XcodeArchitecture Architecture);

/// <summary>Reads Xcode bundles: Info.plist, version.plist and the architectures of the Xcode executable.</summary>
internal static class XcodeBundle
{
    public const string BundleIdentifier = "com.apple.dt.Xcode";
    private const int MaximumPlistBytes = 4 * 1024 * 1024;

    /// <summary>The Xcode at <paramref name="appPath"/>, or null when it is not an Xcode or cannot be read.</summary>
    public static async Task<XcodeBundleInfo?> ReadAsync(
        string appPath, XcodeToolRunner run, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, string>? info = await ReadPlistAsync(
            Path.Combine(appPath, "Contents", "Info.plist"), run, cancellationToken).ConfigureAwait(false);
        if (info is null || info.GetValueOrDefault("CFBundleIdentifier") != BundleIdentifier
            || info.GetValueOrDefault("CFBundleShortVersionString") is not { Length: > 0 } shortVersion
            || !XcodeVersion.TryParse(shortVersion, out XcodeVersion parsed) || parsed.IsPrerelease)
            return null;

        IReadOnlyDictionary<string, string>? version = await ReadPlistAsync(
            Path.Combine(appPath, "Contents", "version.plist"), run, cancellationToken).ConfigureAwait(false);
        if (version?.GetValueOrDefault("ProductBuildVersion") is not { } build
            || !XcodeSimulatorImageFile.IsValidBuild(build))
            return null;

        string executable = info.GetValueOrDefault("CFBundleExecutable") is { Length: > 0 } name
            && name.IndexOfAny(['/', '\0']) < 0 && name is not ("." or "..") ? name : "Xcode";
        return new XcodeBundleInfo(appPath, shortVersion, build,
            info.GetValueOrDefault("CFBundleIconName") == "XcodeBeta",
            ReadArchitecture(Path.Combine(appPath, "Contents", "MacOS", executable)));
    }

    internal static async Task<IReadOnlyDictionary<string, string>?> ReadPlistAsync(
        string file, XcodeToolRunner run, CancellationToken cancellationToken)
    {
        byte[] bytes;
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists || info.Length > MaximumPlistBytes)
                return null;
            bytes = await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (bytes.AsSpan().StartsWith("bplist"u8))
        {
            // plutil ships with macOS and is the supported reader of binary property lists.
            XcodesResult converted = await run("/usr/bin/plutil", ["-convert", "xml1", "-o", "-", file], cancellationToken)
                .ConfigureAwait(false);
            if (!converted.Success || converted.StandardOutput.Length > MaximumPlistBytes)
                return null;
            bytes = System.Text.Encoding.UTF8.GetBytes(converted.StandardOutput);
        }
        return ParseXmlPlist(bytes);
    }

    /// <summary>The top-level string values of an XML property list, or null when it is not one.</summary>
    internal static IReadOnlyDictionary<string, string>? ParseXmlPlist(byte[] xml)
    {
        XDocument document;
        try
        {
            using var stream = new MemoryStream(xml, writable: false);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                // Property lists name Apple's DTD; it is never fetched or expanded.
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null,
                MaxCharactersFromEntities = 0,
            });
            document = XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }

        if (document.Root?.Element("dict") is not { } dict)
            return null;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        string? key = null;
        foreach (XElement element in dict.Elements())
        {
            if (element.Name == "key")
                key = element.Value;
            else if (key is not null)
            {
                if (element.Name == "string")
                    values.TryAdd(key, element.Value.Trim());
                key = null;
            }
        }
        return values;
    }

    private const uint FatMagic = 0xCAFEBABE;
    private const uint FatMagic64 = 0xCAFEBABF;
    private const uint MachMagic64 = 0xFEEDFACF;
    private const int CpuTypeX64 = 0x01000007;
    private const int CpuTypeArm64 = 0x0100000C;

    internal static XcodeArchitecture ReadArchitecture(string executable)
    {
        try
        {
            using FileStream stream = File.OpenRead(executable);
            byte[] header = new byte[8 + 20 * 32];
            int read = stream.ReadAtLeast(header, 8, throwOnEndOfStream: false);
            return ParseArchitecture(header.AsSpan(0, read));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return XcodeArchitecture.Unspecified;
        }
    }

    internal static XcodeArchitecture ParseArchitecture(ReadOnlySpan<byte> header)
    {
        if (header.Length < 8)
            return XcodeArchitecture.Unspecified;
        bool arm = false, intel = false;
        uint magic = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (magic is FatMagic or FatMagic64)
        {
            uint count = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
            int stride = magic == FatMagic ? 20 : 32;
            for (int i = 0; i < count && 8 + (i + 1) * stride <= header.Length; i++)
            {
                int cpu = BinaryPrimitives.ReadInt32BigEndian(header[(8 + i * stride)..]);
                arm |= cpu == CpuTypeArm64;
                intel |= cpu == CpuTypeX64;
            }
        }
        else if (BinaryPrimitives.ReadUInt32LittleEndian(header) == MachMagic64)
        {
            int cpu = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
            arm = cpu == CpuTypeArm64;
            intel = cpu == CpuTypeX64;
        }
        return arm && intel ? XcodeArchitecture.Universal
            : arm ? XcodeArchitecture.AppleSilicon
            : intel ? XcodeArchitecture.Intel
            : XcodeArchitecture.Unspecified;
    }
}
