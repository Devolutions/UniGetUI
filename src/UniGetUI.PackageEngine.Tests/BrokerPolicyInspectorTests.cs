using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Devolutions.Now.Policy.Api;
using Devolutions.Now.Policy.Client;
using Devolutions.Now.Policy.Model;
using UniGetUI.PackageEngine.AgentBroker;
using ApiElevation = Devolutions.Now.Policy.Api.Elevation;
using ApiTransport = Devolutions.Now.Policy.Api.Transport;
using PolicyArchitecture = Devolutions.Now.Policy.Model.Architecture;
using PolicyDecision = Devolutions.Now.Policy.Model.Decision;
using PolicyElevation = Devolutions.Now.Policy.Model.Elevation;
using PolicyManagerName = Devolutions.Now.Policy.Model.ManagerName;
using PolicyOperation = Devolutions.Now.Policy.Model.Operation;
using PolicyScope = Devolutions.Now.Policy.Model.Scope;

namespace UniGetUI.PackageEngine.Tests;

public class BrokerPolicyInspectorTests
{
    [Fact]
    public async Task InspectAsync_ReturnsSharedPolicyAndCanonicalJson()
    {
        PolicyResponse response = BuildFullResponse();
        var transport = new FakeTransport(new BrokerTransportResponse
        {
            StatusCode = 200,
            Body = BrokerSerializer.Serialize(response),
        });

        BrokerPolicyInspectionResult result = await CreateInspector(transport).InspectAsync(CancellationToken.None);

        Assert.Equal(BrokerPolicyInspectionStatus.Connected, result.Status);
        Assert.Equal(PolicySerializer.Serialize(result.Response!.Policy), result.CanonicalJson);
        Assert.Equal("GET", Assert.Single(transport.Requests).Method);
        Assert.Equal("/v1/policy", transport.Requests[0].Path);
    }

    [Fact]
    public async Task InspectAsync_AcceptsCompatiblePolicyFormatVersion()
    {
        PolicyResponse response = BuildResponse("1.42.7");

        BrokerPolicyInspectionResult result = await InspectAsync(response);

        Assert.Equal(BrokerPolicyInspectionStatus.Connected, result.Status);
        Assert.Equal("1.42.7", result.Response!.Policy.PolicyFormatVersion.Value);
        Assert.Contains("\"PolicyFormatVersion\": \"1.42.7\"", result.CanonicalJson);
    }

    [Fact]
    public async Task InspectAsync_PreservesFinalMatchContractAndCanonicalOmission()
    {
        PolicyResponse response = BuildFullResponse();

        BrokerPolicyInspectionResult result = await InspectAsync(response);

        Assert.Equal(BrokerPolicyInspectionStatus.Connected, result.Status);
        JsonObject match = JsonNode.Parse(result.CanonicalJson!)!["Rules"]!.AsArray()[0]!["Match"]!.AsObject();
        Assert.Equal("community", match["SourceNames"]!.AsArray()[0]!.GetValue<string>());
        Assert.Equal("Contoso.*", match["PackageIdentifiers"]!["Patterns"]!.AsArray()[0]!.GetValue<string>());
        Assert.Equal("1.0.0", match["Version"]!["Range"]!["MinVersion"]!.GetValue<string>());
        Assert.Equal("Standard", match["ExecutionElevation"]!.AsArray()[0]!.GetValue<string>());
        Assert.False(match["Interactive"]!.GetValue<bool>());
        Assert.Null(match["SkipHashCheck"]);

        PolicyResponse minimalResponse = BuildResponse();
        BrokerPolicyInspectionResult minimalResult = await InspectAsync(minimalResponse);
        Assert.Equal(BrokerPolicyInspectionStatus.Connected, minimalResult.Status);
        Assert.DoesNotContain("\"SourceNames\"", minimalResult.CanonicalJson);
        Assert.DoesNotContain("\"PackageIdentifiers\"", minimalResult.CanonicalJson);
        Assert.DoesNotContain("\"ExecutionElevation\"", minimalResult.CanonicalJson);
        Assert.DoesNotContain("\"Interactive\"", minimalResult.CanonicalJson);
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("01.0.0")]
    [InlineData("2.0.0")]
    [InlineData("1.0.0\n")]
    public async Task InspectAsync_ClassifiesInvalidPolicyFormatVersionsAsInvalidResponse(
        string policyFormatVersion)
    {
        JsonObject body = ResponseJson(BuildResponse());
        body["Policy"]!["PolicyFormatVersion"] = policyFormatVersion;

        BrokerPolicyInspectionResult result = await InspectBodyAsync(body.ToJsonString());

        Assert.Equal(BrokerPolicyInspectionStatus.InvalidResponse, result.Status);
    }

