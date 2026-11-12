using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UniGetUI.Extensions.Xcode;

/// <summary>Public release information, separate from installation and account state.</summary>
public sealed record XcodeCatalogRelease(
    string Version,
    string Build,
    XcodeArchitecture Architecture,
    DateOnly? ReleaseDate,
    Version? RequiredMacOS,
    Uri? ReleaseNotes,
    IReadOnlyList<string> SDKs,
    IReadOnlyList<string> Compilers,
    int? SizeMB)
{
    public IReadOnlyList<XcodeSdkIdentity> SDKIdentities { get; init; } = [];

    /// <summary>Where Apple serves this release's archive, always on download.developer.apple.com; null when unknown.</summary>
    public Uri? Download { get; init; }

    /// <summary>The archive's SHA-1 in lowercase hexadecimal, when the catalog has it.</summary>
    public string? Sha1 { get; init; }
}

public interface IXcodeReleasesCatalog
{
    Task<IReadOnlyList<XcodeCatalogRelease>> GetAsync(CancellationToken cancellationToken);
}

public sealed record XcodeSavedReleaseCatalog(IReadOnlyList<XcodeCatalogRelease> Releases, DateTime SavedAtUtc);

public interface IXcodeReleasesCatalogCache
{
    Task<XcodeSavedReleaseCatalog?> GetSavedAsync(CancellationToken cancellationToken);
}

/// <summary>Reads Xcode Releases without credentials. It never falls back to another source.</summary>
public sealed class XcodeReleasesCatalog : IXcodeReleasesCatalog, IXcodeReleasesCatalogCache
{
    private static readonly Uri CatalogUri = new("https://xcodereleases.com/data.json");
    private readonly HttpClient _client;
    private readonly TimeSpan _requestTimeout;
    private readonly XcodePublicCatalogCache? _cache;

    public XcodeReleasesCatalog() : this(XcodePublicCatalogDownload.Client) { }

    public XcodeReleasesCatalog(Func<string> cacheFile) : this(XcodePublicCatalogDownload.Client, cacheFile: cacheFile) { }

    internal XcodeReleasesCatalog(HttpClient client, TimeSpan? requestTimeout = null, Func<string>? cacheFile = null)
    {
        _client = client;
        _requestTimeout = requestTimeout ?? XcodePublicCatalogDownload.DefaultTimeout;
        _cache = cacheFile is null ? null : new XcodePublicCatalogCache(cacheFile, "Xcode Releases details");
    }

    public async Task<IReadOnlyList<XcodeCatalogRelease>> GetAsync(CancellationToken cancellationToken)
    {
        byte[] data = await XcodePublicCatalogDownload.GetAsync(
            _client, CatalogUri, _requestTimeout, "Xcode Releases", cancellationToken).ConfigureAwait(false);
        IReadOnlyList<XcodeCatalogRelease> releases = Parse(data);
        if (_cache is not null)
            await _cache.SaveAsync(data, cancellationToken).ConfigureAwait(false);
        return releases;
    }

