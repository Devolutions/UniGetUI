namespace UniGetUI.Avalonia.ViewModels.Pages.SettingsPages.PolicyEditor;

/// <summary>
/// Produces the fail-closed starting point for a brand-new policy document. Everything the template
/// fixes (schema, policy type, policy version, rule precedence, default decision, empty rule set) is
/// non-negotiable at creation time; only the caller-supplied identity (<paramref name="id"/> in
/// <see cref="CreateNew"/>) and publisher are free-form, because the editor cannot know them in advance.
/// </summary>
public static class PolicyEditorTemplates
{
    public const int ResourceIdMaxLength = 128;

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

        if (string.IsNullOrEmpty(publisher))
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

    public static string CreateReplacementId(string activeId)
    {
        if (!IsValidResourceId(activeId))
        {
            throw new ArgumentException(
                "The active policy identifier is not a valid resource identifier.",
                nameof(activeId));
        }

        const string suffix = "-new";
        int prefixLength = Math.Min(activeId.Length, ResourceIdMaxLength - suffix.Length);
        string candidate = activeId[..prefixLength] + suffix;
        if (!string.Equals(candidate, activeId, StringComparison.Ordinal))
        {
            return candidate;
        }

        char replacement = activeId[^1] == '0' ? '1' : '0';
        return activeId[..^1] + replacement;
    }

    private static bool IsValidResourceId(string? value)
    {
        if (string.IsNullOrEmpty(value)
            || value.Length > ResourceIdMaxLength
            || !IsAsciiLetterOrDigit(value[0]))
        {
            return false;
        }

        return value.AsSpan(1).IndexOfAnyExcept(
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789._:-") < 0;
    }

    private static bool IsAsciiLetterOrDigit(char value) =>
        value is >= 'A' and <= 'Z'
        or >= 'a' and <= 'z'
        or >= '0' and <= '9';
}
