using Devolutions.Now.Policy.Api;

namespace UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation;

public static class PolicyElevationReplacementDispatcher
{
    public static Task<PolicyReplacementResponse> DispatchAsync(
        PolicyElevationRequestMessage request,
        Func<PolicyReplacementRequest, CancellationToken, Task<PolicyReplacementResponse>> replacePolicy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(replacePolicy);

        var replacementRequest = new PolicyReplacementRequest
        {
            Draft = request.Draft,
            Operation = request.Operation switch
            {
                PolicyElevationOperation.Update => PolicyReplacementOperation.Update,
                PolicyElevationOperation.ReplaceIdentity => PolicyReplacementOperation.ReplaceIdentity,
                PolicyElevationOperation.Create => PolicyReplacementOperation.Create,
                PolicyElevationOperation.Repair => PolicyReplacementOperation.Repair,
                _ => throw new InvalidDataException(
                    $"Unsupported policy replacement operation '{request.Operation}'."),
            },
            ConflictHandling = request.ConflictHandling switch
            {
                PolicyElevationConflictHandling.Reject => PolicyConflictHandling.Reject,
                PolicyElevationConflictHandling.ConfirmOverwrite =>
                    PolicyConflictHandling.ConfirmOverwrite,
                _ => throw new InvalidDataException(
                    $"Unsupported policy conflict handling '{request.ConflictHandling}'."),
            },
            ExpectedStoreToken = request.ExpectedStoreToken,
            ValidationReceipt = request.ValidationReceipt,
            WarningsAcknowledged = request.WarningsAcknowledged,
        };

        return replacePolicy(replacementRequest, cancellationToken);
    }
}
