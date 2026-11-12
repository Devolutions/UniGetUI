using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using UniGetUI.Core.Logging;

namespace UniGetUI.Extensions.Xcode;

public enum XcodeSimulatorRuntimeOrigin
{
    AppleCatalog,
    Installed,
    Bundled,
}

public enum XcodeSimulatorRuntimeDistribution
{
    Unknown,
    XcodeManaged,
    PublicImage,
}

/// <summary>A simulator runtime as reported by Apple's public catalog or the selected developer tools.</summary>
public sealed record XcodeSimulatorRuntime(
    string Name,
    string Platform,
    string Version,
    string? Build,
    XcodeArchitecture Architecture,
    bool? IsPrerelease,
    XcodeSimulatorRuntimeOrigin Origin,
    long? SizeBytes,
    string? State = null,
    string? Path = null,
    XcodeSimulatorRuntimeDistribution Distribution = XcodeSimulatorRuntimeDistribution.Unknown,
    string? DownloadUrl = null);

public sealed record XcodeSimulatorSdkMapping(XcodeSdkIdentity SDK, string SimulatorBuild);

/// <param name="SDKMappings">Null when Apple's optional mapping data is missing or cannot be read safely.</param>
public sealed record XcodeSimulatorRuntimeCatalog(
    IReadOnlyList<XcodeSimulatorRuntime> Runtimes,
    IReadOnlyList<XcodeSimulatorSdkMapping>? SDKMappings);

public sealed record XcodeSavedSimulatorRuntimeCatalog(XcodeSimulatorRuntimeCatalog Catalog, DateTime SavedAtUtc);

public interface IXcodeSimulatorRuntimeService
{
    Task<XcodeSimulatorRuntimeCatalog> GetCatalogAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<XcodeSimulatorRuntime>> GetInstalledAsync(CancellationToken cancellationToken);
}

public interface IXcodeSimulatorRuntimeCatalogCache
{
    Task<XcodeSavedSimulatorRuntimeCatalog?> GetSavedCatalogAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Reads Apple's public simulator index without an account, and the runtimes visible to the selected
/// developer tools. It never downloads, installs, or removes a runtime.
/// </summary>
public sealed partial class XcodeSimulatorRuntimeService : IXcodeSimulatorRuntimeService, IXcodeSimulatorRuntimeCatalogCache
{
    private static readonly Uri CatalogUri =
        new("https://devimages-cdn.apple.com/downloads/xcode/simulators/index2.dvtdownloadableindex");
    internal delegate Task<XcodesResult> CommandRunner(
        IReadOnlyList<string> arguments, TimeSpan? timeout, CancellationToken cancellationToken);

    private static readonly TimeSpan InstalledTimeout = TimeSpan.FromSeconds(30);
    private readonly HttpClient _client;
    private readonly CommandRunner _runInstalled;
    private readonly TimeSpan _requestTimeout;
    private readonly XcodePublicCatalogCache? _cache;

    public XcodeSimulatorRuntimeService()
        : this(XcodePublicCatalogDownload.Client, CreateInstalledRunner("/usr/bin/xcrun").RunAsync)
    {
    }

    public XcodeSimulatorRuntimeService(Func<string> cacheFile)
        : this(XcodePublicCatalogDownload.Client, CreateInstalledRunner("/usr/bin/xcrun").RunAsync, cacheFile: cacheFile)
    {
    }

    internal static XcodesRunner CreateInstalledRunner(string executable)
        => new(executable, new Dictionary<string, string?>
        {
            ["XCODES_USERNAME"] = null,
            ["XCODES_PASSWORD"] = null,
        });

    internal XcodeSimulatorRuntimeService(HttpClient client, CommandRunner runInstalled,
        TimeSpan? requestTimeout = null, Func<string>? cacheFile = null)
    {
        _client = client;
        _runInstalled = runInstalled;
        _requestTimeout = requestTimeout ?? XcodePublicCatalogDownload.DefaultTimeout;
        _cache = cacheFile is null ? null : new XcodePublicCatalogCache(cacheFile, "Apple simulator catalog");
    }

    public async Task<XcodeSimulatorRuntimeCatalog> GetCatalogAsync(CancellationToken cancellationToken)
    {
        byte[] data = await XcodePublicCatalogDownload.GetAsync(
            _client, CatalogUri, _requestTimeout, "simulator runtime", cancellationToken).ConfigureAwait(false);
        XcodeSimulatorRuntimeCatalog catalog = ParseCatalog(data);
        if (_cache is not null)
            await _cache.SaveAsync(data, cancellationToken).ConfigureAwait(false);
        return catalog;
    }

