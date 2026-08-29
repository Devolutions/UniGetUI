using System.Text.Json;
using Devolutions.Now.Policy.Api;
using UniGetUI.Core.Logging;
using UniGetUI.PackageEngine.AgentBroker.PolicyManagement;
using UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation;

namespace UniGetUI.Avalonia.ViewModels.Pages.SettingsPages.PolicyEditor;

/// <summary>
/// Production bridge from the editor-facing <see cref="IPolicyValidationClient"/> seam to
/// <see cref="IBrokerPolicyManagementService.ValidateAsync"/>. Every <see cref="BrokerPolicyValidationStatus"/>
/// outcome that is not "the Agent produced a validation result" is mapped onto the narrower
/// <see cref="PolicyEditorValidationOutcome"/> contract via an <see cref="ErrorCode"/>, so
/// <see cref="PolicyEditorSessionViewModel"/> always has something to report instead of a silently
/// empty findings list.
/// </summary>
public sealed class BrokerPolicyEditorValidationClient : IPolicyValidationClient
{
    private readonly IBrokerPolicyManagementService _service;

    public BrokerPolicyEditorValidationClient()
        : this(new BrokerPolicyManagementService())
    {
    }

    public BrokerPolicyEditorValidationClient(IBrokerPolicyManagementService service)
    {
        _service = service;
    }

    public async Task<PolicyEditorValidationOutcome> ValidateAsync(JsonElement draft, CancellationToken cancellationToken)
    {
        BrokerPolicyValidationOutcome outcome = await _service.ValidateAsync(draft, cancellationToken).ConfigureAwait(false);
        return outcome.Status switch
        {
            BrokerPolicyValidationStatus.Completed when outcome.Validation is not null =>
                BuildCompletedOutcome(outcome),
            BrokerPolicyValidationStatus.MalformedDraft =>
                new PolicyEditorValidationOutcome(null, ErrorCode.MalformedDraft),
            BrokerPolicyValidationStatus.RequestTooLarge =>
                new PolicyEditorValidationOutcome(null, ErrorCode.PayloadTooLarge),
            BrokerPolicyValidationStatus.AccessDenied =>
                new PolicyEditorValidationOutcome(null, ErrorCode.Forbidden),
            BrokerPolicyValidationStatus.Unsupported =>
                new PolicyEditorValidationOutcome(null, ErrorCode.UnsupportedEndpoint),
            _ => new PolicyEditorValidationOutcome(null, ErrorCode.InternalError),
        };
    }

    private static PolicyEditorValidationOutcome BuildCompletedOutcome(
        BrokerPolicyValidationOutcome outcome)
    {
        if (outcome.Validation is null || outcome.Diagnostics is null)
            return new PolicyEditorValidationOutcome(outcome.Validation);

        IReadOnlyList<PolicyValidationFinding> findings =
        [
            .. outcome.Diagnostics.Findings.Select(PolicyValidationFinding.FromSanitized),
        ];
        int omitted = Math.Max(
            0,
            outcome.Validation.Findings.Count - outcome.Diagnostics.Findings.Count);
        if (outcome.Diagnostics.FindingsTruncated && omitted == 0)
        {
            omitted = 1;
        }

        return new PolicyEditorValidationOutcome(
            outcome.Validation,
            BoundedFindings: findings,
            OmittedFindingCount: omitted);
    }
}

/// <summary>
/// Production bridge from the editor-facing <see cref="IPolicyWriteClient"/> seam to
/// <see cref="IPolicyWriteElevator.ReplacePolicyAsync"/> (the Windows elevated-helper write path).
/// Maps the editor's shared <see cref="PolicyReplacementOperation"/>/<see cref="PolicyConflictHandling"/>
/// onto the AgentBroker package's own (structurally identical, but distinct) elevation enums, and maps
/// every <see cref="PolicyElevationOutcome"/> onto a <see cref="PolicyWriteFailureKind"/> so the session
/// view model can present a specific, translated failure reason instead of a generic error.
/// </summary>
public sealed class WindowsPolicyEditorWriteClient : IPolicyWriteClient
{
    private readonly IPolicyWriteElevator _elevator;

