using System.Text.Json;
using Devolutions.Now.Policy.Api;

namespace UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation;

/// <summary>
/// Every distinct way an elevated policy write can end. The UI is expected to branch on this
/// rather than on a message string.
/// </summary>
public enum PolicyElevationOutcome
{
    /// <summary>The broker persisted the replacement.</summary>
    Replaced,

    /// <summary>The broker refused the replacement (validation, conflict, permissions, …).</summary>
    BrokerRejected,

    /// <summary>The elevated helper could not reach the broker, or the broker timed out.</summary>
    BrokerUnavailable,

    /// <summary>The broker answered with something that could not be interpreted.</summary>
    BrokerInvalidResponse,

    /// <summary>Elevated policy writes are only implemented on Windows.</summary>
    UnsupportedPlatform,

    /// <summary>The helper was not present at the exact packaged path (fail-closed discovery).</summary>
    HelperUnavailable,

    /// <summary>The helper exists but failed signature, signer-identity or install-location checks.</summary>
    HelperUntrusted,

    /// <summary>The user dismissed the consent prompt (ERROR_CANCELLED / 1223).</summary>
    UserDeclinedElevation,

    /// <summary>The consent prompt could not be raised, or the process could not be started.</summary>
    LaunchFailed,

    /// <summary>Mutual authentication over the pipe failed; no payload was exchanged.</summary>
    PeerAuthenticationFailed,

    /// <summary>A frame exceeded the negotiated budget in either direction.</summary>
    PayloadTooLarge,

    /// <summary>A frame could not be parsed against the protocol contract.</summary>
    MalformedResponse,

    /// <summary>A protocol stage exceeded its timeout.</summary>
    TimedOut,

    /// <summary>The helper closed the connection before answering.</summary>
    ConnectionClosed,

    /// <summary>The helper terminated abnormally, or exited with a non-zero status.</summary>
    HelperCrashed,

    /// <summary>The caller cancelled the operation.</summary>
    Cancelled,

    /// <summary>The request was dispatched, but its commit result could not be authenticated.</summary>
    WriteResultUnknown,
}

/// <summary>What the caller asks the elevated helper to persist.</summary>
public sealed record PolicyElevationWriteRequest
{
    public PolicyElevationWriteRequest(JsonElement draft)
    {
        // Clone so the request stays valid after the caller disposes the owning JsonDocument.
        Draft = draft.Clone();
    }

    /// <summary>The policy draft, exactly as the caller composed it.</summary>
    public JsonElement Draft { get; }

    public PolicyElevationOperation Operation { get; init; } = PolicyElevationOperation.Update;

    public PolicyElevationConflictHandling ConflictHandling { get; init; } =
        PolicyElevationConflictHandling.Reject;

    public string ExpectedStoreToken { get; init; } = string.Empty;

    public string ValidationReceipt { get; init; } = string.Empty;

    public bool WarningsAcknowledged { get; init; }
}

/// <summary>
/// The result of an elevated policy write. <see cref="Request"/> always round-trips the caller's
/// draft so a failed attempt can restore the editor without the caller keeping its own copy.
/// </summary>
public sealed record PolicyElevationResult(
    PolicyElevationOutcome Outcome,
    PolicyElevationWriteRequest Request,
    string? ErrorMessage = null,
    int? Win32ErrorCode = null,
    int? HelperExitCode = null,
    int? BrokerStatusCode = null,
    string? BrokerErrorCode = null,
    JsonElement? Payload = null,
    PolicyReplacementResponse? Response = null,
    ErrorResponse? Error = null,
    string? CommittedStoreToken = null,
    string? ConflictStoreToken = null,
    PolicyElevationManagementState? ConflictState = null,
    string? ConflictPolicyId = null)
{
    public bool Succeeded => Outcome is PolicyElevationOutcome.Replaced;

    /// <summary>The draft the caller submitted, preserved verbatim.</summary>
    public JsonElement Draft => Request.Draft;
}

/// <summary>Performs a policy replacement through an elevated, authenticated helper.</summary>
public interface IPolicyWriteElevator
{
    Task<PolicyElevationResult> ReplacePolicyAsync(
        PolicyElevationWriteRequest request,
        CancellationToken cancellationToken);
}

/// <summary>Fail-closed implementation used when the Windows elevation path is unavailable.</summary>
public sealed class UnsupportedPolicyWriteElevator : IPolicyWriteElevator
{
    public Task<PolicyElevationResult> ReplacePolicyAsync(
        PolicyElevationWriteRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new PolicyElevationResult(
            PolicyElevationOutcome.UnsupportedPlatform,
            request,
            "Elevated policy writes are only supported on Windows."));
    }
}
