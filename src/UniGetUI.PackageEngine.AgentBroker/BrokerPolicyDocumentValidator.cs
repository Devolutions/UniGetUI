using Devolutions.Now.Policy.Model;

namespace UniGetUI.PackageEngine.AgentBroker;

internal static class BrokerPolicyDocumentValidator
{
    public static bool HasRequiredData(PolicyDocument? policy)
    {
        if (policy is null
            || policy.PolicyFormatVersion is null
            || policy.PolicyType != "PackageBrokerPolicy"
            || policy.Metadata is null
            || !IsResourceId(policy.Metadata.Id)
            || !IsRequiredString(policy.Metadata.Publisher, 128)
            || policy.Metadata.Revision is 0 or > int.MaxValue
            || !HasMaximumLength(policy.Metadata.Description, 512)
            || !IsHttpUrl(policy.Metadata.SupportUrl)
            || policy.Enforcement is null
            || !Enum.IsDefined(policy.Enforcement.DefaultDecision)
            || policy.Enforcement.RulePrecedence != RulePrecedence.PriorityThenDeny
            || policy.Rules is null
            || policy.Rules.Count > 1024)
        {
            return false;
        }

        foreach (PolicyRule? rule in policy.Rules)
        {
            PolicyMatch? match = rule?.Match;
            if (rule is null
                || !IsResourceId(rule.Id)
                || rule.Priority > int.MaxValue
                || !Enum.IsDefined(rule.Decision)
                || !HasMaximumLength(rule.Reason, 512)
                || match is null
                || !IsValidMatch(match)
                || !IsValidConstraints(rule.Constraints))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidMatch(PolicyMatch match) =>
        IsValidEnumList(match.Operations, 3)
        && IsValidEnumList(match.Managers, 16)
        && IsValidStringList(match.Sources, 128, 256)
        && IsValidStringList(match.PackageIdentifiers, 1024, 256)
        && IsValidStringList(match.PackageNames, 1024, 256)
        && IsValidStringList(match.Versions, 256, 128)
        && IsValidEnumList(match.Scopes, 2)
        && IsValidEnumList(match.Architectures, 5)
        && IsValidEnumList(match.Elevation, 2)
        && IsValidBooleanList(match.Interactive)
        && IsValidBooleanList(match.SkipHashCheck)
        && IsValidBooleanList(match.PreRelease)
        && IsValidBooleanList(match.HasCustomParameters)
        && IsValidBooleanList(match.HasCustomInstallLocation)
        && IsValidBooleanList(match.HasPrePostCommands)
        && IsValidBooleanList(match.HasKillBeforeOperation)
        && IsValidBooleanList(match.HasUninstallPrevious)
        && IsValidVersionRange(match.VersionRange)
        && HasMatchCriterion(match);

    private static bool IsValidConstraints(PolicyConstraints? constraints) =>
        constraints is null
        || (IsValidStringList(constraints.AllowedInstallLocationPatterns, 64, 256, false)
            && IsValidStringList(constraints.AllowedCustomParameters, 128, 512, false)
            && IsValidStringList(constraints.AllowedCustomParameterPatterns, 128, 512, false)
            && IsValidStringList(constraints.DeniedCustomParameters, 128, 512, false));

    private static bool HasMatchCriterion(PolicyMatch match) =>
        match.VersionRange is not null
        || match.Operations.Count > 0
        || match.Managers.Count > 0
        || match.Sources.Count > 0
        || match.PackageIdentifiers.Count > 0
        || match.PackageNames.Count > 0
        || match.Versions.Count > 0
        || match.Scopes.Count > 0
        || match.Architectures.Count > 0
        || match.Elevation.Count > 0
        || match.Interactive.Count > 0
        || match.SkipHashCheck.Count > 0
        || match.PreRelease.Count > 0
        || match.HasCustomParameters.Count > 0
        || match.HasCustomInstallLocation.Count > 0
        || match.HasPrePostCommands.Count > 0
        || match.HasKillBeforeOperation.Count > 0
        || match.HasUninstallPrevious.Count > 0;

    private static bool IsValidEnumList<T>(IReadOnlyCollection<T>? values, int maxItems)
        where T : struct, Enum =>
        values is not null
        && values.Count <= maxItems
        && values.All(Enum.IsDefined)
        && values.Distinct().Count() == values.Count;

    private static bool IsValidBooleanList(IReadOnlyCollection<bool>? values) =>
        values is not null && values.Count <= 1;

    private static bool IsValidStringList(
        IReadOnlyCollection<string>? values,
        int maxItems,
        int maxLength,
        bool requireUnique = true) =>
        values is not null
        && values.Count <= maxItems
        && values.All(value => IsRequiredString(value, maxLength))
        && (!requireUnique
            || values.Distinct(StringComparer.Ordinal).Count() == values.Count);

    private static bool IsValidVersionRange(VersionRange? range) =>
        range is null
        || (IsOptionalString(range.MinVersion, 128)
            && IsOptionalString(range.MaxVersion, 128));

    private static bool IsOptionalString(string? value, int maxLength) =>
        value is null || IsRequiredString(value, maxLength);

    private static bool IsResourceId(string? value)
    {
        if (string.IsNullOrEmpty(value)
            || value.Length > 128
            || !char.IsAsciiLetterOrDigit(value[0]))
        {
            return false;
        }

        return value.AsSpan(1).ContainsAnyExcept(
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789._:-") is false;
    }

    private static bool IsHttpUrl(string? value) =>
        value is null
        || (HasMaximumLength(value, 2048)
            && (value.StartsWith("http://", StringComparison.Ordinal)
                || value.StartsWith("https://", StringComparison.Ordinal))
            && Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            && uri.Scheme is "http" or "https");

    private static bool IsRequiredString(string? value, int maxLength)
    {
        if (value is null)
            return false;

        int length = value.EnumerateRunes().Take(maxLength + 1).Count();
        return length is > 0 && length <= maxLength;
    }

    private static bool HasMaximumLength(string? value, int maxLength) =>
        value is null
        || value.EnumerateRunes().Take(maxLength + 1).Count() <= maxLength;
}
