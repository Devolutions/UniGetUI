using System.Text.Json;
using System.Text.RegularExpressions;
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
            if (!TryGetCanonicalJson(response, out string canonicalJson))
            {
                Logger.Warn("[AgentBroker] Active policy response contained invalid required data.");
                return new(
                    BrokerPolicyInspectionStatus.InvalidResponse,
                    ErrorMessage: "The broker response contained invalid policy data.");
            }

            return new(
                BrokerPolicyInspectionStatus.Connected,
                response,
                canonicalJson);
        }
        catch (BrokerClientException ex)
        {
            Logger.Warn($"[AgentBroker] Active policy inspection failed: {ex}");
            return new(MapFailure(ex), ErrorMessage: ex.BrokerError?.Message ?? ex.Message);
        }
    }

    private static bool TryGetCanonicalJson(PolicyResponse response, out string canonicalJson)
    {
        if (response.ResponseKind != BrokerApi.PolicyResponseKind
            || !IsResponseVersion(response.ResponseVersion)
            || response.Server is null
            || !IsRequiredString(response.Server.ServerVersion, 128)
            || !Enum.IsDefined(response.Server.Transport)
            || response.Policy is null
            || response.Policy.Metadata is null
            || !IsResourceId(response.Policy.Metadata.Id)
            || !IsRequiredString(response.Policy.Metadata.Publisher, 128)
            || response.Policy.Rules is null
            || response.Policy.Rules.Count > 1024)
        {
            canonicalJson = "";
            return false;
        }

        foreach (PolicyRule? rule in response.Policy.Rules)
        {
            if (rule is null
                || !IsResourceId(rule.Id)
                || rule.Priority > int.MaxValue)
            {
                canonicalJson = "";
                return false;
            }
        }

        try
        {
            canonicalJson = PolicySerializer.Serialize(response.Policy);
            return true;
        }
        catch (JsonException ex)
        {
            Logger.Warn($"[AgentBroker] Active policy failed shared contract validation: {ex.Message}");
            canonicalJson = "";
            return false;
        }
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

    private static bool IsResponseVersion(string? value)
    {
        return !string.IsNullOrEmpty(value)
            && ResponseVersionRegex().IsMatch(value);
    }

    [GeneratedRegex(@"^[0-9]+\.[0-9]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex ResponseVersionRegex();

    private static BrokerPolicyInspectionStatus MapFailure(BrokerClientException ex)
    {
        if (ex.StatusCode == 404 && ex.BrokerError is null)
        {
            return BrokerPolicyInspectionStatus.Unsupported;
        }

        if (BrokerPolicyFailure.IsAccessDenied(ex))
        {
            return BrokerPolicyInspectionStatus.AccessDenied;
        }

        return ex.Kind switch
        {
            _ when BrokerPolicyFailure.IsTransportUnavailable(ex) =>
                BrokerPolicyInspectionStatus.AgentUnavailable,
            BrokerClientErrorKind.EmptyResponse or BrokerClientErrorKind.InvalidResponse =>
                BrokerPolicyInspectionStatus.InvalidResponse,
            BrokerClientErrorKind.BrokerError when ex.BrokerError is not null =>
                BrokerPolicyInspectionStatus.PolicyUnavailable,
            _ => BrokerPolicyInspectionStatus.InvalidResponse,
        };
    }
}
