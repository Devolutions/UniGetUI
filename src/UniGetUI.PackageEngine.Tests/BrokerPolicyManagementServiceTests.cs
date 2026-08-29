using System.Text.Json;
using System.Text.Json.Nodes;
using Devolutions.Now.Policy.Api;
using Devolutions.Now.Policy.Client;
using Devolutions.Now.Policy.Model;
using UniGetUI.PackageEngine.AgentBroker.PolicyManagement;
using ApiElevation = Devolutions.Now.Policy.Api.Elevation;
using ApiTransport = Devolutions.Now.Policy.Api.Transport;
using ModelDecision = Devolutions.Now.Policy.Model.Decision;
using ModelOperation = Devolutions.Now.Policy.Model.Operation;

namespace UniGetUI.PackageEngine.Tests;

public class BrokerPolicyManagementServiceTests
{
    // --- GetManagementAsync: snapshot mapping ----------------------------------------------------

    [Fact]
    public async Task GetManagementAsync_ReturnsActiveSnapshotWithNoDiagnostics()
    {
        PolicyManagementSnapshot snapshot = BuildActiveSnapshot();
        var service = CreateService(new FakeTransport(new BrokerTransportResponse
        {
            StatusCode = 200,
            Body = BrokerJson.Serialize(BuildManagementResponse(snapshot)),
        }));

        BrokerPolicyManagementResult result = await service.GetManagementAsync(CancellationToken.None);

        Assert.Equal(BrokerPolicyManagementStatus.Retrieved, result.Status);
        Assert.Equal(PolicyManagementState.Active, result.Snapshot!.State);
        Assert.NotNull(result.Snapshot.Policy);
        Assert.Null(result.Diagnostics);
    }

    [Fact]
    public async Task GetManagementAsync_ReturnsMissingSnapshot()
    {
        var snapshot = new PolicyManagementSnapshot
        {
            State = PolicyManagementState.Missing,
            ConfiguredPath = "",
            StoreToken = "missing-token",
            Source = PolicyConfigurationSource.DefaultPath,
            WriteCapability = PolicyWriteCapability.Writable,
        };
        var service = CreateService(new FakeTransport(new BrokerTransportResponse
        {
            StatusCode = 200,
            Body = BrokerJson.Serialize(BuildManagementResponse(snapshot)),
        }));

        BrokerPolicyManagementResult result = await service.GetManagementAsync(CancellationToken.None);

        Assert.Equal(BrokerPolicyManagementStatus.Retrieved, result.Status);
        Assert.Equal(PolicyManagementState.Missing, result.Snapshot!.State);
        Assert.Null(result.Snapshot.Policy);
        Assert.Null(result.Diagnostics);
    }

