using Devolutions.Now.Policy.Api;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.ViewModels.Pages.SettingsPages.PolicyEditor;

internal static class PolicyEditorLocalValidation
{
    public static IReadOnlyList<PolicyValidationFinding> ValidateDraft(
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
            if (!PolicyEditorTemplates.IsValidResourceId(rule.Id))
            {
                findings.Add(new(
                    $"/Rules/{index}/Id",
                    rule.Id,
                    PolicyValidationSeverity.Error,
                    DescribeRuleIdError(rule.Id, index),
                    PolicyFindingCode.InvalidFieldValue));
            }

            if (PolicyEditorRuleSemantics.IsCatchAll(rule.Match))
            {
                findings.Add(new(
                    $"/Rules/{index}/Match",
                    rule.Id,
                    PolicyValidationSeverity.Error,
                    CoreTools.Translate(
                        "Rule {0} needs at least one request condition. Configure Request characteristics or another match field, or delete the rule.",
                        index + 1),
                    PolicyFindingCode.InvalidFieldValue));
            }

            if (rule.Match.SourceNames.Count > 0 && rule.Match.Managers.Count != 1)
            {
                findings.Add(new(
                    $"/Rules/{index}/Match/Managers",
                    rule.Id,
                    PolicyValidationSeverity.Error,
                    CoreTools.Translate(
                        "Rule {0} uses Source names and must select exactly one Package manager. Create separate rules for different managers.",
                        index + 1),
                    PolicyFindingCode.InvalidFieldValue));
            }
            else if (rule.Match.SourceNames.Count > 0
                && !PolicyEditorRuleSemantics.SupportsSourceNames(rule.Match.Managers[0]))
            {
                findings.Add(new(
                    $"/Rules/{index}/Match/Managers",
                    rule.Id,
                    PolicyValidationSeverity.Error,
                    CoreTools.Translate(
                        "Rule {0} uses Source names, but the selected package manager does not support configured sources. Choose a source-capable manager or remove Source names.",
                        index + 1),
                    PolicyFindingCode.InvalidFieldValue));
            }

            AddExclusiveConditionFindings(findings, rule, index);
        }

        return findings;
    }

    private static void AddExclusiveConditionFindings(
        List<PolicyValidationFinding> findings,
        PolicyEditorDraftRule rule,
        int index)
    {
        if (rule.Match.PackageIdentifierMode != PackageIdentifierMode.Omitted
            && !PolicyEditorRuleSemantics.HasPackageIdentifierCriterion(rule.Match))
        {
            string member = rule.Match.PackageIdentifierMode == PackageIdentifierMode.Exact
                ? "Exact"
                : "Patterns";
            findings.Add(new(
                $"/Rules/{index}/Match/PackageIdentifiers/{member}",
                rule.Id,
                PolicyValidationSeverity.Error,
                CoreTools.Translate(
                    "Rule {0} must include at least one package identifier for the selected match mode.",
                    index + 1),
                PolicyFindingCode.InvalidFieldValue));
        }

        if (rule.Match.VersionMode != PackageVersionMode.Omitted
            && !PolicyEditorRuleSemantics.HasVersionCriterion(rule.Match))
        {
            string member = rule.Match.VersionMode == PackageVersionMode.Exact
                ? "Exact"
                : "Range";
            findings.Add(new(
                $"/Rules/{index}/Match/Version/{member}",
                rule.Id,
                PolicyValidationSeverity.Error,
                CoreTools.Translate(
                    "Rule {0} must include at least one exact version or a semantic-version range for the selected match mode.",
                    index + 1),
                PolicyFindingCode.InvalidFieldValue));
        }
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
