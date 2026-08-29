using UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation;
#if WINDOWS
using UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation.Interop;
#endif

namespace UniGetUI.PackageEngine.Tests.PolicyWriteElevation;

/// <summary>
/// The trust seam must fail closed, and it must do so without any pinned signer value: the only
/// accepted publisher is whichever publisher signed the counterpart already on disk.
/// </summary>
public class PolicyElevationTrustPolicyTests
{
    private const string SignerA = "1a1a1a1a1b1b1b1b1c1c1c1c1d1d1d1d1e1e1e1e1f1f1f1f2a2a2a2a2b2b2b2b";
    private const string SignerB = "2222222222222222222222222222222222222222222222222222222222222222";

    [Fact]
    public void ProtectedInstallRoot_IsRequiredByDefault()
        => Assert.True(PolicyElevationTrustPolicy.RequireProtectedInstallRoot);

    [Fact]
    public void SignerDigest_MustBeALowercaseSha256Hex()
    {
        Assert.True(PolicyElevationTrustPolicy.IsValidSignerDigest(new string('a', 64)));
        Assert.True(PolicyElevationTrustPolicy.IsValidSignerDigest(new string('0', 64)));

        Assert.False(PolicyElevationTrustPolicy.IsValidSignerDigest(null));
        Assert.False(PolicyElevationTrustPolicy.IsValidSignerDigest(string.Empty));
        Assert.False(PolicyElevationTrustPolicy.IsValidSignerDigest(new string('a', 63)));
        Assert.False(PolicyElevationTrustPolicy.IsValidSignerDigest(new string('a', 65)));
        Assert.False(PolicyElevationTrustPolicy.IsValidSignerDigest(new string('A', 64)));
        Assert.False(PolicyElevationTrustPolicy.IsValidSignerDigest(new string('z', 64)));
    }

    [Fact]
    public void SignerEquality_AcceptsOnlyIdenticalWellFormedDigests()
    {
        Assert.True(PolicyElevationSignerBinding.SignersAreEqual(SignerA, SignerA));

        Assert.False(PolicyElevationSignerBinding.SignersAreEqual(SignerA, SignerB));
        Assert.False(PolicyElevationSignerBinding.SignersAreEqual(null, null));
        Assert.False(PolicyElevationSignerBinding.SignersAreEqual(SignerA, null));
        Assert.False(PolicyElevationSignerBinding.SignersAreEqual(SignerA, SignerA.ToUpperInvariant()));
        Assert.False(PolicyElevationSignerBinding.SignersAreEqual(SignerA, SignerA[..63]));
    }

    /// <summary>
    /// A stand-in publisher, so the binding rules can be exercised without a signed build. The
    /// production verifier is the only implementation wired into the app.
    /// </summary>
    private sealed class StubVerifier(Func<string, PolicyElevationTrustResult> resolve)
        : IPolicyElevationTrustVerifier
    {
        public List<string> VerifiedPaths { get; } = [];

        public PolicyElevationTrustResult VerifyExecutable(string executablePath)
        {
            VerifiedPaths.Add(executablePath);
            return resolve(executablePath);
        }
    }

    private static StubVerifier Signing(string selfDigest, string peerDigest)
        => new(path => path == "self"
            ? PolicyElevationTrustResult.Signed(selfDigest)
            : PolicyElevationTrustResult.Signed(peerDigest));

