using System.Text.Json.Serialization;

namespace UniGetUI.Interface.Telemetry;

public sealed class UniGetUIActivityEvent : TelemetryEventBase
{
    [JsonPropertyName("enabledManagers")]
    public string[] EnabledManagers { get; set; } = [];

    [JsonPropertyName("foundManagers")]
    public string[] FoundManagers { get; set; } = [];

    [JsonPropertyName("activeSettings")]
    public int ActiveSettings { get; set; }
}