    public async Task<XcodeSavedSimulatorRuntimeCatalog?> GetSavedCatalogAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        XcodeSavedPublicCatalog? saved = _cache is null
            ? null : await _cache.GetAsync(cancellationToken).ConfigureAwait(false);
        return saved is null ? null : new XcodeSavedSimulatorRuntimeCatalog(ParseCatalog(saved.Data), saved.SavedAtUtc);
    }

    public async Task<IReadOnlyList<XcodeSimulatorRuntime>> GetInstalledAsync(CancellationToken cancellationToken)
    {
        XcodesResult result = await _runInstalled(["simctl", "runtime", "list", "-j"], InstalledTimeout, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Success)
            throw new XcodesException(string.IsNullOrWhiteSpace(result.Output)
                ? $"xcrun exited with code {result.ExitCode}."
                : XcodesError.Summarize(result.Output, result.ExitCode));
        return ParseInstalled(result.StandardOutput);
    }

    internal static XcodeSimulatorRuntimeCatalog ParseCatalog(ReadOnlySpan<byte> data)
    {
        using var stream = new MemoryStream(data.ToArray(), writable: false);
        using XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            MaxCharactersInDocument = 8 * 1024 * 1024,
        });
        XDocument document = XDocument.Load(reader);
        if (document.Root is not { Name.LocalName: "plist" } root)
            throw new XcodesException("The simulator runtime catalog is not a property list.");

        Dictionary<string, XElement> fields = Dictionary(root.Element("dict"));
        if (!fields.TryGetValue("downloadables", out XElement? downloads) || downloads.Name != "array")
            throw new XcodesException("The simulator runtime catalog has no runtime list.");

        HashSet<(string Platform, string Build)>? seeds = ReadSeeds(fields);
        List<XcodeSimulatorRuntime> runtimes = [];
        foreach (XElement element in downloads.Elements())
        {
            Dictionary<string, XElement> item = Dictionary(element);
            string category = RequiredString(item, "category");
            if (!string.Equals(category, "simulator", StringComparison.Ordinal))
                continue;

            string platformId = RequiredString(item, "platform");
            string platform = PlatformName(platformId);
            string name = RequiredString(item, "name");
            Dictionary<string, XElement> simulator = Dictionary(RequiredElement(item, "simulatorVersion"));
            string version = RequiredString(simulator, "version");
            string build = RequiredString(simulator, "buildUpdate");
            string[]? architectures = ReadStringArray(item, "architectures");
            bool? prerelease = Prerelease(name, version);
            if (seeds?.Contains((platformId, build)) == true)
                prerelease = true;
            else if (seeds is null && prerelease == false)
                prerelease = null;
            long? size = item.TryGetValue("fileSize", out XElement? value)
                && value.Name == "integer"
                && long.TryParse(value.Value, NumberStyles.None, CultureInfo.InvariantCulture, out long bytes)
                && bytes > 0 ? bytes : null;
            item.TryGetValue("contentType", out XElement? contentType);
            string? downloadUrl = contentType?.Name == "string" && contentType.Value == "package"
                && item.TryGetValue("source", out XElement? source) && source.Name == "string"
                && XcodePublicSimulatorRuntimeDownloader.TryGetPublicUri(source.Value, out _)
                    ? source.Value : null;
            XcodeSimulatorRuntimeDistribution distribution =
                contentType?.Name == "string"
                && contentType.Value == "cryptexDiskImage"
                && item.TryGetValue("downloadMethod", out XElement? method) && method.Name == "string"
                && method.Value == "mobileAsset"
                    ? XcodeSimulatorRuntimeDistribution.XcodeManaged
                    : downloadUrl is not null
                        ? XcodeSimulatorRuntimeDistribution.PublicImage
                        : XcodeSimulatorRuntimeDistribution.Unknown;
            runtimes.Add(new XcodeSimulatorRuntime(name, platform, version, build,
                Architecture(architectures), prerelease, XcodeSimulatorRuntimeOrigin.AppleCatalog, size,
                Distribution: distribution, DownloadUrl: downloadUrl));
        }

        if (runtimes.Count == 0)
            throw new XcodesException("The simulator runtime catalog contained no simulator runtimes.");
        return new XcodeSimulatorRuntimeCatalog(runtimes, ReadSDKMappings(fields));
    }

    internal static IReadOnlyList<XcodeSimulatorRuntime> ParseInstalled(string json)
    {
        Dictionary<string, InstalledSimulatorRuntime>? values =
            JsonSerializer.Deserialize(json, SimulatorRuntimeJsonContext.Default.InstalledRuntimes);
        if (values is null)
            throw new XcodesException("The installed simulator runtime list is empty.");

        List<XcodeSimulatorRuntime> runtimes = [];
        foreach ((string identifier, InstalledSimulatorRuntime? value) in values)
        {
            if (value is null || string.IsNullOrWhiteSpace(identifier)
                || string.IsNullOrWhiteSpace(value.Version) || string.IsNullOrWhiteSpace(value.PlatformIdentifier))
                throw new XcodesException("The installed simulator runtime list contains an unrecognized identity.");

            string platform = PlatformName(value.PlatformIdentifier);
            string? build = string.IsNullOrWhiteSpace(value.Build) || value.Build == "UnknownBuildNumber"
                ? null : value.Build;
            XcodeSimulatorRuntimeOrigin origin = value.Kind == "Bundled with Xcode"
                ? XcodeSimulatorRuntimeOrigin.Bundled : XcodeSimulatorRuntimeOrigin.Installed;
            runtimes.Add(new XcodeSimulatorRuntime($"{platform} {value.Version}", platform, value.Version, build,
                Architecture(value.SupportedArchitectures), null, origin, value.SizeBytes is > 0 ? value.SizeBytes : null,
                value.State, value.RuntimeBundlePath ?? value.Path));
        }
        return runtimes;
    }

    private static HashSet<(string Platform, string Build)>? ReadSeeds(Dictionary<string, XElement> fields)
    {
        if (!fields.TryGetValue("sdkToSeedMappings", out XElement? mappings))
            return null;
        if (mappings.Name != "array")
            throw new XcodesException("The simulator runtime catalog has invalid release mappings.");

        HashSet<(string Platform, string Build)> seeds = [];
        foreach (XElement element in mappings.Elements())
        {
            Dictionary<string, XElement> item = Dictionary(element);
            string platform = RequiredString(item, "platform");
            string build = RequiredString(item, "buildUpdate");
            if (!item.TryGetValue("seedNumber", out XElement? number) || number.Name != "integer"
                || !int.TryParse(number.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int seed) || seed <= 0)
                throw new XcodesException("The simulator runtime catalog has invalid release mappings.");
            seeds.Add((platform, build));
        }
        return seeds;
    }

    private static IReadOnlyList<XcodeSimulatorSdkMapping>? ReadSDKMappings(Dictionary<string, XElement> fields)
    {
        if (!fields.TryGetValue("sdkToSimulatorMappings", out XElement? mappings))
            return null;

        try
        {
            if (mappings.Name != "array")
                throw new XcodesException("The SDK-to-simulator mappings are not an array.");
            HashSet<XcodeSimulatorSdkMapping> result = [];
            bool unsupportedPlatform = false;
            foreach (XElement element in mappings.Elements())
            {
                Dictionary<string, XElement> item = Dictionary(element);
                string identifier = RequiredString(item, "sdkIdentifier");
                (string prefix, string platform) = identifier switch
                {
                    var value when value.StartsWith("iphoneos", StringComparison.Ordinal) => ("iphoneos", "iOS"),
                    var value when value.StartsWith("watchos", StringComparison.Ordinal) => ("watchos", "watchOS"),
                    var value when value.StartsWith("appletvos", StringComparison.Ordinal) => ("appletvos", "tvOS"),
                    var value when value.StartsWith("xros", StringComparison.Ordinal) => ("xros", "visionOS"),
                    _ => ("", ""),
                };
                if (prefix.Length == 0)
                {
                    unsupportedPlatform = true;
                    continue;
                }

                string sdkBuild = RequiredString(item, "sdkBuildUpdate");
                string simulatorBuild = RequiredString(item, "simulatorBuildUpdate");
                if (!XcodeSdkIdentity.TryCreate(platform, identifier[prefix.Length..], sdkBuild, out var sdk)
                    || !XcodeSimulatorImageFile.IsValidBuild(simulatorBuild))
                    throw new XcodesException("An SDK-to-simulator mapping has an unrecognized identity.");
                result.Add(new XcodeSimulatorSdkMapping(sdk, simulatorBuild));
            }
            if (unsupportedPlatform)
                Logger.Warn("Skipped Apple SDK-to-simulator mappings for unsupported platforms.");
            return [.. result];
        }
        catch (XcodesException ex)
        {
            Logger.Warn($"Could not read the Apple SDK-to-simulator mappings: {ex.Message}");
            return null;
        }
    }

    private static Dictionary<string, XElement> Dictionary(XElement? element)
    {
        if (element is null || element.Name != "dict")
            throw new XcodesException("The simulator runtime catalog contains an invalid dictionary.");
        XElement[] elements = [.. element.Elements()];
        if (elements.Length % 2 != 0)
            throw new XcodesException("The simulator runtime catalog contains an invalid dictionary.");

        Dictionary<string, XElement> result = new(StringComparer.Ordinal);
        for (int i = 0; i < elements.Length; i += 2)
        {
            if (elements[i].Name != "key" || !result.TryAdd(elements[i].Value, elements[i + 1]))
                throw new XcodesException("The simulator runtime catalog contains an invalid dictionary.");
        }
        return result;
    }

    private static XElement RequiredElement(Dictionary<string, XElement> values, string name)
        => values.TryGetValue(name, out XElement? value)
            ? value : throw new XcodesException("The simulator runtime catalog contains an unrecognized identity.");

    private static string RequiredString(Dictionary<string, XElement> values, string name)
    {
        XElement value = RequiredElement(values, name);
        return value.Name == "string" && !string.IsNullOrWhiteSpace(value.Value)
            ? value.Value : throw new XcodesException("The simulator runtime catalog contains an unrecognized identity.");
    }

    private static string[]? ReadStringArray(Dictionary<string, XElement> values, string name)
    {
        if (!values.TryGetValue(name, out XElement? array))
            return null;
        if (array.Name != "array" || array.Elements().Any(item => item.Name != "string"))
            throw new XcodesException("The simulator runtime catalog contains invalid architectures.");
        return [.. array.Elements().Select(item => item.Value)];
    }

    private static XcodeArchitecture Architecture(string[]? names)
    {
        if (names is null || names.Length == 0 || names.Any(name => name is not ("arm64" or "x86_64")))
            return XcodeArchitecture.Unspecified;
        bool arm = names.Contains("arm64", StringComparer.Ordinal);
        bool intel = names.Contains("x86_64", StringComparer.Ordinal);
        return arm && intel ? XcodeArchitecture.Universal : arm ? XcodeArchitecture.AppleSilicon : XcodeArchitecture.Intel;
    }

    private static string PlatformName(string platform) => platform switch
    {
        "com.apple.platform.iphoneos" or "com.apple.platform.iphonesimulator" => "iOS",
        "com.apple.platform.appletvos" or "com.apple.platform.appletvsimulator" => "tvOS",
        "com.apple.platform.watchos" or "com.apple.platform.watchsimulator" => "watchOS",
        "com.apple.platform.xros" or "com.apple.platform.xrsimulator" => "visionOS",
        _ => platform,
    };

    private static bool? Prerelease(string name, string version)
    {
        if (PrereleaseWords().IsMatch(name))
            return true;
        string suffix = name.EndsWith(" Simulator Runtime", StringComparison.OrdinalIgnoreCase)
            ? " Simulator Runtime" : name.EndsWith(" Simulator", StringComparison.OrdinalIgnoreCase) ? " Simulator" : "";
        int firstSpace = name.IndexOf(' ');
        if (suffix.Length == 0 || firstSpace < 0 || firstSpace + suffix.Length >= name.Length)
            return null;
        string namedVersion = name[(firstSpace + 1)..^suffix.Length];
        return XcodeVersion.TryParse(namedVersion, out XcodeVersion named)
            && XcodeVersion.TryParse(version, out XcodeVersion actual)
            && !named.IsPrerelease && named.CompareTo(actual) == 0 ? false : null;
    }

    [GeneratedRegex(@"\b(?:alpha|beta|release candidate|rc|seed|preview)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PrereleaseWords();
}

internal sealed class InstalledSimulatorRuntime
{
    public string? Build { get; set; }
    public string? Kind { get; set; }
    public string? PlatformIdentifier { get; set; }
    public string? Version { get; set; }
    public string? State { get; set; }
    public string? Path { get; set; }
    public string? RuntimeBundlePath { get; set; }
    public long? SizeBytes { get; set; }
    public string[]? SupportedArchitectures { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Dictionary<string, InstalledSimulatorRuntime>), TypeInfoPropertyName = "InstalledRuntimes")]
internal partial class SimulatorRuntimeJsonContext : JsonSerializerContext;
