using System.Text.Json;
using UniGetUI.Core.Logging;

namespace UniGetUI.Extensions.Drivers;

public sealed record DriverScanSnapshot(IReadOnlyList<DriverUpdate> Updates, DateTimeOffset CheckedAt);

public sealed class DriverScanCache(string path)
{
    public string Path { get; } = path;

    public DriverScanSnapshot? Load()
    {
        try
        {
            if (!File.Exists(Path)) return null;
            CachePayload? payload = JsonSerializer.Deserialize(File.ReadAllText(Path), DriversJsonContext.Default.CachePayload);
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
            File.WriteAllText(Path, JsonSerializer.Serialize(
                new CachePayload { CheckedAt = snapshot.CheckedAt, Updates = [.. snapshot.Updates] },
                DriversJsonContext.Default.CachePayload));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warn($"Could not save the driver scan: {ex.Message}");
        }
    }
}
