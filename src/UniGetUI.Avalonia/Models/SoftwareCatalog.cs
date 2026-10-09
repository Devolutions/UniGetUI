using System.Text.Json;
using System.Text.Json.Serialization;
using UniGetUI.Core.Data;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Classes.Serializable;

namespace UniGetUI.Avalonia.Models;

public sealed class CatalogDocument
{
    [JsonPropertyName("version")]
    public required int Version { get; init; }

    [JsonPropertyName("catalogs")]
    public required CatalogDefinition[] Catalogs { get; init; }
}

public sealed class CatalogDefinition
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }
    [JsonPropertyName("name")]
    public required string Name { get; init; }
    [JsonPropertyName("packages")]
    public required CatalogEntry[] Packages { get; init; }
}

public sealed class CatalogEntry
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Version { get; set; } = "";
    public required string Source { get; init; }
    public required string ManagerName { get; init; }

    public static CatalogEntry FromPackage(IPackage package) => new()
    {
        Id = package.Id, Name = package.Name, Version = "",
        Source = package.Source.Name, ManagerName = package.Manager.DisplayName,
    };

    public SerializablePackage AsSerializable() => new()
    {
        Id = Id, Name = Name, Version = Version, Source = Source, ManagerName = ManagerName,
    };

    public bool Matches(string id, string manager, string source) =>
        string.Equals(Id, id, StringComparison.OrdinalIgnoreCase)
        && string.Equals(ManagerName, manager, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Source, source, StringComparison.OrdinalIgnoreCase);

    public bool MatchesManager(IPackageManager manager) =>
        string.Equals(ManagerName, manager.Id, StringComparison.OrdinalIgnoreCase)
        || string.Equals(ManagerName, manager.Name, StringComparison.OrdinalIgnoreCase)
        || string.Equals(ManagerName, manager.DisplayName, StringComparison.OrdinalIgnoreCase);

    public bool Matches(IPackage package) =>
        string.Equals(Id, package.Id, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Source, package.Source.Name, StringComparison.OrdinalIgnoreCase)
        && MatchesManager(package.Manager);

    public bool MatchesInstalled(IPackage package) =>
        string.Equals(Id, package.Id, StringComparison.OrdinalIgnoreCase)
        && MatchesManager(package.Manager)
        // Chocolatey's installed inventory assigns DefaultSource and does not retain feed provenance.
        && (string.Equals(package.Manager.Id, "chocolatey", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Source, package.Source.Name, StringComparison.OrdinalIgnoreCase));
}

internal static class SoftwareCatalog
{
    public static string FilePath => Path.Combine(CoreData.UniGetUIGlobalDirectory, "SoftwareCatalog.json");

    internal static async Task SaveAsync(string filePath, CatalogDefinition[] catalogs)
    {
        var document = new CatalogDocument { Version = 1, Catalogs = catalogs };
        string json = JsonSerializer.Serialize(document, CatalogJsonContext.Default.CatalogDocument);
        Parse(json);
        string destination = Path.GetFullPath(filePath);
        string temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".catalog-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, json + Environment.NewLine);
            if (File.Exists(destination))
                File.Replace(temporary, destination, null);
            else
                File.Move(temporary, destination);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    public static Task<CatalogDefinition[]> LoadAsync() => LoadAsync(FilePath);

    internal static async Task<CatalogDefinition[]> LoadAsync(string filePath)
    {
        await using var stream = File.OpenRead(filePath);
        using var reader = new StreamReader(stream);
        return Parse(await reader.ReadToEndAsync());
    }

    internal static CatalogDefinition[] Parse(string json)
    {
        var document = JsonSerializer.Deserialize(json, CatalogJsonContext.Default.CatalogDocument)
            ?? throw new JsonException("The software catalog must be a versioned JSON object.");
        if (document.Version != 1)
            throw new JsonException($"Unsupported software catalog version: {document.Version}.");
        var catalogs = document.Catalogs
            ?? throw new JsonException("The software catalog requires a catalogs array.");
        var catalogIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var catalog in catalogs)
        {
            if (catalog is null || string.IsNullOrWhiteSpace(catalog.Id)
                || string.IsNullOrWhiteSpace(catalog.Name) || catalog.Packages is null)
                throw new JsonException("Every catalog requires an id, name and packages array.");
            if (!catalogIds.Add(catalog.Id))
                throw new JsonException($"Duplicate catalog identifier: {catalog.Id}.");

            var identities = new HashSet<(string, string, string)>();
            foreach (var entry in catalog.Packages)
            {
                if (entry is null || string.IsNullOrWhiteSpace(entry.Name)
                    || string.IsNullOrWhiteSpace(entry.Id) || string.IsNullOrWhiteSpace(entry.ManagerName)
                    || string.IsNullOrWhiteSpace(entry.Source) || entry.Version is null)
                    throw new JsonException("Every catalog entry requires Name, Id, ManagerName and Source, with a non-null Version.");

                if (!identities.Add((entry.Id.ToUpperInvariant(), entry.ManagerName.ToUpperInvariant(), entry.Source.ToUpperInvariant())))
                    throw new JsonException($"Duplicate package in catalog {catalog.Id}: {entry.ManagerName}/{entry.Source}/{entry.Id}.");
            }
        }
        return catalogs;
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(CatalogDocument))]
internal partial class CatalogJsonContext : JsonSerializerContext;
