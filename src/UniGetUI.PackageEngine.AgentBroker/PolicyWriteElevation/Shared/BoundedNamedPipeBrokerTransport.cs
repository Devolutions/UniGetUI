using System.Globalization;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using Devolutions.Now.Policy.Api;
using Devolutions.Now.Policy.Client;

namespace UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation;

/// <summary>
/// HTTP/1.1-over-named-pipe transport with a caller-supplied response-body budget.
/// The fixed-length broker protocol lets the transport reject an oversized response before
/// allocating its body buffer or handing JSON to <see cref="BrokerClient"/>.
/// </summary>
internal sealed class BoundedNamedPipeBrokerTransport : IBrokerTransport
{
    private const int ConnectTimeoutMilliseconds = 5000;
    private const int ReadTimeoutMilliseconds = 30000;
    private const int MaxHeaderBytes = 65536;
    // A replacement response can contain the accepted policy three times: the parsed policy,
    // Validation.CanonicalDraft, and Management.Policy. A fourth full contract budget covers the
    // response envelope, findings, and metadata without constraining any one policy below 16 MiB.
    internal const int MaxPolicyManagementResponseBodyBytes =
        BrokerApi.MaxPolicyManagementBodyBytes * 4;

    private readonly string _pipeName;
    private readonly int _maxResponseBodyBytes;
    private readonly TokenImpersonationLevel _impersonationLevel;

    public BoundedNamedPipeBrokerTransport(
        int maxResponseBodyBytes,
        string? pipeName = null,
        TokenImpersonationLevel impersonationLevel = TokenImpersonationLevel.None)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxResponseBodyBytes);
        _maxResponseBodyBytes = maxResponseBodyBytes;
        _pipeName = string.IsNullOrWhiteSpace(pipeName) ? BrokerApi.DefaultPipeName : pipeName;
        _impersonationLevel = impersonationLevel;
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
                ".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, _impersonationLevel);
            using (CancellationTokenSource connectCancellation =
                   CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                connectCancellation.CancelAfter(ConnectTimeoutMilliseconds);
                await pipe.ConnectAsync(connectCancellation.Token).ConfigureAwait(false);
            }

            await WriteRequestAsync(pipe, request, cancellationToken).ConfigureAwait(false);
            using CancellationTokenSource readCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readCancellation.CancelAfter(ReadTimeoutMilliseconds);
            return await ReadResponseAsync(
                pipe,
                request.Path,
                _maxResponseBodyBytes,
                BrokerClientErrorKind.InvalidResponse,
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

    internal static async Task WriteRequestAsync(
        Stream pipe,
        BrokerTransportRequest request,
        CancellationToken cancellationToken)
    {
        var headers = new StringBuilder()
            .Append(request.Method).Append(' ').Append(request.Path).Append(" HTTP/1.1\r\n")
            .Append("Host: now-package-broker\r\n")
            .Append("Connection: close\r\n");
        foreach ((string name, string value) in request.Headers)
        {
            if (!name.Equals("Host", StringComparison.OrdinalIgnoreCase))
                headers.Append(name).Append(": ").Append(value).Append("\r\n");
        }

        byte[]? body = request.Body is null ? null : Encoding.UTF8.GetBytes(request.Body);
        headers.Append("Content-Length: ").Append(body?.Length ?? 0).Append("\r\n\r\n");
        await pipe.WriteAsync(Encoding.ASCII.GetBytes(headers.ToString()), cancellationToken)
            .ConfigureAwait(false);
        if (body is not null)
            await pipe.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<BrokerTransportResponse> ReadResponseAsync(
        Stream pipe,
        string path,
        int maxResponseBodyBytes,
        BrokerClientErrorKind incompleteResponseKind,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[MaxHeaderBytes];
        int totalRead = 0;
        while (totalRead < MaxHeaderBytes)
        {
            int read = await pipe.ReadAsync(
                buffer.AsMemory(totalRead, MaxHeaderBytes - totalRead), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                throw BrokerFailure(
                    incompleteResponseKind,
                    $"The package broker disconnected before sending a complete response for {path}.",
                    path);
            }

            totalRead += read;
            string received = Encoding.ASCII.GetString(buffer, 0, totalRead);
            int headerEnd = received.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (headerEnd < 0)
                continue;

            string[] lines = received[..headerEnd].Split("\r\n");
            string[] status = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (status.Length < 2
                || !int.TryParse(status[1], NumberStyles.None, CultureInfo.InvariantCulture, out int statusCode))
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
                    continue;

                string name = lines[index][..separator].Trim();
                if (!name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (contentLength is not null
                    || !int.TryParse(
                        lines[index][(separator + 1)..].Trim(),
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out int parsed)
                    || parsed < 0
                    || parsed > maxResponseBodyBytes)
                {
                    throw BrokerFailure(
                        BrokerClientErrorKind.InvalidResponse,
                        $"The package broker returned an invalid or oversized Content-Length for {path}.",
                        path,
                        statusCode: statusCode);
                }

                contentLength = parsed;
            }

            if (contentLength is null)
            {
                throw BrokerFailure(
                    BrokerClientErrorKind.InvalidResponse,
                    $"The package broker response omitted Content-Length for {path}.",
                    path,
                    statusCode: statusCode);
            }

            int bodyLength = contentLength.Value;
            int bodyStart = headerEnd + 4;
            int bodyRead = totalRead - bodyStart;
            if (bodyRead > bodyLength)
            {
                throw BrokerFailure(
                    BrokerClientErrorKind.InvalidResponse,
                    $"The package broker returned more response data than declared for {path}.",
                    path,
                    statusCode: statusCode);
            }

            if (bodyStart + bodyLength > buffer.Length)
                Array.Resize(ref buffer, bodyStart + bodyLength);

            while (bodyRead < bodyLength)
            {
                read = await pipe.ReadAsync(
                    buffer.AsMemory(bodyStart + bodyRead, bodyLength - bodyRead), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    throw BrokerFailure(
                        incompleteResponseKind,
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
        Exception? innerException = null,
        int? statusCode = null) =>
        new(kind, message, path, statusCode, null, innerException);
}
