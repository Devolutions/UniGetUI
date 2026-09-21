using Devolutions.Now.Policy.Model;

namespace UniGetUI.Avalonia.ViewModels.Pages.SettingsPages.PolicyEditor;

/// <summary>
/// Single source of truth for policy defaults chosen by the editor.
/// </summary>
public static class PolicyEditorPolicyContract
{
    /// <summary>
    /// The fail-closed default decision applied to brand-new policy documents: deny unless a rule
    /// explicitly allows the operation.
    /// </summary>
    public const Decision DefaultTemplateDecision = Decision.Deny;

}
