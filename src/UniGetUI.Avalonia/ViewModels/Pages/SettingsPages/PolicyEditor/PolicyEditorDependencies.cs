using System.Text.Json;
using Devolutions.Now.Policy.Api;

namespace UniGetUI.Avalonia.ViewModels.Pages.SettingsPages.PolicyEditor;

public sealed record PolicyEditorValidationOutcome(
    PolicyValidationResult? Validation,
    ErrorCode? ErrorCode = null,
    IReadOnlyList<PolicyValidationFinding>? BoundedFindings = null,
    int OmittedFindingCount = 0)
{
    public bool Completed => Validation is not null;
}

public interface IPolicyValidationClient
{
    Task<PolicyEditorValidationOutcome> ValidateAsync(
        JsonElement draft,
        CancellationToken cancellationToken);
}

public sealed record PolicyEditorWriteRequest(
    PolicyReplacementOperation Operation,
    PolicyConflictHandling ConflictHandling,
    string ExpectedStoreToken,
    JsonElement Draft,
    string ValidationReceipt,
    bool WarningsAcknowledged)
{
    public PolicyReplacementRequest ToSharedRequest() => new()
    {
        ExpectedStoreToken = ExpectedStoreToken,
        Operation = Operation,
        ConflictHandling = ConflictHandling,
        WarningsAcknowledged = WarningsAcknowledged,
        Draft = Draft.Clone(),
        ValidationReceipt = ValidationReceipt,
    };
}

public enum PolicyWriteFailureKind
{
    None,
    UacCanceled,
    LaunchFailed,
    AuthenticationFailed,
    ProtocolFailed,
    HelperFailed,
    BrokerRejected,
    WriteResultUnknown,
}

public sealed record PolicyWriteOutcome(
    PolicyReplacementResponse? Response,
    ErrorResponse? Error,
    PolicyWriteFailureKind FailureKind = PolicyWriteFailureKind.None,
    PolicyEditorRetryDecision? ConflictDecision = null,
    bool SavedThenSuperseded = false)
{
    public bool Succeeded => Response is not null;

    public static PolicyWriteOutcome Success(
        PolicyReplacementResponse response,
        bool savedThenSuperseded = false) =>
        new(response, null, SavedThenSuperseded: savedThenSuperseded);

    public static PolicyWriteOutcome Failure(
        PolicyWriteFailureKind kind,
        ErrorResponse? error = null,
        PolicyEditorRetryDecision? conflictDecision = null) =>
        new(null, error, kind, conflictDecision);
}

public interface IPolicyWriteClient
{
    Task<PolicyWriteOutcome> WriteAsync(
        PolicyEditorWriteRequest request,
        CancellationToken cancellationToken);
}

public sealed record PolicyEditorConfirmationRequest(
    PolicyEditorConfirmationKind Kind,
    PolicyReplacementOperation Operation,
    string DraftId,
    string ExpectedStoreToken,
    PolicyManagementState State,
    string? ActivePolicyId,
    IReadOnlyList<PolicyValidationFinding> Findings,
    int WarningCount = 0);

public interface IPolicyEditorConfirmationPrompt
{
    Task<bool> ConfirmAsync(
        PolicyEditorConfirmationRequest request,
        CancellationToken cancellationToken);
}