    [Fact]
    public async Task GetManagementAsync_ReturnsInvalidSnapshotWithSanitizedDiagnostics()
    {
        string hugePath = new string('p', 100_000);
        string hugeMessage = string.Concat(Enumerable.Repeat("\U0001F600", 5_000));
        var arguments = Enumerable.Range(0, 200)
            .ToDictionary(i => $"arg{i}", i => JsonDocument.Parse($"\"{new string('v', 1_000)}\"").RootElement);
        var snapshot = new PolicyManagementSnapshot
        {
            State = PolicyManagementState.Invalid,
            ConfiguredPath = hugePath,
            StoreToken = "invalid-token",
            Source = PolicyConfigurationSource.ConfiguredPath,
            WriteCapability = PolicyWriteCapability.Writable,
            InvalidDiagnostics = new InvalidPolicyDiagnostics
            {
                DiagnosticsVersion = "1.0",
                Findings =
                [
                    new PolicyFinding
                    {
                        FindingVersion = "1.0",
                        Severity = PolicyFindingSeverity.Error,
                        Code = PolicyFindingCode.SchemaViolation,
                        Path = new string('x', 10_000),
                        RuleId = new string('y', 10_000),
                        Message = hugeMessage,
                        Arguments = arguments,
                    },
                ],
            },
        };
        var service = CreateService(new FakeTransport(new BrokerTransportResponse
        {
            StatusCode = 200,
            Body = BrokerJson.Serialize(BuildManagementResponse(snapshot)),
        }));

        BrokerPolicyManagementResult result = await service.GetManagementAsync(CancellationToken.None);

        Assert.Equal(BrokerPolicyManagementStatus.Retrieved, result.Status);
        Assert.Equal(PolicyManagementState.Invalid, result.Snapshot!.State);
        Assert.NotNull(result.Diagnostics);
        BrokerPolicySanitizedFinding finding = Assert.Single(result.Diagnostics!.Findings);
        Assert.True(finding.PathTruncated);
        Assert.True(finding.RuleIdTruncated);
        Assert.True(finding.MessageTruncated);
        Assert.True(finding.ArgumentsTruncated);
        Assert.True(finding.Path!.Length <= BrokerPolicyManagementLimits.MaxSanitizedTextLength);
        Assert.True(finding.RuleId!.Length <= BrokerPolicyManagementLimits.MaxSanitizedTextLength);
        Assert.True(finding.Message.EnumerateRunes().Count() <= BrokerPolicyManagementLimits.MaxSanitizedTextLength);
        Assert.True(finding.Arguments.Count <= BrokerPolicyManagementLimits.MaxSanitizedArgumentEntries);
    }

    [Fact]
    public async Task GetManagementAsync_StripsControlCharactersFromDiagnostics()
    {
        var snapshot = new PolicyManagementSnapshot
        {
            State = PolicyManagementState.Invalid,
            ConfiguredPath = "C:\\policy.json",
            StoreToken = "tok",
            Source = PolicyConfigurationSource.ConfiguredPath,
            WriteCapability = PolicyWriteCapability.Writable,
            InvalidDiagnostics = new InvalidPolicyDiagnostics
            {
                DiagnosticsVersion = "1.0",
                Findings =
                [
                    new PolicyFinding
                    {
                        FindingVersion = "1.0",
                        Severity = PolicyFindingSeverity.Error,
                        Code = PolicyFindingCode.SchemaViolation,
                        Message = "line1\u0007\nline2\u0000end",
                    },
                ],
            },
        };
        var service = CreateService(new FakeTransport(new BrokerTransportResponse
        {
            StatusCode = 200,
            Body = BrokerJson.Serialize(BuildManagementResponse(snapshot)),
        }));

        BrokerPolicyManagementResult result = await service.GetManagementAsync(CancellationToken.None);

        BrokerPolicySanitizedFinding finding = Assert.Single(result.Diagnostics!.Findings);
        Assert.DoesNotContain(finding.Message, char.IsControl);
    }

    [Theory]
    [InlineData(PolicyWriteCapability.Writable, null)]
    [InlineData(PolicyWriteCapability.ReadOnly, PolicyReadOnlyReason.ManagementDisabled)]
    [InlineData(PolicyWriteCapability.ReadOnly, PolicyReadOnlyReason.PathNotConfigured)]
    [InlineData(PolicyWriteCapability.ReadOnly, PolicyReadOnlyReason.UnsupportedFormat)]
    [InlineData(PolicyWriteCapability.ReadOnly, PolicyReadOnlyReason.UnsafePath)]
    [InlineData(PolicyWriteCapability.ReadOnly, PolicyReadOnlyReason.InsufficientPermissions)]
    [InlineData(PolicyWriteCapability.ReadOnly, PolicyReadOnlyReason.UnsupportedFileSystem)]
    [InlineData(PolicyWriteCapability.Unsupported, PolicyReadOnlyReason.UnsupportedFormat)]
    public async Task GetManagementAsync_PreservesWriteCapabilityAndReadOnlyReason(
        PolicyWriteCapability capability,
        PolicyReadOnlyReason? reason)
    {
        var snapshot = new PolicyManagementSnapshot
        {
            State = PolicyManagementState.Missing,
            ConfiguredPath = "",
            StoreToken = "tok",
            Source = PolicyConfigurationSource.DefaultPath,
            WriteCapability = capability,
            ReadOnlyReason = reason,
        };
        var service = CreateService(new FakeTransport(new BrokerTransportResponse
        {
            StatusCode = 200,
            Body = BrokerJson.Serialize(BuildManagementResponse(snapshot)),
        }));

        BrokerPolicyManagementResult result = await service.GetManagementAsync(CancellationToken.None);

        Assert.Equal(BrokerPolicyManagementStatus.Retrieved, result.Status);
        Assert.Equal(capability, result.Snapshot!.WriteCapability);
        Assert.Equal(reason, result.Snapshot.ReadOnlyReason);
    }