    public async Task<XcodeSavedReleaseCatalog?> GetSavedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        XcodeSavedPublicCatalog? saved = _cache is null
            ? null : await _cache.GetAsync(cancellationToken).ConfigureAwait(false);
        return saved is null ? null : new XcodeSavedReleaseCatalog(Parse(saved.Data), saved.SavedAtUtc);
    }

    internal static IReadOnlyList<XcodeCatalogRelease> Parse(ReadOnlySpan<byte> json)
    {
        XcodeCatalogItem?[] items = JsonSerializer.Deserialize(json, XcodeCatalogJsonContext.Default.XcodeCatalogItemArray)
            ?? throw new XcodesException("The Xcode Releases catalog is empty.");
        List<XcodeCatalogRelease> releases = [];
        int skipped = 0;
        foreach (XcodeCatalogItem? item in items)
        {
            if (item is null)
            {
                skipped++;
                continue;
            }
            string? version = FormatVersion(item.Version);
            string? build = item.Version?.Build;
            if (version is null || string.IsNullOrWhiteSpace(build)
                || !TryArchitecture(item.Links?.Download?.Architectures, out XcodeArchitecture architecture))
            {
                skipped++;
                continue;
            }

            DateOnly? date = null;
            if (item.Date is { } ymd
                && DateOnly.TryParseExact($"{ymd.Year:D4}-{ymd.Month:D2}-{ymd.Day:D2}", "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly parsedDate))
                date = parsedDate;

            Version? required = TryMacOSVersion(item.Requires, out Version? macOS) ? macOS : null;
            Uri? notes = Uri.TryCreate(item.Links?.Notes?.Url, UriKind.Absolute, out Uri? uri)
                && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0
                && (uri.Host == "apple.com" || uri.Host.EndsWith(".apple.com", StringComparison.OrdinalIgnoreCase))
                    ? uri : null;
            releases.Add(new XcodeCatalogRelease(version, build, architecture, date, required, notes,
                DescribeVersions(item.Sdks), DescribeVersions(item.Compilers),
                item.Links?.Download?.SizeMB is > 0 ? item.Links.Download.SizeMB : null)
            {
                SDKIdentities = ReadSDKIdentities(item.Sdks),
                Download = TryDownload(item.Links?.Download?.Url),
                Sha1 = item.Checksums?.Sha1 is { Length: 40 } sha1 && sha1.All(char.IsAsciiHexDigit)
                    ? sha1.ToLowerInvariant() : null,
            });
        }

        if (skipped > 0)
            throw new XcodesException($"The Xcode Releases catalog contains {skipped} entries with an unrecognized identity.");
        if (releases.Count == 0)
            throw new XcodesException("The Xcode Releases catalog contained no recognizable releases.");
        return releases;
    }

    internal static bool TryMacOSVersion(string? text, out Version? version)
    {
        version = null;
        if (!Version.TryParse(text, out Version? parsed) || parsed.Major < 1 || parsed.Revision >= 0)
            return false;
        version = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0));
        return true;
    }

    /// <summary>An Apple developer download of an Xcode archive, and nothing else.</summary>
    internal static Uri? TryDownload(string? text)
        => Uri.TryCreate(text, UriKind.Absolute, out Uri? uri)
           && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0
           && uri.Query.Length == 0 && uri.Fragment.Length == 0
           && string.Equals(uri.Host, "download.developer.apple.com", StringComparison.OrdinalIgnoreCase)
           && (uri.AbsolutePath.EndsWith(".xip", StringComparison.OrdinalIgnoreCase)
               || uri.AbsolutePath.EndsWith(".xip.aea", StringComparison.OrdinalIgnoreCase))
            ? uri : null;

    private static string? FormatVersion(XcodeCatalogVersion? version)
    {
        if (!XcodeVersion.TryParse(version?.Number, out XcodeVersion parsed) || parsed.IsPrerelease || version?.Release is not { } release)
            return null;

        int stages = (release.Beta is > 0 ? 1 : 0) + (release.Rc is > 0 ? 1 : 0)
            + (release.GmSeed is > 0 ? 1 : 0) + (release.Dp is > 0 ? 1 : 0)
            + (release.Gm is true ? 1 : 0) + (release.Release is true ? 1 : 0);
        if (stages != 1) return null;

        string baseVersion = $"{parsed.Major}.{parsed.Minor}" + (parsed.Patch > 0 ? $".{parsed.Patch}" : "");
        string? label = release switch
        {
            { Beta: > 0 } => FormatStage("Beta", release.Beta.Value),
            { Rc: > 0 } => FormatStage("Release Candidate", release.Rc.Value),
            { GmSeed: > 0 } => FormatStage("GM Seed", release.GmSeed.Value),
            { Dp: > 0 } => FormatStage("DP", release.Dp.Value),
            { Gm: true } or { Release: true } => "",
            _ => null,
        };
        return label is null ? null : label.Length == 0 ? baseVersion : $"{baseVersion} {label}";
    }

    private static string FormatStage(string stage, int iteration)
        => iteration > 1 ? $"{stage} {iteration}" : stage;

    private static bool TryArchitecture(string[]? names, out XcodeArchitecture architecture)
    {
        architecture = XcodeArchitecture.Unspecified;
        if (names is null || names.Length == 0) return true;
        if (names.Any(name => name is not ("arm64" or "x86_64"))) return false;
        bool arm = names.Contains("arm64", StringComparer.Ordinal);
        bool intel = names.Contains("x86_64", StringComparer.Ordinal);
        architecture = arm && intel ? XcodeArchitecture.Universal : arm ? XcodeArchitecture.AppleSilicon : XcodeArchitecture.Intel;
        return true;
    }

    private static string[] DescribeVersions(Dictionary<string, XcodeCatalogToolVersion?[]?>? versions)
        => versions is null ? [] : [.. versions
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .SelectMany(pair => (pair.Value ?? [])
                .Where(value => !string.IsNullOrWhiteSpace(value?.Number))
                .Select(value => $"{pair.Key} {value!.Number}"))
            .Distinct(StringComparer.Ordinal)];

    private static XcodeSdkIdentity[] ReadSDKIdentities(Dictionary<string, XcodeCatalogToolVersion?[]?>? versions)
    {
        if (versions is null)
            return [];
        return [.. versions.SelectMany(pair => (pair.Value ?? [])
                .OfType<XcodeCatalogToolVersion>()
                .Select(value => XcodeSdkIdentity.TryCreate(pair.Key, value.Number, value.Build, out var identity)
                    ? identity : null))
            .OfType<XcodeSdkIdentity>()
            .Distinct()
            .OrderBy(identity => identity.Platform, StringComparer.Ordinal)
            .ThenBy(identity => identity.Version, StringComparer.Ordinal)
            .ThenBy(identity => identity.Build, StringComparer.Ordinal)];
    }
}