    [Theory]
    [MemberData(nameof(InvalidFinalContractPayloads))]
    public async Task InspectAsync_ClassifiesInvalidFinalContractData(string body)
    {
        BrokerPolicyInspectionResult result = await InspectBodyAsync(body);

        Assert.Equal(BrokerPolicyInspectionStatus.InvalidResponse, result.Status);
    }

    [Fact]
    public async Task InspectAsync_RejectsDuplicatePolicyProperties()
    {
        string body = BrokerSerializer.Serialize(BuildResponse());
        string duplicate = new Regex(
            "\"Publisher\"\\s*:\\s*\"Contoso\"").Replace(
            body,
            "\"Publisher\":\"Contoso\",\"Publisher\":\"Fabrikam\"",
            1);
        Assert.NotEqual(body, duplicate);

        BrokerPolicyInspectionResult result = await InspectBodyAsync(duplicate);

        Assert.Equal(BrokerPolicyInspectionStatus.InvalidResponse, result.Status);
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
    [InlineData(404, ErrorCode.NotFound, BrokerPolicyInspectionStatus.PolicyUnavailable)]
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

        BrokerPolicyInspectionResult result = await CreateInspector(new FakeTransport(new()
        {
            StatusCode = statusCode,
            Body = BrokerSerializer.Serialize(error),
        })).InspectAsync(CancellationToken.None);

        Assert.Equal(expected, result.Status);
        Assert.Equal("simulated failure", result.ErrorMessage);
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

    [Fact]
    public async Task InspectAsync_ClassifiesNamedPipePermissionFailureAsUnavailable()
    {
        var exception = new BrokerClientException(
            BrokerClientErrorKind.BrokerUnavailable,
            "denied",
            innerException: new UnauthorizedAccessException());
        var inspector = CreateInspector(new FakeTransport(exception: exception));

        BrokerPolicyInspectionResult result = await inspector.InspectAsync(CancellationToken.None);
        Assert.Equal(BrokerPolicyInspectionStatus.AgentUnavailable, result.Status);
    }
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("null")]
    public async Task InspectAsync_ClassifiesMalformedPayload(string body)
    {
        BrokerPolicyInspectionResult result = await InspectBodyAsync(body);

        Assert.Equal(BrokerPolicyInspectionStatus.InvalidResponse, result.Status);
    }

