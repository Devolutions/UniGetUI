using System.Text;
using System.Text.Json;
using Devolutions.Now.Policy.Api;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.AgentBroker.PolicyManagement;

namespace UniGetUI.Avalonia.ViewModels.Pages.SettingsPages.PolicyEditor;

/// <summary>
/// A single finding reported by the external (Agent-side) semantic validator, or synthesized locally
/// by <see cref="PolicyEditorRawSyntax"/> for structural/contract failures. <see cref="Pointer"/> is a
/// JSON Pointer (RFC 6901, e.g. <c>/rules/0/match/versions/1</c>) into the raw JSON that was
/// validated; <see cref="RuleId"/> is populated when the finding can be attributed to a specific rule,
/// even if its exact position in the document has since changed.
/// </summary>
public sealed record PolicyValidationFinding(
    string Pointer,
    string? RuleId,
    PolicyValidationSeverity Severity,
    string Message,
    PolicyFindingCode? Code = null,
    IReadOnlyDictionary<string, string>? Arguments = null)
{
    public static PolicyValidationFinding FromShared(PolicyFinding finding) =>
        CreateBounded(new(
            finding.Path ?? "",
            finding.RuleId,
            MapSeverity(finding.Severity),
            PolicyFindingPresentation.Describe(finding.Code, finding.Arguments, finding.Message),
            finding.Code,
            PolicyFindingPresentation.CopyArguments(finding.Arguments)));

    public static PolicyValidationFinding FromSanitized(BrokerPolicySanitizedFinding finding) =>
        CreateBounded(new(
            finding.Path ?? "",
            finding.RuleId,
            MapSeverity(finding.Severity),
            PolicyFindingPresentation.Describe(finding.Code, finding.Arguments, finding.Message),
            finding.Code,
            PolicyFindingPresentation.CopyArguments(finding.Arguments)));

    public static PolicyValidationFinding CreateBounded(PolicyValidationFinding finding) => finding with
    {
        Pointer = PolicyFindingPresentation.SanitizeAgentText(
            finding.Pointer,
            BrokerPolicyManagementLimits.MaxSanitizedTextLength),
        RuleId = string.IsNullOrEmpty(finding.RuleId)
            ? null
            : PolicyFindingPresentation.SanitizeAgentText(
                finding.RuleId,
                BrokerPolicyManagementLimits.MaxSanitizedTextLength),
        Message = PolicyFindingPresentation.SanitizeAgentText(
            finding.Message,
            BrokerPolicyManagementLimits.MaxSanitizedTextLength),
        Arguments = finding.Arguments is null
            ? null
            : PolicyFindingPresentation.CopyArguments(finding.Arguments),
    };

    public string SeverityText => CoreTools.Translate(Severity.ToString());

    public bool IsError => Severity == PolicyValidationSeverity.Error;

    public bool IsWarning => Severity == PolicyValidationSeverity.Warning;

    public string AutomationName => string.IsNullOrWhiteSpace(Pointer)
        ? Message
        : CoreTools.Translate("{0}. Location: {1}", Message, Pointer);

    private static PolicyValidationSeverity MapSeverity(PolicyFindingSeverity severity) =>
        severity switch
        {
            PolicyFindingSeverity.Warning => PolicyValidationSeverity.Warning,
            PolicyFindingSeverity.Error => PolicyValidationSeverity.Error,
            _ => throw new ArgumentOutOfRangeException(nameof(severity), severity, null),
        };
}

/// <summary>
/// Converts stable Agent finding codes and structured arguments into localized UI text.
/// Recognized codes never render the Agent's English fallback message.
/// </summary>
public static class PolicyFindingPresentation
{
    private const int MaxArgumentEntries =
        BrokerPolicyManagementLimits.MaxSanitizedArgumentEntries;
    private const int MaxArgumentLength =
        BrokerPolicyManagementLimits.MaxSanitizedArgumentValueLength;
    private const int MaxFallbackLength =
        BrokerPolicyManagementLimits.MaxSanitizedTextLength;

    public static string Describe(
        PolicyFindingCode code,
        IReadOnlyDictionary<string, JsonElement>? arguments,
        string? fallbackMessage)
    {
        IReadOnlyDictionary<string, string> copied = CopyArguments(arguments);
        return Describe(code, copied, fallbackMessage);
    }

