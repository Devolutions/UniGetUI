using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace UniGetUI.Core.Tools.Scheduling;

public static class SchedulingJson
{
    public static string SerializeSchedules(Dictionary<string, MaintenanceTaskSchedule> value)
    {
        return JsonSerializer.Serialize(value, GetRequiredTypeInfo<Dictionary<string, MaintenanceTaskSchedule>>());
    }

    public static Dictionary<string, MaintenanceTaskSchedule>? DeserializeSchedules(string json)
    {
        return JsonSerializer.Deserialize(json, GetRequiredTypeInfo<Dictionary<string, MaintenanceTaskSchedule>>());
    }

    public static MaintenanceTaskSchedule? DeserializeSchedule(string json)
    {
        return JsonSerializer.Deserialize(json, GetRequiredTypeInfo<MaintenanceTaskSchedule>());
    }

    private static JsonTypeInfo<T> GetRequiredTypeInfo<T>()
    {
        return SchedulingJsonContext.Default.GetTypeInfo(typeof(T)) as JsonTypeInfo<T>
            ?? throw new InvalidOperationException(
                $"Scheduling JSON metadata for {typeof(T).FullName} was not generated."
            );
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(Dictionary<string, MaintenanceTaskSchedule>))]
[JsonSerializable(typeof(MaintenanceTaskSchedule))]
internal sealed partial class SchedulingJsonContext : JsonSerializerContext;
