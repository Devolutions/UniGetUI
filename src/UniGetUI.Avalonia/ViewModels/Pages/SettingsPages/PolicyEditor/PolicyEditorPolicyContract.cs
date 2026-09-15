using Devolutions.Now.Policy.Model;

namespace UniGetUI.Avalonia.ViewModels.Pages.SettingsPages.PolicyEditor;

/// <summary>
/// Single source of truth for the parts of the package broker policy document contract that the
/// editor treats as fixed (non-negotiable) rather than user-editable.
/// </summary>
public static class PolicyEditorPolicyContract
{
    /// <summary>
    /// The only <see cref="PolicyDocument.PolicyType"/> value the editor understands.
    /// </summary>
    public const string PolicyType = "PackageBrokerPolicy";

    /// <summary>
    /// The only <see cref="PolicyEnforcement.RulePrecedence"/> value the editor understands.
    /// </summary>
    public const RulePrecedence FixedRulePrecedence = RulePrecedence.PriorityThenDeny;

    /// <summary>
    /// The fail-closed default decision applied to brand-new policy documents: deny unless a rule
    /// explicitly allows the operation.
    /// </summary>
    public const Decision DefaultTemplateDecision = Decision.Deny;

}
