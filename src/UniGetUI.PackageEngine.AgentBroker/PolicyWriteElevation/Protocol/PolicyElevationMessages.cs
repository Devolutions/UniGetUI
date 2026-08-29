using System.Text.Json;
using System.Text.Json.Serialization;

namespace UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation;

/// <summary>
/// The single request the host sends over the authenticated pipe. Exactly one of these is
/// written per helper launch.
/// </summary>
public sealed class PolicyElevationRequestMessage
{
    [JsonPropertyName("protocolVersion")]
    public string ProtocolVersion { get; set; } = PolicyElevationProtocol.Version;

    [JsonPropertyName("requestId")]
    public string RequestId { get; set; } = string.Empty;

    [JsonPropertyName("operation")]
    public PolicyElevationOperation Operation { get; set; }

    [JsonPropertyName("conflictHandling")]
    public PolicyElevationConflictHandling ConflictHandling { get; set; }

    [JsonPropertyName("expectedStoreToken")]
    public string ExpectedStoreToken { get; set; } = string.Empty;

    [JsonPropertyName("validationReceipt")]
    public string ValidationReceipt { get; set; } = string.Empty;

    [JsonPropertyName("warningsAcknowledged")]
    public bool WarningsAcknowledged { get; set; }

    [JsonPropertyName("draft")]
    public JsonElement Draft { get; set; }
}

/// <summary>
/// Result classification produced by the elevated helper. Mapped onto
/// <see cref="PolicyElevationOutcome"/> by the host.
/// </summary>
[JsonConverter(typeof(PolicyElevationResponseStatusJsonConverter))]
public enum PolicyElevationResponseStatus
{
    /// <summary>The broker accepted and persisted the replacement.</summary>
    Replaced = 0,

    /// <summary>The broker rejected the request (validation, conflict, permissions, …).</summary>
    BrokerRejected = 1,

    /// <summary>The broker could not be reached or timed out.</summary>
    BrokerUnavailable = 2,

    /// <summary>The broker answered with something the helper could not interpret.</summary>
    BrokerInvalidResponse = 3,

    /// <summary>The helper refused the request before contacting the broker.</summary>
    HelperRejected = 4,
}

/// <summary>
/// The single response the helper writes back before closing the connection.
/// <c>payload</c> relays the broker answer verbatim and is bounded by the shared
/// policy-management body budget.
/// </summary>
public sealed class PolicyElevationResponseMessage
{
    [JsonPropertyName("protocolVersion")]
    public string ProtocolVersion { get; set; } = PolicyElevationProtocol.Version;

    [JsonPropertyName("requestId")]
    public string RequestId { get; set; } = string.Empty;

    [JsonPropertyName("outcome")]
    public PolicyElevationResponseStatus Outcome { get; set; }

    [JsonPropertyName("win32ErrorCode")]
    public int? Win32ErrorCode { get; set; }

    [JsonPropertyName("brokerStatusCode")]
    public int? BrokerStatusCode { get; set; }

    [JsonPropertyName("brokerErrorCode")]
    public string? BrokerErrorCode { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("payload")]
    public JsonElement? Payload { get; set; }
}
[JsonConverter(typeof(PolicyElevationOperationJsonConverter))]
public enum PolicyElevationOperation
{
    Update = 0,
    ReplaceIdentity = 1,
    Create = 2,
    Repair = 3,
}

[JsonConverter(typeof(PolicyElevationConflictHandlingJsonConverter))]
public enum PolicyElevationConflictHandling
{
    Reject = 0,
    ConfirmOverwrite = 1,
}
