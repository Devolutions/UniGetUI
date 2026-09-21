#if WINDOWS
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Devolutions.Now.Policy.Api;
using Devolutions.Now.Policy.Client;
using Devolutions.Now.Policy.Model;
using UniGetUI.AgentPolicy.ElevatedHelper;
using ApiElevation = Devolutions.Now.Policy.Api.Elevation;
using ModelDecision = Devolutions.Now.Policy.Model.Decision;

namespace UniGetUI.PackageEngine.Tests.PolicyWriteElevation;

public class AuthenticatedBrokerTransportTests
{
    [Fact]
    public async Task ReplacePolicy_ReachesPutEndpointAndParsesStructuredResponse()
    {
        string pipeName = $"unigetui-policy-broker-{Guid.NewGuid():N}";
        string? requestLine = null;
        string? requestBody = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task server = RunServerAsync(
            pipeName,
            async pipe =>
            {
                (requestLine, requestBody) = await ReadRequestAsync(pipe, timeout.Token);
                await WriteResponseAsync(
                    pipe,
                    200,
                    BrokerSerializer.Serialize(Replacement()),
                    timeout.Token);
            },
            timeout.Token);
        using var transport = TestTransport(pipeName);
        using var client = new BrokerClient(ClientOptions(transport));

        PolicyReplacementResponse response = await client.ReplacePolicy(
            ReplacementRequest(),
            timeout.Token);
        await server;

        Assert.Equal("PUT /v1/policy HTTP/1.1", requestLine);
        using JsonDocument requestJson = JsonDocument.Parse(requestBody!);
        Assert.Equal(
            "Create",
            requestJson.RootElement.GetProperty("Operation").GetString());
        Assert.Equal("new-token", response.Management.StoreToken);
        Assert.Equal(PolicyManagementState.Active, response.Management.State);
    }

    [Fact]
    public async Task ReplacePolicy_MapsStructuredBrokerError()
    {
        string pipeName = $"unigetui-policy-broker-{Guid.NewGuid():N}";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task server = RunServerAsync(
            pipeName,
            async pipe =>
            {
                await ReadRequestAsync(pipe, timeout.Token);
                await WriteResponseAsync(
                    pipe,
                    403,
                    BrokerSerializer.Serialize(new ErrorResponse
                    {
                        Server = Server(),
                        Code = ErrorCode.Forbidden,
                        Message = "forbidden",
                    }),
                    timeout.Token);
            },
            timeout.Token);
        using var transport = TestTransport(pipeName);
        using var client = new BrokerClient(ClientOptions(transport));

        BrokerClientException exception = await Assert.ThrowsAsync<BrokerClientException>(
            () => client.ReplacePolicy(ReplacementRequest(), timeout.Token));
        await server;

        Assert.Equal(BrokerClientErrorKind.BrokerError, exception.Kind);
        Assert.Equal(403, exception.StatusCode);
        Assert.Equal(ErrorCode.Forbidden, exception.BrokerError!.Code);
    }

    [Fact]
    public async Task Transport_EofBeforeHeadersIsUnavailable()
    {
        string pipeName = $"unigetui-policy-broker-{Guid.NewGuid():N}";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task server = RunServerAsync(
            pipeName,
            async pipe =>
            {
                await ReadRequestAsync(pipe, timeout.Token);
            },
            timeout.Token);
        using var transport = TestTransport(pipeName);

        BrokerClientException exception = await Assert.ThrowsAsync<BrokerClientException>(
            () => transport.Send(
                new BrokerTransportRequest
                {
                    Method = "PUT",
                    Path = "/v1/policy",
                    Headers = new Dictionary<string, string>(),
                    Body = "{}",
                },
                timeout.Token));
        await server;

        Assert.Equal(BrokerClientErrorKind.BrokerUnavailable, exception.Kind);
    }

    private static AuthenticatedBrokerTransport TestTransport(string pipeName) =>
        new(pipeName, (_, _) => new NoopDisposable());

