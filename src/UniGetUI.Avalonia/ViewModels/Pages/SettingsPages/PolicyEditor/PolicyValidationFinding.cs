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
    public static PolicyValidationFinding FromShared(PolicyFinding finding)
    {
        IReadOnlyDictionary<string, string> arguments =
            PolicyFindingPresentation.CopyArguments(finding.Arguments);
        return CreateBounded(new(
            finding.Path ?? "",
            finding.RuleId,
            MapSeverity(finding.Severity),
            PolicyFindingPresentation.Describe(
                finding.Code,
                arguments,
                finding.Message,
                finding.Path,
                finding.RuleId),
            finding.Code,
            arguments));
    }

    public static PolicyValidationFinding FromSanitized(BrokerPolicySanitizedFinding finding)
    {
        IReadOnlyDictionary<string, string> arguments =
            PolicyFindingPresentation.CopyArguments(finding.Arguments);
        return CreateBounded(new(
            finding.Path ?? "",
            finding.RuleId,
            MapSeverity(finding.Severity),
            PolicyFindingPresentation.Describe(
                finding.Code,
                arguments,
                finding.Message,
                finding.Path,
                finding.RuleId),
            finding.Code,
            arguments));
    }

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

    public string NavigationPointer =>
        PolicyFindingPresentation.GetStructuredNavigationPointer(
            Code,
            Arguments,
            Pointer);

    public string RawNavigationPointer =>
        PolicyFindingPresentation.GetRawNavigationPointer(
            Code,
            Arguments,
            Pointer);

    public string FriendlyLocation =>
        PolicyFindingPresentation.DescribeLocation(NavigationPointer, RuleId);

    public string ConfirmationMessage =>
        Code == PolicyFindingCode.SensitiveOptionAllowed
        && PolicyFindingPresentation.HasKnownSensitiveOption(Arguments)
            ? Message
            : CoreTools.Translate("{0}: {1}", FriendlyLocation, Message);

    public bool HasRawPointer => !string.IsNullOrWhiteSpace(Pointer);

    public string AutomationName => HasRawPointer
        ? CoreTools.Translate(
            "{0}. Location: {1}. JSON pointer: {2}",
            Message,
            FriendlyLocation,
            Pointer)
        : CoreTools.Translate("{0}. Location: {1}", Message, FriendlyLocation);

    public bool TargetsPointer(string pointer)
    {
        if (string.IsNullOrEmpty(pointer) || string.IsNullOrEmpty(Pointer))
            return false;

        return NavigationPointer.Equals(pointer, StringComparison.OrdinalIgnoreCase)
            || (NavigationPointer.StartsWith(pointer, StringComparison.OrdinalIgnoreCase)
                && NavigationPointer.Length > pointer.Length
                && NavigationPointer[pointer.Length] == '/');
    }

    private static PolicyValidationSeverity MapSeverity(PolicyFindingSeverity severity) =>
        severity switch
        {
            PolicyFindingSeverity.Warning => PolicyValidationSeverity.Warning,
            PolicyFindingSeverity.Error => PolicyValidationSeverity.Error,
            _ => throw new ArgumentOutOfRangeException(nameof(severity), severity, null),
        };
}

