using System.Diagnostics.CodeAnalysis;
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
        JsonTypeInfo<List<T>>? typeInfo = GetGeneratedTypeInfo<List<T>>();
        return typeInfo is not null
            ? JsonSerializer.Serialize(value, typeInfo)
            : SerializeListWithReflection(value);
    }

    public static List<T>? DeserializeList<T>(string json)
    {
        JsonTypeInfo<List<T>>? typeInfo = GetGeneratedTypeInfo<List<T>>();
        return typeInfo is not null
            ? JsonSerializer.Deserialize(json, typeInfo)
            : DeserializeListWithReflection<T>(json);
    }

    public static string SerializeDictionary<KeyT, ValueT>(Dictionary<KeyT, ValueT> value)
        where KeyT : notnull
    {
        JsonTypeInfo<Dictionary<KeyT, ValueT>>? typeInfo =
            GetGeneratedTypeInfo<Dictionary<KeyT, ValueT>>();
        return typeInfo is not null
            ? JsonSerializer.Serialize(value, typeInfo)
            : SerializeDictionaryWithReflection(value);
    }

    public static Dictionary<KeyT, ValueT>? DeserializeDictionary<KeyT, ValueT>(string json)
        where KeyT : notnull
    {
        JsonTypeInfo<Dictionary<KeyT, ValueT>>? typeInfo =
            GetGeneratedTypeInfo<Dictionary<KeyT, ValueT>>();
        return typeInfo is not null
            ? JsonSerializer.Deserialize(json, typeInfo)
            : DeserializeDictionaryWithReflection<KeyT, ValueT>(json);
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

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "Runtime settings use generated metadata for known app types; this fallback preserves generic settings tests and extension scenarios outside trimmed app paths.")]
    private static string SerializeListWithReflection<T>(List<T> value)
    {
        return JsonSerializer.Serialize(value, Settings.SerializationOptions);
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "Runtime settings use generated metadata for known app types; this fallback preserves generic settings tests and extension scenarios outside trimmed app paths.")]
    private static List<T>? DeserializeListWithReflection<T>(string json)
    {
        return JsonSerializer.Deserialize<List<T>>(json, Settings.SerializationOptions);
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "Runtime settings use generated metadata for known app types; this fallback preserves generic settings tests and extension scenarios outside trimmed app paths.")]
    private static string SerializeDictionaryWithReflection<KeyT, ValueT>(
        Dictionary<KeyT, ValueT> value
    )
        where KeyT : notnull
    {
        return JsonSerializer.Serialize(value, Settings.SerializationOptions);
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "Runtime settings use generated metadata for known app types; this fallback preserves generic settings tests and extension scenarios outside trimmed app paths.")]
    private static Dictionary<KeyT, ValueT>? DeserializeDictionaryWithReflection<KeyT, ValueT>(
        string json
    )
        where KeyT : notnull
    {
        return JsonSerializer.Deserialize<Dictionary<KeyT, ValueT>>(
            json,
            Settings.SerializationOptions
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
[JsonSerializable(typeof(List<object>))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
