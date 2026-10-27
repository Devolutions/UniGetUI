using System.Buffers.Binary;
using System.Diagnostics;
using System.Xml.Linq;
using UniGetUI.Core.Logging;

namespace UniGetUI.PackageEngine.Managers.MacPkgManager.Internal;

/// <summary>The application a feed package puts on disk: where, with which bundle id and version.</summary>
internal sealed record AppBundleInfo(string BundleId, string Path, string? Version);

/// <summary>
/// Reads .app bundles: their Info.plist and the architectures of their executable. An installed
/// app is recognized by its bundle id at the path the package installs it to, however it got there.
/// </summary>
internal static class AppBundle
{
    private const int MaxPlistBytes = 4 * 1024 * 1024;

    /// <summary>Prefixed to absolute app paths, so tests can stand in for /Applications.</summary>
    internal static string? TestRootOverride { get; set; }

    internal static string OnDisk(string absolutePath) =>
        TestRootOverride is null ? absolutePath : System.IO.Path.Join(TestRootOverride, absolutePath);

    /// <summary>The app installed where <paramref name="expected"/> says, or null when absent or another app.</summary>
    public static AppBundleInfo? FindInstalled(AppBundleInfo expected)
    {
        string path = OnDisk(expected.Path);
        if (!Directory.Exists(path))
            return null;
        try
        {
            var plist = ReadInfoPlist(path);
            return plist.GetValueOrDefault("CFBundleIdentifier") == expected.BundleId
                ? new AppBundleInfo(expected.BundleId, expected.Path, VersionOf(plist))
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
            or System.Xml.XmlException)
        {
            return null;
        }
    }

    /// <summary>What an app installed by copying it to /Applications is: its bundle id, version and name.</summary>
    public static PkgMetadata Describe(string appName, IReadOnlyDictionary<string, string> plist,
        IReadOnlyList<string> architectures)
    {
        string id = plist.GetValueOrDefault("CFBundleIdentifier") ?? "";
        string? version = VersionOf(plist);
        if (id.Length == 0 || string.IsNullOrEmpty(version))
            throw new InvalidDataException($"{appName} has no bundle identifier or version.");
        string title = plist.GetValueOrDefault("CFBundleDisplayName") is { Length: > 0 } display
            ? display
            : System.IO.Path.GetFileNameWithoutExtension(appName);
        return new PkgMetadata(id, version, title, architectures, new AppBundleInfo(id, "/Applications/" + appName, version));
    }

    public static string? VersionOf(IReadOnlyDictionary<string, string> plist) =>
        plist.GetValueOrDefault("CFBundleShortVersionString") is { Length: > 0 } version
            ? version
            : plist.GetValueOrDefault("CFBundleVersion");

    /// <summary>The top-level string values of an app's Info.plist, in XML or binary form.</summary>
    public static IReadOnlyDictionary<string, string> ReadInfoPlist(string appPath)
    {
        string file = System.IO.Path.Combine(appPath, "Contents", "Info.plist");
        var info = new FileInfo(file);
        if (!info.Exists || info.Length > MaxPlistBytes)
            throw new InvalidDataException($"{appPath} has no readable Info.plist.");
        return ParsePlist(File.ReadAllBytes(file));
    }

    /// <summary>The top-level string values of a property list in XML or binary form.</summary>
    public static IReadOnlyDictionary<string, string> ParsePlist(byte[] bytes) =>
        ParseXmlPlist(bytes.AsSpan().StartsWith("bplist"u8) ? ConvertBinaryPlist(bytes) : bytes);

    /// <summary>The .icns file an app names as its icon, with the extension the bundle may omit.</summary>
    public static string? IconFileName(IReadOnlyDictionary<string, string> plist) =>
        plist.GetValueOrDefault("CFBundleIconFile") is { Length: > 0 } icon && !icon.Contains('/')
            ? icon.EndsWith(".icns", StringComparison.OrdinalIgnoreCase) ? icon : icon + ".icns"
            : null;

    /// <summary>Converts an .icns to a 256-pixel PNG with sips, which ships with macOS.</summary>
    public static bool ConvertIcon(string icns, string destinationPng)
    {
        // Elsewhere, such as pkg-index on Linux, the package is simply published without an icon.
        if (!OperatingSystem.IsMacOS())
            return false;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destinationPng)!);
        var result = Tool.Run("/usr/bin/sips", ["-s", "format", "png", "-Z", "256", icns, "--out", destinationPng],
            timeout: TimeSpan.FromMinutes(1));
        if (result.ExitCode == 0)
            return true;
        Logger.Warn($"Could not convert the icon {icns}: {result.Output.Trim()}");
        return false;
    }

    internal static IReadOnlyDictionary<string, string> ParseXmlPlist(byte[] xml)
    {
        XElement dict = XarArchive.ParseXml(xml).Root?.Element("dict")
            ?? throw new InvalidDataException("The property list has no top-level dictionary.");
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

    // plutil ships with macOS and is the supported reader of binary property lists.
    private static byte[] ConvertBinaryPlist(byte[] binary)
    {
        if (!OperatingSystem.IsMacOS())
            throw new InvalidDataException("Binary property lists can only be read on macOS.");
        var result = Tool.Run("/usr/bin/plutil", ["-convert", "xml1", "-o", "-", "-"], binary, TimeSpan.FromSeconds(30));
        if (result.ExitCode != 0 || result.StandardOutput.Length > MaxPlistBytes)
            throw new InvalidDataException($"plutil could not read a binary property list: {result.StandardError.Trim()}");
        return result.StandardOutput;
    }

    private const uint FatMagic = 0xCAFEBABE;
    private const uint FatMagic64 = 0xCAFEBABF;
    private const uint MachMagic64 = 0xFEEDFACF;
    private const int CpuTypeX64 = 0x01000007;
    private const int CpuTypeArm64 = 0x0100000C;

    /// <summary>The architectures of an app's main executable, from its Mach-O header; empty when unknown.</summary>
    public static IReadOnlyList<string> ReadArchitectures(string appPath, IReadOnlyDictionary<string, string> plist)
    {
        if (plist.GetValueOrDefault("CFBundleExecutable") is not { Length: > 0 } executable || executable.Contains('/'))
            return [];
        string file = System.IO.Path.Combine(appPath, "Contents", "MacOS", executable);
        try
        {
            using var stream = File.OpenRead(file);
            byte[] header = new byte[8 + 20 * 32];
            int read = stream.ReadAtLeast(header, 8, throwOnEndOfStream: false);
            return ParseArchitectures(header.AsSpan(0, read));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    internal static IReadOnlyList<string> ParseArchitectures(ReadOnlySpan<byte> header)
    {
        if (header.Length < 8)
            return [];
        uint magic = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (magic is FatMagic or FatMagic64)
        {
            int count = (int)BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
            int stride = magic == FatMagic ? 20 : 32;
            var architectures = new List<string>();
            for (int i = 0; i < count && 8 + (i + 1) * stride <= header.Length; i++)
                if (Name(BinaryPrimitives.ReadInt32BigEndian(header[(8 + i * stride)..])) is { } name)
                    architectures.Add(name);
            return architectures;
        }
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) == MachMagic64
            && Name(BinaryPrimitives.ReadInt32LittleEndian(header[4..])) is { } single)
            return [single];
        return [];
    }

    private static string? Name(int cpuType) => cpuType switch
    {
        CpuTypeX64 => "x86_64",
        CpuTypeArm64 => "arm64",
        _ => null,
    };
}
