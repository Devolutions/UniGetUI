using System.Text;
using System.Text.RegularExpressions;
using Devolutions.Now.Policy.Api;
using Devolutions.Now.Policy.Client;
using Devolutions.Now.Policy.Model;
using UniGetUI.Core.Logging;
using ApiElevation = Devolutions.Now.Policy.Api.Elevation;
using PolicySchemaUris = Devolutions.Now.Policy.Model.SchemaUris;

namespace UniGetUI.PackageEngine.AgentBroker;

public enum BrokerPolicyInspectionStatus
{
    Connected,
    AgentUnavailable,
    Unsupported,
    AccessDenied,
    PolicyUnavailable,
    InvalidResponse,
    UnsupportedPlatform,
}

public sealed record BrokerPolicyInspectionResult(
    BrokerPolicyInspectionStatus Status,
    PolicyResponse? Response = null,
    string? CanonicalJson = null,
    string? ErrorMessage = null);

public interface IBrokerPolicyInspector
{
    Task<BrokerPolicyInspectionResult> InspectAsync(CancellationToken cancellationToken);
}

public sealed partial class BrokerPolicyInspector : IBrokerPolicyInspector
{
    private readonly Func<BrokerClient> _clientFactory;
    private readonly Func<bool> _isWindows;

    public BrokerPolicyInspector()
        : this(
            CreateStandardClient,
            OperatingSystem.IsWindows)
    {
    }

    private static BrokerClient CreateStandardClient() =>
        BrokerClientFactory.Create(ApiElevation.Standard);

    public BrokerPolicyInspector(Func<BrokerClient> clientFactory, Func<bool>? isWindows = null)
    {
        _clientFactory = clientFactory;
        _isWindows = isWindows ?? OperatingSystem.IsWindows;
    }

    public async Task<BrokerPolicyInspectionResult> InspectAsync(CancellationToken cancellationToken)
    {
        if (!_isWindows())
        {
            return new(BrokerPolicyInspectionStatus.UnsupportedPlatform);
        }

        try
        {
            using BrokerClient client = _clientFactory();
            PolicyResponse response = await client.GetPolicy(cancellationToken).ConfigureAwait(false);
            if (!HasRequiredData(response))
            {
                Logger.Warn("[AgentBroker] Active policy response contained invalid required data.");
                return new(
                    BrokerPolicyInspectionStatus.InvalidResponse,
                    ErrorMessage: "The broker response contained invalid policy data.");
            }

            return new(
                BrokerPolicyInspectionStatus.Connected,
                response,
                PolicyJson.Serialize(response.Policy));
        }
        catch (BrokerClientException ex)
        {
            Logger.Warn($"[AgentBroker] Active policy inspection failed: {ex}");
            return new(MapFailure(ex), ErrorMessage: ex.BrokerError?.Message ?? ex.Message);
        }
    }

    private static bool HasRequiredData(PolicyResponse response)
    {
        PolicyDocument? policy = response.Policy;
        if (response.ResponseKind != BrokerApi.PolicyResponseKind
            || !IsResponseVersion(response.ResponseVersion)
            || response.Server is null
            || !IsRequiredString(response.Server.ServerVersion, 128)
            || !Enum.IsDefined(response.Server.Transport)
            || policy is null
            || policy.Schema != PolicySchemaUris.Policy
            || !IsSemanticVersion(policy.PolicyVersion)
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
                || !IsValidMatch(match))
            {
                return false;
            }

            if (!IsValidConstraints(rule.Constraints))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidMatch(PolicyMatch match)
    {
        return IsValidEnumList(match.Operations, 3)
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
    }

    private static bool IsValidConstraints(PolicyConstraints? constraints)
    {
        return constraints is null
            || (IsValidStringList(constraints.AllowedInstallLocationPatterns, 64, 256, false)
                && IsValidStringList(constraints.AllowedCustomParameters, 128, 512, false)
                && IsValidStringList(constraints.AllowedCustomParameterPatterns, 128, 512, false)
                && IsValidStringList(constraints.DeniedCustomParameters, 128, 512, false));
    }

    private static bool HasMatchCriterion(PolicyMatch match)
    {
        return match.VersionRange is not null
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
    }

    private static bool IsValidEnumList<T>(IReadOnlyCollection<T>? values, int maxItems)
        where T : struct, Enum
    {
        return values is not null
            && values.Count <= maxItems
            && values.All(Enum.IsDefined)
            && values.Distinct().Count() == values.Count;
    }

    private static bool IsValidBooleanList(IReadOnlyCollection<bool>? values)
    {
        return values is not null
            && values.Count <= 2
            && values.Distinct().Count() == values.Count;
    }

    private static bool IsValidStringList(
        IReadOnlyCollection<string>? values,
        int maxItems,
        int maxLength,
        bool requireUnique = true)
    {
        return values is not null
            && values.Count <= maxItems
            && values.All(value => IsRequiredString(value, maxLength))
            && (!requireUnique
                || values.Distinct(StringComparer.Ordinal).Count() == values.Count);
    }

    private static bool IsValidVersionRange(VersionRange? range)
    {
        return range is null
            || (IsOptionalString(range.MinVersion, 128)
                && IsOptionalString(range.MaxVersion, 128));
    }

    private static bool IsOptionalString(string? value, int maxLength)
    {
        return value is null || IsRequiredString(value, maxLength);
    }

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

    private static bool IsSemanticVersion(string? value)
    {
        return value is { Length: > 0 and <= 128 }
            && SemanticVersionRegex().IsMatch(value);
    }

    private static bool IsHttpUrl(string? value)
    {
        return value is null
            || (HasMaximumLength(value, 2048)
                && Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
                && uri.Scheme is "http" or "https");
    }

    private static bool IsRequiredString(string? value, int maxLength)
    {
        if (value is null)
        {
            return false;
        }

        int length = value.EnumerateRunes().Take(maxLength + 1).Count();
        return length is > 0 && length <= maxLength;
    }

    private static bool HasMaximumLength(string? value, int maxLength)
    {
        return value is null
            || value.EnumerateRunes().Take(maxLength + 1).Count() <= maxLength;
    }

    [GeneratedRegex(
        @"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-((?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex SemanticVersionRegex();

    private static bool IsResponseVersion(string? value)
    {
        return !string.IsNullOrEmpty(value)
            && ResponseVersionRegex().IsMatch(value);
    }

    [GeneratedRegex(@"^[0-9]+\.[0-9]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex ResponseVersionRegex();

    private static BrokerPolicyInspectionStatus MapFailure(BrokerClientException ex)
    {
        if (ex.StatusCode == 404 || ex.BrokerError?.Code == ErrorCode.NotFound)
        {
            return BrokerPolicyInspectionStatus.Unsupported;
        }

        if (ex.StatusCode is 401 or 403
            || ex.BrokerError?.Code is ErrorCode.Unauthorized or ErrorCode.Forbidden)
        {
            return BrokerPolicyInspectionStatus.AccessDenied;
        }

        return ex.Kind switch
        {
            BrokerClientErrorKind.BrokerUnavailable or BrokerClientErrorKind.Timeout =>
                BrokerPolicyInspectionStatus.AgentUnavailable,
            BrokerClientErrorKind.EmptyResponse or BrokerClientErrorKind.InvalidResponse =>
                BrokerPolicyInspectionStatus.InvalidResponse,
            BrokerClientErrorKind.BrokerError =>
                BrokerPolicyInspectionStatus.PolicyUnavailable,
            _ => BrokerPolicyInspectionStatus.InvalidResponse,
        };
    }
}
