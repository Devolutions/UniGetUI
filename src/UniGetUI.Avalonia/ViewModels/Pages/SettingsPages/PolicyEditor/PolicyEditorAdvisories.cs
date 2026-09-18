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

    public static string SkipHashCheck(PolicyEditorDraftRule rule) =>
        IsAllowWithConstraints(rule, out PolicyEditorDraftConstraints? limits)
        && limits.AllowSkipHashCheck
        && rule.Match.SkipHashCheck != TriState.False
            ? CoreTools.Translate("This rule permits bypassing package integrity checks.")
            : "";

    public static string CustomParameters(PolicyEditorDraftRule rule) =>
        IsBroadlyScoped(rule)
        && IsAllowWithConstraints(rule, out PolicyEditorDraftConstraints? limits)
        && limits.AllowCustomParameters
        && rule.Match.HasCustomParameters != TriState.False
        && limits.AllowedCustomParameters.Count == 0
        && limits.AllowedCustomParameterPatterns.Count == 0
            ? CoreTools.Translate("This Allow rule can permit arbitrary extra package-manager options because it does not limit package identifiers or sources.")
            : "";

    public static string CustomInstallLocation(PolicyEditorDraftRule rule) =>
        IsBroadlyScoped(rule)
        && IsAllowWithConstraints(rule, out PolicyEditorDraftConstraints? limits)
        && limits.AllowCustomInstallLocation
        && rule.Match.HasCustomInstallLocation != TriState.False
        && limits.AllowedInstallLocationPatterns.Count == 0
            ? CoreTools.Translate("This Allow rule can permit any custom install folder because it does not limit package identifiers or sources.")
            : "";

    public static string PrePostCommands(PolicyEditorDraftRule rule) =>
        IsBroadlyScoped(rule)
        && IsAllowWithConstraints(rule, out PolicyEditorDraftConstraints? limits)
        && limits.AllowPrePostCommands
        && rule.Match.HasPrePostCommands != TriState.False
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

    private static bool IsBroadlyScoped(PolicyEditorDraftRule rule) =>
        rule.Match.SourceNames.Count == 0
        && rule.Match.PackageIdentifierMode == PackageIdentifierMode.Omitted;

    private static bool IsUniversalPattern(string value) =>
        value.Trim() is "*" or "**";
}
