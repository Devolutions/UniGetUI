#if WINDOWS
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using Devolutions.Now.Policy.Api;
using UniGetUI.Core.Logging;
using UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation.Interop;

namespace UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation;

/// <summary>Server-side half of the mutual authentication.</summary>
public interface IPolicyElevationPipePeerAuthenticator
{
    PolicyElevationPeerAuthenticationResult Authenticate(
        NamedPipeServerStream pipe,
        IElevatedHelperProcess helper,
        PolicyElevationHelperLocation location);
}

/// <summary>
/// Authenticates the connected pipe client against the helper process the host itself launched.
/// </summary>
public sealed class WindowsPipePeerAuthenticator : IPolicyElevationPipePeerAuthenticator
{
    private readonly IPolicyElevationTrustVerifier _trustVerifier;

    public WindowsPipePeerAuthenticator(IPolicyElevationTrustVerifier trustVerifier)
    {
        _trustVerifier = trustVerifier;
    }

    public PolicyElevationPeerAuthenticationResult Authenticate(
        NamedPipeServerStream pipe,
        IElevatedHelperProcess helper,
        PolicyElevationHelperLocation location)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        ArgumentNullException.ThrowIfNull(helper);
        ArgumentNullException.ThrowIfNull(location);

        if (location.CanonicalHelperPath is null || location.CanonicalInstallRoot is null)
        {
            return PolicyElevationPeerAuthenticationResult.Rejected(
                "The elevation counterpart could not be identified.",
                "The packaged helper location was not resolved before authentication.");
        }

        string? selfImagePath = WindowsProcessInspector.TryGetCurrentProcessCanonicalPath();
        if (selfImagePath is null
            || location.CanonicalHostPath is null
            || !WindowsProcessInspector.PathsAreEqual(selfImagePath, location.CanonicalHostPath))
        {
            return PolicyElevationPeerAuthenticationResult.Rejected(
                "This UniGetUI process is not the packaged host binary.",
                $"The running image '{selfImagePath}' is not '{location.CanonicalHostPath}'.");
        }

        if (!PolicyElevationNative.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint clientProcessId))
        {
            return PolicyElevationPeerAuthenticationResult.Rejected(
                "The elevation counterpart could not be identified.",
                "The kernel did not report the pipe client process id.",
                System.Runtime.InteropServices.Marshal.GetLastWin32Error());
        }

        if (PolicyElevationNative.GetNamedPipeClientSessionId(pipe.SafePipeHandle, out uint clientSessionId)
            && clientSessionId != helper.SessionId)
        {
            return PolicyElevationPeerAuthenticationResult.Rejected(
                "The elevation counterpart runs in a different logon session.",
                $"The pipe client session id {clientSessionId} is not the launched helper's {helper.SessionId}.");
        }

        var expectation = new PolicyElevationPeerExpectation(
            location.CanonicalHelperPath,
            location.CanonicalInstallRoot,
            helper.ProcessId,
            helper.CreationTimeUtcTicks,
            helper.SessionId)
        {
            RequireElevatedAdministrator = true,
            Verification = location.Verification,
        };

        return WindowsPeerAuthenticator.Authenticate(
            helper.Handle,
            clientProcessId,
            expectation,
            _trustVerifier,
            selfImagePath);
    }
}

/// <summary>
/// Stage timeouts. Defaults come from the wire protocol; the constructor overload exists so tests
/// can drive the same code paths without waiting minutes.
/// </summary>
public sealed record PolicyElevationTimeouts(TimeSpan Connect, TimeSpan Exchange, TimeSpan Exit)
{
    public static PolicyElevationTimeouts Default { get; } = new(
        PolicyElevationProtocol.ConnectTimeout,
        PolicyElevationProtocol.ExchangeTimeout,
        PolicyElevationProtocol.ExitTimeout);
}

/// <summary>
/// Drives a single elevated policy replacement: locate the packaged helper, verify it, create a
/// single-use authenticated pipe, raise the consent prompt, authenticate the peer, exchange
/// exactly one request and one response, and map the result onto a
/// <see cref="PolicyElevationOutcome"/>.
/// </summary>
public sealed class WindowsPolicyWriteElevator : IPolicyWriteElevator
{
    private readonly IElevatedHelperLauncher _launcher;
    private readonly IPolicyElevationPipePeerAuthenticator _peerAuthenticator;
    private readonly IPolicyElevationPreflight _preflight;
    private readonly Func<string, NamedPipeServerStream> _pipeFactory;
    private readonly PolicyElevationTimeouts _timeouts;

