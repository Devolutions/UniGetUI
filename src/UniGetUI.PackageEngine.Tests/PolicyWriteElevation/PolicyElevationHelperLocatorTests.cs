#if WINDOWS
using UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation;
using UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation.Interop;

namespace UniGetUI.PackageEngine.Tests.PolicyWriteElevation;

/// <summary>
/// Discovery must be fail-closed: the helper is accepted at one exact packaged path under an
/// administrator-protected root, or not at all.
/// </summary>
public class PolicyElevationHelperLocatorTests
{
    private static readonly string ProtectedRoot =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "UniGetUI");

    private static PolicyElevationHelperLocator Build(
        string installRoot,
        Func<string, bool>? fileExists = null,
        Func<string?, string?>? canonicalize = null,
        IPolicyElevationLocationVerifier? locationVerifier = null)
        => new(
            () => installRoot,
            canonicalize ?? (path => path),
            fileExists ?? (_ => true),
            locationVerifier ?? FakeLocationVerifier.Accepting());

    [Fact]
    public void PackagedLayoutUnderAProtectedRoot_IsAccepted()
    {
        PolicyElevationHelperLocation location = Build(ProtectedRoot).Locate();

        Assert.True(location.Found, location.FailureReason);
        Assert.Equal(PolicyElevationPaths.GetHelperPath(ProtectedRoot), location.CanonicalHelperPath);
        Assert.Equal(PolicyElevationPaths.GetHostPath(ProtectedRoot), location.CanonicalHostPath);
        Assert.Equal(ProtectedRoot, location.CanonicalInstallRoot);
    }

    [Fact]
    public void MissingHelper_FailsClosed()
    {
        string helperPath = PolicyElevationPaths.GetHelperPath(ProtectedRoot);

        PolicyElevationHelperLocation location =
            Build(ProtectedRoot, fileExists: path => !string.Equals(path, helperPath, StringComparison.Ordinal))
                .Locate();

        Assert.False(location.Found);
        Assert.Equal(PolicyElevationHelperLocationFailure.HelperMissing, location.Failure);
        Assert.Contains("elevated policy helper", location.FailureReason);
        Assert.Null(location.CanonicalHelperPath);

        // The path belongs in the developer log, never in the message a user sees.
        Assert.DoesNotContain(@"\", location.FailureReason, StringComparison.Ordinal);
        Assert.Contains(helperPath, location.Detail);
    }

    [Fact]
    public void MissingHost_FailsClosed()
    {
        string hostPath = PolicyElevationPaths.GetHostPath(ProtectedRoot);

        PolicyElevationHelperLocation location =
            Build(ProtectedRoot, fileExists: path => !string.Equals(path, hostPath, StringComparison.Ordinal))
                .Locate();

        Assert.False(location.Found);
        Assert.Contains("packaged install", location.FailureReason);
        Assert.DoesNotContain(@"\", location.FailureReason, StringComparison.Ordinal);
        Assert.Contains(hostPath, location.Detail);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EveryDiscoveryFailure_KeepsProtectedPathsOutOfTheUserFacingReason(bool helperMissing)
    {
        string missing = helperMissing
            ? PolicyElevationPaths.GetHelperPath(ProtectedRoot)
            : PolicyElevationPaths.GetHostPath(ProtectedRoot);

        PolicyElevationHelperLocation[] failures =
        [
            Build(ProtectedRoot, fileExists: path => !string.Equals(path, missing, StringComparison.Ordinal)).Locate(),
            Build(ProtectedRoot, canonicalize: _ => null).Locate(),
            Build("   ").Locate(),
            Build(ProtectedRoot, locationVerifier: FakeLocationVerifier.Rejecting()).Locate(),
            Build(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs",
                    "UniGetUI"),
                locationVerifier: FakeLocationVerifier.Rejecting()).Locate(),
        ];

        foreach (PolicyElevationHelperLocation failure in failures)
        {
            Assert.False(failure.Found);
            Assert.NotNull(failure.FailureReason);
            Assert.DoesNotContain(@"\", failure.FailureReason, StringComparison.Ordinal);
            Assert.DoesNotContain(ProtectedRoot, failure.FailureReason, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void UncanonicalisablePath_FailsClosed()
    {
        PolicyElevationHelperLocation location = Build(ProtectedRoot, canonicalize: _ => null).Locate();

        Assert.False(location.Found);
        Assert.Contains("canonicalised", location.FailureReason);
    }

    [Fact]
    public void HelperOutsideThePackagedLayout_FailsClosed()
    {
        string decoy = Path.Combine(ProtectedRoot, "UniGetUI.PolicyElevator.exe");

        PolicyElevationHelperLocation location = Build(
                ProtectedRoot,
                canonicalize: path => path is null
                    ? null
                    : path.EndsWith("UniGetUI.PolicyElevator.exe", StringComparison.Ordinal) ? decoy : path)
            .Locate();

        Assert.False(location.Found);
        Assert.Contains("packaged binary", location.FailureReason);
    }

    [Fact]
    public void HostInADifferentRoot_FailsClosed()
    {
        string foreignHost = Path.Combine(ProtectedRoot, "Other", "UniGetUI.exe");

        PolicyElevationHelperLocation location = Build(
                ProtectedRoot,
                canonicalize: path => path is null
                    ? null
                    : path.EndsWith(Path.Combine("UniGetUI", "UniGetUI.exe"), StringComparison.Ordinal)
                        ? foreignHost
                        : path)
            .Locate();

        Assert.False(location.Found);
        Assert.Contains("install root", location.FailureReason);
    }

    [Fact]
    public void UserWritableInstallRoot_FailsClosed()
    {
        string userRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "UniGetUI");

        PolicyElevationHelperLocation location =
            Build(userRoot, locationVerifier: FakeLocationVerifier.Rejecting()).Locate();

        Assert.False(location.Found);
        Assert.Equal(PolicyElevationHelperLocationFailure.ProtectedInstallRequired, location.Failure);
        Assert.Contains("administrator-protected", location.FailureReason);
    }

    [Fact]
    public void HandleBasedRejection_FailsClosedAndKeepsTheDetailOutOfTheReason()
    {
        var verifier = FakeLocationVerifier.Rejecting(
            @"'C:\Program Files\UniGetUI' is a reparse point, so the packaged layout can be redirected.");

        PolicyElevationHelperLocation location = Build(ProtectedRoot, locationVerifier: verifier).Locate();

        Assert.False(location.Found);
        Assert.Equal(1, verifier.Invocations);
        Assert.Contains("administrator-protected", location.FailureReason);
        Assert.DoesNotContain(@"\", location.FailureReason, StringComparison.Ordinal);
        Assert.Contains("reparse point", location.Detail);
    }

    [Fact]
    public void PathSwappedBetweenCanonicalisationAndTheHandleOpen_FailsClosed()
    {
        // The kernel resolved the handle to a different file than the one that was checked by name.
        var verifier = FakeLocationVerifier.ResolvingHelperElsewhere(
            @"C:\Program Files\Impostor\Assets\Utilities\UniGetUI.PolicyElevator.exe");

        PolicyElevationHelperLocation location = Build(ProtectedRoot, locationVerifier: verifier).Locate();

        Assert.False(location.Found);
        Assert.Contains("could not be verified", location.FailureReason);
        Assert.DoesNotContain(@"\", location.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public void AcceptedLayout_CarriesTheHandleLeaseForTheCaller()
    {
        PolicyElevationHelperLocation location = Build(ProtectedRoot).Locate();

        Assert.True(location.Found, location.FailureReason);
        Assert.NotNull(location.Verification);
        Assert.True(location.Verification!.IsProtected);
        Assert.Equal(ProtectedRoot, location.Verification.CanonicalInstallRoot);

        // The caller owns the lease and is the one that releases the pinned handles.
        location.Verification.Dispose();
    }

    [Theory]
    [InlineData(
        PolicyElevationHelperLocationFailure.HelperMissing,
        PolicyWriteElevationEligibilityStatus.HelperMissing)]
    [InlineData(
        PolicyElevationHelperLocationFailure.ProtectedInstallRequired,
        PolicyWriteElevationEligibilityStatus.ProtectedInstallRequired)]
    [InlineData(
        PolicyElevationHelperLocationFailure.InvalidInstallation,
        PolicyWriteElevationEligibilityStatus.InvalidInstallation)]
    public async Task PreWriteEligibility_PreservesDiscoveryFailureKind(
        PolicyElevationHelperLocationFailure failure,
        PolicyWriteElevationEligibilityStatus expected)
    {
        var locator = new StubLocator(PolicyElevationHelperLocation.NotFound("Unavailable.", failure: failure));
        PackagedPolicyWriteElevationEligibility eligibility = BuildEligibility(locator);

        PolicyWriteElevationEligibility result =
            await eligibility.EvaluateAsync(CancellationToken.None);

        Assert.False(result.IsEligible);
        Assert.Equal(expected, result.Status);
    }

    [Fact]
    public async Task PreWriteEligibility_AcceptsProtectedPackagedLayout()
    {
        PackagedPolicyWriteElevationEligibility eligibility = BuildEligibility(Build(ProtectedRoot));

        PolicyWriteElevationEligibility result =
            await eligibility.EvaluateAsync(CancellationToken.None);

        Assert.True(result.IsEligible);
        Assert.Equal(PolicyWriteElevationEligibilityStatus.Eligible, result.Status);
    }

    [Fact]
    public async Task PreWriteEligibility_RejectsFoundLocationWithoutVerificationLease()
    {
        PackagedPolicyWriteElevationEligibility eligibility = BuildEligibility(
            new StubLocator(new PolicyElevationHelperLocation(true)));

        PolicyWriteElevationEligibility result =
            await eligibility.EvaluateAsync(CancellationToken.None);

        Assert.False(result.IsEligible);
        Assert.Equal(PolicyWriteElevationEligibilityStatus.InvalidInstallation, result.Status);
    }

    [Fact]
    public async Task PreWriteEligibility_RejectsUnsignedHelper()
    {
        PackagedPolicyWriteElevationEligibility eligibility = BuildEligibility(
            FakeHelperLocator.Found(),
            FakeTrustVerifier.UnsignedFor(FakeHelperLocator.PackagedHelperPath));

        PolicyWriteElevationEligibility result =
            await eligibility.EvaluateAsync(CancellationToken.None);

        Assert.False(result.IsEligible);
        Assert.Equal(PolicyWriteElevationEligibilityStatus.InvalidInstallation, result.Status);
    }

    [Fact]
    public async Task PreWriteEligibility_RejectsHelperWithDifferentSigner()
    {
        PackagedPolicyWriteElevationEligibility eligibility = BuildEligibility(
            FakeHelperLocator.Found(),
            FakeTrustVerifier.DifferentSignerFor(FakeHelperLocator.PackagedHelperPath));

        PolicyWriteElevationEligibility result =
            await eligibility.EvaluateAsync(CancellationToken.None);

        Assert.False(result.IsEligible);
        Assert.Equal(PolicyWriteElevationEligibilityStatus.InvalidInstallation, result.Status);
    }

    [Fact]
    public async Task PreWriteEligibility_RejectsRunningHostOutsidePackagedLayout()
    {
        var verifier = FakeTrustVerifier.SameSigner();
        PackagedPolicyWriteElevationEligibility eligibility = BuildEligibility(
            FakeHelperLocator.Found(),
            verifier,
            () => Path.Combine(Path.GetTempPath(), "Rogue", "UniGetUI.exe"));

        PolicyWriteElevationEligibility result =
            await eligibility.EvaluateAsync(CancellationToken.None);

        Assert.False(result.IsEligible);
        Assert.Equal(PolicyWriteElevationEligibilityStatus.InvalidInstallation, result.Status);
        Assert.Empty(verifier.VerifiedPaths);
    }

    [Fact]
    public async Task PreWriteEligibility_RunsBlockingTrustWorkOffCallerThread()
    {
        var preflight = new BlockingPreflight();
        var eligibility = new PackagedPolicyWriteElevationEligibility(preflight);

        Task<PolicyWriteElevationEligibility> pending =
            eligibility.EvaluateAsync(CancellationToken.None);
        await preflight.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(pending.IsCompleted);
        preflight.Release();
        Assert.True((await pending).IsEligible);
    }

    [Fact]
    public async Task PreWriteEligibility_CancellationStopsWaitingForTrustWork()
    {
        var preflight = new BlockingPreflight();
        var eligibility = new PackagedPolicyWriteElevationEligibility(preflight);
        using var cancellation = new CancellationTokenSource();

        Task<PolicyWriteElevationEligibility> pending =
            eligibility.EvaluateAsync(cancellation.Token);
        await preflight.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public void EmptyInstallRoot_FailsClosed()
    {
        PolicyElevationHelperLocation location = Build("   ").Locate();

        Assert.False(location.Found);
        Assert.NotNull(location.FailureReason);
    }

    private sealed class StubLocator(PolicyElevationHelperLocation location)
        : IPolicyElevationHelperLocator
    {
        public PolicyElevationHelperLocation Locate() => location;
    }

    private static PackagedPolicyWriteElevationEligibility BuildEligibility(
        IPolicyElevationHelperLocator locator,
        IPolicyElevationTrustVerifier? trustVerifier = null,
        Func<string?>? selfImagePathProvider = null) =>
        new(new WindowsPolicyElevationPreflight(
            locator,
            trustVerifier ?? FakeTrustVerifier.SameSigner(),
            selfImagePathProvider ?? (() => FakeHelperLocator.PackagedHostPath)));

    private sealed class BlockingPreflight : IPolicyElevationPreflight
    {
        private readonly ManualResetEventSlim _release = new();

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PolicyElevationPreflightResult Verify(CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            _release.Wait(cancellationToken);
            return PolicyElevationPreflightResult.Success(FakeHelperLocator.Found().Locate());
        }

        public void Release() => _release.Set();
    }
}
#endif
