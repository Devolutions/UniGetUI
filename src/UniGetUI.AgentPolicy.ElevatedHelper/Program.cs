using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation;
using UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation.Interop;

namespace UniGetUI.AgentPolicy.ElevatedHelper;

/// <summary>
/// The elevated policy-write helper.
/// </summary>
/// <remarks>
/// <para>
/// This process is started by a non-elevated UniGetUI through <c>ShellExecuteEx</c> with the
/// <c>runas</c> verb, so it runs with a full administrator token. Its command line carries routing
/// information only — a pipe name, the caller's process id, the caller's process creation time and
/// the logon session. The policy draft, the store token, the validation receipt and every other
/// piece of request state travel exclusively over the authenticated pipe, and no temporary file is
/// ever used.
/// </para>
/// <para>
/// The helper handles exactly one connection, reads exactly one request, writes exactly one
/// response and exits. It performs its half of the mutual authentication before reading a single
/// byte of payload, and it connects with an anonymous impersonation level so a rogue pipe cannot
/// borrow its elevated token.
/// </para>
/// </remarks>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            return PolicyElevationProtocol.ExitInvalidArguments;
        }

        if (!PolicyElevationLaunchArguments.TryParse(args, out PolicyElevationLaunchArguments? launch, out _))
        {
            return PolicyElevationProtocol.ExitInvalidArguments;
        }

        using var stageTimeouts = new PolicyElevationHelperStageTimeouts(
            PolicyElevationProtocol.ConnectTimeout,
            PolicyElevationProtocol.ExchangeTimeout);

        try
        {
            return await RunAsync(launch, stageTimeouts).ConfigureAwait(false);
        }
        catch (PolicyElevationFrameException)
        {
            return PolicyElevationProtocol.ExitProtocolError;
        }
        catch (OperationCanceledException)
        {
            return PolicyElevationProtocol.ExitConnectFailed;
        }
        catch (IOException)
        {
            return PolicyElevationProtocol.ExitConnectFailed;
        }
        catch (Exception)
        {
            return PolicyElevationProtocol.ExitUnexpectedFailure;
        }
    }

    private static async Task<int> RunAsync(
        PolicyElevationLaunchArguments launch,
        PolicyElevationHelperStageTimeouts stageTimeouts)
    {
        AuthenticatedHostContext? authenticatedHost = null;
        NamedPipeClientStream? pipe = null;
        try
        {
            PolicyElevationHelperSynchronousStageResult<AuthenticatedHostContext?> preparation =
                await PolicyElevationHelperSynchronousStageRunner.RunAsync(
                    () => TryPrepareAuthenticatedHost(launch),
                    stageTimeouts.Token,
                    static abandoned => abandoned?.Dispose()).ConfigureAwait(false);
            if (!preparation.Completed)
                return PolicyElevationProtocol.ExitConnectFailed;

            authenticatedHost = preparation.Value;
            if (authenticatedHost is null)
                return PolicyElevationProtocol.ExitPeerAuthenticationFailed;

            pipe = new NamedPipeClientStream(
                ".",
                launch.PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.WriteThrough,
                TokenImpersonationLevel.Anonymous);

            await pipe.ConnectAsync(
                    (int)PolicyElevationProtocol.ConnectTimeout.TotalMilliseconds,
                    stageTimeouts.Token)
                .ConfigureAwait(false);

            AuthenticatedHostContext pipeAuthenticationHost = authenticatedHost;
            NamedPipeClientStream authenticatedPipe = pipe;
            PolicyElevationHelperSynchronousStageResult<int> pipeAuthentication =
                await PolicyElevationHelperSynchronousStageRunner.RunAsync(
                    () => AuthenticateConnectedPipe(pipeAuthenticationHost, authenticatedPipe),
                    stageTimeouts.Token,
                    cleanupAfterAbandonedWork: () =>
                    {
                        authenticatedPipe.Dispose();
                        pipeAuthenticationHost.Dispose();
                    }).ConfigureAwait(false);
            if (!pipeAuthentication.Completed)
            {
                pipe = null;
                authenticatedHost = null;
                return PolicyElevationProtocol.ExitConnectFailed;
            }

            if (pipeAuthentication.Value != PolicyElevationProtocol.ExitSuccess)
                return pipeAuthentication.Value;

            AuthenticatedHostContext identityHost = authenticatedHost;
            NamedPipeClientStream identityPipe = pipe;
            PolicyElevationHelperSynchronousStageResult<InitiatingUserResult> identityResolution =
                await PolicyElevationHelperSynchronousStageRunner.RunAsync(
                    () => ResolveInitiatingUser(identityHost),
                    stageTimeouts.Token,
                    cleanupAfterAbandonedWork: () =>
                    {
                        identityPipe.Dispose();
                        identityHost.Dispose();
                    }).ConfigureAwait(false);
            if (!identityResolution.Completed)
            {
                pipe = null;
                authenticatedHost = null;
                return PolicyElevationProtocol.ExitConnectFailed;
            }

            InitiatingUserResult identity = identityResolution.Value;
            if (identity.ExitCode != PolicyElevationProtocol.ExitSuccess
                || identity.EffectiveUser is null)
            {
                return identity.ExitCode;
            }

            PolicyElevationRequestMessage request =
                await PolicyElevationFrame.ReadRequestAsync(pipe, stageTimeouts.Token).ConfigureAwait(false);
            stageTimeouts.Token.ThrowIfCancellationRequested();
            stageTimeouts.BeginExchange();

            using var brokerCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(stageTimeouts.Token);
            using var disconnectMonitorCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(stageTimeouts.Token);
            Task disconnectMonitor = MonitorHostDisconnectAsync(
                pipe,
                brokerCancellation,
                disconnectMonitorCancellation.Token);

            PolicyElevationResponseMessage response;
            try
            {
                response = await PolicyReplacementExecutor
                    .ExecuteAsync(request, identity.EffectiveUser, brokerCancellation.Token)
                    .ConfigureAwait(false);
            }
            finally
            {
                await disconnectMonitorCancellation.CancelAsync().ConfigureAwait(false);
                try
                {
                    await disconnectMonitor.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (disconnectMonitorCancellation.IsCancellationRequested)
                {
                }
            }

            // WriteResponseAsync completes only once the whole frame has been handed to the pipe and
            // flushed, under the same bounded, cancellable token as every other stage. Closing the
            // handle afterwards is enough: a synchronous drain would block on the reader with no
            // timeout and no cancellation, which is exactly the unbounded hang this design forbids.
            using var responseWrite =
                new CancellationTokenSource(PolicyElevationProtocol.ResponseWriteTimeout);
            await PolicyElevationFrame.WriteResponseAsync(pipe, response, responseWrite.Token).ConfigureAwait(false);

            return PolicyElevationProtocol.ExitSuccess;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return PolicyElevationProtocol.ExitConnectFailed;
        }
        finally
        {
            pipe?.Dispose();
            authenticatedHost?.Dispose();
        }
    }

    private static AuthenticatedHostContext? TryPrepareAuthenticatedHost(
        PolicyElevationLaunchArguments launch)
    {
        IPolicyElevationTrustVerifier trustVerifier = new WindowsAuthenticodeTrustVerifier();
        if (!TryDescribePackagedLayout(
                out string? installRoot,
                out string? hostPath,
                out string? selfPath,
                out PolicyElevationLocationVerification? verification)
            || installRoot is null || hostPath is null || selfPath is null || verification is null)
        {
            verification?.Dispose();
            return null;
        }

        SafeProcessHandle host = PolicyElevationNative.OpenProcess(
            PolicyElevationNative.ProcessQueryLimitedInformation | PolicyElevationNative.Synchronize,
            false,
            unchecked((uint)launch.ParentProcessId));
        if (host.IsInvalid)
        {
            host.Dispose();
            verification.Dispose();
            return null;
        }

        var expectation = new PolicyElevationPeerExpectation(
            hostPath,
            installRoot,
            unchecked((uint)launch.ParentProcessId),
            launch.ParentCreationTimeUtcTicks,
            launch.SessionId)
        {
            RequireElevatedAdministrator = false,
            Verification = verification,
        };

        if (!WindowsPeerAuthenticator
                .Authenticate(
                    host.DangerousGetHandle(),
                    expectation.ExpectedProcessId,
                    expectation,
                    trustVerifier,
                    selfPath)
                .IsAuthenticated)
        {
            host.Dispose();
            verification.Dispose();
            return null;
        }

        return new AuthenticatedHostContext(
            verification,
            host,
            expectation,
            trustVerifier,
            selfPath);
    }

    private static int AuthenticateConnectedPipe(
        AuthenticatedHostContext authenticatedHost,
        NamedPipeClientStream pipe)
    {
        if (!PolicyElevationNative.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint serverProcessId))
            return PolicyElevationProtocol.ExitPeerAuthenticationFailed;

        return WindowsPeerAuthenticator
            .Authenticate(
                authenticatedHost.Host.DangerousGetHandle(),
                serverProcessId,
                authenticatedHost.Expectation,
                authenticatedHost.TrustVerifier,
                authenticatedHost.SelfPath)
            .IsAuthenticated
                ? PolicyElevationProtocol.ExitSuccess
                : PolicyElevationProtocol.ExitPeerAuthenticationFailed;
    }

    private static InitiatingUserResult ResolveInitiatingUser(
        AuthenticatedHostContext authenticatedHost)
    {
        int exitCode = PolicyElevationInitiatingUserResolver.Resolve(
            authenticatedHost.Host.DangerousGetHandle(),
            out string? effectiveUser);
        return new InitiatingUserResult(exitCode, effectiveUser);
    }

    private static async Task MonitorHostDisconnectAsync(
        NamedPipeClientStream pipe,
        CancellationTokenSource brokerCancellation,
        CancellationToken cancellationToken)
    {
        byte[] unexpectedData = new byte[1];
        try
        {
            int read = await pipe.ReadAsync(unexpectedData, cancellationToken).ConfigureAwait(false);
            if (!cancellationToken.IsCancellationRequested)
            {
                await brokerCancellation.CancelAsync().ConfigureAwait(false);
            }
        }
        catch (IOException)
        {
            await brokerCancellation.CancelAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// Confirms this process really is the packaged helper, and derives both the install root the
    /// host must also live in and this process' own canonical image, which the mutual signer
    /// binding needs. The returned verification holds kernel handles to every verified object and
    /// must stay alive for the whole exchange.
    /// </summary>
    private static bool TryDescribePackagedLayout(
        out string? installRoot,
        out string? hostPath,
        out string? selfImagePath,
        out PolicyElevationLocationVerification? verification)
    {
        installRoot = null;
        hostPath = null;
        selfImagePath = null;
        verification = null;

        string? selfPath = WindowsProcessInspector.TryGetCurrentProcessCanonicalPath();
        if (selfPath is null
            || !PolicyElevationPaths.TryGetInstallRootFromHelperPath(selfPath, out string? root)
            || root is null)
        {
            return false;
        }

        string? canonicalHostPath = WindowsProcessInspector.TryGetCanonicalPath(
            PolicyElevationPaths.GetHostPath(root));

        if (canonicalHostPath is null)
        {
            return false;
        }

        // Always handle-verified: this process is about to perform a machine-wide policy write, so
        // the packaged layout it was launched from has to be provably administrator-protected.
        PolicyElevationLocationVerification verified =
            new WindowsProtectedLocationVerifier().Verify(root, selfPath, canonicalHostPath);

        if (!verified.IsProtected
            || !WindowsProcessInspector.PathsAreEqual(verified.CanonicalHelperPath, selfPath)
            || !WindowsProcessInspector.PathsAreEqual(verified.CanonicalHostPath, canonicalHostPath)
            || !WindowsProcessInspector.PathsAreEqual(verified.CanonicalInstallRoot, root))
        {
            verified.Dispose();
            return false;
        }

        verification = verified;
        installRoot = root;
        hostPath = canonicalHostPath;
        selfImagePath = selfPath;
        return true;
    }

    private readonly record struct InitiatingUserResult(int ExitCode, string? EffectiveUser);

    private sealed class AuthenticatedHostContext : IDisposable
    {
        public AuthenticatedHostContext(
            PolicyElevationLocationVerification layout,
            SafeProcessHandle host,
            PolicyElevationPeerExpectation expectation,
            IPolicyElevationTrustVerifier trustVerifier,
            string selfPath)
        {
            Layout = layout;
            Host = host;
            Expectation = expectation;
            TrustVerifier = trustVerifier;
            SelfPath = selfPath;
        }

        public PolicyElevationLocationVerification Layout { get; }
        public SafeProcessHandle Host { get; }
        public PolicyElevationPeerExpectation Expectation { get; }
        public IPolicyElevationTrustVerifier TrustVerifier { get; }
        public string SelfPath { get; }

        public void Dispose()
        {
            Host.Dispose();
            Layout.Dispose();
        }
    }
}
