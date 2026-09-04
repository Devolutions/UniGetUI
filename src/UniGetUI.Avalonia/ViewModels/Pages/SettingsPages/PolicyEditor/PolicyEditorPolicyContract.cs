using Devolutions.Now.Policy.Model;
using PolicySchemaUris = Devolutions.Now.Policy.Model.SchemaUris;

namespace UniGetUI.Avalonia.ViewModels.Pages.SettingsPages.PolicyEditor;

/// <summary>
/// Single source of truth for the parts of the package broker policy document contract that the
/// editor treats as fixed (non-negotiable) rather than user-editable. These values are intentionally
/// duplicated here (as opposed to being read back from an arbitrary document) so that both the "new
/// policy" template and the strict raw-JSON acceptance path can fail closed: a document that disagrees
/// with any of these constants is rejected outright instead of being silently coerced.
/// </summary>
public static class PolicyEditorPolicyContract
{
    /// <summary>
    /// The only <see cref="PolicyDocument.PolicyType"/> value the editor understands.
    /// </summary>
    public const string PolicyType = "PackageBrokerPolicy";

    /// <summary>
    /// The semantic version stamped onto a brand-new policy document created by the editor.
    /// Existing documents keep whatever <see cref="PolicyDocument.PolicyVersion"/> their publisher chose.
    /// </summary>
    public const string InitialPolicyVersion = "1.0.0";

    /// <summary>
    /// The only <see cref="PolicyEnforcement.RulePrecedence"/> value the editor understands.
    /// </summary>
    public const RulePrecedence FixedRulePrecedence = RulePrecedence.PriorityThenDeny;

    /// <summary>
    /// The fail-closed default decision applied to brand-new policy documents: deny unless a rule
    /// explicitly allows the operation.
    /// </summary>
    public const Decision DefaultTemplateDecision = Decision.Deny;

    /// <summary>
    /// The schema URI used for editable <see cref="PolicyDraftDocument"/> instances.
    /// </summary>
    public static string DraftSchema => PolicySchemaUris.PolicyDraft;

    /// <summary>
    /// The schema URI used only when projecting an authoritative committed <see cref="PolicyDocument"/>.
    /// </summary>
    public static string CommittedSchema => PolicySchemaUris.Policy;
}
