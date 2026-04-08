using System.Text.Json.Serialization;

namespace UniGetUI.Interface.Telemetry;

/// <summary>
/// Common fields shared by all UniGetUI telemetry events.
/// Mirrors the field names expected by the Devolutions OpenSearch schema.
/// </summary>
public abstract class TelemetryEventBase
{
    protected TelemetryEventBase()
    {
        EventID = Guid.NewGuid().ToString();
        EventDate = DateTime.UtcNow;
    }

    [JsonPropertyName("eventID")]
    public string EventID { get; set; }

    [JsonPropertyName("eventDate")]
    public DateTime EventDate { get; set; }

    [JsonPropertyName("installID")]
    public required string InstallID { get; set; }

    [JsonPropertyName("locale")]
    public string? Locale { get; set; }

    [JsonPropertyName("application")]
    public required TelemetryApplicationInfo Application { get; set; }

    [JsonPropertyName("platform")]
    public TelemetryPlatformInfo? Platform { get; set; }
}

public sealed class TelemetryApplicationInfo
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("version")]
    public required string Version { get; set; }

    [JsonPropertyName("dataSource")]
    public required string DataSource { get; set; }

    [JsonPropertyName("pricing")]
    public required string Pricing { get; set; }

    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("architectureType")]
    public string? ArchitectureType { get; set; }
}

public sealed class TelemetryPlatformInfo
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("architecture")]
    public string? Architecture { get; set; }
}
