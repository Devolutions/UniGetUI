using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using UniGetUI.Core.Logging;

namespace UniGetUI.Extensions.Drivers;

public sealed record DriverScanSnapshot(IReadOnlyList<DriverUpdate> Updates, DateTimeOffset CheckedAt);

public sealed class DriverScanCache(string path)
{
    private static JsonTypeInfo<CachePayload> CacheJsonTypeInfo => DriversJsonContext.Default.CachePayload;

    public string Path { get; } = path;

    public DriverScanSnapshot? Load()
    {
        try
        {
            if (!File.Exists(Path)) return null;
            CachePayload? payload = JsonSerializer.Deserialize(File.ReadAllText(Path), CacheJsonTypeInfo);
            return payload is null ? null : new DriverScanSnapshot(payload.Updates ?? [], payload.CheckedAt);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Logger.Warn($"Could not read the cached driver scan: {ex.Message}");
            return null;
        }
    }

    public void Save(DriverScanSnapshot snapshot)
    {
        try
        {
            if (System.IO.Path.GetDirectoryName(Path) is { Length: > 0 } directory)
                Directory.CreateDirectory(directory);
            CachePayload payload = new() { CheckedAt = snapshot.CheckedAt, Updates = [.. snapshot.Updates] };
            File.WriteAllText(Path, JsonSerializer.Serialize(payload, CacheJsonTypeInfo));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warn($"Could not save the driver scan: {ex.Message}");
        }
    }
}
