using System.IO.Pipes;
using System.Security.Principal;
using Devolutions.Now.Policy.Api;
using Devolutions.Now.Policy.Client;
using Microsoft.Win32.SafeHandles;
using UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation;
using UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation.Interop;

namespace UniGetUI.AgentPolicy.ElevatedHelper;

internal sealed class AuthenticatedBrokerTransport : IBrokerTransport
{
    private const int ConnectTimeoutMilliseconds = 5000;
    private const int ReadTimeoutMilliseconds = 30000;
    internal const int MaxPolicyManagementResponseBytes =
        BoundedNamedPipeBrokerTransport.MaxPolicyManagementResponseBodyBytes;
    private readonly string _pipeName;
    private readonly Func<NamedPipeClientStream, string, IDisposable> _authenticate;

    public AuthenticatedBrokerTransport(string? pipeName = null)
        : this(pipeName, AuthenticatedBrokerServer.Authenticate)
    {
    }

    internal AuthenticatedBrokerTransport(
        string? pipeName,
        Func<NamedPipeClientStream, string, IDisposable> authenticate)
    {
        _pipeName = string.IsNullOrWhiteSpace(pipeName) ? BrokerApi.DefaultPipeName : pipeName;
        _authenticate = authenticate;
    }

    public Transport Kind => Transport.HttpNamedPipe;

    public async Task<BrokerTransportResponse> Send(
        BrokerTransportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var pipe = new NamedPipeClientStream(
            ".",
            _pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        bool cleanupTransferred = false;
        try
        {
            using (CancellationTokenSource connectCancellation =
                   CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                connectCancellation.CancelAfter(ConnectTimeoutMilliseconds);
                await pipe.ConnectAsync(connectCancellation.Token).ConfigureAwait(false);
            }

            PolicyElevationHelperSynchronousStageResult<IDisposable> authentication =
                await PolicyElevationHelperSynchronousStageRunner.RunAsync(
                    () => _authenticate(pipe, request.Path),
                    cancellationToken,
                    static abandoned => abandoned.Dispose(),
                    pipe.Dispose).ConfigureAwait(false);
            if (!authentication.Completed)
            {
                // Authentication may still hold the pipe handle. Its continuation now owns both the
                // eventual authentication result and the pipe, so the helper can stop waiting safely.
                cleanupTransferred = true;
                cancellationToken.ThrowIfCancellationRequested();
                throw BrokerFailure(
                    BrokerClientErrorKind.Timeout,
                    $"Timed out authenticating the package broker at {request.Path}.",
                    request.Path);
            }

            using IDisposable server = authentication.Value;
            await BoundedNamedPipeBrokerTransport
                .WriteRequestAsync(pipe, request, cancellationToken)
                .ConfigureAwait(false);
            using CancellationTokenSource readCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readCancellation.CancelAfter(ReadTimeoutMilliseconds);
            return await BoundedNamedPipeBrokerTransport.ReadResponseAsync(
                pipe,
                request.Path,
                MaxPolicyManagementResponseBytes,
                BrokerClientErrorKind.BrokerUnavailable,
                readCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw BrokerFailure(
                BrokerClientErrorKind.Timeout,
                $"Timed out communicating with the package broker at {request.Path}.",
                request.Path,
                ex);
        }
        catch (IOException ex)
        {
            throw BrokerFailure(
                BrokerClientErrorKind.BrokerUnavailable,
                $"Unable to communicate with the package broker at {request.Path}.",
                request.Path,
                ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw BrokerFailure(
                BrokerClientErrorKind.BrokerUnavailable,
                $"Access to the package broker was denied while calling {request.Path}.",
                request.Path,
                ex);
        }
        finally
        {
            if (!cleanupTransferred)
                pipe.Dispose();
        }
    }

    public void Dispose()
    {
    }

    private static BrokerClientException BrokerFailure(
        BrokerClientErrorKind kind,
        string message,
        string path,
        Exception? innerException = null) =>
        new(kind, message, path, null, null, innerException);

    private sealed class AuthenticatedBrokerServer : IDisposable
    {
        private readonly SafeProcessHandle _process;
        private readonly PolicyElevationLocationVerification _location;

        private AuthenticatedBrokerServer(
            SafeProcessHandle process,
            PolicyElevationLocationVerification location)
        {
            _process = process;
            _location = location;
        }

        public static AuthenticatedBrokerServer Authenticate(
            NamedPipeClientStream pipe,
            string path)
        {
            if (!PolicyElevationNative.GetNamedPipeServerProcessId(
                    pipe.SafePipeHandle,
                    out uint serverProcessId)
                || serverProcessId == 0)
            {
                throw BrokerFailure(
                    BrokerClientErrorKind.BrokerUnavailable,
                    $"The package broker identity could not be verified for {path}.",
                    path);
            }

            SafeProcessHandle process = PolicyElevationNative.OpenProcess(
                PolicyElevationNative.ProcessQueryLimitedInformation,
                false,
                serverProcessId);
            if (process.IsInvalid)
            {
                process.Dispose();
                throw BrokerFailure(
                    BrokerClientErrorKind.BrokerUnavailable,
                    $"The package broker identity could not be verified for {path}.",
                    path);
            }

            PolicyElevationLocationVerification? location = null;
            try
            {
                if (!WindowsProcessInspector.TryGetProcessId(
                        process.DangerousGetHandle(),
                        out uint heldProcessId)
                    || heldProcessId != serverProcessId
                    || !WindowsProcessInspector.TryGetImagePath(
                        process.DangerousGetHandle(),
                        out string? imagePath)
                    || (imagePath = WindowsProcessInspector.TryGetCanonicalPath(imagePath)) is null
                    || !WindowsProcessInspector.TryGetTokenElevation(
                        process.DangerousGetHandle(),
                        out bool elevated,
                        out bool administrator)
                    || !elevated
                    || !administrator)
                {
                    throw BrokerFailure(
                        BrokerClientErrorKind.BrokerUnavailable,
                        $"The package broker identity could not be verified for {path}.",
                        path);
                }

                location = new WindowsProtectedLocationVerifier().VerifyExecutable(imagePath);
                if (!location.IsProtected
                    || !WindowsProcessInspector.PathsAreEqual(
                        location.CanonicalHelperPath,
                        imagePath))
                {
                    throw BrokerFailure(
                        BrokerClientErrorKind.BrokerUnavailable,
                        $"The package broker executable is not in a protected location for {path}.",
                        path);
                }

                PolicyElevationTrustResult trust =
                    new WindowsAuthenticodeTrustVerifier().VerifyExecutable(imagePath);
                if (!trust.IsTrusted)
                {
                    throw BrokerFailure(
                        BrokerClientErrorKind.BrokerUnavailable,
                        $"The package broker publisher could not be verified for {path}.",
                        path);
                }

                var authenticated = new AuthenticatedBrokerServer(process, location);
                process = null!;
                location = null;
                return authenticated;
            }
            finally
            {
                process?.Dispose();
                location?.Dispose();
            }
        }

        public void Dispose()
        {
            _location.Dispose();
            _process.Dispose();
        }
    }
}
