using Devolutions.Now.Policy.Model;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.ViewModels.Pages.SettingsPages.PolicyEditor;

internal static class PolicyEditorAdvisories
{
    public static IReadOnlyList<string> ForRule(PolicyEditorDraftRule rule)
    {
        var messages = new HashSet<string>(StringComparer.Ordinal);
        if (rule.Enabled
            && rule.Decision == Decision.Allow
            && PolicyEditorRuleSemantics.IsCatchAll(rule.Match))
        {
            messages.Add(CoreTools.Translate(
                "This enabled Allow rule applies to every package request. Add match conditions to limit its scope."));
        }

        if (rule.Enabled
            && rule.Decision == Decision.Allow
            && rule.Match.PackageIdentifierMode == PackageIdentifierMode.Patterns
            && rule.Match.PackageIdentifierPatterns.Any(IsUniversalPattern))
        {
            messages.Add(CoreTools.Translate(
                "This enabled Allow rule uses a universal package identifier pattern and may authorize requests far beyond the intended scope."));
        }

        return [.. messages];
    }

    public static string SkipHashCheck(
        PolicyEditorDraftRule rule,
        IReadOnlyList<PolicyEditorDraftRule>? rules = null,
        int ruleIndex = -1) =>
        IsAllowWithConstraints(rule, out PolicyEditorDraftConstraints? limits)
        && limits.AllowSkipHashCheck
        && rule.Match.SkipHashCheck != TriState.False
        && !PolicyEditorRiskCoverage.IsCovered(rules, ruleIndex, PolicyEditorRisk.SkipHashCheck)
            ? CoreTools.Translate("This rule permits bypassing package integrity checks.")
            : "";

    public static string CustomParameters(
        PolicyEditorDraftRule rule,
        IReadOnlyList<PolicyEditorDraftRule>? rules = null,
        int ruleIndex = -1) =>
        IsBroadlyScoped(rule)
        && IsAllowWithConstraints(rule, out PolicyEditorDraftConstraints? limits)
        && limits.AllowCustomParameters
        && rule.Match.HasCustomParameters != TriState.False
        && limits.AllowedCustomParameters.Count == 0
        && limits.AllowedCustomParameterPatterns.Count == 0
        && !PolicyEditorRiskCoverage.IsCovered(rules, ruleIndex, PolicyEditorRisk.CustomParameters)
            ? CoreTools.Translate("This Allow rule can permit arbitrary extra package-manager options because it does not limit package identifiers or sources.")
            : "";

    public static string CustomInstallLocation(
        PolicyEditorDraftRule rule,
        IReadOnlyList<PolicyEditorDraftRule>? rules = null,
        int ruleIndex = -1) =>
        IsBroadlyScoped(rule)
        && IsAllowWithConstraints(rule, out PolicyEditorDraftConstraints? limits)
        && limits.AllowCustomInstallLocation
        && rule.Match.HasCustomInstallLocation != TriState.False
        && limits.AllowedInstallLocationPatterns.Count == 0
        && !PolicyEditorRiskCoverage.IsCovered(rules, ruleIndex, PolicyEditorRisk.CustomInstallLocation)
            ? CoreTools.Translate("This Allow rule can permit any custom install folder because it does not limit package identifiers or sources.")
            : "";

    public static string PrePostCommands(
        PolicyEditorDraftRule rule,
        IReadOnlyList<PolicyEditorDraftRule>? rules = null,
        int ruleIndex = -1) =>
        IsBroadlyScoped(rule)
        && IsAllowWithConstraints(rule, out PolicyEditorDraftConstraints? limits)
        && limits.AllowPrePostCommands
        && rule.Match.HasPrePostCommands != TriState.False
        && !PolicyEditorRiskCoverage.IsCovered(rules, ruleIndex, PolicyEditorRisk.PrePostCommands)
            ? CoreTools.Translate("This Allow rule can permit arbitrary commands before or after package operations because it does not limit package identifiers or sources.")
            : "";

    public static IReadOnlyList<string> FieldSpecific(PolicyEditorDraftRule rule) =>
    [
        .. new[]
        {
            SkipHashCheck(rule),
            CustomParameters(rule),
            CustomInstallLocation(rule),
            PrePostCommands(rule),
        }.Where(message => !string.IsNullOrEmpty(message)),
    ];

    private static bool IsAllowWithConstraints(
        PolicyEditorDraftRule rule,
        out PolicyEditorDraftConstraints limits)
    {
        limits = rule.Constraints!;
        return rule.Enabled
            && rule.Decision == Decision.Allow
            && limits is not null;
    }

    internal enum PolicyEditorRisk
    {
        SkipHashCheck,
        CustomParameters,
        CustomInstallLocation,
        PrePostCommands,
    }

    internal static class PolicyEditorRiskCoverage
    {
        public static bool IsCovered(
            IReadOnlyList<PolicyEditorDraftRule>? rules,
            int allowIndex,
            PolicyEditorRisk risk)
        {
            if (rules is null || allowIndex <= 0 || allowIndex >= rules.Count)
                return false;

            PolicyEditorDraftMatch allow = rules[allowIndex].Match;
            for (int index = 0; index < allowIndex; index++)
            {
                PolicyEditorDraftRule deny = rules[index];
                if (deny.Enabled
                    && deny.Decision == Decision.Deny
                    && MatchesRisk(deny.Match, risk)
                    && ContainsScope(deny.Match, allow, risk))
                {
                    return true;
                }
            }

            return false;
        }

