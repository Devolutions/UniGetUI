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

        if (rule.Decision == Decision.Allow
            && (rule.Match.PackageIdentifiers.Any(IsUniversalPattern)
                || rule.Match.Sources.Any(IsUniversalPattern)))
        {
            messages.Add(CoreTools.Translate(
                "This Allow rule uses a universal package or source pattern and may authorize requests far beyond the intended scope."));
        }

        if (rule.Decision != Decision.Allow || rule.Constraints is not { } limits)
            return [.. messages];

        if (limits.AllowSkipHashCheck)
            messages.Add(CoreTools.Translate("This rule permits bypassing package integrity checks."));
        bool broadScope = rule.Match.Managers.Count == 0
            && rule.Match.Sources.Count == 0
            && rule.Match.PackageIdentifiers.Count == 0;
        if (limits.AllowPrePostCommands && broadScope)
            messages.Add(CoreTools.Translate("This broadly scoped rule permits arbitrary commands before or after package operations."));
        if (limits.AllowCustomParameters
            && limits.AllowedCustomParameters.Count == 0
            && limits.AllowedCustomParameterPatterns.Count == 0
            && broadScope)
        {
            messages.Add(CoreTools.Translate("This broadly scoped rule permits arbitrary extra package-manager options unless explicitly denied."));
        }
        if (limits.AllowCustomInstallLocation
            && limits.AllowedInstallLocationPatterns.Count == 0
            && broadScope)
        {
            messages.Add(CoreTools.Translate("This broadly scoped rule permits any custom install folder."));
        }

        return [.. messages];
    }

    private static bool IsUniversalPattern(string value) =>
        value.Trim() is "*" or "**";
}