    public static string Describe(
        PolicyFindingCode code,
        IReadOnlyDictionary<string, string>? arguments,
        string? fallbackMessage) => code switch
        {
            PolicyFindingCode.SchemaViolation =>
                CoreTools.Translate("The policy draft does not match the required JSON schema."),
            PolicyFindingCode.UnknownField =>
                CoreTools.Translate("The policy draft contains an unknown field."),
            PolicyFindingCode.MissingRequiredField =>
                CoreTools.Translate("The policy draft is missing a required field."),
            PolicyFindingCode.InvalidFieldType =>
                CoreTools.Translate("A policy field has the wrong value type."),
            PolicyFindingCode.InvalidFieldValue =>
                CoreTools.Translate("A policy field has an invalid value."),
            PolicyFindingCode.DuplicateRuleId =>
                CoreTools.Translate("Rule IDs must be unique."),
            PolicyFindingCode.IneffectiveBooleanMatch =>
                CoreTools.Translate("A boolean match must be omitted, true, or false; mixed arrays are invalid."),
            PolicyFindingCode.InvalidVersionRange =>
                CoreTools.Translate("The version range is invalid."),
            PolicyFindingCode.EmptyVersionRange =>
                CoreTools.Translate("The version range does not restrict any versions."),
            PolicyFindingCode.InvalidWildcardPattern =>
                CoreTools.Translate("A wildcard pattern is invalid."),
            PolicyFindingCode.ContradictoryConstraints =>
                CoreTools.Translate("The rule contains contradictory constraints."),
            PolicyFindingCode.InvalidValidityInterval =>
                CoreTools.Translate("The policy validity interval is invalid."),
            PolicyFindingCode.UnsupportedSchema =>
                CoreTools.Translate("The policy schema is unsupported."),
            PolicyFindingCode.UnsupportedPolicyType =>
                CoreTools.Translate("The policy type is unsupported."),
            PolicyFindingCode.UnsupportedPolicyVersion =>
                CoreTools.Translate("The policy version is unsupported."),
            PolicyFindingCode.AuditModeEnabled =>
                CoreTools.Translate("Audit mode is enabled; decisions are logged but not enforced."),
            PolicyFindingCode.DefaultAllow =>
                CoreTools.Translate("The default decision is Allow; requests matching no rule are permitted."),
            PolicyFindingCode.SensitiveOptionAllowed =>
                DescribeSensitiveOption(arguments),
            _ => SanitizeFallback(fallbackMessage),
        };

    public static IReadOnlyDictionary<string, string> CopyArguments(
        IReadOnlyDictionary<string, JsonElement>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
            return new Dictionary<string, string>();

        var copied = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, JsonElement> argument in arguments
                     .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                     .Take(MaxArgumentEntries))
        {
            string key = Sanitize(argument.Key, MaxArgumentLength);
            string value;
            try
            {
                value = argument.Value.GetRawText();
            }
            catch (InvalidOperationException)
            {
                value = "";
            }

            copied[key] = Sanitize(value, MaxArgumentLength);
        }

