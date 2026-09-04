using System.Text;
using System.Text.Json;
using Devolutions.Now.Policy.Api;

namespace UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation;

public static class PolicyElevationReplacementDispatcher
{
    public static int GetBrokerRequestBodyByteCount(PolicyElevationRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return GetBrokerRequestBodyByteCount(CreateReplacementRequest(
            request.Draft,
            request.Operation,
            request.ConflictHandling,
            request.ExpectedStoreToken,
            request.ValidationReceipt,
            request.WarningsAcknowledged));
    }

    public static bool IsBrokerRequestWithinLimit(PolicyElevationRequestMessage request) =>
        GetBrokerRequestBodyByteCount(request) <= BrokerApi.MaxPolicyManagementBodyBytes;

    public static Task<PolicyReplacementResponse> DispatchAsync(
        PolicyElevationRequestMessage request,
        Func<PolicyReplacementRequest, CancellationToken, Task<PolicyReplacementResponse>> replacePolicy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(replacePolicy);

        PolicyReplacementRequest replacementRequest = CreateReplacementRequest(
            request.Draft,
            request.Operation,
            request.ConflictHandling,
            request.ExpectedStoreToken,
            request.ValidationReceipt,
            request.WarningsAcknowledged);

        if (GetBrokerRequestBodyByteCount(replacementRequest) > BrokerApi.MaxPolicyManagementBodyBytes)
        {
            throw new InvalidDataException(
                "The serialized policy replacement request exceeds the broker request limit.");
        }

        return replacePolicy(replacementRequest, cancellationToken);
    }

    private static PolicyReplacementRequest CreateReplacementRequest(
        JsonElement draft,
        PolicyElevationOperation operation,
        PolicyElevationConflictHandling conflictHandling,
        string expectedStoreToken,
        string validationReceipt,
        bool warningsAcknowledged) =>
        new()
        {
            Draft = draft,
            Operation = operation switch
            {
                PolicyElevationOperation.Update => PolicyReplacementOperation.Update,
                PolicyElevationOperation.ReplaceIdentity => PolicyReplacementOperation.ReplaceIdentity,
                PolicyElevationOperation.Create => PolicyReplacementOperation.Create,
                PolicyElevationOperation.Repair => PolicyReplacementOperation.Repair,
                _ => throw new InvalidDataException(
                    $"Unsupported policy replacement operation '{operation}'."),
            },
            ConflictHandling = conflictHandling switch
            {
                PolicyElevationConflictHandling.Reject => PolicyConflictHandling.Reject,
                PolicyElevationConflictHandling.ConfirmOverwrite =>
                    PolicyConflictHandling.ConfirmOverwrite,
                _ => throw new InvalidDataException(
                    $"Unsupported policy conflict handling '{conflictHandling}'."),
            },
            ExpectedStoreToken = expectedStoreToken,
            ValidationReceipt = validationReceipt,
            WarningsAcknowledged = warningsAcknowledged,
        };

    private static int GetBrokerRequestBodyByteCount(PolicyReplacementRequest request) =>
        Encoding.UTF8.GetByteCount(BrokerSerializer.Serialize(request));
}
