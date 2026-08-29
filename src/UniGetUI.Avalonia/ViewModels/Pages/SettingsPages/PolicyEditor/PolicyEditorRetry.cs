using Devolutions.Now.Policy.Api;

namespace UniGetUI.Avalonia.ViewModels.Pages.SettingsPages.PolicyEditor;

public sealed record PolicyEditorRetryDecision(
    PolicyReplacementOperation Operation,
    string Token,
    PolicyManagementState State,
    string? ActivePolicyId);

public sealed record PolicyEditorConfirmationContext(
    PolicyReplacementOperation Operation,
    PolicyManagementState State,
    string? ActivePolicyId,
    string Token,
    string DraftId)
{
    public static PolicyEditorConfirmationContext For(
        PolicyEditorRetryDecision decision,
        string draftId) =>
        new(decision.Operation, decision.State, decision.ActivePolicyId, decision.Token, draftId);
}

public static class PolicyEditorRetryResolver
{
    public static PolicyEditorRetryDecision Resolve(
        string draftId,
        PolicyManagementSnapshot management)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(draftId);
        ArgumentNullException.ThrowIfNull(management);
        ArgumentException.ThrowIfNullOrWhiteSpace(management.StoreToken);

        return management.State switch
        {
            PolicyManagementState.Active when management.Policy is not null
                && string.Equals(
                    management.Policy.Metadata.Id,
                    draftId,
                    StringComparison.Ordinal) =>
                new(
                    PolicyReplacementOperation.Update,
                    management.StoreToken,
                    management.State,
                    management.Policy.Metadata.Id),
            PolicyManagementState.Active when management.Policy is not null =>
                new(
                    PolicyReplacementOperation.ReplaceIdentity,
                    management.StoreToken,
                    management.State,
                    management.Policy.Metadata.Id),
            PolicyManagementState.Missing =>
                new(
                    PolicyReplacementOperation.Create,
                    management.StoreToken,
                    management.State,
                    null),
            PolicyManagementState.Invalid =>
                new(
                    PolicyReplacementOperation.Repair,
                    management.StoreToken,
                    management.State,
                    null),
            _ => throw new InvalidDataException(
                "The management snapshot is inconsistent with its policy state."),
        };
    }

    public static bool RequiresFreshConfirmation(
        PolicyEditorConfirmationContext? existing,
        PolicyEditorRetryDecision decision,
        string draftId) =>
        existing != PolicyEditorConfirmationContext.For(decision, draftId);
}
