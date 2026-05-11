using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace UniGetUI.Core.IconEngine;

internal static class IconStoreJson
{
    public static IconScreenshotDatabase_v2 DeserializeIconDatabase(string json)
    {
        return JsonSerializer.Deserialize(json, GetTypeInfo<IconScreenshotDatabase_v2>());
    }

    private static JsonTypeInfo<T> GetTypeInfo<T>()
    {
        return (JsonTypeInfo<T>?)IconStoreJsonContext.Default.GetTypeInfo(typeof(T))
            ?? throw new InvalidOperationException(
                $"Icon store JSON metadata for {typeof(T).FullName} was not generated."
            );
    }
}

[JsonSourceGenerationOptions(AllowTrailingCommas = true, WriteIndented = true)]
[JsonSerializable(typeof(IconScreenshotDatabase_v2))]
internal sealed partial class IconStoreJsonContext : JsonSerializerContext;
