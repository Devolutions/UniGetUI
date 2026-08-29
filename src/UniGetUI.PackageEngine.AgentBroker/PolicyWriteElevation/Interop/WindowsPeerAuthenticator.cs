#if WINDOWS
namespace UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation.Interop;

/// <summary>What one peer requires of the other before any payload is exchanged.</summary>
public sealed record PolicyElevationPeerExpectation(
    string ExpectedCanonicalImagePath,
    string ExpectedCanonicalInstallRoot,
    uint ExpectedProcessId,
    long ExpectedCreationTimeUtcTicks,
    uint ExpectedSessionId)
{
    /// <summary>Require the peer token to be elevated and a member of BUILTIN\Administrators.</summary>
    public bool RequireElevatedAdministrator { get; init; }

    /// <summary>Require the peer to live under an administrator-protected install root.</summary>
    public bool RequireProtectedInstallRoot { get; init; } =
        PolicyElevationTrustPolicy.RequireProtectedInstallRoot;

    /// <summary>
    /// The handle-based verification of the packaged layout, whose kernel handles the caller is
    /// still holding. Required whenever <see cref="RequireProtectedInstallRoot"/> is set: it is the
    /// evidence that the expected paths were resolved from objects that no untrusted principal can
    /// write, delete, replace or re-permission, and that they cannot be swapped during the
    /// exchange.
    /// </summary>
    public PolicyElevationLocationVerification? Verification { get; init; }
}

public sealed record PolicyElevationPeerAuthenticationResult(
    bool IsAuthenticated,
    string? FailureReason = null,
    string? Detail = null,
    int? Win32ErrorCode = null)
{
    public static PolicyElevationPeerAuthenticationResult Authenticated { get; } = new(true);

    public static PolicyElevationPeerAuthenticationResult Rejected(
        string reason,
        string? detail = null,
        int? win32ErrorCode = null)
        => new(false, reason, detail, win32ErrorCode);
}

