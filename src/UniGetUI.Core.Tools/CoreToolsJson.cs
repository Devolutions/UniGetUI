using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace UniGetUI.Core.Data;

internal static class CoreToolsJson
{
    public static Dictionary<string, string>? DeserializeStringDictionary(string json)
    {
        return JsonSerializer.Deserialize(json, GetTypeInfo<Dictionary<string, string>>());
    }

    private static JsonTypeInfo<T> GetTypeInfo<T>()
    {
        return (JsonTypeInfo<T>?)CoreToolsJsonContext.Default.GetTypeInfo(typeof(T))
            ?? throw new InvalidOperationException(
                $"Core tools JSON metadata for {typeof(T).FullName} was not generated."
            );
    }
}

[JsonSourceGenerationOptions(AllowTrailingCommas = true, WriteIndented = true)]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class CoreToolsJsonContext : JsonSerializerContext;
