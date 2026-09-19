using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Devolutions.Now.Policy.Api;
using Devolutions.Now.Policy.Client;
using UniGetUI.PackageEngine.AgentBroker;
using UniGetUI.PackageEngine.AgentBroker.PolicyManagement;

namespace UniGetUI.PackageEngine.Tests;

public class BrokerPolicyTransportTests
{
    public static IEnumerable<object?[]> WireResponses()
    {
        foreach (string endpoint in new[] { "inspection", "management", "validation" })
        {
            // Disconnect before reading the request, EOF before headers, and truncated headers/body.
            yield return [endpoint, null, "AgentUnavailable"];
            yield return [endpoint, "", "AgentUnavailable"];
            yield return [endpoint, "HTTP/1.1 200 OK\r\nContent-Length:", "AgentUnavailable"];
            yield return [endpoint, "HTTP/1.1 200 OK\r\nContent-Length: 10\r\n\r\n", "AgentUnavailable"];
            yield return [endpoint, "HTTP/1.1 200 OK\r\nContent-Length: 10\r\n\r\n{", "AgentUnavailable"];
            yield return [endpoint, "HTTP/1.1 403 Forbidden\r\nContent-Length: 10\r\n\r\n{", "AgentUnavailable"];

            foreach (string body in new[] { "", "{", "null", "{}" })
            {
                yield return [endpoint, HttpResponse(200, body), "InvalidResponse"];
            }

            foreach (int status in new[] { 401, 403 })
            {
                yield return [endpoint, HttpResponse(status, "{"), "InvalidResponse"];
                yield return [endpoint, HttpResponse(status, ""), "InvalidResponse"];
                string body = BrokerSerializer.Serialize(new ErrorResponse
                {
                    Server = new ServerContext { ServerVersion = "tests", Transport = Transport.HttpNamedPipe },
                    Code = status == 401 ? ErrorCode.Unauthorized : ErrorCode.Forbidden,
                    Message = "structured denial",
                });
                yield return [endpoint, HttpResponse(status, body), "AccessDenied"];
            }
        }
    }

    [Theory]
    [MemberData(nameof(WireResponses))]
    public async Task PolicyAdapters_DistinguishDisconnectsFromReceivedResponses(
        string endpoint,
        string? wireResponse,
        string expectedStatus)
    {
        string status = await WithPipeAsync(wireResponse, async (client, token) =>
        {
            if (endpoint == "inspection")
            {
                var inspector = new BrokerPolicyInspector(() => client, () => true);
                return (await inspector.InspectAsync(token)).Status.ToString();
            }

            var service = new BrokerPolicyManagementService(() => client, () => true);
            if (endpoint == "management")
            {
                return (await service.GetManagementAsync(token)).Status.ToString();
            }

            using JsonDocument draft = JsonDocument.Parse("{}");
            return (await service.ValidateAsync(draft.RootElement, token)).Status.ToString();
        });

        Assert.Equal(expectedStatus, status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("HTTP/1.1 200 OK\r\nContent-Length: 10\r\n\r\n{")]
    public async Task PinnedClient_ReportsEofWithoutHttpStatus(string wireResponse)
    {
        BrokerClientException exception = await WithPipeAsync(wireResponse, (client, token) =>
            Assert.ThrowsAsync<BrokerClientException>(() => client.GetPolicyManagement(token)));

        Assert.Equal(BrokerClientErrorKind.InvalidResponse, exception.Kind);
        Assert.Null(exception.StatusCode);
        Assert.Null(exception.BrokerError);
    }

    private static string HttpResponse(int status, string body) =>
        $"HTTP/1.1 {status} Test\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}";

    private static async Task<T> WithPipeAsync<T>(
        string? wireResponse,
        Func<BrokerClient, CancellationToken, Task<T>> action)
    {
        string pipeName = $"unigetui-policy-tests-{Guid.NewGuid():N}";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var client = new BrokerClient(new BrokerClientOptions
        {
            Transport = new NamedPipeBrokerTransport(pipeName),
            RequestedElevation = Elevation.Standard,
            EffectiveUser = "CONTOSO\\tester",
            ClientExecutablePath = @"C:\Tests\UniGetUI.exe",
            ClientVersion = "tests",
        });

        Task serve = ServeAsync();
        Task<T> result = action(client, timeout.Token);
        await Task.WhenAll(serve, result);
        return await result;

        async Task ServeAsync()
        {
            await server.WaitForConnectionAsync(timeout.Token);
            if (wireResponse is not null)
            {
                // Drain the ASCII test request so closing the pipe models response EOF, not a write failure.
                using var reader = new StreamReader(server, Encoding.ASCII, leaveOpen: true);
                int contentLength = 0;
                while (true)
                {
                    string? line = await reader.ReadLineAsync(timeout.Token);
                    Assert.NotNull(line);
                    if (line.Length == 0)
                        break;
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        contentLength = int.Parse(line["Content-Length:".Length..].Trim());
                }

                if (contentLength > 0)
                {
                    char[] requestBody = new char[contentLength];
                    Assert.Equal(contentLength, await reader.ReadBlockAsync(requestBody.AsMemory(), timeout.Token));
                }

                if (wireResponse.Length > 0)
                {
                    await server.WriteAsync(Encoding.UTF8.GetBytes(wireResponse), timeout.Token);
                    await server.FlushAsync(timeout.Token);
                    if (OperatingSystem.IsWindows())
                        server.WaitForPipeDrain();
                }
            }

            server.Disconnect();
        }
    }
}
