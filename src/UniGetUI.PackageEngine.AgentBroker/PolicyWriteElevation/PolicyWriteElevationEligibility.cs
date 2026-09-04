namespace UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation;

public enum PolicyWriteElevationEligibilityStatus
{
    Eligible,
    HelperMissing,
    ProtectedInstallRequired,
    InvalidInstallation,
    UnsupportedPlatform,
}

public readonly record struct PolicyWriteElevationEligibility(
    PolicyWriteElevationEligibilityStatus Status)
{
    public bool IsEligible => Status == PolicyWriteElevationEligibilityStatus.Eligible;

    public static PolicyWriteElevationEligibility Eligible =>
        new(PolicyWriteElevationEligibilityStatus.Eligible);
}

public interface IPolicyWriteElevationEligibility
{
    Task<PolicyWriteElevationEligibility> EvaluateAsync(CancellationToken cancellationToken);
}

public sealed class PackagedPolicyWriteElevationEligibility : IPolicyWriteElevationEligibility
{
#if WINDOWS
    private readonly IPolicyElevationPreflight _preflight;

    public PackagedPolicyWriteElevationEligibility()
        : this(new WindowsPolicyElevationPreflight())
    {
    }

    public PackagedPolicyWriteElevationEligibility(IPolicyElevationPreflight preflight)
    {
        _preflight = preflight;
    }
#endif

    public async Task<PolicyWriteElevationEligibility> EvaluateAsync(
        CancellationToken cancellationToken)
    {
#if WINDOWS
        cancellationToken.ThrowIfCancellationRequested();
        Task<PolicyWriteElevationEligibility> worker = Task.Run(
            () => EvaluateCore(cancellationToken),
            CancellationToken.None);
        return await worker.WaitAsync(cancellationToken).ConfigureAwait(false);
#else
        await Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();
        return new(PolicyWriteElevationEligibilityStatus.UnsupportedPlatform);
#endif
    }

#if WINDOWS
    private PolicyWriteElevationEligibility EvaluateCore(CancellationToken cancellationToken)
    {
        using PolicyElevationPreflightResult preflight = _preflight.Verify(cancellationToken);
        if (preflight.Succeeded)
            return PolicyWriteElevationEligibility.Eligible;

        PolicyWriteElevationEligibilityStatus status = preflight.Location.Failure switch
        {
            PolicyElevationHelperLocationFailure.HelperMissing =>
                PolicyWriteElevationEligibilityStatus.HelperMissing,
            PolicyElevationHelperLocationFailure.ProtectedInstallRequired =>
                PolicyWriteElevationEligibilityStatus.ProtectedInstallRequired,
            _ => PolicyWriteElevationEligibilityStatus.InvalidInstallation,
        };
        return new(status);
    }
#endif
}
