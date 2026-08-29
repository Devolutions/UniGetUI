namespace UniGetUI.Avalonia.ViewModels.Pages.SettingsPages.PolicyEditor;

/// <summary>
/// Produces the fail-closed starting point for a brand-new policy document. Everything the template
/// fixes (schema, policy type, policy version, rule precedence, default decision, empty rule set) is
/// non-negotiable at creation time; only the caller-supplied identity (<paramref name="id"/> in
/// <see cref="CreateNew"/>) and publisher are free-form, because the editor cannot know them in advance.
/// </summary>
public static class PolicyEditorTemplates
{
    /// <summary>
    /// Creates a brand-new draft document: fixed schema/type/version, <c>PriorityThenDeny</c>
    /// precedence, a default decision of <c>Deny</c> (fail closed), and no rules. The caller must
    /// supply the new policy's <paramref name="id"/> and <paramref name="publisher"/>; both are
    /// validated to be non-empty since the write path (external to this domain) requires them.
    /// </summary>
    public static PolicyEditorDraftDocument CreateNew(string id, string publisher)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("A new policy requires a non-empty identifier.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(publisher))
        {
            throw new ArgumentException("A new policy requires a non-empty publisher.", nameof(publisher));
        }

        return new PolicyEditorDraftDocument
        {
            PolicyVersion = PolicyEditorPolicyContract.InitialPolicyVersion,
            Metadata = new PolicyEditorDraftMetadata
            {
                Id = id,
                Publisher = publisher,
            },
            Enforcement = new PolicyEditorDraftEnforcement
            {
                DefaultDecision = PolicyEditorPolicyContract.DefaultTemplateDecision,
            },
            Rules = [],
        };
    }
}