    public WindowsPolicyEditorWriteClient()
        : this(CreateDefaultElevator())
    {
    }

    public WindowsPolicyEditorWriteClient(IPolicyWriteElevator elevator)
    {
        _elevator = elevator;
    }

    private static IPolicyWriteElevator CreateDefaultElevator()
    {
#if WINDOWS
        return new WindowsPolicyWriteElevator();
#else
        return new UnsupportedPolicyWriteElevator();
#endif
    }

    public async Task<PolicyWriteOutcome> WriteAsync(PolicyEditorWriteRequest request, CancellationToken cancellationToken)
    {
        var elevationRequest = new PolicyElevationWriteRequest(request.Draft)
        {
            Operation = MapOperation(request.Operation),
            ConflictHandling = MapConflictHandling(request.ConflictHandling),
            ExpectedStoreToken = request.ExpectedStoreToken,
            ValidationReceipt = request.ValidationReceipt,
            WarningsAcknowledged = request.WarningsAcknowledged,
        };

        PolicyElevationResult result;
        try
        {
            result = await _elevator.ReplacePolicyAsync(elevationRequest, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }

        if (result.Succeeded && result.Response is not null)
        {
            return PolicyWriteOutcome.Success(result.Response);
        }

        if (result.ErrorMessage is not null)
        {
            Logger.Warn($"[PolicyEditor] Elevated policy write did not succeed ({result.Outcome}): {result.ErrorMessage}");
        }

        return PolicyWriteOutcome.Failure(MapFailureKind(result.Outcome), result.Error);
    }

    private static PolicyWriteFailureKind MapFailureKind(PolicyElevationOutcome outcome) => outcome switch
    {
        PolicyElevationOutcome.Replaced => PolicyWriteFailureKind.None,
        PolicyElevationOutcome.UserDeclinedElevation => PolicyWriteFailureKind.UacCanceled,
        PolicyElevationOutcome.UnsupportedPlatform
            or PolicyElevationOutcome.HelperUnavailable
            or PolicyElevationOutcome.LaunchFailed => PolicyWriteFailureKind.LaunchFailed,
        PolicyElevationOutcome.HelperUntrusted
            or PolicyElevationOutcome.PeerAuthenticationFailed => PolicyWriteFailureKind.AuthenticationFailed,
        PolicyElevationOutcome.PayloadTooLarge
            or PolicyElevationOutcome.MalformedResponse
            or PolicyElevationOutcome.TimedOut
            or PolicyElevationOutcome.ConnectionClosed => PolicyWriteFailureKind.ProtocolFailed,
        PolicyElevationOutcome.HelperCrashed => PolicyWriteFailureKind.HelperFailed,
        PolicyElevationOutcome.BrokerRejected
            or PolicyElevationOutcome.BrokerUnavailable
            or PolicyElevationOutcome.BrokerInvalidResponse => PolicyWriteFailureKind.BrokerRejected,
        PolicyElevationOutcome.Cancelled => PolicyWriteFailureKind.LaunchFailed,
        _ => PolicyWriteFailureKind.HelperFailed,
    };

    private static PolicyElevationOperation MapOperation(PolicyReplacementOperation operation) => operation switch
    {
        PolicyReplacementOperation.Update => PolicyElevationOperation.Update,
        PolicyReplacementOperation.ReplaceIdentity => PolicyElevationOperation.ReplaceIdentity,
        PolicyReplacementOperation.Create => PolicyElevationOperation.Create,
        PolicyReplacementOperation.Repair => PolicyElevationOperation.Repair,
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null),
    };

    private static PolicyElevationConflictHandling MapConflictHandling(PolicyConflictHandling handling) => handling switch
    {
        PolicyConflictHandling.Reject => PolicyElevationConflictHandling.Reject,
        PolicyConflictHandling.ConfirmOverwrite => PolicyElevationConflictHandling.ConfirmOverwrite,
        _ => throw new ArgumentOutOfRangeException(nameof(handling), handling, null),
    };
}
