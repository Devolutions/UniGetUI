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
        using PolicyElevationPreflightResult preflight =
            await PolicyElevationPreflightRunner
                .VerifyAsync(_preflight, cancellationToken)
                .ConfigureAwait(false);
        return Evaluate(preflight);
#else
        await Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();
        return new(PolicyWriteElevationEligibilityStatus.UnsupportedPlatform);
#endif
    }

#if WINDOWS
    private static PolicyWriteElevationEligibility Evaluate(
        PolicyElevationPreflightResult preflight)
    {
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
