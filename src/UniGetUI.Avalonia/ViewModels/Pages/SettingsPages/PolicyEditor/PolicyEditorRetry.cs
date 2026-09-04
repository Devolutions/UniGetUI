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
        ArgumentNullException.ThrowIfNull(management);
        return Resolve(
            draftId,
            management.State,
            management.StoreToken,
            management.Policy?.Metadata.Id);
    }

    public static PolicyEditorRetryDecision Resolve(
        string draftId,
        PolicyManagementState state,
        string token,
        string? activePolicyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(draftId);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return state switch
        {
            PolicyManagementState.Active when activePolicyId is not null
                && string.Equals(
                    activePolicyId,
                    draftId,
                    StringComparison.Ordinal) =>
                new(
                    PolicyReplacementOperation.Update,
                    token,
                    state,
                    activePolicyId),
            PolicyManagementState.Active when activePolicyId is not null =>
                new(
                    PolicyReplacementOperation.ReplaceIdentity,
                    token,
                    state,
                    activePolicyId),
            PolicyManagementState.Missing =>
                new(
                    PolicyReplacementOperation.Create,
                    token,
                    state,
                    null),
            PolicyManagementState.Invalid =>
                new(
                    PolicyReplacementOperation.Repair,
                    token,
                    state,
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