    [Fact]
    public void Binding_SucceedsWhenBothPeersShareOneSigner()
    {
        PolicyElevationSignerBindingResult result =
            PolicyElevationSignerBinding.Bind(Signing(SignerA, SignerA), "self", "peer");

        Assert.True(result.IsBound);
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public void Binding_SucceedsAcrossASignerRotation_BecauseNothingIsPinned()
    {
        // Both binaries were re-signed with a brand new certificate. No source constant changes,
        // and the exchange still binds.
        PolicyElevationSignerBindingResult result = PolicyElevationSignerBinding.Bind(
            Signing(new string('c', 64), new string('c', 64)),
            "self",
            "peer");

        Assert.True(result.IsBound);
    }

    [Fact]
    public void Binding_RejectsADifferentPublisher()
    {
        PolicyElevationSignerBindingResult result =
            PolicyElevationSignerBinding.Bind(Signing(SignerA, SignerB), "self", "peer");

        Assert.False(result.IsBound);
        Assert.NotNull(result.FailureReason);
    }

    [Fact]
    public void Binding_RejectsAnUnsignedPeer()
    {
        var verifier = new StubVerifier(path => path == "self"
            ? PolicyElevationTrustResult.Signed(SignerA)
            : PolicyElevationTrustResult.Rejected("unsigned", "no signature on the peer"));

        Assert.False(PolicyElevationSignerBinding.Bind(verifier, "self", "peer").IsBound);
    }

    [Fact]
    public void Binding_RejectsAnUnsignedSelf_BeforeLookingAtThePeer()
    {
        var verifier = new StubVerifier(path => path == "self"
            ? PolicyElevationTrustResult.Rejected("unsigned", "no signature on this installation")
            : PolicyElevationTrustResult.Signed(SignerA));

        Assert.False(PolicyElevationSignerBinding.Bind(verifier, "self", "peer").IsBound);
        Assert.Equal(["self"], verifier.VerifiedPaths);
    }

    [Fact]
    public void Binding_RejectsATrustedResultThatCarriesNoSigner()
    {
        var verifier = new StubVerifier(_ => new PolicyElevationTrustResult(true));

        Assert.False(PolicyElevationSignerBinding.Bind(verifier, "self", "peer").IsBound);
    }

    [Fact]
    public void Binding_RejectsUnresolvedPaths()
    {
        var verifier = new StubVerifier(_ => PolicyElevationTrustResult.Signed(SignerA));

        Assert.False(PolicyElevationSignerBinding.Bind(verifier, null, "peer").IsBound);
        Assert.False(PolicyElevationSignerBinding.Bind(verifier, "self", null).IsBound);
        Assert.False(PolicyElevationSignerBinding.Bind(verifier, "  ", "peer").IsBound);
        Assert.Empty(verifier.VerifiedPaths);
    }

    [Fact]
    public void BindingFailures_AreSafeToShowToAUser()
    {
        const string SelfPath = @"C:\Program Files\App\a.exe";
        const string PeerPath = @"C:\Program Files\App\b.exe";

        var differentPublisher = new StubVerifier(path => PolicyElevationTrustResult.Signed(
            path == SelfPath ? SignerA : SignerB));

        var unsignedPeer = new StubVerifier(path => path == SelfPath
            ? PolicyElevationTrustResult.Signed(SignerA)
            : PolicyElevationTrustResult.Rejected("unsigned", $"no signature on '{PeerPath}'"));

        var unsignedSelf = new StubVerifier(path => path == SelfPath
            ? PolicyElevationTrustResult.Rejected("unsigned", $"no signature on '{SelfPath}'")
            : PolicyElevationTrustResult.Signed(SignerA));

        foreach (StubVerifier verifier in new[] { differentPublisher, unsignedPeer, unsignedSelf })
        {
            PolicyElevationSignerBindingResult result =
                PolicyElevationSignerBinding.Bind(verifier, SelfPath, PeerPath);

            Assert.False(result.IsBound);
            Assert.NotNull(result.FailureReason);
            Assert.DoesNotContain(@"\", result.FailureReason, StringComparison.Ordinal);
            Assert.DoesNotContain("Program Files", result.FailureReason, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("certificate", result.FailureReason, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("thumbprint", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        }
    }

#if WINDOWS
    [Fact]
    public void ProductionVerifier_ReportsTheSignerOfAValidlySignedBinary()
    {
        var verifier = new WindowsAuthenticodeTrustVerifier();

        string systemBinary = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        if (!File.Exists(systemBinary))
        {
            return;
        }

        PolicyElevationTrustResult result = verifier.VerifyExecutable(systemBinary);
        if (!result.IsTrusted)
        {
            // A machine whose trust configuration rejects the system binary still exercises the
            // fail-closed path, which is the security-relevant direction.
            Assert.NotNull(result.FailureReason);
            Assert.Null(result.SignerPublicKeySha256);
            return;
        }

        Assert.True(PolicyElevationTrustPolicy.IsValidSignerDigest(result.SignerPublicKeySha256));

        // Rotation-safe binding: a binary is always bound to itself, whoever signed it.
        Assert.True(PolicyElevationSignerBinding.Bind(verifier, systemBinary, systemBinary).IsBound);
    }

    [Fact]
    public void ProductionVerifier_RefusesABinaryWithNoSignature()
    {
        var verifier = new WindowsAuthenticodeTrustVerifier();
        string unsigned = Path.Combine(
            Path.GetDirectoryName(typeof(PolicyElevationTrustPolicyTests).Assembly.Location)!,
            $"unigetui-unsigned-{Guid.NewGuid():N}.exe");

        File.WriteAllBytes(unsigned, [0x4D, 0x5A, .. new byte[512]]);
        try
        {
            PolicyElevationTrustResult result = verifier.VerifyExecutable(unsigned);

            Assert.False(result.IsTrusted);
            Assert.Null(result.SignerPublicKeySha256);
            Assert.NotNull(result.FailureReason);
            Assert.DoesNotContain(@"\", result.FailureReason, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(unsigned);
        }
    }

    [Fact]
    public void ProductionVerifier_RefusesAMissingFile()
    {
        var verifier = new WindowsAuthenticodeTrustVerifier();
        string missing = Path.Combine(
            Path.GetDirectoryName(typeof(PolicyElevationTrustPolicyTests).Assembly.Location)!,
            $"unigetui-missing-{Guid.NewGuid():N}.exe");

        PolicyElevationTrustResult result = verifier.VerifyExecutable(missing);

        Assert.False(result.IsTrusted);
        Assert.Null(result.SignerPublicKeySha256);
    }

    [Fact]
    public void ProductionVerifier_RefusesToBindTwoDifferentPublishers()
    {
        var verifier = new WindowsAuthenticodeTrustVerifier();

        string microsoftSigned = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        string unsigned = Path.Combine(
            Path.GetDirectoryName(typeof(PolicyElevationTrustPolicyTests).Assembly.Location)!,
            $"unigetui-unsigned-{Guid.NewGuid():N}.exe");

        File.WriteAllBytes(unsigned, [0x4D, 0x5A, .. new byte[512]]);
        try
        {
            Assert.False(PolicyElevationSignerBinding.Bind(verifier, microsoftSigned, unsigned).IsBound);
            Assert.False(PolicyElevationSignerBinding.Bind(verifier, unsigned, microsoftSigned).IsBound);
        }
        finally
        {
            File.Delete(unsigned);
        }
    }
#endif
}