        public static bool ShouldSuppressFinding(
            PolicyValidationFinding finding,
            IReadOnlyList<PolicyEditorDraftRule> rules)
        {
            if (!finding.IsWarning
                || finding.Code != Devolutions.Now.Policy.Api.PolicyFindingCode.SensitiveOptionAllowed
                || !TryGetRisk(finding, out PolicyEditorRisk risk)
                || !TryGetRuleIndex(finding, rules, out int index))
            {
                return false;
            }

            return IsCovered(rules, index, risk);
        }

        private static bool MatchesRisk(PolicyEditorDraftMatch match, PolicyEditorRisk risk) =>
            risk switch
            {
                PolicyEditorRisk.SkipHashCheck => match.SkipHashCheck == TriState.True,
                PolicyEditorRisk.CustomParameters => match.HasCustomParameters == TriState.True,
                PolicyEditorRisk.CustomInstallLocation => match.HasCustomInstallLocation == TriState.True,
                PolicyEditorRisk.PrePostCommands => match.HasPrePostCommands == TriState.True,
                _ => false,
            };

        private static bool ContainsScope(
            PolicyEditorDraftMatch deny,
            PolicyEditorDraftMatch allow,
            PolicyEditorRisk risk) =>
            ContainsSet(deny.Operations, allow.Operations)
            && ContainsSet(deny.Managers, allow.Managers)
            && ContainsSet(deny.SourceNames, allow.SourceNames)
            && ContainsPackageIdentifiers(deny, allow)
            && ContainsVersions(deny, allow)
            && ContainsSet(deny.Scopes, allow.Scopes)
            && ContainsSet(deny.Architectures, allow.Architectures)
            && ContainsSet(deny.ExecutionElevation, allow.ExecutionElevation)
            && ContainsBoolean(deny.Interactive, allow.Interactive)
            && ContainsBooleanExceptRisk(deny.SkipHashCheck, allow.SkipHashCheck, risk, PolicyEditorRisk.SkipHashCheck)
            && ContainsBoolean(deny.PreRelease, allow.PreRelease)
            && ContainsBooleanExceptRisk(deny.HasCustomParameters, allow.HasCustomParameters, risk, PolicyEditorRisk.CustomParameters)
            && ContainsBooleanExceptRisk(deny.HasCustomInstallLocation, allow.HasCustomInstallLocation, risk, PolicyEditorRisk.CustomInstallLocation)
            && ContainsBooleanExceptRisk(deny.HasPrePostCommands, allow.HasPrePostCommands, risk, PolicyEditorRisk.PrePostCommands)
            && ContainsBoolean(deny.HasKillBeforeOperation, allow.HasKillBeforeOperation)
            && ContainsBoolean(deny.HasUninstallPrevious, allow.HasUninstallPrevious);

        private static bool ContainsSet<T>(IReadOnlyCollection<T> deny, IReadOnlyCollection<T> allow)
            where T : notnull =>
            deny.Count == 0 || (allow.Count > 0 && allow.All(deny.Contains));

        private static bool ContainsPackageIdentifiers(
            PolicyEditorDraftMatch deny,
            PolicyEditorDraftMatch allow) =>
            deny.PackageIdentifierMode == PackageIdentifierMode.Omitted
            || (deny.PackageIdentifierMode == PackageIdentifierMode.Exact
                && allow.PackageIdentifierMode == PackageIdentifierMode.Exact
                && ContainsSet(deny.ExactPackageIdentifiers, allow.ExactPackageIdentifiers));

        private static bool ContainsVersions(
            PolicyEditorDraftMatch deny,
            PolicyEditorDraftMatch allow) =>
            deny.VersionMode == PackageVersionMode.Omitted
            || (deny.VersionMode == PackageVersionMode.Exact
                && allow.VersionMode == PackageVersionMode.Exact
                && ContainsSet(deny.ExactVersions, allow.ExactVersions));

        private static bool ContainsBoolean(TriState deny, TriState allow) =>
            deny == TriState.Omitted || deny == allow;

        private static bool ContainsBooleanExceptRisk(
            TriState deny,
            TriState allow,
            PolicyEditorRisk actualRisk,
            PolicyEditorRisk testedRisk) =>
            actualRisk == testedRisk || ContainsBoolean(deny, allow);

        private static bool TryGetRisk(
            PolicyValidationFinding finding,
            out PolicyEditorRisk risk)
        {
            risk = default;
            if (finding.Arguments is null
                || !finding.Arguments.TryGetValue("option", out string? option))
                return false;

            return option.Trim().Trim('"') switch
            {
                "SkipHashCheck" => SetRisk(PolicyEditorRisk.SkipHashCheck, out risk),
                "AllowCustomParameters" => SetRisk(PolicyEditorRisk.CustomParameters, out risk),
                "AllowCustomInstallLocation" => SetRisk(PolicyEditorRisk.CustomInstallLocation, out risk),
                "AllowPrePostCommands" => SetRisk(PolicyEditorRisk.PrePostCommands, out risk),
                _ => false,
            };
        }

        private static bool TryGetRuleIndex(
            PolicyValidationFinding finding,
            IReadOnlyList<PolicyEditorDraftRule> rules,
            out int index)
        {
            index = rules
                .Select((rule, candidate) => (rule, candidate))
                .Where(item => string.Equals(item.rule.Id, finding.RuleId, StringComparison.Ordinal))
                .Select(item => item.candidate)
                .DefaultIfEmpty(-1)
                .First();
            return index >= 0;
        }

        private static bool SetRisk(PolicyEditorRisk value, out PolicyEditorRisk risk)
        {
            risk = value;
            return true;
        }
    }

    private static bool IsBroadlyScoped(PolicyEditorDraftRule rule) =>
        rule.Match.SourceNames.Count == 0
        && rule.Match.PackageIdentifierMode == PackageIdentifierMode.Omitted;

    private static bool IsUniversalPattern(string value) =>
        value.Trim() is "*" or "**";
}
