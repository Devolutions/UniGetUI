using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace UniGetUI.PackageEngine.Managers.CargoManager;

internal static class CargoJson
{
    public static T? Deserialize<T>(string json)
    {
        return JsonSerializer.Deserialize(json, GetTypeInfo<T>());
    }

    private static JsonTypeInfo<T> GetTypeInfo<T>()
    {
        return (JsonTypeInfo<T>?)CargoJsonContext.Default.GetTypeInfo(typeof(T))
            ?? throw new InvalidOperationException(
                $"Cargo JSON metadata for {typeof(T).FullName} was not generated."
            );
    }
}

[JsonSourceGenerationOptions(AllowTrailingCommas = true, WriteIndented = true)]
[JsonSerializable(typeof(CargoManifest))]
[JsonSerializable(typeof(CargoManifestVersionWrapper))]
internal sealed partial class CargoJsonContext : JsonSerializerContext;
