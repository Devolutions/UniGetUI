using Devolutions.Now.Policy.Api;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.ViewModels.Pages.SettingsPages.PolicyEditor;

internal static class PolicyEditorLocalValidation
{
    public static IReadOnlyList<PolicyValidationFinding> ValidateResourceIds(
        PolicyEditorDraftDocument draft)
    {
        var findings = new List<PolicyValidationFinding>();
        if (!PolicyEditorTemplates.IsValidResourceId(draft.Metadata.Id))
        {
            findings.Add(new(
                "/Metadata/Id",
                null,
                PolicyValidationSeverity.Error,
                DescribePolicyIdError(draft.Metadata.Id),
                PolicyFindingCode.InvalidFieldValue));
        }

        for (int index = 0; index < draft.Rules.Count; index++)
        {
            PolicyEditorDraftRule rule = draft.Rules[index];
            if (PolicyEditorTemplates.IsValidResourceId(rule.Id))
            {
                continue;
            }

            findings.Add(new(
                $"/Rules/{index}/Id",
                rule.Id,
                PolicyValidationSeverity.Error,
                DescribeRuleIdError(rule.Id, index),
                PolicyFindingCode.InvalidFieldValue));
        }

        return findings;
    }

    private static string DescribePolicyIdError(string value) =>
        GetErrorKind(value) switch
        {
            ResourceIdErrorKind.Empty =>
                CoreTools.Translate("Policy ID is required."),
            ResourceIdErrorKind.TooLong =>
                CoreTools.Translate("Policy ID cannot exceed 128 characters."),
            ResourceIdErrorKind.InvalidFirstCharacter =>
                CoreTools.Translate("Policy ID must start with an ASCII letter or number."),
            _ =>
                CoreTools.Translate("Policy ID can contain only ASCII letters, numbers, '.', '_', ':' and '-'; spaces are not allowed."),
        };

    private static string DescribeRuleIdError(string value, int index) =>
        GetErrorKind(value) switch
        {
            ResourceIdErrorKind.Empty =>
                CoreTools.Translate("Rule {0} ID is required.", index + 1),
            ResourceIdErrorKind.TooLong =>
                CoreTools.Translate("Rule {0} ID cannot exceed 128 characters.", index + 1),
            ResourceIdErrorKind.InvalidFirstCharacter =>
                CoreTools.Translate(
                    "Rule {0} ID must start with an ASCII letter or number.",
                    index + 1),
            _ =>
                CoreTools.Translate(
                    "Rule {0} ID can contain only ASCII letters, numbers, '.', '_', ':' and '-'; spaces are not allowed.",
                    index + 1),
        };

    private static ResourceIdErrorKind GetErrorKind(string value)
    {
        if (string.IsNullOrEmpty(value))
            return ResourceIdErrorKind.Empty;
        if (value.Length > PolicyEditorTemplates.ResourceIdMaxLength)
            return ResourceIdErrorKind.TooLong;
        if (!IsAsciiLetterOrDigit(value[0]))
            return ResourceIdErrorKind.InvalidFirstCharacter;
        return ResourceIdErrorKind.InvalidCharacter;
    }

    private static bool IsAsciiLetterOrDigit(char value) =>
        value is >= 'A' and <= 'Z'
        or >= 'a' and <= 'z'
        or >= '0' and <= '9';

    private enum ResourceIdErrorKind
    {
        Empty,
        TooLong,
        InvalidFirstCharacter,
        InvalidCharacter,
    }
}
