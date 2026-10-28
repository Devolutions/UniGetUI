using System.Text.Json;
using System.Text.Json.Serialization;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;

namespace UniGetUI.PackageEngine.Managers.InstallerFeeds;

// A feed is identified by its catalog's id, which is unique and survives renames; its name is for display.
public readonly record struct FeedDeclaration(string Id, string Name, Uri Location, bool AllowUnsignedPackages = false);

/// <summary>
/// Reads the feeds that the machine-wide software catalog declares through each catalog's
/// <c>pkgFeed</c>, so an administrator configures a feed once, in the file they already deploy.
/// </summary>
public static class CatalogFeeds
{
    public static string? TestCatalogPathOverride { get; set; }

    public static string CatalogPath =>
        TestCatalogPathOverride ?? Path.Combine(CoreData.UniGetUIGlobalDirectory, "SoftwareCatalog.json");

    public static IReadOnlyList<FeedDeclaration> Load() => Load(CatalogPath);

    public static IReadOnlyList<FeedDeclaration> Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return [];
            return Parse(File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Logger.Warn($"Could not read the package feeds of the software catalog at {path}: {ex.Message}");
            return [];
        }
    }

    public static IReadOnlyList<FeedDeclaration> Parse(byte[] json)
    {
        var document = FeedJson.Deserialize(json, CatalogFeedsJsonContext.Default.CatalogFeedsDocument);
        var feeds = new List<FeedDeclaration>();
        foreach (var catalog in document?.Catalogs ?? [])
        {
            if (catalog?.PkgFeed is null || string.IsNullOrWhiteSpace(catalog.Id) || string.IsNullOrWhiteSpace(catalog.Name))
                continue;
            if (!FeedLocation.TryParse(catalog.PkgFeed, out Uri location))
            {
                Logger.Warn($"Ignoring the package feed of catalog {catalog.Name}: \"{catalog.PkgFeed}\" is not an https URL or an absolute folder path.");
                continue;
            }
            string id = catalog.Id.Trim();
            if (feeds.Any(f => string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase)))
                Logger.Warn($"Ignoring the package feed of a second catalog with the id {id}.");
            else
                feeds.Add(new FeedDeclaration(id, catalog.Name.Trim(), location, catalog.AllowUnsignedPackages is true));
        }
        return feeds;
    }
}

internal sealed class CatalogFeedsDocument
{
    [JsonPropertyName("catalogs")]
    public CatalogFeedsDefinition?[]? Catalogs { get; init; }
}

internal sealed class CatalogFeedsDefinition
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("pkgFeed")]
    public string? PkgFeed { get; init; }

    [JsonPropertyName("allowUnsignedPackages")]
    public bool? AllowUnsignedPackages { get; init; }
}

[JsonSerializable(typeof(CatalogFeedsDocument))]
internal partial class CatalogFeedsJsonContext : JsonSerializerContext;
