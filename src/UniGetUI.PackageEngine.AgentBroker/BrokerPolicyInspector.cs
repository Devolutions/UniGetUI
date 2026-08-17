using Devolutions.Now.Policy.Api;
using Devolutions.Now.Policy.Client;
using Devolutions.Now.Policy.Model;
using UniGetUI.Core.Logging;
using ApiElevation = Devolutions.Now.Policy.Api.Elevation;

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

public sealed class BrokerPolicyInspector : IBrokerPolicyInspector
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
                Logger.Warn("[AgentBroker] Active policy response contained null required data.");
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
        if (response.ResponseKind is null
            || response.ResponseVersion is null
            || response.Server is null
            || response.Server.ServerVersion is null
            || policy is null
            || policy.Schema is null
            || policy.PolicyVersion is null
            || policy.PolicyType is null
            || policy.Metadata is null
            || policy.Metadata.Id is null
            || policy.Metadata.Publisher is null
            || policy.Enforcement is null
            || policy.Rules is null)
        {
            return false;
        }

        foreach (PolicyRule? rule in policy.Rules)
        {
            PolicyMatch? match = rule?.Match;
            if (rule?.Id is null
                || match is null
                || match.Operations is null
                || match.Managers is null
                || match.Sources is null
                || ContainsNull(match.Sources)
                || match.PackageIdentifiers is null
                || ContainsNull(match.PackageIdentifiers)
                || match.PackageNames is null
                || ContainsNull(match.PackageNames)
                || match.Versions is null
                || ContainsNull(match.Versions)
                || match.Scopes is null
                || match.Architectures is null
                || match.Elevation is null
                || match.Interactive is null
                || match.SkipHashCheck is null
                || match.PreRelease is null
                || match.HasCustomParameters is null
                || match.HasCustomInstallLocation is null
                || match.HasPrePostCommands is null
                || match.HasKillBeforeOperation is null
                || match.HasUninstallPrevious is null)
            {
                return false;
            }

            PolicyConstraints? constraints = rule!.Constraints;
            if (constraints is not null
                && (constraints.AllowedInstallLocationPatterns is null
                    || ContainsNull(constraints.AllowedInstallLocationPatterns)
                    || constraints.AllowedCustomParameters is null
                    || ContainsNull(constraints.AllowedCustomParameters)
                    || constraints.AllowedCustomParameterPatterns is null
                    || ContainsNull(constraints.AllowedCustomParameterPatterns)
                    || constraints.DeniedCustomParameters is null
                    || ContainsNull(constraints.DeniedCustomParameters)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ContainsNull(IEnumerable<string> values) =>
        values.Any(value => value is null);

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
