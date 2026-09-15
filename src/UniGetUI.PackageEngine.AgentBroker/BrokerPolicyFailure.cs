using Devolutions.Now.Policy.Api;
using Devolutions.Now.Policy.Client;

namespace UniGetUI.PackageEngine.AgentBroker;

internal static class BrokerPolicyFailure
{
    public static bool IsTransportUnavailable(BrokerClientException exception) =>
        exception.Kind is BrokerClientErrorKind.BrokerUnavailable or BrokerClientErrorKind.Timeout
        // Client 2026.9.3 reports named-pipe EOF (including a truncated body) as InvalidResponse
        // without a status. Errors parsing a received body always carry its HTTP status.
        || (exception.Kind is BrokerClientErrorKind.InvalidResponse or BrokerClientErrorKind.EmptyResponse
            && exception.StatusCode is null
            && exception.BrokerError is null);

    public static bool IsAccessDenied(BrokerClientException exception) =>
        exception.BrokerError is { } error
        && (exception.StatusCode is 401 or 403
            || error.Code is ErrorCode.Unauthorized or ErrorCode.Forbidden
                or ErrorCode.Unauthenticated or ErrorCode.AdministratorRequired);
}