    public WindowsPolicyWriteElevator()
        : this(new WindowsAuthenticodeTrustVerifier())
    {
    }

    public WindowsPolicyWriteElevator(IPolicyElevationTrustVerifier trustVerifier)
        : this(
            new PolicyElevationHelperLocator(),
            new WindowsElevatedHelperLauncher(),
            trustVerifier,
            new WindowsPipePeerAuthenticator(trustVerifier),
            PolicyElevationPipeServer.Create)
    {
    }

    /// <param name="selfImagePathProvider">
    /// How the elevator learns which binary it is itself running as. Injected purely so the
    /// loopback tests can stand in a packaged layout; every shipping constructor above supplies the
    /// kernel-backed <see cref="WindowsProcessInspector.TryGetCurrentProcessCanonicalPath"/>, so no
    /// bypass exists in the product.
    /// </param>
    public WindowsPolicyWriteElevator(
        IPolicyElevationHelperLocator locator,
        IElevatedHelperLauncher launcher,
        IPolicyElevationTrustVerifier trustVerifier,
        IPolicyElevationPipePeerAuthenticator peerAuthenticator,
        Func<string, NamedPipeServerStream> pipeFactory,
        PolicyElevationTimeouts? timeouts = null,
        Func<string?>? selfImagePathProvider = null)
    {
        _launcher = launcher;
        _peerAuthenticator = peerAuthenticator;
        _pipeFactory = pipeFactory;
        _timeouts = timeouts ?? PolicyElevationTimeouts.Default;
        _preflight = new WindowsPolicyElevationPreflight(
            locator,
            trustVerifier,
            selfImagePathProvider);
    }

