using System.Text.Json;
using System.Text.Json.Serialization;

namespace UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation;

/// <summary>
/// The single request the host sends over the authenticated pipe. Exactly one of these is
/// written per helper launch.
/// </summary>
public sealed class PolicyElevationRequestMessage
{
    [JsonRequired]
    [JsonPropertyName("protocolVersion")]
    public string ProtocolVersion { get; set; } = PolicyElevationProtocol.Version;

    [JsonRequired]
    [JsonPropertyName("requestId")]
    public string RequestId { get; set; } = string.Empty;

    [JsonRequired]
    [JsonPropertyName("operation")]
    public PolicyElevationOperation Operation { get; set; }

    [JsonRequired]
    [JsonPropertyName("conflictHandling")]
    public PolicyElevationConflictHandling ConflictHandling { get; set; }

    [JsonRequired]
    [JsonPropertyName("expectedStoreToken")]
    public string ExpectedStoreToken { get; set; } = string.Empty;

    [JsonRequired]
    [JsonPropertyName("validationReceipt")]
    public string ValidationReceipt { get; set; } = string.Empty;

    [JsonRequired]
    [JsonPropertyName("warningsAcknowledged")]
    public bool WarningsAcknowledged { get; set; }

    [JsonRequired]
    [JsonPropertyName("draft")]
    public JsonElement Draft { get; set; }
}

/// <summary>
/// Bounded write disposition produced by the elevated helper. Mapped onto
/// <see cref="PolicyElevationOutcome"/> by the host.
/// </summary>
[JsonConverter(typeof(PolicyElevationDispositionJsonConverter))]
public enum PolicyElevationDisposition
{
    /// <summary>No valid wire disposition was supplied.</summary>
    Invalid = 0,

    /// <summary>The broker accepted and persisted the replacement.</summary>
    Committed = 1,

    /// <summary>The broker definitively rejected the request without persisting it.</summary>
    Rejected = 2,

    /// <summary>The request may have been persisted, but the helper could not prove the result.</summary>
    Unknown = 3,
}

/// <summary>
/// The single response the helper writes back before closing the connection.
/// It deliberately carries no policy, validation findings, broker message, or raw broker payload:
/// those are unbounded and could otherwise turn a committed write into a protocol failure.
/// </summary>
public sealed class PolicyElevationResponseMessage
{
    [JsonRequired]
    [JsonPropertyName("protocolVersion")]
    public string ProtocolVersion { get; set; } = PolicyElevationProtocol.Version;

    [JsonRequired]
    [JsonPropertyName("requestId")]
    public string RequestId { get; set; } = string.Empty;

    [JsonRequired]
    [JsonPropertyName("disposition")]
    public PolicyElevationDisposition Disposition { get; set; }

    [JsonRequired]
    [JsonPropertyName("brokerStatusCode")]
    public int? BrokerStatusCode { get; set; }

    [JsonRequired]
    [JsonPropertyName("brokerErrorCode")]
    public string? BrokerErrorCode { get; set; }

    [JsonRequired]
    [JsonPropertyName("committedStoreToken")]
    public string? CommittedStoreToken { get; set; }

    [JsonRequired]
    [JsonPropertyName("conflictStoreToken")]
    public string? ConflictStoreToken { get; set; }

    [JsonRequired]
    [JsonPropertyName("conflictState")]
    public PolicyElevationManagementState? ConflictState { get; set; }

    [JsonRequired]
    [JsonPropertyName("conflictPolicyId")]
    public string? ConflictPolicyId { get; set; }
}

[JsonConverter(typeof(PolicyElevationManagementStateJsonConverter))]
public enum PolicyElevationManagementState
{
    Active = 0,
    Missing = 1,
    Invalid = 2,
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
