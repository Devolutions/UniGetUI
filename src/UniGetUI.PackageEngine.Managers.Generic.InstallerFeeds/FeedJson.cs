using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace UniGetUI.PackageEngine.Managers.InstallerFeeds;

/// <summary>Reads and writes JSON through source-generated metadata only, as NativeAOT requires.</summary>
public static class FeedJson
{
    public static string Serialize<T>(T value, JsonTypeInfo<T> typeInfo) => JsonSerializer.Serialize(value, typeInfo);

    public static byte[] SerializeToUtf8Bytes<T>(T value, JsonTypeInfo<T> typeInfo) =>
        JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);

    public static T? Deserialize<T>(string json, JsonTypeInfo<T> typeInfo) => JsonSerializer.Deserialize(json, typeInfo);

    public static T? Deserialize<T>(byte[] json, JsonTypeInfo<T> typeInfo) => JsonSerializer.Deserialize(json, typeInfo);
}
