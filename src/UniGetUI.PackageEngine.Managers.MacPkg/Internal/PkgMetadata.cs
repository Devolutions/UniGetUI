using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using UniGetUI.Core.Logging;

namespace UniGetUI.PackageEngine.Managers.MacPkgManager.Internal;

/// <summary>
/// What UniGetUI needs from a feed package, read from the file itself so that IT never writes
/// detection rules: its receipt identifier (or bundle id), version, title and the app it installs.
/// </summary>
internal sealed record PkgMetadata(string Id, string Version, string? Title, IReadOnlyList<string> Architectures,
    AppBundleInfo? App = null, bool WrapsPackage = false)
{
    internal const int MaxMetadataBytes = 4 * 1024 * 1024;

    /// <summary>
    /// Reads a .pkg, .dmg or .zip; with <paramref name="iconFile"/>, also saves its app's icon there as a
    /// PNG, unless that means reading a package's payload and <paramref name="iconFromPayload"/> is false.
    /// </summary>
    public static PkgMetadata Read(string path, string? iconFile = null, bool iconFromPayload = true)
    {
        switch (FeedFile.KindOf(path))
        {
            case FeedFileKind.DiskImage:
                return DiskImage.ReadMetadata(path, iconFile, iconFromPayload);
            case FeedFileKind.Archive:
                return ZipApp.ReadMetadata(path, iconFile);
            default:
                using (var stream = File.OpenRead(path))
                    return Read(stream, iconFromPayload ? iconFile : null);
        }
    }

    public static PkgMetadata Read(Stream stream, string? iconFile = null)
    {
        var archive = XarArchive.Open(stream);
        if (archive.ReadFile("Distribution", MaxMetadataBytes) is { } distribution)
            return FromDistribution(XarArchive.ParseXml(distribution).Root!, archive, iconFile);
        if (archive.ReadFile("PackageInfo", MaxMetadataBytes) is { } packageInfo)
            return FromPackageInfo(XarArchive.ParseXml(packageInfo).Root!, archive, iconFile);
        throw new InvalidDataException("The installer package has neither a Distribution nor a PackageInfo file.");
    }

    // A product archive lists its components as <pkg-ref id version>#file.pkg</pkg-ref>. The first
    // component is the one whose receipt marks the product as installed.
    private static PkgMetadata FromDistribution(XElement script, XarArchive archive, string? iconFile)
    {
        foreach (XElement pkgRef in script.Elements("pkg-ref"))
        {
            string? id = pkgRef.Attribute("id")?.Value.Trim();
            string file = pkgRef.Value.Trim();
            if (string.IsNullOrEmpty(id) || !file.StartsWith('#'))
                continue;
            string component = Uri.UnescapeDataString(file[1..]);
            XElement? info = archive.ReadFile(component + "/PackageInfo", MaxMetadataBytes) is { } bytes
                ? XarArchive.ParseXml(bytes).Root
                : null;
            string? version = pkgRef.Attribute("version")?.Value.Trim();
            if (string.IsNullOrEmpty(version))
                version = info?.Attribute("version")?.Value.Trim();
            if (string.IsNullOrEmpty(version))
                continue;

            string? hosts = script.Element("options")?.Attribute("hostArchitectures")?.Value;
            var (app, appTitle) = info is null ? (null, null) : ReadComponentApp(info, archive, component + "/Payload", iconFile);
            string? title = CleanTitle(script.Element("title")?.Value)
                ?? appTitle
                ?? AppName(script.Elements("pkg-ref").Where(r => r.Attribute("id")?.Value.Trim() == id).Descendants("bundle"));
            return new PkgMetadata(id, version, title, SplitArchitectures(hosts), app);
        }
        throw new InvalidDataException("The installer package's Distribution lists no component with an identifier and version.");
    }

    private static PkgMetadata FromPackageInfo(XElement info, XarArchive archive, string? iconFile)
    {
        string id = info.Attribute("identifier")?.Value.Trim() ?? "";
        string version = info.Attribute("version")?.Value.Trim() ?? "";
        if (id.Length == 0 || version.Length == 0)
            throw new InvalidDataException("The installer package's PackageInfo has no identifier or version.");
        var (app, title) = ReadComponentApp(info, archive, "Payload", iconFile);
        return new PkgMetadata(id, version, title ?? AppName(info.Elements("bundle")), [], app);
    }

    /// <summary>
    /// The app a component installs, where it lands and its name, saving its icon when asked. The payload
    /// holds the icon and an app-folder install's identity, so it is read, in one pass, only for those.
    /// </summary>
    private static (AppBundleInfo? App, string? Title) ReadComponentApp(XElement info, XarArchive archive,
        string payloadPath, string? iconFile)
    {
        string location = (info.Attribute("install-location")?.Value ?? "/").Trim();
        // A component installed into the app's own folder lists only the bundles nested in it (such as
        // Sparkle's Updater.app); the app itself is described by the payload's Contents/Info.plist.
        bool locationIsApp = location.TrimEnd('/').EndsWith(".app", StringComparison.OrdinalIgnoreCase);
        XElement? bundle = locationIsApp ? null : OutermostApps(info.Elements("bundle"))
            .FirstOrDefault(b => !string.IsNullOrWhiteSpace(b.Attribute("id")?.Value));
        string? appInPayload = locationIsApp ? "." : bundle?.Attribute("path")?.Value.Trim();
        if (appInPayload is null || JoinPath(location, appInPayload) is not { } path)
            return (null, null);

        IReadOnlyDictionary<string, string>? plist = null;
        if (locationIsApp || iconFile is not null)
        {
            try
            {
                using var payload = archive.OpenFile(payloadPath);
                if (payload is not null)
                {
                    var (plistBytes, icons) = PkgPayload.FindAppResources(payload, appInPayload, keepIcons: iconFile is not null);
                    plist = plistBytes is null ? null : AppBundle.ParsePlist(plistBytes);
                    if (iconFile is not null)
                        PkgPayload.SaveIcon(plist, icons, iconFile);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or System.Xml.XmlException)
            {
                Logger.Warn($"Could not read the app in the payload {payloadPath}: {ex.Message}");
            }
        }

        AppBundleInfo? app = bundle is not null
            ? new AppBundleInfo(bundle.Attribute("id")!.Value.Trim(), path,
                bundle.Attribute("CFBundleShortVersionString")?.Value is { Length: > 0 } shortVersion
                    ? shortVersion
                    : bundle.Attribute("CFBundleVersion")?.Value)
            : plist?.GetValueOrDefault("CFBundleIdentifier") is { Length: > 0 } bundleId
                ? new AppBundleInfo(bundleId, path, AppBundle.VersionOf(plist))
                : null;
        string? title = plist?.GetValueOrDefault("CFBundleDisplayName") is { Length: > 0 } display
            ? display
            : Path.GetFileNameWithoutExtension(path);
        return (app, title);
    }

    // The absolute path of a bundle from its component's install location; null if it climbs out.
    private static string? JoinPath(string location, string relative)
    {
        string[] segments = (location + "/" + relative.TrimEnd('/')).Split('/')
            .Where(segment => segment.Length > 0 && segment != ".").ToArray();
        return segments.Contains("..") ? null : "/" + string.Join('/', segments);
    }

    private static IEnumerable<XElement> OutermostApps(IEnumerable<XElement> bundles) => bundles
        .Where(b => b.Attribute("path")?.Value.TrimEnd('/').EndsWith(".app", StringComparison.OrdinalIgnoreCase) == true)
        .OrderBy(b => b.Attribute("path")!.Value.Trim().TrimStart('.', '/').TrimEnd('/').Count(c => c == '/'));

    // Without a usable title, the outermost application the package installs: Electron apps list
    // their nested helpers (GitKraken.app/Contents/Frameworks/… Helper.app) before the app itself.
    private static string? AppName(IEnumerable<XElement> bundles) => OutermostApps(bundles)
        .Select(b => Path.GetFileNameWithoutExtension(b.Attribute("path")!.Value.TrimEnd('/')))
        .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));

    // A title such as "SU_TITLE" is a key into the package's localized strings, not a name.
    private static string? CleanTitle(string? title)
    {
        title = title?.Trim();
        return string.IsNullOrEmpty(title) || Regex.IsMatch(title, "^[A-Z0-9_]+$") ? null : title;
    }

    private static string[] SplitArchitectures(string? hosts) =>
        hosts?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];

    /// <summary>
    /// How well a package suits this Mac: 2 runs natively, 1 needs Rosetta, 0 cannot run at all.
    /// A package that declares no architectures is assumed to run anywhere.
    /// </summary>
    public static int ArchitectureFit(IReadOnlyList<string> architectures, Architecture machine)
    {
        if (architectures.Count == 0)
            return 2;
        string native = machine == Architecture.Arm64 ? "arm64" : "x86_64";
        if (architectures.Contains(native, StringComparer.OrdinalIgnoreCase))
            return 2;
        return machine == Architecture.Arm64
            && architectures.Contains("x86_64", StringComparer.OrdinalIgnoreCase) ? 1 : 0;
    }
}

// Lets a feed keep what it read from each file across restarts, instead of mounting every disk
// image and reading payloads again at each launch.
[JsonSerializable(typeof(PkgMetadata))]
internal partial class PkgMetadataJsonContext : JsonSerializerContext;
