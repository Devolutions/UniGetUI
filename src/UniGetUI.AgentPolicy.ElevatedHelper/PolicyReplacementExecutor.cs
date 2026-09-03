using System.Text.Json;
using Devolutions.Now.Policy.Api;
using Devolutions.Now.Policy.Client;
using UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation;

namespace UniGetUI.AgentPolicy.ElevatedHelper;

/// <summary>
/// Turns the single broker replacement call into the bounded response frame contract.
/// </summary>
internal static class PolicyReplacementExecutor
{
    public static async Task<PolicyElevationResponseMessage> ExecuteAsync(
        PolicyElevationRequestMessage request,
        CancellationToken cancellationToken)
    {
        var response = new PolicyElevationResponseMessage
        {
            ProtocolVersion = PolicyElevationProtocol.Version,
            RequestId = request.RequestId,
        };

        try
        {
            using var client = CreateClient();

            PolicyReplacementResponse replacement = await client.ReplacePolicy(
                new PolicyReplacementRequest
                {
                    Draft = request.Draft,
                    Operation = (PolicyReplacementOperation)request.Operation,
                    ConflictHandling = (PolicyConflictHandling)request.ConflictHandling,
                    ExpectedStoreToken = request.ExpectedStoreToken,
                    ValidationReceipt = request.ValidationReceipt,
                    WarningsAcknowledged = request.WarningsAcknowledged,
                },
                cancellationToken).ConfigureAwait(false);

            response.Outcome = PolicyElevationResponseStatus.Replaced;
            response.Payload = SerializePayload(replacement);
            return response;
        }
        catch (BrokerClientException ex)
        {
            response.Outcome = ex.Kind switch
            {
                BrokerClientErrorKind.BrokerUnavailable => PolicyElevationResponseStatus.BrokerUnavailable,
                BrokerClientErrorKind.Timeout => PolicyElevationResponseStatus.BrokerUnavailable,
                BrokerClientErrorKind.EmptyResponse => PolicyElevationResponseStatus.BrokerInvalidResponse,
                BrokerClientErrorKind.InvalidResponse => PolicyElevationResponseStatus.BrokerInvalidResponse,
                _ => PolicyElevationResponseStatus.BrokerRejected,
            };

            response.BrokerStatusCode = ex.StatusCode;
            response.BrokerErrorCode = Truncate(
                ex.BrokerError?.Code.ToString() ?? ex.Kind.ToString(),
                PolicyElevationProtocol.MaxBrokerErrorCodeCharacters);
            response.Message = "The Agent rejected the policy write.";
            response.Payload = ex.BrokerError is null
                ? null
                : SerializePayload(ex.BrokerError);
            return response;
        }
        catch (OperationCanceledException)
        {
            response.Outcome = PolicyElevationResponseStatus.BrokerUnavailable;
            response.Message = "The broker did not answer before the elevated helper timed out.";
            return response;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException)
        {
            response.Outcome = PolicyElevationResponseStatus.BrokerInvalidResponse;
            response.Message = "The Agent returned an invalid policy response.";
            return response;
        }
    }

    public static PolicyElevationResponseMessage Rejected(string requestId, string reason)
        => new()
        {
            ProtocolVersion = PolicyElevationProtocol.Version,
            RequestId = requestId,
            Outcome = PolicyElevationResponseStatus.HelperRejected,
            Message = Truncate(reason, PolicyElevationProtocol.MaxMessageCharacters),
        };

    private static BrokerClient CreateClient()
        => new(new BrokerClientOptions
        {
            RequestedElevation = Elevation.Elevated,
            EffectiveUser = GetEffectiveUser(),
            ClientExecutablePath = Environment.ProcessPath,
            ClientVersion = typeof(PolicyReplacementExecutor).Assembly.GetName().Version?.ToString(),
        });

    private static string GetEffectiveUser()
        => string.IsNullOrWhiteSpace(Environment.UserDomainName)
            ? Environment.UserName
            : $"{Environment.UserDomainName}\\{Environment.UserName}";

    private static JsonElement SerializePayload<T>(T payload)
    {
        using JsonDocument document = JsonDocument.Parse(BrokerSerializer.Serialize(payload));
        return document.RootElement.Clone();
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
