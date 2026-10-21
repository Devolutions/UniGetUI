using System.Collections.Concurrent;
using System.Text;
using UniGetUI.Core.Data;
using UniGetUI.Core.Tools;

namespace UniGetUI.PackageEngine.Managers.CargoManager;

internal sealed record CargoManifest
{
    public CargoManifestCategory[]? categories { get; init; }
    public required CargoManifestCrate crate { get; init; }
    public required CargoManifestVersion[] versions { get; init; }
}

internal sealed record CargoManifestCategory
{
    public required string category { get; init; }
    public required string description { get; init; }
    public required string id { get; init; }
}

internal sealed record CargoManifestCrate
{
    public string[]? categories { get; init; }
    public string? description { get; init; }
    public string? documentation { get; init; }
    public double? downloads { get; init; }
    public string? homepage { get; init; }
    public string[]? keywords { get; init; }
    public required string max_stable_version { get; init; }
    public required string max_version { get; init; }
    public required string name { get; init; }
    public required string newest_version { get; init; }
    public string? repository { get; init; }
    public string? updated_at { get; init; }
}

internal sealed record CargoManifestVersion
{
    public string[]? bin_names { get; init; }
    public required string checksum { get; init; }
    public long? crate_size { get; init; }
    public string? created_at { get; init; }
    public required string dl_path { get; init; }
    public string? license { get; init; }
    public required string num { get; init; }
    public CargoManifestPublisher? published_by { get; init; }
    public string? updated_at { get; init; }
    public bool yanked { get; init; }
}

internal sealed record CargoManifestVersionWrapper
{
    public required CargoManifestVersion version { get; init; }
}

internal sealed class CargoManifestPublisher
{
    public string? avatar { get; init; }
    public required string name { get; init; }
    public string? url { get; init; }
}

internal sealed class CratesIOClient
{
    public const string ApiUrl = "https://crates.io/api/v1";
    internal static string? TEST_ApiUrlOverride { private get; set; }
    private static readonly ConcurrentDictionary<string, (DateTime Time, string Json)> Cache = new();

    internal static void InvalidateCache() => Cache.Clear();

    internal static async Task<CargoManifest> GetManifestAsync(string packageId,
        CancellationToken cancellationToken)
    {
        var manifest = await FetchAsync<CargoManifest>(
            new Uri($"{GetApiUrl()}/crates/{Uri.EscapeDataString(packageId)}"), cancellationToken);
        if (manifest.crate is null || manifest.versions is null)
            throw new NullResponseException($"Null response for Cargo package {packageId}");
        return manifest;
    }

    internal static CargoManifestVersion? SelectUpdate(CargoManifest manifest, string installed)
    {
        if (!SemanticVersion.TryParse(installed, out var current))
            throw new InvalidDataException($"Invalid installed Cargo version: {installed}");
        return manifest.versions
            .Where(version => !version.yanked
                && SemanticVersion.TryParse(version.num, out var parsed)
                && !parsed.IsPreRelease && parsed.CompareTo(current) > 0)
            .OrderByDescending(version =>
            {
                SemanticVersion.TryParse(version.num, out var parsed);
                return parsed;
            })
            .FirstOrDefault();
    }

    public static Tuple<Uri, CargoManifest> GetManifest(string packageId)
    {
        var manifestUrl = new Uri($"{GetApiUrl()}/crates/{packageId}");
        var manifest = Fetch<CargoManifest>(manifestUrl);
        if (manifest.crate is null)
        {
            throw new NullResponseException($"Null response for package {packageId}");
        }
        return Tuple.Create(manifestUrl, manifest);
    }

    public static CargoManifestVersion GetManifestVersion(string packageId, string version)
    {
        var manifestUrl = new Uri($"{GetApiUrl()}/crates/{packageId}/{version}");
        var manifest = Fetch<CargoManifestVersionWrapper>(manifestUrl);
        if (manifest.version is null)
        {
            throw new NullResponseException($"Null response for package {packageId}");
        }
        return manifest.version;
    }

    private static string GetApiUrl() => TEST_ApiUrlOverride ?? ApiUrl;

    internal static T Fetch<T>(Uri url)
        => FetchAsync<T>(url, CancellationToken.None).GetAwaiter().GetResult();

    private static async Task<T> FetchAsync<T>(Uri url, CancellationToken cancellationToken)
    {
        string key = url.AbsoluteUri;
        if (Cache.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.Time < TimeSpan.FromMinutes(5))
            return CargoJson.Deserialize<T>(cached.Json)
                ?? throw new NullResponseException($"Null cached response for {url}");
        await CargoHttp.PaceApiAsync(cancellationToken);
        using HttpClient client = new(CoreTools.GenericHttpClientParameters);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(CoreData.UserAgentString);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using HttpResponseMessage response = await client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using MemoryStream output = new();
        byte[] buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (output.Length + read > CargoHttp.ManifestLimit)
                throw new InvalidDataException($"Cargo API response exceeds the allowed size: {url}");
            output.Write(buffer, 0, read);
        }
        string manifestStr = Encoding.UTF8.GetString(output.ToArray());

        var manifest =
            CargoJson.Deserialize<T>(manifestStr)
            ?? throw new NullResponseException($"Null response for request to {url}");
        if (Cache.Count >= 512)
            Cache.Clear();
        Cache[key] = (DateTime.UtcNow, manifestStr);
        return manifest;
    }
}

public class NullResponseException(string message) : Exception(message);
