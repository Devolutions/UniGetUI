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
    string ValidationReceipt)
{
    public PolicyReplacementRequest ToSharedRequest() => new()
    {
        ExpectedStoreToken = ExpectedStoreToken,
        Operation = Operation,
        ConflictHandling = ConflictHandling,
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

internal static class PolicyWriteDiagnosticCodes
{
    internal const string PostCommitRefreshTimeout = nameof(PostCommitRefreshTimeout);
    internal const string PostCommitRefreshUnavailable = nameof(PostCommitRefreshUnavailable);
}

public sealed record PolicyWriteOutcome(
    PolicyReplacementResponse? Response,
    ErrorResponse? Error,
    PolicyWriteFailureKind FailureKind = PolicyWriteFailureKind.None,
    PolicyEditorRetryDecision? ConflictDecision = null,
    bool SavedThenSuperseded = false,
    string? DiagnosticCode = null)
{
    public bool Succeeded => Response is not null;

    public static PolicyWriteOutcome Success(
        PolicyReplacementResponse response,
        bool savedThenSuperseded = false) =>
        new(response, null, SavedThenSuperseded: savedThenSuperseded);

    public static PolicyWriteOutcome Failure(
        PolicyWriteFailureKind kind,
        ErrorResponse? error = null,
        PolicyEditorRetryDecision? conflictDecision = null,
        string? diagnosticCode = null) =>
        new(
            null,
            error,
            kind,
            conflictDecision,
            DiagnosticCode: diagnosticCode);
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
    string? RuleId = null);

public interface IPolicyEditorConfirmationPrompt
{
    Task<bool> ConfirmAsync(
        PolicyEditorConfirmationRequest request,
        CancellationToken cancellationToken);
}
