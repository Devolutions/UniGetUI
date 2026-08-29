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
    private readonly IPolicyElevationHelperLocator _locator;
    private readonly IElevatedHelperLauncher _launcher;
    private readonly IPolicyElevationTrustVerifier _trustVerifier;
    private readonly IPolicyElevationPipePeerAuthenticator _peerAuthenticator;
    private readonly Func<string, NamedPipeServerStream> _pipeFactory;
    private readonly Func<string?> _selfImagePathProvider;
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
        _locator = locator;
        _launcher = launcher;
        _trustVerifier = trustVerifier;
        _peerAuthenticator = peerAuthenticator;
        _pipeFactory = pipeFactory;
        _timeouts = timeouts ?? PolicyElevationTimeouts.Default;
        _selfImagePathProvider = selfImagePathProvider
            ?? WindowsProcessInspector.TryGetCurrentProcessCanonicalPath;
    }

    public async Task<PolicyElevationResult> ReplacePolicyAsync(
        PolicyElevationWriteRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!OperatingSystem.IsWindows())
        {
            return Fail(request, PolicyElevationOutcome.UnsupportedPlatform,
                "Elevated policy writes are only supported on Windows.");
        }

        PolicyElevationHelperLocation location = _locator.Locate();
        if (!location.Found || location.CanonicalHelperPath is null)
        {
            if (location.Detail is not null)
            {
                Logger.Warn($"[PolicyElevation] Helper discovery failed: {location.Detail}");
            }

            return Fail(request, PolicyElevationOutcome.HelperUnavailable, location.FailureReason);
        }

        // The handle lease pins every verified packaged object for the whole exchange, so nothing
        // on the path to the helper can be deleted, renamed or redirected after it was verified.
        using PolicyElevationLocationVerification? verificationLease = location.Verification;

        string? selfImagePath = _selfImagePathProvider();
        if (selfImagePath is null
            || location.CanonicalHostPath is null
            || !WindowsProcessInspector.PathsAreEqual(selfImagePath, location.CanonicalHostPath))
        {
            Logger.Warn(
                $"[PolicyElevation] The running image '{selfImagePath}' is not the packaged host "
                + $"'{location.CanonicalHostPath}'.");

            return Fail(
                request,
                PolicyElevationOutcome.HelperUntrusted,
                "This UniGetUI process is not the packaged host binary, so it cannot request an elevated policy write.");
        }

        // Rotation-safe mutual signer binding: the helper must be signed by exactly the publisher
        // that signed this installation. Nothing is pinned, so a signer rotation needs no change.
        PolicyElevationSignerBindingResult binding = PolicyElevationSignerBinding.Bind(
            _trustVerifier,
            selfImagePath,
            location.CanonicalHelperPath);

        if (!binding.IsBound)
        {
            if (binding.Detail is not null)
            {
                Logger.Warn($"[PolicyElevation] Signer binding failed: {binding.Detail}");
            }

            return Fail(
                request,
                PolicyElevationOutcome.HelperUntrusted,
                binding.FailureReason,
                binding.Win32ErrorCode);
        }

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

            timeout.CancelAfter(_timeouts.Exchange);

            string requestId = Convert.ToHexStringLower(
                RandomNumberGenerator.GetBytes(PolicyElevationProtocol.RequestIdCharacters / 2));

            var message = new PolicyElevationRequestMessage
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

            await PolicyElevationFrame.WriteRequestAsync(pipe, message, timeout.Token).ConfigureAwait(false);

            PolicyElevationResponseMessage response =
                await PolicyElevationFrame.ReadResponseAsync(pipe, timeout.Token).ConfigureAwait(false);

            if (!string.Equals(response.RequestId, requestId, StringComparison.Ordinal))
            {
                return Fail(
                    request,
                    PolicyElevationOutcome.MalformedResponse,
                    "The elevated helper answered a different request.");
            }

            int? helperExit = await helper
                .WaitForExitAsync(_timeouts.Exit, cancellationToken)
                .ConfigureAwait(false);

            if (helperExit is not PolicyElevationProtocol.ExitSuccess)
            {
                return new PolicyElevationResult(
                    PolicyElevationOutcome.HelperCrashed,
                    request,
                    "The elevated helper terminated abnormally after answering.",
                    HelperExitCode: helperExit,
                    BrokerStatusCode: response.BrokerStatusCode,
                    BrokerErrorCode: response.BrokerErrorCode,
                    Payload: response.Payload);
            }

            return MapResponse(request, response);
        }
        catch (PolicyElevationFrameException ex)
        {
            Logger.Warn($"[PolicyElevation] Framing failure: {ex}");
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
            return Fail(
                request,
                PolicyElevationOutcome.Cancelled,
                "The elevated policy write was interrupted. Refresh policy management state before retrying.");
        }
        catch (OperationCanceledException)
        {
            return Fail(request, PolicyElevationOutcome.TimedOut, "The elevated policy write timed out.");
        }
        catch (IOException ex)
        {
            Logger.Warn($"[PolicyElevation] The elevation channel failed: {ex}");
            return Fail(request, PolicyElevationOutcome.ConnectionClosed, "The elevation channel was interrupted.");
        }
    }

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
        PolicyElevationOutcome outcome = response.Outcome switch
        {
            PolicyElevationResponseStatus.Replaced => PolicyElevationOutcome.Replaced,
            PolicyElevationResponseStatus.BrokerRejected => PolicyElevationOutcome.BrokerRejected,
            PolicyElevationResponseStatus.BrokerUnavailable => PolicyElevationOutcome.BrokerUnavailable,
            PolicyElevationResponseStatus.BrokerInvalidResponse => PolicyElevationOutcome.BrokerInvalidResponse,
            PolicyElevationResponseStatus.HelperRejected => PolicyElevationOutcome.PeerAuthenticationFailed,
            _ => PolicyElevationOutcome.MalformedResponse,
        };

        if (response.Message is { Length: > 0 } helperMessage)
        {
            Logger.Debug($"[PolicyElevation] Helper reported: {helperMessage}");
        }

        try
        {
            PolicyReplacementResponse? replacement = null;
            ErrorResponse? error = null;

            if (response.Outcome == PolicyElevationResponseStatus.Replaced)
            {
                if (response.Payload is not { } payload)
                    throw new InvalidDataException("A successful helper response had no payload.");

                replacement = BrokerJson.DeserializeStrict<PolicyReplacementResponse>(payload.GetRawText());
                ValidateReplacement(replacement);
            }
            else if (response.Payload is { ValueKind: not JsonValueKind.Undefined } errorPayload)
            {
                // Any non-success status may carry the broker's own error document. Relaying it
                // whole and unmodified is what lets the UI localise a recognised error code.
                error = BrokerJson.DeserializeStrict<ErrorResponse>(errorPayload.GetRawText());
                ValidateError(error);
            }

            return new PolicyElevationResult(
                outcome,
                request,
                DescribeOutcome(outcome),
                response.Win32ErrorCode,
                PolicyElevationProtocol.ExitSuccess,
                response.BrokerStatusCode,
                response.BrokerErrorCode,
                response.Payload,
                replacement,
                error);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            Logger.Warn($"[PolicyElevation] Invalid shared response payload: {ex}");
            return Fail(
                request,
                PolicyElevationOutcome.MalformedResponse,
                DescribeOutcome(PolicyElevationOutcome.MalformedResponse));
        }
    }

    private static void ValidateReplacement(PolicyReplacementResponse? replacement)
    {
        if (replacement is null
            || !string.Equals(replacement.ResponseKind, BrokerApi.PolicyReplacementResponseKind, StringComparison.Ordinal)
            || !string.Equals(replacement.ResponseVersion, BrokerApi.Version, StringComparison.Ordinal)
            || replacement.Policy is null
            || replacement.Validation is null
            || !replacement.Validation.IsValid
            || replacement.Validation.CanonicalDraft is null
            || string.IsNullOrWhiteSpace(replacement.Validation.ValidationReceipt)
            || replacement.Management is null
            || replacement.Management.Policy is null
            || replacement.Management.State != PolicyManagementState.Active
            || string.IsNullOrWhiteSpace(replacement.Management.StoreToken))
        {
            throw new InvalidDataException("The helper returned an inconsistent replacement response.");
        }
    }

    private static void ValidateError(ErrorResponse? error)
    {
        if (error is null
            || !string.Equals(error.ResponseKind, BrokerApi.ErrorResponseKind, StringComparison.Ordinal)
            || !string.Equals(error.ResponseVersion, BrokerApi.Version, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The helper returned an inconsistent error response.");
        }

        // The broker only reports a stale store token together with the management snapshot the
        // caller has to reconcile against, so a document without it is not the broker's.
        if (error.Code is ErrorCode.StalePolicyStoreToken && error.Management is null)
        {
            throw new InvalidDataException("A stale store token error arrived without a management snapshot.");
        }
    }

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