        return copied;
    }

    public static IReadOnlyDictionary<string, string> CopyArguments(
        IReadOnlyDictionary<string, string>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
            return new Dictionary<string, string>();

        var copied = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> argument in arguments
                     .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                     .Take(MaxArgumentEntries))
        {
            copied[Sanitize(argument.Key, MaxArgumentLength)] =
                Sanitize(argument.Value, MaxArgumentLength);
        }

        return copied;
    }

    private static string DescribeSensitiveOption(IReadOnlyDictionary<string, string>? arguments)
    {
        string? option = ReadJsonString(arguments, "Option");
        string description = option switch
        {
            "SkipHashCheck" => CoreTools.Translate("An enabled Allow rule permits skipping package hash verification."),
            "PreRelease" => CoreTools.Translate("An enabled Allow rule permits prerelease package versions."),
            "AllowCustomInstallLocation" => CoreTools.Translate("An enabled Allow rule permits custom install locations."),
            "AllowCustomParameters" => CoreTools.Translate("An enabled Allow rule permits custom command-line parameters."),
            "AllowPrePostCommands" => CoreTools.Translate("An enabled Allow rule permits pre-operation or post-operation commands."),
            "AllowKillBeforeOperation" => CoreTools.Translate("An enabled Allow rule permits killing processes before an operation."),
            "AllowUninstallPrevious" => CoreTools.Translate("An enabled Allow rule permits uninstalling a previous version."),
            _ => CoreTools.Translate("An enabled Allow rule permits a sensitive option."),
        };

        string[] restrictions =
        [
            FormatRestriction(arguments, "AllowedInstallLocationPatterns", "Allowed install location patterns"),
            FormatRestriction(arguments, "AllowedCustomParameters", "Allowed custom parameters"),
            FormatRestriction(arguments, "AllowedCustomParameterPatterns", "Allowed custom parameter patterns"),
            FormatRestriction(arguments, "DeniedCustomParameters", "Denied custom parameters"),
        ];
        string restrictionText = string.Join(
            "; ",
            restrictions.Where(value => !string.IsNullOrEmpty(value)));
        return restrictionText.Length == 0
            ? description
            : $"{description} {CoreTools.Translate("Restrictions: {0}", restrictionText)}";
    }

    private static string FormatRestriction(
        IReadOnlyDictionary<string, string>? arguments,
        string key,
        string label)
    {
        if (arguments is null
            || !arguments.TryGetValue(key, out string? value)
            || string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        return $"{CoreTools.Translate(label)}: {Sanitize(value, MaxArgumentLength)}";
    }

    private static string? ReadJsonString(
        IReadOnlyDictionary<string, string>? arguments,
        string key)
    {
        if (arguments is null || !arguments.TryGetValue(key, out string? raw))
            return null;

        try
        {
            using JsonDocument document = JsonDocument.Parse(raw);
            return document.RootElement.ValueKind == JsonValueKind.String
                ? Sanitize(document.RootElement.GetString() ?? "", MaxArgumentLength)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string SanitizeFallback(string? message)
    {
        string sanitized = Sanitize(message ?? "", MaxFallbackLength);
        return string.IsNullOrWhiteSpace(sanitized)
            ? CoreTools.Translate("Devolutions Agent reported an unrecognized policy finding.")
            : sanitized;
    }

    public static string SanitizeAgentText(string? value, int maxLength) =>
        Sanitize(value ?? "", maxLength);

    private static string Sanitize(string value, int maxLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxLength);

        var result = new StringBuilder(Math.Min(value.Length, maxLength));
        int scalarCount = 0;
        foreach (Rune rune in value.EnumerateRunes())
        {
            if (Rune.IsControl(rune))
                continue;
            if (scalarCount == maxLength)
                break;

            result.Append(rune);
            scalarCount++;
        }

        return result.ToString();
    }
}

/// <summary>
/// Indexes a flat list of <see cref="PolicyValidationFinding"/> for quick lookup by JSON Pointer or by
/// rule ID, so the UI can highlight the right field/rule without re-scanning the whole finding list on
/// every render.
/// </summary>
public sealed class PolicyEditorFindingIndex
{
    public const int MaxDisplayedFindings =
        BrokerPolicyManagementLimits.MaxSanitizedFindings;

    private static readonly IReadOnlyList<PolicyValidationFinding> Empty = [];

    public IReadOnlyList<PolicyValidationFinding> All { get; }
    public bool FindingsTruncated { get; }
    public int OmittedFindingCount { get; }

    private readonly IReadOnlyDictionary<string, IReadOnlyList<PolicyValidationFinding>> _byPointer;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<PolicyValidationFinding>> _byRuleId;

    private PolicyEditorFindingIndex(
        IReadOnlyList<PolicyValidationFinding> all,
        IReadOnlyDictionary<string, IReadOnlyList<PolicyValidationFinding>> byPointer,
        IReadOnlyDictionary<string, IReadOnlyList<PolicyValidationFinding>> byRuleId,
        int omittedFindingCount)
    {
        All = all;
        _byPointer = byPointer;
        _byRuleId = byRuleId;
        OmittedFindingCount = omittedFindingCount;
        FindingsTruncated = omittedFindingCount > 0;
    }

    public static PolicyEditorFindingIndex Build(
        IReadOnlyList<PolicyValidationFinding> findings,
        int omittedFindingCount = 0)
    {
        ArgumentNullException.ThrowIfNull(findings);
        ArgumentOutOfRangeException.ThrowIfNegative(omittedFindingCount);

        int totalOmitted = omittedFindingCount;
        int retainedLimit = findings.Count + totalOmitted > MaxDisplayedFindings
            ? MaxDisplayedFindings - 1
            : MaxDisplayedFindings;
        if (findings.Count > retainedLimit)
        {
            totalOmitted += findings.Count - retainedLimit;
        }

        var all = new List<PolicyValidationFinding>(MaxDisplayedFindings);
        for (int index = 0; index < Math.Min(findings.Count, retainedLimit); index++)
        {
            all.Add(PolicyValidationFinding.CreateBounded(findings[index]));
        }

        if (totalOmitted > 0)
        {
            all.Add(new PolicyValidationFinding(
                "",
                null,
                PolicyValidationSeverity.Warning,
                CoreTools.Translate(
                    "{0} additional validation finding(s) were omitted.",
                    totalOmitted)));
        }

        Dictionary<string, IReadOnlyList<PolicyValidationFinding>> byPointer = all
            .GroupBy(finding => finding.Pointer, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                IReadOnlyList<PolicyValidationFinding> (group) => [.. group],
                StringComparer.Ordinal);

        Dictionary<string, IReadOnlyList<PolicyValidationFinding>> byRuleId = all
            .Where(finding => finding.RuleId is not null)
            .GroupBy(finding => finding.RuleId!, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                IReadOnlyList<PolicyValidationFinding> (group) => [.. group],
                StringComparer.Ordinal);

        return new PolicyEditorFindingIndex(all, byPointer, byRuleId, totalOmitted);
    }

    public IReadOnlyList<PolicyValidationFinding> ForPointer(string pointer) =>
        _byPointer.TryGetValue(pointer, out IReadOnlyList<PolicyValidationFinding>? findings) ? findings : Empty;

    public IReadOnlyList<PolicyValidationFinding> ForRule(string ruleId) =>
        _byRuleId.TryGetValue(ruleId, out IReadOnlyList<PolicyValidationFinding>? findings) ? findings : Empty;
}