/// <summary>
/// Converts stable Agent finding codes and structured arguments into localized UI text. Generic codes
/// retain a bounded, sanitized Agent detail because that is where value/constraint specifics live.
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
        string? fallbackMessage,
        string? pointer = null,
        string? ruleId = null) => code switch
        {
            PolicyFindingCode.SchemaViolation =>
                CoreTools.Translate("The policy draft does not match the required JSON schema."),
            PolicyFindingCode.UnknownField =>
                DescribeWithSpecificDetail(
                    CoreTools.Translate("The policy draft contains an unknown field."),
                    fallbackMessage),
            PolicyFindingCode.MissingRequiredField =>
                DescribeWithSpecificDetail(
                    CoreTools.Translate("The policy draft is missing a required field."),
                    fallbackMessage),
            PolicyFindingCode.InvalidFieldType =>
                DescribeWithSpecificDetail(
                    CoreTools.Translate("A policy field has the wrong value type."),
                    fallbackMessage),
            PolicyFindingCode.InvalidFieldValue =>
                DescribeWithSpecificDetail(
                    CoreTools.Translate("A policy field has an invalid value."),
                    fallbackMessage),
            PolicyFindingCode.DuplicateRuleId =>
                CoreTools.Translate("Rule IDs must be unique."),
            PolicyFindingCode.InvalidVersionRange =>
                DescribeWithSpecificDetail(
                    CoreTools.Translate("The version range is invalid."),
                    fallbackMessage),
            PolicyFindingCode.EmptyVersionRange =>
                CoreTools.Translate("The version range does not restrict any versions."),
            PolicyFindingCode.InvalidWildcardPattern =>
                CoreTools.Translate("A wildcard pattern is invalid."),
            PolicyFindingCode.ContradictoryConstraints =>
                CoreTools.Translate("The rule contains contradictory constraints."),
            PolicyFindingCode.InvalidValidityInterval =>
                DescribeWithSpecificDetail(
                    CoreTools.Translate("The policy validity interval is invalid."),
                    fallbackMessage),
            PolicyFindingCode.UnsupportedPolicyFormatVersion =>
                DescribeWithSpecificDetail(
                    CoreTools.Translate("The policy format version is unsupported."),
                    fallbackMessage),
            PolicyFindingCode.AuditModeEnabled =>
                CoreTools.Translate("Audit mode is enabled; decisions are logged but not enforced."),
            PolicyFindingCode.DefaultAllow =>
                CoreTools.Translate("The default decision is Allow; requests matching no rule are permitted."),
            PolicyFindingCode.SensitiveOptionAllowed =>
                DescribeSensitiveOption(arguments, pointer, ruleId, fallbackMessage),
            _ => SanitizeFallback(fallbackMessage),
        };

    public static string DescribeLocation(string? pointer, string? ruleId)
    {
        string sanitizedPointer = Sanitize(pointer ?? "", MaxFallbackLength);
        string sanitizedRuleId = Sanitize(ruleId ?? "", MaxArgumentLength);
        if (string.IsNullOrWhiteSpace(sanitizedPointer))
            return CoreTools.Translate("Policy document");

        string[] segments = sanitizedPointer
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(DecodePointerSegment)
            .ToArray();
        if (segments.Length == 2
            && segments[0].Equals("Metadata", StringComparison.OrdinalIgnoreCase)
            && segments[1].Equals("Id", StringComparison.OrdinalIgnoreCase))
        {
            return CoreTools.Translate("Policy ID");
        }

        var parts = new List<string>(3);
        int index = 0;
        bool isRule = false;
        if (segments.Length >= 2
            && segments[0].Equals("Rules", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(segments[1], out int ruleIndex))
        {
            isRule = true;
            parts.Add(string.IsNullOrWhiteSpace(sanitizedRuleId)
                ? CoreTools.Translate("Rule: {0}", ruleIndex + 1)
                : CoreTools.Translate("Rule: {0}", $"'{sanitizedRuleId}'"));
            index = 2;
        }

        for (; index < segments.Length; index++)
        {
            string segment = segments[index];
            if (int.TryParse(segment, out int itemIndex))
            {
                parts.Add(CoreTools.Translate("Item {0}", itemIndex + 1));
                continue;
            }

            string? label = isRule
                && index == 2
                && segment.Equals("Id", StringComparison.OrdinalIgnoreCase)
                    ? CoreTools.Translate("Rule ID")
                    : FieldLabel(segment);
            if (label is not null
                && (parts.Count == 0 || !parts[^1].Equals(label, StringComparison.Ordinal)))
            {
                parts.Add(label);
            }
        }

        return parts.Count == 0
            ? CoreTools.Translate("Policy document")
            : string.Join(" \u00b7 ", parts);
    }

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

    public static string GetStructuredNavigationPointer(
        PolicyFindingCode? code,
        IReadOnlyDictionary<string, string>? arguments,
        string pointer)
    {
        if (code != PolicyFindingCode.SensitiveOptionAllowed)
            return pointer;

        string? option = ReadJsonString(arguments, "option");
        string? rulePointer = GetRulePointer(pointer);
        if (rulePointer is null)
            return pointer;

        return option switch
        {
            "SkipHashCheck" => $"{rulePointer}/Constraints/AllowSkipHashCheck",
            "PreRelease" => $"{rulePointer}/Constraints/AllowPreRelease",
            "AllowCustomParameters" when HasRestriction(
                arguments,
                "allowedCustomParameters") =>
                $"{rulePointer}/Constraints/AllowedCustomParameters",
            "AllowCustomParameters" when HasRestriction(
                arguments,
                "allowedCustomParameterPatterns") =>
                $"{rulePointer}/Constraints/AllowedCustomParameterPatterns",
            "AllowCustomParameters" => $"{rulePointer}/Constraints/AllowCustomParameters",
            "AllowCustomInstallLocation" when HasRestriction(
                arguments,
                "allowedInstallLocationPatterns") =>
                $"{rulePointer}/Constraints/AllowedInstallLocationPatterns",
            "AllowCustomInstallLocation" =>
                $"{rulePointer}/Constraints/AllowCustomInstallLocation",
            "AllowPrePostCommands" => $"{rulePointer}/Constraints/AllowPrePostCommands",
            "AllowKillBeforeOperation" => $"{rulePointer}/Constraints/AllowKillBeforeOperation",
            "AllowUninstallPrevious" => $"{rulePointer}/Constraints/AllowUninstallPrevious",
            _ => pointer,
        };
    }

    public static string GetRawNavigationPointer(
        PolicyFindingCode? code,
        IReadOnlyDictionary<string, string>? arguments,
        string pointer)
    {
        if (code != PolicyFindingCode.SensitiveOptionAllowed
            || pointer.Split('/', StringSplitOptions.RemoveEmptyEntries).Length > 2)
        {
            return pointer;
        }

        return GetStructuredNavigationPointer(code, arguments, pointer);
    }

    public static bool HasKnownSensitiveOption(
        IReadOnlyDictionary<string, string>? arguments) =>
        ReadJsonString(arguments, "option") is
            "SkipHashCheck"
            or "PreRelease"
            or "AllowCustomParameters"
            or "AllowCustomInstallLocation"
            or "AllowPrePostCommands"
            or "AllowKillBeforeOperation"
            or "AllowUninstallPrevious";

    private static string DescribeSensitiveOption(
        IReadOnlyDictionary<string, string>? arguments,
        string? pointer,
        string? ruleId,
        string? fallbackMessage)
    {
        string? option = ReadJsonString(arguments, "option");
        string rule = DescribeRule(pointer, ruleId);
        string description = option switch
        {
            "SkipHashCheck" => CoreTools.Translate(
                "{0} allows skipping hash verification.",
                rule),
            "PreRelease" => CoreTools.Translate(
                "{0} allows prerelease packages.",
                rule),
            "AllowCustomInstallLocation" when HasRestriction(
                arguments,
                "allowedInstallLocationPatterns") => CoreTools.Translate(
                    "{0} allows custom installation locations within configured approved paths.",
                    rule),
            "AllowCustomInstallLocation" => CoreTools.Translate(
                "{0} allows a custom installation location without approved paths.",
                rule),
            "AllowCustomParameters" when HasRestriction(
                arguments,
                "allowedCustomParameters")
                || HasRestriction(arguments, "allowedCustomParameterPatterns") =>
                CoreTools.Translate(
                    "{0} allows custom parameters subject to configured restrictions.",
                    rule),
            "AllowCustomParameters" => CoreTools.Translate(
                "{0} allows custom parameters without an allowlist.",
                rule),
            "AllowPrePostCommands" => CoreTools.Translate(
                "{0} allows pre/post commands.",
                rule),
            "AllowKillBeforeOperation" => CoreTools.Translate(
                "{0} allows stopping running applications.",
                rule),
            "AllowUninstallPrevious" => CoreTools.Translate(
                "{0} allows uninstalling the previous version.",
                rule),
            _ => DescribeWithSpecificDetail(
                CoreTools.Translate("{0} allows a sensitive option.", rule),
                fallbackMessage),
        };

        string[] restrictions =
        [
            FormatRestriction(arguments, "allowedInstallLocationPatterns", "Allowed install location patterns"),
            FormatRestriction(arguments, "allowedCustomParameters", "Allowed custom parameters"),
            FormatRestriction(arguments, "allowedCustomParameterPatterns", "Allowed custom parameter patterns"),
            FormatRestriction(arguments, "deniedCustomParameters", "Denied custom parameters"),
        ];
        string restrictionText = string.Join(
            "; ",
            restrictions.Where(value => !string.IsNullOrEmpty(value)));
        return restrictionText.Length == 0
            ? description
            : $"{description} {CoreTools.Translate("Restrictions: {0}", restrictionText)}";
    }

    private static string DescribeRule(string? pointer, string? ruleId)
    {
        string sanitizedRuleId = Sanitize(ruleId ?? "", MaxArgumentLength);
        if (!string.IsNullOrWhiteSpace(sanitizedRuleId))
            return CoreTools.Translate("Rule “{0}”", sanitizedRuleId);

        string? rulePointer = GetRulePointer(pointer);
        string[] segments = rulePointer?.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries) ?? [];
        return segments.Length == 2
            && int.TryParse(segments[1], out int index)
                ? CoreTools.Translate("Rule {0}", index + 1)
                : CoreTools.Translate("An enabled Allow rule");
    }

    private static string? GetRulePointer(string? pointer)
    {
        string[] segments = (pointer ?? "").Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 2
            && segments[0].Equals("Rules", StringComparison.Ordinal)
            && int.TryParse(segments[1], out _)
                ? $"/Rules/{segments[1]}"
                : null;
    }

    private static bool HasRestriction(
        IReadOnlyDictionary<string, string>? arguments,
        string key)
    {
        if (arguments is null
            || !arguments.TryGetValue(key, out string? raw)
            || string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(raw);
            return document.RootElement.ValueKind switch
            {
                JsonValueKind.Array => document.RootElement.GetArrayLength() > 0,
                JsonValueKind.String => !string.IsNullOrWhiteSpace(
                    document.RootElement.GetString()),
                _ => false,
            };
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string DescribeWithSpecificDetail(string summary, string? fallbackMessage)
    {
        string detail = Sanitize(fallbackMessage ?? "", MaxArgumentLength);
        if (string.IsNullOrWhiteSpace(detail)
            || detail.Equals(summary, StringComparison.OrdinalIgnoreCase))
        {
            return summary;
        }

        return CoreTools.Translate("{0} Detail: {1}", summary, detail);
    }

    private static string DecodePointerSegment(string segment) =>
        segment.Replace("~1", "/", StringComparison.Ordinal)
            .Replace("~0", "~", StringComparison.Ordinal);

    private static string? FieldLabel(string segment) =>
        segment.ToUpperInvariant() switch
        {
            "$SCHEMA" => CoreTools.Translate("Schema"),
            "POLICYFORMATVERSION" => CoreTools.Translate("Policy format version"),
            "METADATA" => CoreTools.Translate("Metadata"),
            "ID" => CoreTools.Translate("ID"),
            "PUBLISHER" => CoreTools.Translate("Publisher"),
            "DESCRIPTION" => CoreTools.Translate("Description"),
            "SUPPORTURL" => CoreTools.Translate("Support URL"),
            "VALIDFROM" => CoreTools.Translate("Valid from"),
            "VALIDUNTIL" => CoreTools.Translate("Valid until"),
            "ENFORCEMENT" => CoreTools.Translate("Enforcement"),
            "DEFAULTDECISION" => CoreTools.Translate("Default decision"),
            "AUDITMODE" => CoreTools.Translate("Audit mode"),
            "RULES" => null,
            "ENABLED" => CoreTools.Translate("Enabled"),
            "PRIORITY" => CoreTools.Translate("Priority"),
            "DECISION" => CoreTools.Translate("Decision"),
            "REASON" => CoreTools.Translate("Reason"),
            "MATCH" => CoreTools.Translate("Match criteria"),
            "OPERATIONS" => CoreTools.Translate("Operations"),
            "MANAGERS" => CoreTools.Translate("Package managers"),
            "SOURCENAMES" => CoreTools.Translate("Source names"),
            "PACKAGEIDENTIFIERS" => CoreTools.Translate("Package identifiers"),
            "EXACT" => CoreTools.Translate("Exact values"),
            "PATTERNS" => CoreTools.Translate("Patterns"),
            "VERSION" => CoreTools.Translate("Package version"),
            "RANGE" => CoreTools.Translate("Semantic version range"),
            "MINVERSION" => CoreTools.Translate("Minimum version"),
            "MAXVERSION" => CoreTools.Translate("Maximum version"),
            "INCLUDEPRERELEASE" => CoreTools.Translate("Include prerelease versions"),
            "SCOPES" => CoreTools.Translate("Scopes"),
            "ARCHITECTURES" => CoreTools.Translate("Architectures"),
            "EXECUTIONELEVATION" => CoreTools.Translate("Execution privilege"),
            "INTERACTIVE" => CoreTools.Translate("Interactive"),
            "SKIPHASHCHECK" => CoreTools.Translate("Skip hash check"),
            "PRERELEASE" => CoreTools.Translate("Prerelease"),
            "HASCUSTOMPARAMETERS" => CoreTools.Translate("Custom parameters"),
            "HASCUSTOMINSTALLLOCATION" => CoreTools.Translate("Custom install location"),
            "HASPREPOSTCOMMANDS" => CoreTools.Translate("Pre/post commands"),
            "HASKILLBEFOREOPERATION" => CoreTools.Translate("Stop running apps before operation"),
            "HASUNINSTALLPREVIOUS" => CoreTools.Translate("Uninstall previous version"),
            "CONSTRAINTS" => CoreTools.Translate("Additional safety limits"),
            "ALLOWSKIPHASHCHECK" => CoreTools.Translate("Skip hash verification"),
            "ALLOWPRERELEASE" => CoreTools.Translate("Prerelease packages"),
            "ALLOWCUSTOMPARAMETERS" => CoreTools.Translate("Allow custom parameters"),
            "ALLOWCUSTOMINSTALLLOCATION" => CoreTools.Translate("Allow custom installation location"),
            "ALLOWPREPOSTCOMMANDS" => CoreTools.Translate("Allow pre/post commands"),
            "ALLOWKILLBEFOREOPERATION" => CoreTools.Translate("Allow stopping running applications"),
            "ALLOWUNINSTALLPREVIOUS" => CoreTools.Translate("Allow uninstalling the previous version"),
            "ALLOWUPGRADE" => CoreTools.Translate("Allow upgrade"),
            _ => Humanize(segment),
        };

    private static string Humanize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var result = new StringBuilder(value.Length + 4);
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (index > 0 && char.IsUpper(character) && char.IsLower(value[index - 1]))
                result.Append(' ');
            result.Append(character);
        }

        return Sanitize(result.ToString(), MaxArgumentLength);
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