/// <summary>
/// The mutual authentication both peers run before a single byte of payload moves.
/// </summary>
/// <remarks>
/// <para>
/// The identity of the peer always comes from the kernel — the client/server process id attached
/// to the connected pipe instance — never from anything the peer said about itself. The caller
/// must keep the peer process handle open for the whole exchange, which is what makes the process
/// id meaningful: a live handle prevents the id from being recycled onto an attacker process.
/// </para>
/// <para>
/// The final gate is <see cref="PolicyElevationSignerBinding"/>: this process and its peer must be
/// signed by exactly the same publisher. No signer value is pinned anywhere, so the check survives
/// certificate rotation while still refusing any binary from a different publisher.
/// </para>
/// <para>
/// Every rejection reason here is written for a user interface: it names no path, certificate or
/// process id. The specifics travel in <c>Detail</c>, for the developer log only.
/// </para>
/// </remarks>
public static class WindowsPeerAuthenticator
{
    public static PolicyElevationPeerAuthenticationResult Authenticate(
        nint peerProcessHandle,
        uint pipeReportedProcessId,
        PolicyElevationPeerExpectation expectation,
        IPolicyElevationTrustVerifier trustVerifier,
        string selfCanonicalImagePath)
    {
        ArgumentNullException.ThrowIfNull(expectation);
        ArgumentNullException.ThrowIfNull(trustVerifier);

        if (peerProcessHandle == nint.Zero)
        {
            return PolicyElevationPeerAuthenticationResult.Rejected(
                "The elevation counterpart could not be identified.",
                "No live handle to the peer process was held during authentication.");
        }

        if (pipeReportedProcessId is 0)
        {
            return PolicyElevationPeerAuthenticationResult.Rejected(
                "The elevation counterpart could not be identified.",
                "The kernel did not report a process id for the connected pipe peer.");
        }

        if (pipeReportedProcessId != expectation.ExpectedProcessId)
        {
            return PolicyElevationPeerAuthenticationResult.Rejected(
                "The elevation counterpart is not the expected process.",
                $"The pipe peer process id {pipeReportedProcessId} is not the expected "
                + $"{expectation.ExpectedProcessId}.");
        }

        if (!WindowsProcessInspector.TryGetProcessId(peerProcessHandle, out uint handleProcessId)
            || handleProcessId != pipeReportedProcessId)
        {
            return PolicyElevationPeerAuthenticationResult.Rejected(
                "The elevation counterpart is not the expected process.",
                "The held peer process handle does not refer to the connected pipe peer.");
        }

        if (!WindowsProcessInspector.TryGetCreationTimeUtcTicks(peerProcessHandle, out long creationTime)
            || creationTime != expectation.ExpectedCreationTimeUtcTicks)
        {
            return PolicyElevationPeerAuthenticationResult.Rejected(
                "The elevation counterpart is not the expected process.",
                "The peer process creation time does not match the expected process instance.");
        }

        if (!WindowsProcessInspector.TryGetSessionId(pipeReportedProcessId, out uint sessionId)
            || sessionId != expectation.ExpectedSessionId)
        {
            return PolicyElevationPeerAuthenticationResult.Rejected(
                "The elevation counterpart runs in a different logon session.",
                $"The peer session id {sessionId} is not the expected {expectation.ExpectedSessionId}.");
        }

        if (!WindowsProcessInspector.TryGetImagePath(peerProcessHandle, out string? imagePath))
        {
            return PolicyElevationPeerAuthenticationResult.Rejected(
                "The elevation counterpart could not be identified.",
                "The peer process image path could not be read from the kernel.");
        }

        string? canonicalImagePath = WindowsProcessInspector.TryGetCanonicalPath(imagePath);
        if (canonicalImagePath is null
            || !WindowsProcessInspector.PathsAreEqual(canonicalImagePath, expectation.ExpectedCanonicalImagePath))
        {
            return PolicyElevationPeerAuthenticationResult.Rejected(
                "The elevation counterpart is not the expected packaged binary.",
                $"The peer image '{canonicalImagePath ?? imagePath}' is not "
                + $"'{expectation.ExpectedCanonicalImagePath}'.");
        }

        if (expectation.RequireProtectedInstallRoot)
        {
            PolicyElevationLocationVerification? verification = expectation.Verification;

            if (verification is null || !verification.IsProtected)
            {
                return PolicyElevationPeerAuthenticationResult.Rejected(
                    "Elevated policy writes require an administrator-protected install location.",
                    verification?.Detail
                    ?? "No handle-based verification of the packaged install location was supplied.");
            }

            if (!WindowsProcessInspector.PathsAreEqual(
                    verification.CanonicalInstallRoot,
                    expectation.ExpectedCanonicalInstallRoot))
            {
                return PolicyElevationPeerAuthenticationResult.Rejected(
                    "Elevated policy writes require an administrator-protected install location.",
                    "The verified install root does not match the install root the peer was expected in.");
            }

            if (!WindowsProcessInspector.PathsAreEqual(
                    verification.CanonicalHelperPath,
                    expectation.ExpectedCanonicalImagePath)
                && !WindowsProcessInspector.PathsAreEqual(
                    verification.CanonicalHostPath,
                    expectation.ExpectedCanonicalImagePath))
            {
                return PolicyElevationPeerAuthenticationResult.Rejected(
                    "The elevation counterpart is not the expected packaged binary.",
                    "The expected peer image is not one of the handle-verified packaged binaries.");
            }
        }

        if (expectation.RequireElevatedAdministrator)
        {
            if (!WindowsProcessInspector.TryGetTokenElevation(
                    peerProcessHandle,
                    out bool isElevated,
                    out bool isAdministrator))
            {
                return PolicyElevationPeerAuthenticationResult.Rejected(
                    "The elevation counterpart could not be identified.",
                    "The peer process token could not be inspected.");
            }

            if (!isElevated || !isAdministrator)
            {
                return PolicyElevationPeerAuthenticationResult.Rejected(
                    "The elevation counterpart is not running as an administrator.",
                    $"Peer token elevation={isElevated}, administrator={isAdministrator}.");
            }
        }

        PolicyElevationSignerBindingResult binding = PolicyElevationSignerBinding.Bind(
            trustVerifier,
            selfCanonicalImagePath,
            canonicalImagePath);

        return binding.IsBound
            ? PolicyElevationPeerAuthenticationResult.Authenticated
            : PolicyElevationPeerAuthenticationResult.Rejected(
                binding.FailureReason ?? "The elevation counterpart failed trust verification.",
                binding.Detail,
                binding.Win32ErrorCode);
    }
}
#endif
