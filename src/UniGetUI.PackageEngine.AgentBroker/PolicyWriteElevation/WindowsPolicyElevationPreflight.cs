#if WINDOWS
using UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation.Interop;

namespace UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation;

public enum PolicyElevationPreflightFailureKind
{
    None,
    HelperUnavailable,
    RunningHostMismatch,
    SignerBindingFailed,
}

public sealed class PolicyElevationPreflightResult : IDisposable
{
    private PolicyElevationLocationVerification? _verification;

    private PolicyElevationPreflightResult(
        bool succeeded,
        PolicyElevationHelperLocation location,
        PolicyElevationPreflightFailureKind failure,
        string? failureReason,
        string? detail,
        int? win32ErrorCode)
    {
        Succeeded = succeeded;
        Location = location;
        Failure = failure;
        FailureReason = failureReason;
        Detail = detail;
        Win32ErrorCode = win32ErrorCode;
        _verification = location.Verification;
    }

    public bool Succeeded { get; }
    public PolicyElevationHelperLocation Location { get; }
    public PolicyElevationPreflightFailureKind Failure { get; }
    public string? FailureReason { get; }
    public string? Detail { get; }
    public int? Win32ErrorCode { get; }

    public static PolicyElevationPreflightResult Success(PolicyElevationHelperLocation location) =>
        new(true, location, PolicyElevationPreflightFailureKind.None, null, null, null);

    public static PolicyElevationPreflightResult Rejected(
        PolicyElevationHelperLocation location,
        PolicyElevationPreflightFailureKind failure,
        string? failureReason,
        string? detail = null,
        int? win32ErrorCode = null) =>
        new(false, location, failure, failureReason, detail, win32ErrorCode);

    public void Dispose() =>
        Interlocked.Exchange(ref _verification, null)?.Dispose();
}

public interface IPolicyElevationPreflight
{
    PolicyElevationPreflightResult Verify(CancellationToken cancellationToken);
}

public sealed class WindowsPolicyElevationPreflight : IPolicyElevationPreflight
{
    private readonly IPolicyElevationHelperLocator _locator;
    private readonly IPolicyElevationTrustVerifier _trustVerifier;
    private readonly Func<string?> _selfImagePathProvider;

    public WindowsPolicyElevationPreflight()
        : this(new PolicyElevationHelperLocator(), new WindowsAuthenticodeTrustVerifier())
    {
    }

    public WindowsPolicyElevationPreflight(
        IPolicyElevationHelperLocator locator,
        IPolicyElevationTrustVerifier trustVerifier,
        Func<string?>? selfImagePathProvider = null)
    {
        _locator = locator;
        _trustVerifier = trustVerifier;
        _selfImagePathProvider = selfImagePathProvider
            ?? WindowsProcessInspector.TryGetCurrentProcessCanonicalPath;
    }

    public PolicyElevationPreflightResult Verify(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PolicyElevationHelperLocation location = _locator.Locate();

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!location.Found
                || location.CanonicalHelperPath is null
                || location.CanonicalHostPath is null
                || location.Verification is null)
            {
                return PolicyElevationPreflightResult.Rejected(
                    location,
                    PolicyElevationPreflightFailureKind.HelperUnavailable,
                    location.FailureReason ?? "The packaged policy write helper is unavailable.",
                    location.Detail);
            }

            string? selfImagePath = _selfImagePathProvider();
            if (selfImagePath is null
                || !WindowsProcessInspector.PathsAreEqual(selfImagePath, location.CanonicalHostPath))
            {
                return PolicyElevationPreflightResult.Rejected(
                    location,
                    PolicyElevationPreflightFailureKind.RunningHostMismatch,
                    "This UniGetUI process is not the packaged host binary, so it cannot request an elevated policy write.",
                    $"The running image '{selfImagePath}' is not the packaged host '{location.CanonicalHostPath}'.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            PolicyElevationSignerBindingResult binding = PolicyElevationSignerBinding.Bind(
                _trustVerifier,
                selfImagePath,
                location.CanonicalHelperPath);
            cancellationToken.ThrowIfCancellationRequested();

            return binding.IsBound
                ? PolicyElevationPreflightResult.Success(location)
                : PolicyElevationPreflightResult.Rejected(
                    location,
                    PolicyElevationPreflightFailureKind.SignerBindingFailed,
                    binding.FailureReason,
                    binding.Detail,
                    binding.Win32ErrorCode);
        }
        catch
        {
            location.Verification?.Dispose();
            throw;
        }
    }
}
#endif