    // --- GetManagementAsync: envelope validation (gaps not enforced by the package) --------------

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("")]
    [InlineData("1.0\n")]
    public async Task GetManagementAsync_RejectsInvalidResponseVersion(string responseVersion)
    {
        JsonObject root = ToJsonObject(BuildManagementResponse(BuildMissingSnapshot()));
        root["ResponseVersion"] = responseVersion;
        var service = CreateService(new FakeTransport(new BrokerTransportResponse
        {
            StatusCode = 200,
            Body = root.ToJsonString(),
        }));

        BrokerPolicyManagementResult result = await service.GetManagementAsync(CancellationToken.None);

        Assert.Equal(BrokerPolicyManagementStatus.InvalidResponse, result.Status);
    }

    [Fact]
    public async Task GetManagementAsync_RejectsEmptyServerVersion()
    {
        JsonObject root = ToJsonObject(BuildManagementResponse(BuildMissingSnapshot()));
        root["Server"]!["ServerVersion"] = "";
        var service = CreateService(new FakeTransport(new BrokerTransportResponse
        {
            StatusCode = 200,
            Body = root.ToJsonString(),
        }));

        BrokerPolicyManagementResult result = await service.GetManagementAsync(CancellationToken.None);

        Assert.Equal(BrokerPolicyManagementStatus.InvalidResponse, result.Status);
    }

    [Fact]
    public async Task GetManagementAsync_RejectsOverlongServerVersion()
    {
        JsonObject root = ToJsonObject(BuildManagementResponse(BuildMissingSnapshot()));
        root["Server"]!["ServerVersion"] = new string('v', 129);
        var service = CreateService(new FakeTransport(new BrokerTransportResponse
        {
            StatusCode = 200,
            Body = root.ToJsonString(),
        }));

        BrokerPolicyManagementResult result = await service.GetManagementAsync(CancellationToken.None);

        Assert.Equal(BrokerPolicyManagementStatus.InvalidResponse, result.Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("null")]
    public async Task GetManagementAsync_ClassifiesInvalidPayload(string body)
    {
        var service = CreateService(new FakeTransport(new BrokerTransportResponse
        {
            StatusCode = 200,
            Body = body,
        }));

        BrokerPolicyManagementResult result = await service.GetManagementAsync(CancellationToken.None);

        Assert.Equal(BrokerPolicyManagementStatus.InvalidResponse, result.Status);
    }

    // --- GetManagementAsync: failure classification ------------------------------------------------

    [Fact]
    public async Task GetManagementAsync_DoesNotConstructClientOnNonWindows()
    {
        bool constructed = false;
        var service = new BrokerPolicyManagementService(
            () =>
            {
                constructed = true;
                return CreateClient(new FakeTransport());
            },
            () => false);

        BrokerPolicyManagementResult result = await service.GetManagementAsync(CancellationToken.None);

        Assert.Equal(BrokerPolicyManagementStatus.UnsupportedPlatform, result.Status);
        Assert.False(constructed);
    }

    [Theory]
    [InlineData(404, ErrorCode.NotFound, BrokerPolicyManagementStatus.Unsupported)]
    [InlineData(404, ErrorCode.UnsupportedEndpoint, BrokerPolicyManagementStatus.Unsupported)]
    [InlineData(401, ErrorCode.Unauthenticated, BrokerPolicyManagementStatus.AccessDenied)]
    [InlineData(403, ErrorCode.Forbidden, BrokerPolicyManagementStatus.AccessDenied)]
    [InlineData(403, ErrorCode.AdministratorRequired, BrokerPolicyManagementStatus.AccessDenied)]
    [InlineData(400, ErrorCode.UnsafePolicyPath, BrokerPolicyManagementStatus.UnsafePolicyPath)]
    [InlineData(400, ErrorCode.UnsupportedPolicyFormat, BrokerPolicyManagementStatus.UnsupportedPolicyFormat)]
    [InlineData(400, ErrorCode.UnsupportedPolicyFilesystem, BrokerPolicyManagementStatus.UnsupportedPolicyFilesystem)]
    [InlineData(409, ErrorCode.Conflict, BrokerPolicyManagementStatus.PolicyUnavailable)]
    [InlineData(503, ErrorCode.BrokerPaused, BrokerPolicyManagementStatus.PolicyUnavailable)]
    [InlineData(500, ErrorCode.InternalError, BrokerPolicyManagementStatus.PolicyUnavailable)]
    public async Task GetManagementAsync_ClassifiesStructuredBrokerErrors(
        int statusCode,
        ErrorCode errorCode,
        BrokerPolicyManagementStatus expected)
    {
        var error = new ErrorResponse
        {
            Server = new ServerContext { ServerVersion = "tests", Transport = ApiTransport.HttpNamedPipe },
            Code = errorCode,
            Message = "simulated failure",
        };
        var service = CreateService(new FakeTransport(new BrokerTransportResponse
        {
            StatusCode = statusCode,
            Body = BrokerJson.Serialize(error),
        }));

        BrokerPolicyManagementResult result = await service.GetManagementAsync(CancellationToken.None);

        Assert.Equal(expected, result.Status);
        Assert.Equal("simulated failure", result.ErrorMessage);
    }

    [Fact]
    public async Task GetManagementAsync_ClassifiesLegacyEmptyNotFoundAsUnsupported()
    {
        var service = CreateService(new FakeTransport(new BrokerTransportResponse
        {
            StatusCode = 404,
            Body = "",
        }));

        BrokerPolicyManagementResult result = await service.GetManagementAsync(CancellationToken.None);

        Assert.Equal(BrokerPolicyManagementStatus.Unsupported, result.Status);
    }

    [Theory]
    [InlineData(BrokerClientErrorKind.BrokerUnavailable)]
    [InlineData(BrokerClientErrorKind.Timeout)]
    public async Task GetManagementAsync_ClassifiesTransportFailureAsUnavailable(BrokerClientErrorKind kind)
    {
        var service = CreateService(new FakeTransport(exception: new BrokerClientException(kind, "offline")));

        BrokerPolicyManagementResult result = await service.GetManagementAsync(CancellationToken.None);

        Assert.Equal(BrokerPolicyManagementStatus.AgentUnavailable, result.Status);
    }

    [Fact]
    public async Task GetManagementAsync_PropagatesCallerCancellation()
    {
        var transport = new FakeTransport(waitForCancellation: true);
        var service = CreateService(transport);
        using var cancellation = new CancellationTokenSource();

        Task<BrokerPolicyManagementResult> pending = service.GetManagementAsync(cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    // --- ValidateAsync: success mapping -------------------------------------------------------------

    [Fact]
    public async Task ValidateAsync_ReturnsCanonicalDraftReceiptAndFindingsWhenValid()
    {
        var validation = new PolicyValidationResult
        {
            ResultVersion = "1.0",
            ValidatorVersion = "1.0",
            IsValid = true,
            CanonicalDraft = PolicyDraftDocument.Create("contoso.policy", "Contoso", ModelDecision.Deny),
            ValidationReceipt = "receipt-token",
            Findings = [],
        };
        var service = CreateService(new FakeTransport(new BrokerTransportResponse
        {
            StatusCode = 200,
            Body = BrokerJson.Serialize(BuildValidationResponse(validation)),
        }));

        BrokerPolicyValidationOutcome outcome = await service.ValidateAsync(EmptyDraft(), CancellationToken.None);

        Assert.Equal(BrokerPolicyValidationStatus.Completed, outcome.Status);
        Assert.True(outcome.Validation!.IsValid);
        Assert.NotNull(outcome.Validation.CanonicalDraft);
        Assert.Equal("receipt-token", outcome.Validation.ValidationReceipt);
        Assert.Empty(outcome.Diagnostics!.Findings);
    }

    [Fact]
    public async Task ValidateAsync_ReturnsSanitizedFindingsWhenInvalid()
    {
        var validation = new PolicyValidationResult
        {
            ResultVersion = "1.0",
            ValidatorVersion = "1.0",
            IsValid = false,
            Findings =
            [
                new PolicyFinding
                {
                    FindingVersion = "1.0",
                    Severity = PolicyFindingSeverity.Error,
                    Code = PolicyFindingCode.MissingRequiredField,
                    Path = "$.rules[0].id",
                    Message = string.Concat(Enumerable.Repeat("m", 5_000)),
                },
                new PolicyFinding
                {
                    FindingVersion = "1.0",
                    Severity = PolicyFindingSeverity.Warning,
                    Code = PolicyFindingCode.DefaultAllow,
                    Message = "default allow is enabled",
                },
            ],
        };
        var service = CreateService(new FakeTransport(new BrokerTransportResponse
        {
            StatusCode = 200,
            Body = BrokerJson.Serialize(BuildValidationResponse(validation)),
        }));

        BrokerPolicyValidationOutcome outcome = await service.ValidateAsync(EmptyDraft(), CancellationToken.None);

        Assert.Equal(BrokerPolicyValidationStatus.Completed, outcome.Status);
        Assert.False(outcome.Validation!.IsValid);
        Assert.Equal(2, outcome.Diagnostics!.Findings.Count);
        Assert.True(outcome.Diagnostics.Findings[0].MessageTruncated);
        Assert.False(outcome.Diagnostics.Findings[1].MessageTruncated);
    }

    [Fact]
    public async Task ValidateAsync_BoundsSingleOversizedArgumentValue()
    {
        using JsonDocument argument = JsonDocument.Parse(
            $"\"{new string('v', 100_000)}\"");
        var validation = new PolicyValidationResult
        {
            ResultVersion = "1.0",
            ValidatorVersion = "1.0",
            IsValid = false,
            Findings =
            [
                new PolicyFinding
                {
                    FindingVersion = "1.0",
                    Severity = PolicyFindingSeverity.Error,
                    Code = PolicyFindingCode.SchemaViolation,
                    Message = "invalid",
                    Arguments = new Dictionary<string, JsonElement>
                    {
                        ["value"] = argument.RootElement.Clone(),
                    },
                },
            ],
        };
        var service = CreateService(new FakeTransport(new BrokerTransportResponse
        {
            StatusCode = 200,
            Body = BrokerJson.Serialize(BuildValidationResponse(validation)),
        }));

        BrokerPolicyValidationOutcome outcome =
            await service.ValidateAsync(EmptyDraft(), CancellationToken.None);

        BrokerPolicySanitizedFinding finding = Assert.Single(outcome.Diagnostics!.Findings);
        Assert.True(finding.ArgumentsTruncated);
        Assert.True(
            finding.Arguments["value"].EnumerateRunes().Count()
            <= BrokerPolicyManagementLimits.MaxSanitizedArgumentValueLength);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("null")]
    public async Task ValidateAsync_ClassifiesInvalidPayload(string body)
    {
        var service = CreateService(new FakeTransport(new BrokerTransportResponse
        {
            StatusCode = 200,
            Body = body,
        }));

        BrokerPolicyValidationOutcome outcome = await service.ValidateAsync(EmptyDraft(), CancellationToken.None);

        Assert.Equal(BrokerPolicyValidationStatus.InvalidResponse, outcome.Status);
    }

    // --- ValidateAsync: failure classification ------------------------------------------------------

    [Fact]
    public async Task ValidateAsync_DoesNotConstructClientOnNonWindows()
    {
        bool constructed = false;
        var service = new BrokerPolicyManagementService(
            () =>
            {
                constructed = true;
                return CreateClient(new FakeTransport());
            },
            () => false);

        BrokerPolicyValidationOutcome outcome = await service.ValidateAsync(EmptyDraft(), CancellationToken.None);

        Assert.Equal(BrokerPolicyValidationStatus.UnsupportedPlatform, outcome.Status);
        Assert.False(constructed);
    }

    [Theory]
    [InlineData(404, ErrorCode.NotFound, BrokerPolicyValidationStatus.Unsupported)]
    [InlineData(404, ErrorCode.UnsupportedEndpoint, BrokerPolicyValidationStatus.Unsupported)]
    [InlineData(401, ErrorCode.Unauthenticated, BrokerPolicyValidationStatus.AccessDenied)]
    [InlineData(403, ErrorCode.AdministratorRequired, BrokerPolicyValidationStatus.AccessDenied)]
    [InlineData(400, ErrorCode.MalformedDraft, BrokerPolicyValidationStatus.MalformedDraft)]
    [InlineData(413, ErrorCode.PayloadTooLarge, BrokerPolicyValidationStatus.RequestTooLarge)]
    [InlineData(400, ErrorCode.ValidationFailed, BrokerPolicyValidationStatus.ValidationUnavailable)]
    [InlineData(503, ErrorCode.BrokerPaused, BrokerPolicyValidationStatus.ValidationUnavailable)]
    public async Task ValidateAsync_ClassifiesStructuredBrokerErrors(
        int statusCode,
        ErrorCode errorCode,
        BrokerPolicyValidationStatus expected)
    {
        var error = new ErrorResponse
        {
            Server = new ServerContext { ServerVersion = "tests", Transport = ApiTransport.HttpNamedPipe },
            Code = errorCode,
            Message = "simulated failure",
        };
        var service = CreateService(new FakeTransport(new BrokerTransportResponse
        {
            StatusCode = statusCode,
            Body = BrokerJson.Serialize(error),
        }));

        BrokerPolicyValidationOutcome outcome = await service.ValidateAsync(EmptyDraft(), CancellationToken.None);

        Assert.Equal(expected, outcome.Status);
        Assert.Equal("simulated failure", outcome.ErrorMessage);
    }

    [Fact]
    public async Task ValidateAsync_ClassifiesLegacyEmptyNotFoundAsUnsupported()
    {
        var service = CreateService(new FakeTransport(new BrokerTransportResponse
        {
            StatusCode = 404,
            Body = "",
        }));

        BrokerPolicyValidationOutcome outcome = await service.ValidateAsync(EmptyDraft(), CancellationToken.None);

        Assert.Equal(BrokerPolicyValidationStatus.Unsupported, outcome.Status);
    }

    [Fact]
    public async Task ValidateAsync_ClassifiesOversizedDraftAsRequestTooLargeWithoutCallingTransport()
    {
        var transport = new FakeTransport();
        var service = CreateService(transport);
        using JsonDocument oversized = JsonDocument.Parse(
            "{\"pad\":\"" + new string('a', BrokerPolicyManagementLimits.MaxRequestBodyBytes + 1) + "\"}");

        BrokerPolicyValidationOutcome outcome = await service.ValidateAsync(oversized.RootElement, CancellationToken.None);

        Assert.Equal(BrokerPolicyValidationStatus.RequestTooLarge, outcome.Status);
        Assert.Empty(transport.Requests);
    }

    [Theory]
    [InlineData(BrokerClientErrorKind.BrokerUnavailable)]
    [InlineData(BrokerClientErrorKind.Timeout)]
    public async Task ValidateAsync_ClassifiesTransportFailureAsUnavailable(BrokerClientErrorKind kind)
    {
        var service = CreateService(new FakeTransport(exception: new BrokerClientException(kind, "offline")));

        BrokerPolicyValidationOutcome outcome = await service.ValidateAsync(EmptyDraft(), CancellationToken.None);

        Assert.Equal(BrokerPolicyValidationStatus.AgentUnavailable, outcome.Status);
    }

    [Fact]
    public async Task ValidateAsync_PropagatesCallerCancellation()
    {
        var transport = new FakeTransport(waitForCancellation: true);
        var service = CreateService(transport);
        using var cancellation = new CancellationTokenSource();

        Task<BrokerPolicyValidationOutcome> pending = service.ValidateAsync(EmptyDraft(), cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public void MaxRequestBodyBytes_MatchesSharedBrokerApiConstant()
    {
        Assert.Equal(BrokerApi.MaxPolicyManagementBodyBytes, BrokerPolicyManagementLimits.MaxRequestBodyBytes);
        Assert.Equal(16_777_216, BrokerPolicyManagementLimits.MaxRequestBodyBytes);
    }

    // --- Helpers ------------------------------------------------------------------------------------

    private static BrokerPolicyManagementService CreateService(FakeTransport transport) =>
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

    private static JsonElement EmptyDraft()
    {
        using JsonDocument document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    private static PolicyManagementResponse BuildManagementResponse(PolicyManagementSnapshot snapshot) => new()
    {
        ResponseKind = BrokerApi.PolicyManagementResponseKind,
        ResponseVersion = "1.0",
        Server = new ServerContext { ServerVersion = "2026.8-tests", Transport = ApiTransport.HttpNamedPipe },
        Management = snapshot,
    };

    private static PolicyValidationResponse BuildValidationResponse(PolicyValidationResult validation) => new()
    {
        ResponseKind = BrokerApi.PolicyValidationResponseKind,
        ResponseVersion = "1.0",
        Server = new ServerContext { ServerVersion = "2026.8-tests", Transport = ApiTransport.HttpNamedPipe },
        Validation = validation,
    };

    private static PolicyManagementSnapshot BuildMissingSnapshot() => new()
    {
        State = PolicyManagementState.Missing,
        ConfiguredPath = "",
        StoreToken = "tok",
        Source = PolicyConfigurationSource.DefaultPath,
        WriteCapability = PolicyWriteCapability.Writable,
    };

    private static PolicyManagementSnapshot BuildActiveSnapshot() => new()
    {
        State = PolicyManagementState.Active,
        ConfiguredPath = @"C:\ProgramData\Devolutions\Agent\policy.json",
        StoreToken = "active-token",
        Source = PolicyConfigurationSource.ConfiguredPath,
        WriteCapability = PolicyWriteCapability.ReadOnly,
        ReadOnlyReason = PolicyReadOnlyReason.InsufficientPermissions,
        Policy = BuildPolicyDocument(),
    };

    private static PolicyDocument BuildPolicyDocument() => new()
    {
        PolicyVersion = "1.0.0",
        Metadata = new PolicyMetadata
        {
            Id = "contoso.policy",
            Publisher = "Contoso",
            Revision = 1,
            PublishedAt = DateTimeOffset.Parse("2026-08-18T00:00:00Z"),
        },
        Enforcement = new PolicyEnforcement
        {
            DefaultDecision = ModelDecision.Deny,
            RulePrecedence = RulePrecedence.PriorityThenDeny,
        },
        Rules =
        [
            new PolicyRule
            {
                Id = "allow-install",
                Priority = 10,
                Decision = ModelDecision.Allow,
                Match = new PolicyMatch
                {
                    Operations = [ModelOperation.Install],
                },
                Constraints = new PolicyConstraints(),
            },
        ],
    };

    private static JsonObject ToJsonObject<T>(T value) =>
        JsonNode.Parse(BrokerJson.Serialize(value))!.AsObject();

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