    private static BrokerClientOptions ClientOptions(IBrokerTransport transport) => new()
    {
        Transport = transport,
        RequestedElevation = ApiElevation.Elevated,
        EffectiveUser = @"CONTOSO\tester",
        ClientExecutablePath = @"C:\Tests\UniGetUI.PolicyElevator.exe",
        ClientVersion = "tests",
    };

    private static PolicyReplacementRequest ReplacementRequest() => new()
    {
        Draft = JsonDocument.Parse(PolicySerializer.Serialize(CanonicalDraft()))
            .RootElement.Clone(),
        Operation = PolicyReplacementOperation.Create,
        ConflictHandling = PolicyConflictHandling.Reject,
        ExpectedStoreToken = "store-token",
        ValidationReceipt = "validation-receipt",
    };

    private static PolicyReplacementResponse Replacement() => new()
    {
        Server = Server(),
        Policy = Policy(),
        Validation = new PolicyValidationResult
        {
            ValidatorVersion = "2026.9.17",
            IsValid = true,
            CanonicalDraft = CanonicalDraft(),
            ValidationReceipt = "new-receipt",
            Findings = [],
        },
        Management = new PolicyManagementSnapshot
        {
            State = PolicyManagementState.Active,
            ConfiguredPath = @"C:\ProgramData\Devolutions\PackageBroker\policy.json",
            StoreToken = "new-token",
            Source = PolicyConfigurationSource.DefaultPath,
            WriteCapability = PolicyWriteCapability.Writable,
            ElevationRequired = true,
            Policy = Policy(),
        },
    };

    private static PolicyDocument Policy() => new()
    {
        Metadata = new PolicyMetadata
        {
            Id = "policy-id",
            Publisher = "publisher",
            Revision = 1,
            PublishedAt = DateTimeOffset.Parse("2026-09-17T00:00:00Z"),
        },
        Enforcement = new PolicyEnforcement { DefaultDecision = ModelDecision.Deny },
        Rules = [],
    };

    private static PolicyDraftDocument CanonicalDraft() => new()
    {
        PolicyFormatVersion = PolicyFormatVersion.Current,
        Metadata = new PolicyDraftMetadata { Id = "policy-id", Publisher = "publisher" },
        Enforcement = new PolicyEnforcement { DefaultDecision = ModelDecision.Deny },
        Rules = [],
    };

    private static ServerContext Server() => new()
    {
        ServerVersion = "2026.9.17",
        Transport = Transport.HttpNamedPipe,
    };

    private static async Task RunServerAsync(
        string pipeName,
        Func<NamedPipeServerStream, Task> handler,
        CancellationToken cancellationToken)
    {
        using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        await server.WaitForConnectionAsync(cancellationToken);
        await handler(server);
    }

    private static async Task<(string RequestLine, string Body)> ReadRequestAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var bytes = new List<byte>();
        byte[] single = new byte[1];
        while (true)
        {
            int read = await stream.ReadAsync(single, cancellationToken);
            Assert.Equal(1, read);
            bytes.Add(single[0]);
            if (bytes.Count >= 4
                && bytes[^4] == '\r'
                && bytes[^3] == '\n'
                && bytes[^2] == '\r'
                && bytes[^1] == '\n')
            {
                break;
            }
        }

        string headers = Encoding.ASCII.GetString([.. bytes]);
        string[] lines = headers.Split("\r\n", StringSplitOptions.None);
        string contentLengthHeader = Assert.Single(
            lines,
            line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
        int contentLength = int.Parse(
            contentLengthHeader.Split(':', 2)[1],
            System.Globalization.CultureInfo.InvariantCulture);
        byte[] body = new byte[contentLength];
        await stream.ReadExactlyAsync(body, cancellationToken);
        return (lines[0], Encoding.UTF8.GetString(body));
    }

    private static async Task WriteResponseAsync(
        Stream stream,
        int statusCode,
        string body,
        CancellationToken cancellationToken)
    {
        byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
        byte[] headers = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {statusCode} Result\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(headers, cancellationToken);
        await stream.WriteAsync(bodyBytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
#endif