    public async Task<PolicyElevationResult> ReplacePolicyAsync(
        PolicyElevationWriteRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        PolicyElevationRequestMessage preflightRequest = CreateRequestMessage(request, string.Empty);
        if (!PolicyElevationReplacementDispatcher.IsBrokerRequestWithinLimit(preflightRequest))
        {
            return Fail(
                request,
                PolicyElevationOutcome.PayloadTooLarge,
                "The serialized policy replacement request exceeds the broker request limit.");
        }

        if (!OperatingSystem.IsWindows())
        {
            return Fail(request, PolicyElevationOutcome.UnsupportedPlatform,
                "Elevated policy writes are only supported on Windows.");
        }

        using PolicyElevationPreflightResult preflight = _preflight.Verify(cancellationToken);
        if (!preflight.Succeeded)
        {
            if (preflight.Detail is not null)
            {
                Logger.Warn($"[PolicyElevation] Preflight failed: {preflight.Detail}");
            }

            PolicyElevationOutcome outcome =
                preflight.Failure == PolicyElevationPreflightFailureKind.HelperUnavailable
                    ? PolicyElevationOutcome.HelperUnavailable
                    : PolicyElevationOutcome.HelperUntrusted;
            return Fail(
                request,
                outcome,
                preflight.FailureReason,
                preflight.Win32ErrorCode);
        }

        // The handle lease pins every verified packaged object for the whole exchange, so nothing
        // on the path to the helper can be deleted, renamed or redirected after it was verified.
        PolicyElevationHelperLocation location = preflight.Location;

        if (!TryDescribeCurrentProcess(out uint hostProcessId, out long hostCreationTicks, out uint hostSessionId))
        {
            return Fail(
                request,
                PolicyElevationOutcome.LaunchFailed,
                "The identity of the calling UniGetUI process could not be established.");
        }

        string pipeName = PolicyElevationPipeServer.CreatePipeName();
        var arguments = new PolicyElevationLaunchArguments(
            PolicyElevationProtocol.Version,
            pipeName,
            unchecked((int)hostProcessId),
            hostCreationTicks,
            hostSessionId);

        NamedPipeServerStream pipe;
        try
        {
            pipe = _pipeFactory(pipeName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Logger.Error($"[PolicyElevation] The single-use elevation pipe could not be created: {ex}");
            return Fail(request, PolicyElevationOutcome.LaunchFailed, "The elevation channel could not be created.");
        }

        await using (pipe.ConfigureAwait(false))
        {
            ElevatedHelperLaunchResult launch = await _launcher
                .LaunchAsync(
                    location.CanonicalHelperPath,
                    arguments.Format(),
                    _timeouts.Connect,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!launch.Succeeded || launch.Process is null)
            {
                return Fail(request, launch.FailureOutcome, launch.FailureReason, launch.Win32ErrorCode);
            }

            using IElevatedHelperProcess helper = launch.Process;
            return await ExchangeAsync(request, pipe, helper, location, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<PolicyElevationResult> ExchangeAsync(
        PolicyElevationWriteRequest request,
        NamedPipeServerStream pipe,
        IElevatedHelperProcess helper,
        PolicyElevationHelperLocation location,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bool requestDispatched = false;

        try
        {
            timeout.CancelAfter(_timeouts.Connect);

            Task connect = pipe.WaitForConnectionAsync(timeout.Token);
            Task<int?> exit = helper.WaitForExitAsync(_timeouts.Connect, timeout.Token);

            // The losing wait is abandoned; make sure it can never surface as an unobserved fault.
            _ = exit.ContinueWith(
                static t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            _ = connect.ContinueWith(
                static t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            Task completed = await Task.WhenAny(connect, exit).ConfigureAwait(false);
            if (completed == exit && !connect.IsCompletedSuccessfully)
            {
                int? exitCode = await exit.ConfigureAwait(false);
                return MapPrematureExit(request, exitCode);
            }

            await connect.ConfigureAwait(false);

            PolicyElevationPeerAuthenticationResult authentication =
                _peerAuthenticator.Authenticate(pipe, helper, location);

            if (!authentication.IsAuthenticated)
            {
                Logger.Warn(
                    "[PolicyElevation] Peer authentication failed: "
                    + (authentication.Detail ?? authentication.FailureReason));

                return Fail(
                    request,
                    PolicyElevationOutcome.PeerAuthenticationFailed,
                    authentication.FailureReason,
                    authentication.Win32ErrorCode);
            }

            timeout.CancelAfter(_timeouts.Exchange + PolicyElevationProtocol.ResponseWriteTimeout);

            string requestId = Convert.ToHexStringLower(
                RandomNumberGenerator.GetBytes(PolicyElevationProtocol.RequestIdCharacters / 2));

            PolicyElevationRequestMessage message = CreateRequestMessage(request, requestId);

            requestDispatched = true;
            await PolicyElevationFrame.WriteRequestAsync(pipe, message, timeout.Token).ConfigureAwait(false);

            PolicyElevationResponseMessage response =
                await PolicyElevationFrame.ReadResponseAsync(pipe, timeout.Token).ConfigureAwait(false);

            if (!string.Equals(response.RequestId, requestId, StringComparison.Ordinal))
            {
                return Unknown(request);
            }

            PolicyElevationResult result = MapResponse(request, response);
            int? helperExit = await helper
                .WaitForExitAsync(_timeouts.Exit, CancellationToken.None)
                .ConfigureAwait(false);

            if (helperExit is not PolicyElevationProtocol.ExitSuccess)
            {
                Logger.Warn(
                    $"[PolicyElevation] Helper exited with {helperExit} after a valid "
                    + $"{response.Disposition} acknowledgement.");
            }

            return result with { HelperExitCode = helperExit };
        }
        catch (PolicyElevationFrameException ex)
        {
            Logger.Warn($"[PolicyElevation] Framing failure: {ex}");
            if (requestDispatched)
                return Unknown(request);
            return Fail(request, ex.Error switch
            {
                PolicyElevationFrameError.Oversized => PolicyElevationOutcome.PayloadTooLarge,
                PolicyElevationFrameError.EndOfStream => PolicyElevationOutcome.ConnectionClosed,
                _ => PolicyElevationOutcome.MalformedResponse,
            }, ex.Error switch
            {
                PolicyElevationFrameError.Oversized => "The elevated helper answered with an oversized response.",
                PolicyElevationFrameError.EndOfStream => "The elevated helper closed the channel before answering.",
                _ => "The elevated helper answered with a malformed response.",
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (requestDispatched)
                return Unknown(request);
            return Fail(
                request,
                PolicyElevationOutcome.Cancelled,
                "The elevated policy write was interrupted. Refresh policy management state before retrying.");
        }
        catch (OperationCanceledException)
        {
            if (requestDispatched)
                return Unknown(request);
            return Fail(request, PolicyElevationOutcome.TimedOut, "The elevated policy write timed out.");
        }
        catch (IOException ex)
        {
            Logger.Warn($"[PolicyElevation] The elevation channel failed: {ex}");
            if (requestDispatched)
                return Unknown(request);
            return Fail(request, PolicyElevationOutcome.ConnectionClosed, "The elevation channel was interrupted.");
        }
    }

    private static PolicyElevationRequestMessage CreateRequestMessage(
        PolicyElevationWriteRequest request,
        string requestId) =>
        new()
        {
            ProtocolVersion = PolicyElevationProtocol.Version,
            RequestId = requestId,
            Operation = request.Operation,
            ConflictHandling = request.ConflictHandling,
            ExpectedStoreToken = request.ExpectedStoreToken,
            ValidationReceipt = request.ValidationReceipt,
            WarningsAcknowledged = request.WarningsAcknowledged,
            Draft = request.Draft,
        };

    private static PolicyElevationResult MapPrematureExit(PolicyElevationWriteRequest request, int? exitCode)
    {
        PolicyElevationOutcome outcome = exitCode switch
        {
            PolicyElevationProtocol.ExitPeerAuthenticationFailed => PolicyElevationOutcome.PeerAuthenticationFailed,
            PolicyElevationProtocol.ExitProtocolError => PolicyElevationOutcome.MalformedResponse,
            null => PolicyElevationOutcome.TimedOut,
            _ => PolicyElevationOutcome.HelperCrashed,
        };

        return new PolicyElevationResult(
            outcome,
            request,
            "The elevated helper exited before answering.",
            HelperExitCode: exitCode);
    }

    /// <summary>
    /// Stable, host-authored text for each outcome. The helper's own message is never surfaced:
    /// it may embed broker text or exception detail, and a user interface must not be handed a
    /// string whose contents this process does not control. Recognised broker error codes are
    /// relayed structurally on <see cref="PolicyElevationResult.Error"/> so the UI can localise
    /// them; this text is only the bounded generic fallback.
    /// </summary>
    private static string DescribeOutcome(PolicyElevationOutcome outcome) => outcome switch
    {
        PolicyElevationOutcome.Replaced => "The policy was replaced.",
        PolicyElevationOutcome.BrokerRejected => "The agent rejected the policy replacement.",
        PolicyElevationOutcome.BrokerUnavailable => "The agent could not be reached to replace the policy.",
        PolicyElevationOutcome.BrokerInvalidResponse => "The agent returned a response that could not be understood.",
        PolicyElevationOutcome.PeerAuthenticationFailed =>
            "The elevated helper refused the request because the elevation channel could not be authenticated.",
        _ => "The elevated helper returned an invalid policy response.",
    };

    private static PolicyElevationResult MapResponse(
        PolicyElevationWriteRequest request,
        PolicyElevationResponseMessage response)
    {
        PolicyElevationOutcome outcome = response.Disposition switch
        {
            PolicyElevationDisposition.Committed => PolicyElevationOutcome.Replaced,
            PolicyElevationDisposition.Rejected => PolicyElevationOutcome.BrokerRejected,
            PolicyElevationDisposition.Unknown => PolicyElevationOutcome.WriteResultUnknown,
            _ => PolicyElevationOutcome.MalformedResponse,
        };

        return new PolicyElevationResult(
            outcome,
            request,
            DescribeOutcome(outcome),
            HelperExitCode: PolicyElevationProtocol.ExitSuccess,
            BrokerStatusCode: response.BrokerStatusCode,
            BrokerErrorCode: response.BrokerErrorCode,
            CommittedStoreToken: response.CommittedStoreToken,
            ConflictStoreToken: response.ConflictStoreToken,
            ConflictState: response.ConflictState,
            ConflictPolicyId: response.ConflictPolicyId);
    }

    private static PolicyElevationResult Unknown(PolicyElevationWriteRequest request) =>
        new(
            PolicyElevationOutcome.WriteResultUnknown,
            request,
            "The elevated write result is unknown. Refresh policy management state before retrying.");

    private static bool TryDescribeCurrentProcess(out uint processId, out long creationTicks, out uint sessionId)
    {
        nint pseudoHandle = -1;
        creationTicks = 0;
        sessionId = 0;
        processId = 0;

        return WindowsProcessInspector.TryGetProcessId(pseudoHandle, out processId)
            && WindowsProcessInspector.TryGetCreationTimeUtcTicks(pseudoHandle, out creationTicks)
            && WindowsProcessInspector.TryGetSessionId(processId, out sessionId);
    }

    private static PolicyElevationResult Fail(
        PolicyElevationWriteRequest request,
        PolicyElevationOutcome outcome,
        string? reason,
        int? win32ErrorCode = null)
        => new(outcome, request, reason, win32ErrorCode);

    /// <summary>Convenience accessor used by tests and callers that only need the draft back.</summary>
    internal static JsonElement DraftOf(PolicyElevationResult result) => result.Draft;
}
#endif
