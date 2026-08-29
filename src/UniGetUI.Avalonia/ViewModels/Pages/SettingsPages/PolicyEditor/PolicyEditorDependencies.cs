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
}

public sealed record PolicyWriteOutcome(
    PolicyReplacementResponse? Response,
    ErrorResponse? Error,
    PolicyWriteFailureKind FailureKind = PolicyWriteFailureKind.None)
{
    public bool Succeeded => Response is not null;

    public static PolicyWriteOutcome Success(PolicyReplacementResponse response) =>
        new(response, null);

    public static PolicyWriteOutcome Failure(
        PolicyWriteFailureKind kind,
        ErrorResponse? error = null) =>
        new(null, error, kind);
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
    IReadOnlyList<PolicyValidationFinding> Findings);

public interface IPolicyEditorConfirmationPrompt
{
    Task<bool> ConfirmAsync(
        PolicyEditorConfirmationRequest request,
        CancellationToken cancellationToken);
}