    [Fact]
    public async Task InspectAsync_PropagatesCallerCancellation()
    {
        var transport = new FakeTransport(waitForCancellation: true);
        using var cancellation = new CancellationTokenSource();
        Task<BrokerPolicyInspectionResult> pending =
            CreateInspector(transport).InspectAsync(cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    public static IEnumerable<object[]> InvalidFinalContractPayloads()
    {
        yield return [Mutate(root => root["Unexpected"] = true)];
        yield return [Mutate(root => root["Policy"]!["Metadata"]!["Unexpected"] = true)];
        yield return [Mutate(root => root["Policy"]!["Metadata"]!["Id"] = "")];
        yield return [Mutate(root => root["Policy"]!["Metadata"]!["Publisher"] = "")];
        yield return [Mutate(root =>
        {
            root["Policy"]!["Metadata"]!["ValidFrom"] = "2026-08-18T00:00:00+00:00";
            root["Policy"]!["Metadata"]!["ValidUntil"] = "2026-08-18T00:00:00+00:00";
        })];
        yield return [Mutate(root => FirstRule(root)["Match"]!["SourceNames"] =
            new JsonArray("community", "community"))];
        yield return [Mutate(root => FirstRule(root)["Match"]!["PackageIdentifiers"] =
            new JsonObject
            {
                ["Exact"] = new JsonArray("Contoso.App"),
                ["Patterns"] = new JsonArray("Contoso.*"),
            })];
        yield return [Mutate(root => FirstRule(root)["Match"]!["Version"] =
            new JsonObject
            {
                ["Exact"] = new JsonArray("1.0.0"),
                ["Range"] = new JsonObject { ["MinVersion"] = "1.0.0" },
            })];
        yield return [Mutate(root =>
        {
            FirstRule(root)["Match"]!["Managers"] = new JsonArray("Winget", "Scoop");
            FirstRule(root)["Match"]!["SourceNames"] = new JsonArray("community");
        })];
        yield return [Mutate(root =>
        {
            FirstRule(root)["Decision"] = "Deny";
            FirstRule(root)["Constraints"] = new JsonObject();
        })];
        yield return [Mutate(root => FirstRule(root)["Priority"] = int.MaxValue + 1u)];
        yield return [Mutate(root => FirstRule(root)["Match"] = new JsonObject())];
        yield return [Mutate(root =>
        {
            JsonArray rules = root["Policy"]!["Rules"]!.AsArray();
            JsonObject template = rules[0]!.AsObject();
            for (int index = 1; index <= 1024; index++)
            {
                JsonObject rule = template.DeepClone().AsObject();
                rule["Id"] = $"allow-install-{index}";
                rules.Add(rule);
            }
        })];
    }

    private static async Task<BrokerPolicyInspectionResult> InspectAsync(PolicyResponse response) =>
        await CreateInspector(new FakeTransport(new()
        {
            StatusCode = 200,
            Body = BrokerSerializer.Serialize(response),
        })).InspectAsync(CancellationToken.None);

    private static async Task<BrokerPolicyInspectionResult> InspectBodyAsync(string body) =>
        await CreateInspector(new FakeTransport(new()
        {
            StatusCode = 200,
            Body = body,
        })).InspectAsync(CancellationToken.None);

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

    private static PolicyResponse BuildResponse(string policyFormatVersion = "1.0.0") =>
        new()
        {
            Server = new ServerContext
            {
                ServerVersion = "2026.8-tests",
                Transport = ApiTransport.HttpNamedPipe,
            },
            Policy = new PolicyDocument
            {
                PolicyFormatVersion = PolicyFormatVersion.Parse(policyFormatVersion),
                Metadata = new PolicyMetadata
                {
                    Id = "contoso.policy",
                    Publisher = "Contoso",
                    Revision = 3,
                    PublishedAt = DateTimeOffset.Parse("2026-08-18T00:00:00Z"),
                },
                Enforcement = new PolicyEnforcement { DefaultDecision = PolicyDecision.Deny },
                Rules =
                [
                    new PolicyRule
                    {
                        Id = "allow-install",
                        Priority = 10,
                        Decision = PolicyDecision.Allow,
                        Match = new PolicyMatch { Operations = [PolicyOperation.Install] },
                    },
                ],
            },
        };

    private static PolicyResponse BuildFullResponse()
    {
        PolicyResponse response = BuildResponse();
        PolicyMatch match = response.Policy.Rules[0].Match;
        match.Managers = [PolicyManagerName.Winget];
        match.SourceNames = ["community"];
        match.PackageIdentifiers = PackageIdentifiersWithPatterns("Contoso.*");
        match.Version = VersionWithRange("1.0.0", "2.0.0");
        match.Scopes = [PolicyScope.User];
        match.Architectures = [PolicyArchitecture.X64];
        match.ExecutionElevation = [PolicyElevation.Standard];
        match.Interactive = false;
        match.PreRelease = true;
        response.Policy.Rules[0].Constraints = new PolicyConstraints
        {
            AllowInteractive = false,
            AllowedCustomParameters = ["--silent"],
        };
        return response;
    }

    private static PackageIdentifierCondition PackageIdentifiersWithPatterns(params string[] patterns)
    {
        var condition = new PackageIdentifierCondition();
        condition.UsePatterns([.. patterns]);
        return condition;
    }

    private static VersionCondition VersionWithRange(string minVersion, string maxVersion)
    {
        var condition = new VersionCondition();
        condition.UseRange(new VersionRange
        {
            MinVersion = minVersion,
            MaxVersion = maxVersion,
        });
        return condition;
    }

    private static JsonObject ResponseJson(PolicyResponse response) =>
        JsonNode.Parse(BrokerSerializer.Serialize(response))!.AsObject();

    private static string Mutate(Action<JsonObject> mutation)
    {
        JsonObject root = ResponseJson(BuildFullResponse());
        mutation(root);
        return root.ToJsonString();
    }

    private static JsonNode FirstRule(JsonObject root) =>
        root["Policy"]!["Rules"]!.AsArray()[0]!;

    private sealed class FakeTransport : IBrokerTransport
    {
        private readonly BrokerTransportResponse? _response;
        private readonly bool _waitForCancellation;

        public FakeTransport(BrokerTransportResponse? response = null, bool waitForCancellation = false)
        {
            _response = response;
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

            return _response ?? throw new InvalidOperationException("No response configured.");
        }

        public void Dispose()
        {
        }
    }
}
