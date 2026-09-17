using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using Devolutions.Now.Policy.Api;
using Devolutions.Now.Policy.Client;
using Microsoft.Win32.SafeHandles;
using UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation;
using UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation.Interop;

namespace UniGetUI.AgentPolicy.ElevatedHelper;

internal sealed class AuthenticatedBrokerTransport : IBrokerTransport
{
    private const string DefaultPipeName = "Devolutions.Now.PackageBroker.v1";
    private const int ConnectTimeoutMilliseconds = 5000;
    private const int ReadTimeoutMilliseconds = 30000;
    private const int MaxHeaderBytes = 65536;
    private readonly string _pipeName;

    public AuthenticatedBrokerTransport(string? pipeName = null)
    {
        _pipeName = string.IsNullOrWhiteSpace(pipeName) ? DefaultPipeName : pipeName;
    }

    public Transport Kind => Transport.HttpNamedPipe;

    public async Task<BrokerTransportResponse> Send(
        BrokerTransportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            using var pipe = new NamedPipeClientStream(
                ".",
                _pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous,
                TokenImpersonationLevel.Identification);
            using (CancellationTokenSource connectCancellation =
                   CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                connectCancellation.CancelAfter(ConnectTimeoutMilliseconds);
                await pipe.ConnectAsync(connectCancellation.Token).ConfigureAwait(false);
            }

            using AuthenticatedBrokerServer server =
                AuthenticatedBrokerServer.Authenticate(pipe, request.Path);
            await WriteRequestAsync(pipe, request, cancellationToken).ConfigureAwait(false);

            using CancellationTokenSource readCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readCancellation.CancelAfter(ReadTimeoutMilliseconds);
            return await ReadResponseAsync(
                pipe,
                request.Path,
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
    }

    public void Dispose()
    {
    }

    private static async Task WriteRequestAsync(
        Stream pipe,
        BrokerTransportRequest request,
        CancellationToken cancellationToken)
    {
        var headers = new StringBuilder()
            .Append(request.Method)
            .Append(' ')
            .Append(request.Path)
            .Append(" HTTP/1.1\r\n")
            .Append("Host: now-package-broker\r\n")
            .Append("Connection: close\r\n");
        foreach ((string name, string value) in request.Headers)
        {
            if (!name.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                headers.Append(name).Append(": ").Append(value).Append("\r\n");
            }
        }

        byte[]? body = request.Body is null ? null : Encoding.UTF8.GetBytes(request.Body);
        headers.Append("Content-Length: ").Append(body?.Length ?? 0).Append("\r\n\r\n");
        await pipe.WriteAsync(
            Encoding.ASCII.GetBytes(headers.ToString()),
            cancellationToken).ConfigureAwait(false);
        if (body is not null)
        {
            await pipe.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        }

        await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<BrokerTransportResponse> ReadResponseAsync(
        Stream pipe,
        string path,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[MaxHeaderBytes];
        int totalRead = 0;
        while (totalRead < MaxHeaderBytes)
        {
            int read = await pipe.ReadAsync(
                buffer.AsMemory(totalRead, MaxHeaderBytes - totalRead),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw BrokerFailure(
                    BrokerClientErrorKind.BrokerUnavailable,
                    $"The package broker disconnected before sending a complete response for {path}.",
                    path);
            }

            totalRead += read;
            string received = Encoding.ASCII.GetString(buffer, 0, totalRead);
            int headerEnd = received.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (headerEnd < 0)
            {
                continue;
            }

            string[] lines = received[..headerEnd].Split("\r\n");
            string[] status = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (status.Length < 2 || !int.TryParse(status[1], out int statusCode))
            {
                throw BrokerFailure(
                    BrokerClientErrorKind.InvalidResponse,
                    $"The package broker returned an invalid HTTP status line for {path}.",
                    path);
            }

            int? contentLength = null;
            for (int index = 1; index < lines.Length; index++)
            {
                int separator = lines[index].IndexOf(':');
                if (separator <= 0)
                {
                    continue;
                }

                string name = lines[index][..separator].Trim();
                if (!name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (contentLength is not null
                    || !int.TryParse(lines[index][(separator + 1)..].Trim(), out int parsed)
                    || parsed < 0
                    || parsed > BrokerApi.MaxPolicyManagementBodyBytes)
                {
                    throw BrokerFailure(
                        BrokerClientErrorKind.InvalidResponse,
                        $"The package broker returned an invalid Content-Length for {path}.",
                        path);
                }

                contentLength = parsed;
            }

            int bodyLength = contentLength ?? 0;
            int bodyStart = headerEnd + 4;
            if (bodyStart + bodyLength > buffer.Length)
            {
                Array.Resize(ref buffer, bodyStart + bodyLength);
            }

            int bodyRead = totalRead - bodyStart;
            while (bodyRead < bodyLength)
            {
                read = await pipe.ReadAsync(
                    buffer.AsMemory(bodyStart + bodyRead, bodyLength - bodyRead),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw BrokerFailure(
                        BrokerClientErrorKind.BrokerUnavailable,
                        $"The package broker disconnected before sending the complete response body for {path}.",
                        path);
                }

                bodyRead += read;
            }

            return new BrokerTransportResponse
            {
                StatusCode = statusCode,
                Body = Encoding.UTF8.GetString(buffer, bodyStart, bodyLength),
            };
        }

        throw BrokerFailure(
            BrokerClientErrorKind.InvalidResponse,
            $"The package broker returned response headers that are too large for {path}.",
            path);
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

                string? selfPath = WindowsProcessInspector.TryGetCurrentProcessCanonicalPath();
                if (selfPath is null
                    || !PolicyElevationSignerBinding.Bind(
                        new WindowsAuthenticodeTrustVerifier(),
                        selfPath,
                        imagePath).IsBound)
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