internal sealed class XcodeCatalogItem
{
    public XcodeCatalogVersion? Version { get; set; }
    public XcodeCatalogDate? Date { get; set; }
    public string? Requires { get; set; }
    public XcodeCatalogLinks? Links { get; set; }
    public Dictionary<string, XcodeCatalogToolVersion?[]?>? Sdks { get; set; }
    public Dictionary<string, XcodeCatalogToolVersion?[]?>? Compilers { get; set; }
    public XcodeCatalogChecksums? Checksums { get; set; }
}

internal sealed class XcodeCatalogChecksums
{
    public string? Sha1 { get; set; }
}

internal sealed class XcodeCatalogVersion
{
    public string? Number { get; set; }
    public string? Build { get; set; }
    public XcodeCatalogStage? Release { get; set; }
}

internal sealed class XcodeCatalogStage
{
    public int? Beta { get; set; }
    public int? Rc { get; set; }
    public int? GmSeed { get; set; }
    public int? Dp { get; set; }
    public bool? Gm { get; set; }
    public bool? Release { get; set; }
}

internal sealed class XcodeCatalogDate
{
    public int Year { get; set; }
    public int Month { get; set; }
    public int Day { get; set; }
}

internal sealed class XcodeCatalogLinks
{
    public XcodeCatalogLink? Download { get; set; }
    public XcodeCatalogLink? Notes { get; set; }
}

internal sealed class XcodeCatalogLink
{
    public string? Url { get; set; }
    [JsonPropertyName("sizeMB")]
    public int? SizeMB { get; set; }
    public string[]? Architectures { get; set; }
}

internal sealed class XcodeCatalogToolVersion
{
    public string? Number { get; set; }
    public string? Build { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(XcodeCatalogItem[]))]
internal partial class XcodeCatalogJsonContext : JsonSerializerContext;
