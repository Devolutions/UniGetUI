using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using UniGetUI.Core.Data;
using UniGetUI.PackageEngine.Interfaces;

namespace UniGetUI.Avalonia.Models;

public sealed class CatalogDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required CatalogEntry[] Packages { get; init; }
}

public sealed class CatalogEntry
{
    public required string Name { get; init; }
    public required string Id { get; init; }
    public required string Manager { get; init; }
    public required string Source { get; init; }

    public bool Matches(string id, string manager, string source) =>
        string.Equals(Id, id, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Manager, manager, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Source, source, StringComparison.OrdinalIgnoreCase);

    public bool Matches(IPackage package) =>
        Matches(package.Id, package.Manager.Id, package.Source.Name);
}

internal static class SoftwareCatalog
{
    public static string FilePath => Path.Combine(CoreData.UniGetUIGlobalDirectory, "SoftwareCatalog.json");

    internal static ProcessStartInfo CreateEditorStartInfo()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "notepad.exe"
                : OperatingSystem.IsMacOS() ? "open" : "xdg-open",
            UseShellExecute = false,
        };
        if (OperatingSystem.IsMacOS())
            startInfo.ArgumentList.Add("-t");
        startInfo.ArgumentList.Add(FilePath);
        return startInfo;
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
        var catalogs = JsonSerializer.Deserialize(json, CatalogJsonContext.Default.CatalogDefinitionArray)
            ?? throw new JsonException("The software catalog must be a JSON array.");
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
                    || string.IsNullOrWhiteSpace(entry.Id) || string.IsNullOrWhiteSpace(entry.Manager)
                    || string.IsNullOrWhiteSpace(entry.Source))
                    throw new JsonException("Every catalog entry requires a name, id, manager and source.");

                if (!identities.Add((entry.Id.ToUpperInvariant(), entry.Manager.ToUpperInvariant(), entry.Source.ToUpperInvariant())))
                    throw new JsonException($"Duplicate package in catalog {catalog.Id}: {entry.Manager}/{entry.Source}/{entry.Id}.");
            }
        }
        return catalogs;
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CatalogDefinition[]))]
internal partial class CatalogJsonContext : JsonSerializerContext;
