using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace UniGetUI.PackageEngine.Managers.Generic.NuGet.Internal
{
    internal sealed class V3ServiceIndex
    {
        [JsonPropertyName("resources")]
        public List<V3Resource>? Resources { get; set; }
    }

    internal sealed class V3Resource
    {
        [JsonPropertyName("@id")]
        public string? Id { get; set; }

        [JsonPropertyName("@type")]
        public JsonElement Type { get; set; }
    }

    internal sealed class V3SearchResponse
    {
        [JsonPropertyName("totalHits")]
        public int TotalHits { get; set; }

        [JsonPropertyName("data")]
        public List<V3SearchResult>? Data { get; set; }
    }

    internal sealed class V3SearchResult
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("summary")]
        public string? Summary { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("iconUrl")]
        public string? IconUrl { get; set; }

        [JsonPropertyName("licenseUrl")]
        public string? LicenseUrl { get; set; }

        [JsonPropertyName("projectUrl")]
        public string? ProjectUrl { get; set; }

        [JsonPropertyName("authors")]
        public JsonElement Authors { get; set; }

        [JsonPropertyName("tags")]
        public JsonElement Tags { get; set; }

        [JsonIgnore]
        public bool IsExactIdFallback { get; set; }
    }

    internal sealed class V3FlatContainerIndex
    {
        [JsonPropertyName("versions")]
        public List<string>? Versions { get; set; }
    }

    internal sealed class V3RegistrationLeaf
    {
        [JsonPropertyName("catalogEntry")]
        public JsonElement CatalogEntry { get; set; }

        [JsonPropertyName("packageContent")]
        public string? PackageContent { get; set; }

        [JsonPropertyName("published")]
        public string? Published { get; set; }

        [JsonPropertyName("listed")]
        public bool? Listed { get; set; }
    }

    internal sealed class V3CatalogEntry
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("summary")]
        public string? Summary { get; set; }

        [JsonPropertyName("iconUrl")]
        public string? IconUrl { get; set; }

        [JsonPropertyName("iconFile")]
        public string? IconFile { get; set; }

        [JsonPropertyName("licenseUrl")]
        public string? LicenseUrl { get; set; }

        [JsonPropertyName("licenseExpression")]
        public string? LicenseExpression { get; set; }

        [JsonPropertyName("projectUrl")]
        public string? ProjectUrl { get; set; }

        [JsonPropertyName("releaseNotes")]
        public string? ReleaseNotes { get; set; }

        [JsonPropertyName("published")]
        public string? Published { get; set; }

        [JsonPropertyName("listed")]
        public bool? Listed { get; set; }

        [JsonPropertyName("packageHash")]
        public string? PackageHash { get; set; }

        [JsonPropertyName("packageHashAlgorithm")]
        public string? PackageHashAlgorithm { get; set; }

        [JsonPropertyName("packageSize")]
        public long PackageSize { get; set; }

        [JsonPropertyName("packageContent")]
        public string? PackageContent { get; set; }

        [JsonPropertyName("authors")]
        public JsonElement Authors { get; set; }

        [JsonPropertyName("tags")]
        public JsonElement Tags { get; set; }

        [JsonPropertyName("dependencyGroups")]
        public List<V3DependencyGroup>? DependencyGroups { get; set; }

        [JsonPropertyName("packageTypes")]
        public List<V3PackageType>? PackageTypes { get; set; }

        [JsonIgnore]
        public string? AuthorsOverride { get; set; }

        [JsonIgnore]
        public IReadOnlyList<string>? TagsOverride { get; set; }

        public string? GetAuthors() => AuthorsOverride ?? NuGetV3Json.AsJoinedString(Authors);

        public IReadOnlyList<string> GetTags() =>
            TagsOverride ?? NuGetV3Json.AsStringList(Tags);

        public bool HasPackageType(string name)
        {
            if (PackageTypes is not { Count: > 0 })
                return false;

            foreach (V3PackageType packageType in PackageTypes)
            {
                if (string.Equals(packageType.Name, name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
    }

    internal sealed class V3PackageType
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    internal sealed class V3DependencyGroup
    {
        [JsonPropertyName("targetFramework")]
        public string? TargetFramework { get; set; }

        [JsonPropertyName("dependencies")]
        public List<V3Dependency>? Dependencies { get; set; }
    }

    internal sealed class V3Dependency
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("range")]
        public string? Range { get; set; }
    }

    [JsonSourceGenerationOptions(AllowTrailingCommas = true)]
    [JsonSerializable(typeof(V3ServiceIndex))]
    [JsonSerializable(typeof(V3SearchResponse))]
    [JsonSerializable(typeof(V3FlatContainerIndex))]
    [JsonSerializable(typeof(V3RegistrationLeaf))]
    [JsonSerializable(typeof(V3CatalogEntry))]
    internal sealed partial class NuGetV3JsonContext : JsonSerializerContext;

    internal static class NuGetV3Json
    {
        public static V3ServiceIndex? DeserializeServiceIndex(string json) =>
            Deserialize<V3ServiceIndex>(json);

        public static V3SearchResponse? DeserializeSearchResponse(string json) =>
            Deserialize<V3SearchResponse>(json);

        public static V3FlatContainerIndex? DeserializeFlatContainerIndex(string json) =>
            Deserialize<V3FlatContainerIndex>(json);

        public static V3RegistrationLeaf? DeserializeRegistrationLeaf(string json) =>
            Deserialize<V3RegistrationLeaf>(json);

        public static V3CatalogEntry? DeserializeCatalogEntry(string json) =>
            Deserialize<V3CatalogEntry>(json);

        public static V3CatalogEntry? DeserializeCatalogEntry(JsonElement element) =>
            element.ValueKind is JsonValueKind.Object
                ? element.Deserialize(GetTypeInfo<V3CatalogEntry>())
                : null;

        public static IReadOnlyList<string> AsStringList(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    string? single = element.GetString();
                    if (string.IsNullOrWhiteSpace(single))
                        return [];
                    return single.Contains(',')
                        ? single
                            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .ToArray()
                        : [single.Trim()];

                case JsonValueKind.Array:
                    List<string> values = [];
                    foreach (JsonElement item in element.EnumerateArray())
                    {
                        if (item.ValueKind is not JsonValueKind.String)
                            continue;

                        string? value = item.GetString();
                        if (!string.IsNullOrWhiteSpace(value))
                            values.Add(value.Trim());
                    }
                    return values;

                default:
                    return [];
            }
        }

        public static string? AsJoinedString(JsonElement element)
        {
            IReadOnlyList<string> values = AsStringList(element);
            return values.Count is 0 ? null : string.Join(", ", values);
        }

        private static T? Deserialize<T>(string json)
        {
            return JsonSerializer.Deserialize(json, GetTypeInfo<T>());
        }

        private static JsonTypeInfo<T> GetTypeInfo<T>()
        {
            return (JsonTypeInfo<T>?)NuGetV3JsonContext.Default.GetTypeInfo(typeof(T))
                ?? throw new InvalidOperationException(
                    $"NuGet V3 JSON metadata for {typeof(T).FullName} was not generated."
                );
        }
    }
}
