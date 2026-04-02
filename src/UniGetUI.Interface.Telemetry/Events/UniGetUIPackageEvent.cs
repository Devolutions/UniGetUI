using System.Text.Json.Serialization;

namespace UniGetUI.Interface.Telemetry;

public sealed class UniGetUIPackageEvent : TelemetryEventBase
{
    [JsonPropertyName("operation")]
    public required string Operation { get; set; }

    [JsonPropertyName("packageId")]
    public required string PackageId { get; set; }

    [JsonPropertyName("managerName")]
    public required string ManagerName { get; set; }

    [JsonPropertyName("sourceName")]
    public required string SourceName { get; set; }

    [JsonPropertyName("operationResult")]
    public string? OperationResult { get; set; }

    [JsonPropertyName("eventSource")]
    public string? EventSource { get; set; }
}
