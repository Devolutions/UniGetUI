using System.Text.Json.Serialization;

namespace UniGetUI.Interface.Telemetry;

public sealed class UniGetUIBundleEvent : TelemetryEventBase
{
    [JsonPropertyName("operation")]
    public required string Operation { get; set; }

    [JsonPropertyName("bundleType")]
    public required string BundleType { get; set; }
}
