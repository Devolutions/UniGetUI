using System.Text.Json;
using Devolutions.Now.Policy.Api;
using Devolutions.Now.Policy.Client;
using UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation;
using UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation.Interop;

namespace UniGetUI.AgentPolicy.ElevatedHelper;

/// <summary>
/// Turns the single broker replacement call into the bounded response frame contract.
/// </summary>
internal static class PolicyReplacementExecutor
{
    public static async Task<PolicyElevationResponseMessage> ExecuteAsync(
        PolicyElevationRequestMessage request,
        string effectiveUser,
        CancellationToken cancellationToken)
    {
        var response = new PolicyElevationResponseMessage
        {
            ProtocolVersion = PolicyElevationProtocol.Version,
            RequestId = request.RequestId,
        };

        try
        {
            using var client = new BrokerClient(CreateClientOptions(effectiveUser));

            PolicyReplacementResponse replacement =
                await PolicyElevationReplacementDispatcher.DispatchAsync(
                    request,
                    client.ReplacePolicy,
                    cancellationToken).ConfigureAwait(false);

            response.Disposition = PolicyElevationDisposition.Committed;
            response.CommittedStoreToken = replacement.Management.StoreToken;
            PolicyElevationFrame.ValidateResponse(response);
            return response;
        }
        catch (BrokerClientException ex)
        {
            response.Disposition = ex.Kind is
                BrokerClientErrorKind.BrokerUnavailable
                or BrokerClientErrorKind.Timeout
                or BrokerClientErrorKind.EmptyResponse
                or BrokerClientErrorKind.InvalidResponse
                    ? PolicyElevationDisposition.Unknown
                    : PolicyElevationDisposition.Rejected;
            response.BrokerStatusCode = ex.StatusCode;
            response.BrokerErrorCode = Truncate(
                ex.BrokerError?.Code.ToString() ?? ex.Kind.ToString(),
                PolicyElevationProtocol.MaxBrokerErrorCodeCharacters);
            if (response.Disposition == PolicyElevationDisposition.Rejected
                && ex.BrokerError is
                {
                    Code: ErrorCode.StalePolicyStoreToken,
                    Management: not null,
                } stale)
            {
                response.ConflictStoreToken = stale.Management.StoreToken;
                response.ConflictState = stale.Management.State switch
                {
                    PolicyManagementState.Active => PolicyElevationManagementState.Active,
                    PolicyManagementState.Missing => PolicyElevationManagementState.Missing,
                    PolicyManagementState.Invalid => PolicyElevationManagementState.Invalid,
                    _ => throw new InvalidDataException("The stale response carried an invalid management state."),
                };
                response.ConflictPolicyId = stale.Management.Policy?.Metadata.Id;
            }
            return response;
        }
        catch (OperationCanceledException)
        {
            response.Disposition = PolicyElevationDisposition.Unknown;
            response.BrokerErrorCode = BrokerClientErrorKind.Timeout.ToString();
            return response;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException)
        {
            response.Disposition = PolicyElevationDisposition.Unknown;
            response.BrokerErrorCode = BrokerClientErrorKind.InvalidResponse.ToString();
            return response;
        }
    }

    internal static BrokerClientOptions CreateClientOptions(string effectiveUser)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(effectiveUser);
        if (!WindowsProcessInspector.IsValidEffectiveUser(effectiveUser))
        {
            throw new ArgumentException("The effective user is not a bounded Windows account name.", nameof(effectiveUser));
        }

        return new BrokerClientOptions
        {
            RequestedElevation = Elevation.Elevated,
            EffectiveUser = effectiveUser,
            ClientExecutablePath = Environment.ProcessPath,
            ClientVersion = typeof(PolicyReplacementExecutor).Assembly.GetName().Version?.ToString(),
        };
    }

    private static string? Truncate(string? value, int maxCharacters)
    {
        if (value is null)
        {
            return null;
        }

        return value.Length <= maxCharacters ? value : value[..maxCharacters];
    }
}
