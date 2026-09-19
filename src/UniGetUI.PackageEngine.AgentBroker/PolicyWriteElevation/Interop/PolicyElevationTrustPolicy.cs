using System.Security.Cryptography;

namespace UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation;

/// <summary>Outcome of an Authenticode / signer-identity check for a single binary.</summary>
/// <remarks>
/// <see cref="FailureReason"/> is safe to show to a user: it never names a filesystem path, a
/// certificate subject, a thumbprint or any other identifying material. <see cref="Detail"/> holds
/// the same failure with those specifics attached and must only ever reach the developer log.
/// </remarks>
public sealed record PolicyElevationTrustResult(
    bool IsTrusted,
    string? SignerPublicKeySha256 = null,
    string? FailureReason = null,
    string? Detail = null,
    int? Win32ErrorCode = null)
{
    /// <summary>The binary carries a valid signature made by the identified signer.</summary>
    public static PolicyElevationTrustResult Signed(string signerPublicKeySha256)
        => new(true, signerPublicKeySha256);

    public static PolicyElevationTrustResult Rejected(
        string reason,
        string? detail = null,
        int? win32ErrorCode = null)
        => new(false, null, reason, detail, win32ErrorCode);
}

/// <summary>
/// Verifies that a binary on disk carries a valid Authenticode signature, and reports which signer
/// made it. Injected so tests can exercise the surrounding protocol without shipping a bypass: the
/// production implementation is the only one wired into the app, and it fails closed.
/// </summary>
public interface IPolicyElevationTrustVerifier
{
    PolicyElevationTrustResult VerifyExecutable(string executablePath);
}

/// <summary>Outcome of binding two peers to one signer identity.</summary>
public sealed record PolicyElevationSignerBindingResult(
    bool IsBound,
    string? FailureReason = null,
    string? Detail = null,
    int? Win32ErrorCode = null)
{
    public static PolicyElevationSignerBindingResult Bound { get; } = new(true);

    public static PolicyElevationSignerBindingResult Rejected(
        string reason,
        string? detail = null,
        int? win32ErrorCode = null)
        => new(false, reason, detail, win32ErrorCode);
}

/// <summary>
/// Rotation-safe mutual signer binding.
/// </summary>
/// <remarks>
/// <para>
/// Neither peer carries a pinned signer value. Instead, each side verifies that <em>both</em>
/// packaged binaries — its own and the peer's — pass Authenticode verification, and then requires
/// the two signer public keys to be exactly equal. An attacker therefore cannot substitute a
/// binary signed by some other publisher, however reputable, because the only accepted identity is
/// whatever identity signed the already-installed counterpart. Certificate renewal, re-issue or
/// signer rotation needs no code change: a release is internally consistent by construction.
/// </para>
/// <para>
/// The identity is the SHA-256 of the DER encoded <c>SubjectPublicKeyInfo</c> rather than a
/// thumbprint, so a renewal that reuses the key still matches, and the comparison is constant time
/// so the check cannot be turned into an oracle.
/// </para>
/// <para>
/// Signer equality is deliberately <em>not</em> the only gate: it composes with the exact
/// canonical protected paths, the kernel-reported peer process id, the held process handles that
/// stop that id from being recycled, the process creation time, the logon session and the peer
/// token elevation checks. Any one of those failing rejects the exchange before a payload moves.
/// </para>
/// <para>
/// If a release ever needs to narrow this further to an explicit publisher allowlist, that
/// allowlist must be produced by the signing build as a generated compile-time artifact, and the
/// release build must fail when it is absent. It must never be an empty constant checked into
/// source: an empty constant silently disables the feature in every build and invites being
/// "temporarily" filled in with a placeholder.
/// </para>
/// </remarks>
public static class PolicyElevationSignerBinding
{
    /// <summary>
    /// Requires <paramref name="selfExecutablePath"/> and <paramref name="peerExecutablePath"/> to
    /// be validly signed by one and the same signer.
    /// </summary>
    public static PolicyElevationSignerBindingResult Bind(
        IPolicyElevationTrustVerifier trustVerifier,
        string? selfExecutablePath,
        string? peerExecutablePath)
    {
        ArgumentNullException.ThrowIfNull(trustVerifier);

        if (string.IsNullOrWhiteSpace(selfExecutablePath) || string.IsNullOrWhiteSpace(peerExecutablePath))
        {
            return PolicyElevationSignerBindingResult.Rejected(
                "The policy elevation binaries could not be identified.",
                "A signer binding was attempted without both binary paths resolved.");
        }

        PolicyElevationTrustResult self = trustVerifier.VerifyExecutable(selfExecutablePath);
        if (!self.IsTrusted || self.SignerPublicKeySha256 is null)
        {
            return PolicyElevationSignerBindingResult.Rejected(
                "This UniGetUI installation is not validly signed, so it cannot take part in an elevated policy write.",
                self.Detail ?? self.FailureReason,
                self.Win32ErrorCode);
        }

        PolicyElevationTrustResult peer = trustVerifier.VerifyExecutable(peerExecutablePath);
        if (!peer.IsTrusted || peer.SignerPublicKeySha256 is null)
        {
            return PolicyElevationSignerBindingResult.Rejected(
                "The policy elevation counterpart is not validly signed.",
                peer.Detail ?? peer.FailureReason,
                peer.Win32ErrorCode);
        }

        return SignersAreEqual(self.SignerPublicKeySha256, peer.SignerPublicKeySha256)
            ? PolicyElevationSignerBindingResult.Bound
            : PolicyElevationSignerBindingResult.Rejected(
                "The policy elevation counterpart was signed by a different publisher.",
                "The peer signer public key does not match this installation's signer public key.");
    }

    /// <summary>Constant-time equality of two signer public-key digests.</summary>
    public static bool SignersAreEqual(string? left, string? right)
    {
        if (!PolicyElevationTrustPolicy.IsValidSignerDigest(left)
            || !PolicyElevationTrustPolicy.IsValidSignerDigest(right))
        {
            return false;
        }

        byte[] leftBytes;
        byte[] rightBytes;
        try
        {
            leftBytes = Convert.FromHexString(left!);
            rightBytes = Convert.FromHexString(right!);
        }
        catch (FormatException)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}

/// <summary>Structural rules that govern which binaries may take part in an elevated policy write.</summary>
public static class PolicyElevationTrustPolicy
{
    /// <summary>
    /// Whether both peers must live under an administrator-protected install root. An elevated
    /// machine-policy write launched from a user-writable directory would be a privilege
    /// escalation primitive, so this ships enabled.
    /// </summary>
    /// <remarks>
    /// "Protected" is decided by <c>WindowsProtectedLocationVerifier</c> from held kernel handles —
    /// no reparse point in the chain, handle-resolved paths that match exactly, and write, delete,
    /// replace and security-control rights restricted to SYSTEM, Administrators and
    /// TrustedInstaller. It is never decided by inspecting the shape of a path string.
    /// </remarks>
    public const bool RequireProtectedInstallRoot = true;

    /// <summary>Length in characters of a SHA-256 signer digest in lowercase hexadecimal.</summary>
    public const int SignerDigestLength = 64;

    /// <summary>Length in bytes of a SHA-256 signer digest.</summary>
    public const int SignerDigestByteLength = SignerDigestLength / 2;

    public static bool IsValidSignerDigest(string? digest)
    {
        if (digest is null || digest.Length != SignerDigestLength)
        {
            return false;
        }

        foreach (char c in digest)
        {
            if (!char.IsAsciiHexDigitLower(c))
            {
                return false;
            }
        }

        return true;
    }
}
