using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace UniGetUI.Core.SettingsEngine;

internal static class SettingsJson
{
    public static string SerializeStringDictionary(Dictionary<string, string> value)
    {
        return JsonSerializer.Serialize(value, GetRequiredTypeInfo<Dictionary<string, string>>());
    }

    public static Dictionary<string, string>? DeserializeStringDictionary(string json)
    {
        return JsonSerializer.Deserialize(json, GetRequiredTypeInfo<Dictionary<string, string>>());
    }

    public static string SerializeList<T>(List<T> value)
    {
        return JsonSerializer.Serialize(value, GetRequiredTypeInfo<List<T>>());
    }

    public static List<T>? DeserializeList<T>(string json)
    {
        return JsonSerializer.Deserialize(json, GetRequiredTypeInfo<List<T>>());
    }

    public static string SerializeDictionary<KeyT, ValueT>(Dictionary<KeyT, ValueT> value)
        where KeyT : notnull
    {
        return JsonSerializer.Serialize(value, GetRequiredTypeInfo<Dictionary<KeyT, ValueT>>());
    }

    public static Dictionary<KeyT, ValueT>? DeserializeDictionary<KeyT, ValueT>(string json)
        where KeyT : notnull
    {
        return JsonSerializer.Deserialize(
            json,
            GetRequiredTypeInfo<Dictionary<KeyT, ValueT>>()
        );
    }

    private static JsonTypeInfo<T>? GetGeneratedTypeInfo<T>()
    {
        return SettingsJsonContext.Default.GetTypeInfo(typeof(T)) as JsonTypeInfo<T>;
    }

    private static JsonTypeInfo<T> GetRequiredTypeInfo<T>()
    {
        return GetGeneratedTypeInfo<T>()
            ?? throw new InvalidOperationException(
                $"Settings JSON metadata for {typeof(T).FullName} was not generated."
            );
    }

}

[JsonSourceGenerationOptions(AllowTrailingCommas = true, WriteIndented = true)]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(Dictionary<string, bool>))]
[JsonSerializable(typeof(Dictionary<string, bool?>))]
[JsonSerializable(typeof(Dictionary<string, int>))]
[JsonSerializable(typeof(Dictionary<string, int?>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(List<bool>))]
[JsonSerializable(typeof(List<int>))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
