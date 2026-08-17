using Devolutions.Now.Policy.Api;
using Devolutions.Now.Policy.Client;
using Devolutions.Now.Policy.Model;
using System.Text.Json.Nodes;
using UniGetUI.PackageEngine.AgentBroker;
using ApiTransport = Devolutions.Now.Policy.Api.Transport;
using PolicyDecision = Devolutions.Now.Policy.Model.Decision;
using ApiElevation = Devolutions.Now.Policy.Api.Elevation;

namespace UniGetUI.PackageEngine.Tests;

public class BrokerPolicyInspectorTests
{
    [Fact]
    public async Task InspectAsync_ReturnsSharedPolicyAndCanonicalJson()
    {
        PolicyResponse response = BuildResponse();
        var transport = new FakeTransport(new BrokerTransportResponse
        {
            StatusCode = 200,
            Body = BrokerJson.Serialize(response),
        });
        var inspector = CreateInspector(transport);

        BrokerPolicyInspectionResult result = await inspector.InspectAsync(CancellationToken.None);

        Assert.Equal(BrokerPolicyInspectionStatus.Connected, result.Status);
        Assert.Same(response.Policy.GetType(), result.Response!.Policy.GetType());
        Assert.Equal(PolicyJson.Serialize(result.Response.Policy), result.CanonicalJson);
        BrokerTransportRequest request = Assert.Single(transport.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal("/v1/policy", request.Path);
    }

    [Fact]
    public async Task InspectAsync_DoesNotConstructClientOnNonWindows()
    {
        bool constructed = false;
        var inspector = new BrokerPolicyInspector(
            () =>
            {
                constructed = true;
                return CreateClient(new FakeTransport());
            },
            () => false);

        BrokerPolicyInspectionResult result = await inspector.InspectAsync(CancellationToken.None);

        Assert.Equal(BrokerPolicyInspectionStatus.UnsupportedPlatform, result.Status);
        Assert.False(constructed);
    }

    [Theory]
    [InlineData(404, ErrorCode.NotFound, BrokerPolicyInspectionStatus.Unsupported)]
    [InlineData(401, ErrorCode.Unauthorized, BrokerPolicyInspectionStatus.AccessDenied)]
    [InlineData(403, ErrorCode.Forbidden, BrokerPolicyInspectionStatus.AccessDenied)]
    [InlineData(409, ErrorCode.Conflict, BrokerPolicyInspectionStatus.PolicyUnavailable)]
    [InlineData(500, ErrorCode.InternalError, BrokerPolicyInspectionStatus.PolicyUnavailable)]
    public async Task InspectAsync_ClassifiesStructuredBrokerErrors(
        int statusCode,
        ErrorCode errorCode,
        BrokerPolicyInspectionStatus expected)
    {
        var error = new ErrorResponse
        {
            Server = new ServerContext { ServerVersion = "tests", Transport = ApiTransport.HttpNamedPipe },
            Code = errorCode,
            Message = "simulated failure",
        };
        var inspector = CreateInspector(new FakeTransport(new BrokerTransportResponse
        {
            StatusCode = statusCode,
            Body = BrokerJson.Serialize(error),
        }));

        BrokerPolicyInspectionResult result = await inspector.InspectAsync(CancellationToken.None);

        Assert.Equal(expected, result.Status);
        Assert.Equal("simulated failure", result.ErrorMessage);
    }

    [Fact]
    public async Task InspectAsync_ClassifiesLegacyEmptyNotFoundAsUnsupported()
    {
        var inspector = CreateInspector(new FakeTransport(new BrokerTransportResponse
        {
            StatusCode = 404,
            Body = "",
        }));

        BrokerPolicyInspectionResult result = await inspector.InspectAsync(CancellationToken.None);

        Assert.Equal(BrokerPolicyInspectionStatus.Unsupported, result.Status);
    }

    [Theory]
    [InlineData(BrokerClientErrorKind.BrokerUnavailable)]
    [InlineData(BrokerClientErrorKind.Timeout)]
    public async Task InspectAsync_ClassifiesTransportFailureAsUnavailable(BrokerClientErrorKind kind)
    {
        var inspector = CreateInspector(new FakeTransport(exception: new BrokerClientException(kind, "offline")));

        BrokerPolicyInspectionResult result = await inspector.InspectAsync(CancellationToken.None);

        Assert.Equal(BrokerPolicyInspectionStatus.AgentUnavailable, result.Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("null")]
    public async Task InspectAsync_ClassifiesInvalidPayload(string body)
    {
        var inspector = CreateInspector(new FakeTransport(new BrokerTransportResponse
        {
            StatusCode = 200,
            Body = body,
        }));

        BrokerPolicyInspectionResult result = await inspector.InspectAsync(CancellationToken.None);

        Assert.Equal(BrokerPolicyInspectionStatus.InvalidResponse, result.Status);
    }

    [Theory]
    [MemberData(nameof(InvalidNestedPayloads))]
    public async Task InspectAsync_ClassifiesNullRequiredPolicyData(string body)
    {
        var inspector = CreateInspector(new FakeTransport(new BrokerTransportResponse
        {
            StatusCode = 200,
            Body = body,
        }));

        BrokerPolicyInspectionResult result = await inspector.InspectAsync(CancellationToken.None);

        Assert.Equal(BrokerPolicyInspectionStatus.InvalidResponse, result.Status);
    }

    [Fact]
    public async Task InspectAsync_PropagatesCallerCancellation()
    {
        var transport = new FakeTransport(waitForCancellation: true);
        var inspector = CreateInspector(transport);
        using var cancellation = new CancellationTokenSource();

        Task<BrokerPolicyInspectionResult> pending = inspector.InspectAsync(cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    private static BrokerPolicyInspector CreateInspector(FakeTransport transport) =>
        new(() => CreateClient(transport), () => true);

    private static BrokerClient CreateClient(FakeTransport transport) =>
        new(new BrokerClientOptions
        {
            Transport = transport,
            RequestedElevation = ApiElevation.Standard,
            EffectiveUser = "CONTOSO\\tester",
            ClientExecutablePath = @"C:\Tests\UniGetUI.exe",
            ClientVersion = "tests",
        });

    private static PolicyResponse BuildResponse() =>
        new()
        {
            Server = new ServerContext
            {
                ServerVersion = "2026.8-tests",
                Transport = ApiTransport.HttpNamedPipe,
            },
            Policy = new PolicyDocument
            {
                PolicyVersion = "1.0.0",
                Metadata = new PolicyMetadata
                {
                    Id = "contoso.policy",
                    Publisher = "Contoso",
                    Revision = 3,
                    PublishedAt = DateTimeOffset.Parse("2026-08-18T00:00:00Z"),
                },
                Enforcement = new PolicyEnforcement
                {
                    DefaultDecision = PolicyDecision.Deny,
                    RulePrecedence = RulePrecedence.PriorityThenDeny,
                },
            },
        };

    public static IEnumerable<object[]> InvalidNestedPayloads()
    {
        yield return [WithExplicitNull(root => root["ResponseVersion"] = null)];
        yield return [WithExplicitNull(root => root["Server"] = null)];
        yield return [WithExplicitNull(root => root["Server"]!["ServerVersion"] = null)];
        yield return [WithExplicitNull(root => root["Policy"]!["$schema"] = null)];
        yield return [WithExplicitNull(root => root["Policy"]!["PolicyVersion"] = null)];
        yield return [WithExplicitNull(root => root["Policy"]!["PolicyType"] = null)];
        yield return [WithExplicitNull(root => root["Policy"]!["Metadata"] = null)];
        yield return [WithExplicitNull(root => root["Policy"]!["Metadata"]!["Id"] = null)];
        yield return [WithExplicitNull(root => root["Policy"]!["Metadata"]!["Publisher"] = null)];
        yield return [WithExplicitNull(root => root["Policy"]!["Enforcement"] = null)];
        yield return [WithExplicitNull(root => root["Policy"]!["Rules"] = null)];
        yield return [WithExplicitNull(root => FirstRule(root)["Id"] = null)];
        yield return [WithExplicitNull(root => FirstRule(root)["Match"] = null)];
        yield return [WithExplicitNull(root => FirstRule(root)["Match"]!["Sources"] = null)];
        yield return [WithExplicitNull(
            root => FirstRule(root)["Match"]!["Sources"]!.AsArray().Add(null))];
        yield return [WithExplicitNull(
            root => FirstRule(root)["Constraints"]!["AllowedCustomParameters"] = null)];
        yield return [WithExplicitNull(
            root => FirstRule(root)["Constraints"]!["AllowedCustomParameters"]!.AsArray().Add(null))];
    }

    private static string WithExplicitNull(Action<JsonObject> mutation)
    {
        PolicyResponse response = BuildResponse();
        response.Policy.Rules.Add(new PolicyRule { Constraints = new PolicyConstraints() });
        JsonObject root = JsonNode.Parse(BrokerJson.Serialize(response))!.AsObject();
        mutation(root);
        return root.ToJsonString();
    }

    private static JsonNode FirstRule(JsonObject root) =>
        root["Policy"]!["Rules"]!.AsArray()[0]!;

    private sealed class FakeTransport : IBrokerTransport
    {
        private readonly BrokerTransportResponse? _response;
        private readonly Exception? _exception;
        private readonly bool _waitForCancellation;

        public FakeTransport(
            BrokerTransportResponse? response = null,
            Exception? exception = null,
            bool waitForCancellation = false)
        {
            _response = response;
            _exception = exception;
            _waitForCancellation = waitForCancellation;
        }

        public ApiTransport Kind => ApiTransport.HttpNamedPipe;
        public List<BrokerTransportRequest> Requests { get; } = [];

        public async Task<BrokerTransportResponse> Send(
            BrokerTransportRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (_waitForCancellation)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            if (_exception is not null) throw _exception;
            return _response ?? throw new InvalidOperationException("No response configured.");
        }

        public void Dispose()
        {
        }
    }
}
