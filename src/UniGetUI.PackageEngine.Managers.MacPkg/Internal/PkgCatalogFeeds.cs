using System.Text.Json;
using System.Text.Json.Serialization;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;

namespace UniGetUI.PackageEngine.Managers.MacPkgManager.Internal;

internal readonly record struct PkgFeedDeclaration(string Name, Uri Location);

/// <summary>
/// Reads the feeds that the machine-wide software catalog declares through each catalog's
/// <c>pkgFeed</c>, so an administrator configures a feed once, in the file they already deploy.
/// </summary>
internal static class PkgCatalogFeeds
{
    internal static string? TestCatalogPathOverride { get; set; }

    public static string CatalogPath =>
        TestCatalogPathOverride ?? Path.Combine(CoreData.UniGetUIGlobalDirectory, "SoftwareCatalog.json");

    public static IReadOnlyList<PkgFeedDeclaration> Load() => Load(CatalogPath);

    internal static IReadOnlyList<PkgFeedDeclaration> Load(string path)
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

    // Catalogs that share a name share a source, so the first one wins.
    internal static IReadOnlyList<PkgFeedDeclaration> Parse(byte[] json)
    {
        var document = JsonSerializer.Deserialize(json, PkgCatalogJsonContext.Default.PkgCatalogDocument);
        var feeds = new List<PkgFeedDeclaration>();
        foreach (var catalog in document?.Catalogs ?? [])
        {
            if (catalog?.PkgFeed is null || string.IsNullOrWhiteSpace(catalog.Name))
                continue;
            if (!PkgFeed.TryParseLocation(catalog.PkgFeed, out Uri location))
            {
                Logger.Warn($"Ignoring the package feed of catalog {catalog.Name}: \"{catalog.PkgFeed}\" is not an https URL or an absolute folder path.");
                continue;
            }
            string name = catalog.Name.Trim();
            if (!feeds.Any(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)))
                feeds.Add(new PkgFeedDeclaration(name, location));
        }
        return feeds;
    }
}

internal sealed class PkgCatalogDocument
{
    [JsonPropertyName("catalogs")]
    public PkgCatalogDefinition?[]? Catalogs { get; init; }
}

internal sealed class PkgCatalogDefinition
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("pkgFeed")]
    public string? PkgFeed { get; init; }
}

[JsonSerializable(typeof(PkgCatalogDocument))]
internal partial class PkgCatalogJsonContext : JsonSerializerContext;
