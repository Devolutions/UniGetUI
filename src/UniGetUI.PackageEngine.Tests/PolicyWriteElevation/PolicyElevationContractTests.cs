#if WINDOWS
using System.Text.Json;
using Devolutions.Now.Policy.Api;
using Devolutions.Now.Policy.Model;
using UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation;
using UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation.Interop;

namespace UniGetUI.PackageEngine.Tests.PolicyWriteElevation;

/// <summary>
/// The wire contract between the host and the elevated helper: every shared operation, the exact
/// credential grammar, the response budget, and lossless relay of the shared response documents.
/// </summary>
public class PolicyElevationContractTests
{
    private const string DraftJson = """{"policy":{"rules":["allow"]},"version":3}""";

    private static readonly PolicyElevationTimeouts FastTimeouts =
        new(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(5));

    // ---- Shared document fixtures -----------------------------------------------------------

    private static PolicyDocument Policy() => new()
    {
        Metadata = new PolicyMetadata
        {
            Id = "policy-id",
            Publisher = "publisher",
            Revision = 1,
            PublishedAt = DateTimeOffset.Parse("2026-08-29T00:00:00Z"),
        },
        Enforcement = new PolicyEnforcement
        {
            DefaultDecision = Devolutions.Now.Policy.Model.Decision.Deny,
            RulePrecedence = RulePrecedence.PriorityThenDeny,
        },
        Rules = [],
    };

    private static PolicyDraftDocument CanonicalDraft() => new()
    {
        Schema = Devolutions.Now.Policy.Model.SchemaUris.PolicyDraft,
        Metadata = new PolicyDraftMetadata { Id = "policy-id", Publisher = "publisher" },
        Enforcement = new PolicyEnforcement
        {
            DefaultDecision = Devolutions.Now.Policy.Model.Decision.Deny,
            RulePrecedence = RulePrecedence.PriorityThenDeny,
        },
        Rules = [],
    };

    private static ServerContext Server() => new()
    {
        ServerVersion = "2026.8.29",
        Transport = Transport.HttpNamedPipe,
    };

    private static PolicyReplacementResponse Replacement() => new()
    {
        Server = Server(),
        Policy = Policy(),
        Validation = new PolicyValidationResult
        {
            ValidatorVersion = "2026.8.29",
            IsValid = true,
            CanonicalDraft = CanonicalDraft(),
            ValidationReceipt = "new-receipt",
            Findings = [],
        },
        Management = new PolicyManagementSnapshot
        {
            State = PolicyManagementState.Active,
            ConfiguredPath = @"C:\ProgramData\Devolutions\PackageBroker\package-broker-policy.json",
            StoreToken = "new-token",
            Source = PolicyConfigurationSource.DefaultPath,
            WriteCapability = PolicyWriteCapability.Writable,
            ElevationRequired = true,
            Policy = Policy(),
        },
    };

    private static PolicyManagementSnapshot Snapshot(PolicyManagementState state) => state switch
    {
        PolicyManagementState.Active => new PolicyManagementSnapshot
        {
            State = PolicyManagementState.Active,
            ConfiguredPath = @"C:\ProgramData\Devolutions\PackageBroker\package-broker-policy.json",
            StoreToken = "current-token",
            Source = PolicyConfigurationSource.DefaultPath,
            WriteCapability = PolicyWriteCapability.Writable,
            ElevationRequired = true,
            Policy = Policy(),
        },
        PolicyManagementState.Missing => new PolicyManagementSnapshot
        {
            State = PolicyManagementState.Missing,
            ConfiguredPath = @"C:\ProgramData\Devolutions\PackageBroker\package-broker-policy.json",
            StoreToken = "missing-token",
            Source = PolicyConfigurationSource.DefaultPath,
            WriteCapability = PolicyWriteCapability.Writable,
            ElevationRequired = true,
        },
        _ => new PolicyManagementSnapshot
        {
            State = PolicyManagementState.Invalid,
            ConfiguredPath = @"C:\ProgramData\Devolutions\PackageBroker\package-broker-policy.json",
            StoreToken = "invalid-token",
            Source = PolicyConfigurationSource.DefaultPath,
            WriteCapability = PolicyWriteCapability.Writable,
            ElevationRequired = true,
            InvalidDiagnostics = new InvalidPolicyDiagnostics
            {
                Findings =
                [
                    new PolicyFinding
                    {
                        Severity = PolicyFindingSeverity.Error,
                        Code = PolicyFindingCode.SchemaViolation,
                        Path = "$.metadata.id",
                        Message = "The policy store could not be parsed.",
                    },
                ],
            },
        },
    };

    private static ErrorResponse StaleTokenError(PolicyManagementState state) => new()
    {
        Server = Server(),
        Code = ErrorCode.StalePolicyStoreToken,
        Message = "The policy store token is stale.",
        Management = Snapshot(state),
    };

    private static JsonElement AsPayload<T>(T document)
    {
        using JsonDocument parsed = JsonDocument.Parse(BrokerSerializer.Serialize(document));
        return parsed.RootElement.Clone();
    }

    // ---- Harness -----------------------------------------------------------------------------

    private static PolicyElevationWriteRequest Request(
        PolicyElevationOperation operation = PolicyElevationOperation.Update,
        string token = "store-token",
        string receipt = "validation-receipt")
        => new(JsonDocument.Parse(DraftJson).RootElement)
        {
            Operation = operation,
            ConflictHandling = PolicyElevationConflictHandling.Reject,
            ExpectedStoreToken = token,
            ValidationReceipt = receipt,
            WarningsAcknowledged = true,
        };

    private static WindowsPolicyWriteElevator Build(IElevatedHelperLauncher launcher)
        => new(
            FakeHelperLocator.Found(),
            launcher,
            FakeTrustVerifier.SameSigner(),
            new FakePeerAuthenticator(),
            PolicyElevationPipeServer.Create,
            FastTimeouts,
            () => FakeHelperLocator.PackagedHostPath);

    /// <summary>A helper that answers every request with one fixed response message.</summary>
    private static FakeHelperLauncher Answering(
        Func<PolicyElevationRequestMessage, PolicyElevationResponseMessage> answer,
        Action<PolicyElevationRequestMessage>? observe = null)
        => FakeHelperLauncher.Running(async (arguments, _) =>
        {
            await using var client = await FakeHelperClient.ConnectAsync(arguments.PipeName);
            PolicyElevationRequestMessage request =
                await PolicyElevationFrame.ReadRequestAsync(client, CancellationToken.None);

            observe?.Invoke(request);

            await PolicyElevationFrame.WriteResponseAsync(client, answer(request), CancellationToken.None);
        });

    // ---- Correction 6: every shared operation ------------------------------------------------

    [Fact]
    public void TheProtocolCarriesExactlyTheSharedOperationSet()
    {
        string[] shared = [.. Enum.GetNames<PolicyReplacementOperation>().Order()];
        string[] wire = [.. Enum.GetNames<PolicyElevationOperation>().Order()];

        Assert.Equal(shared, wire);

        // The names must match one for one, so a wire value maps onto the shared value by name.
        foreach (string name in shared)
        {
            Assert.True(Enum.TryParse(name, out PolicyElevationOperation parsed));
            Assert.Equal(name, parsed.ToString());
        }
    }

    [Theory]
    [InlineData(PolicyElevationOperation.Update)]
    [InlineData(PolicyElevationOperation.ReplaceIdentity)]
    [InlineData(PolicyElevationOperation.Create)]
    [InlineData(PolicyElevationOperation.Repair)]
    public async Task EveryOperation_SurvivesTheElevationHopUnchanged(PolicyElevationOperation operation)
    {
        PolicyElevationRequestMessage? observed = null;

        FakeHelperLauncher launcher = Answering(
            request => new PolicyElevationResponseMessage
            {
                RequestId = request.RequestId,
                Outcome = PolicyElevationResponseStatus.Replaced,
                BrokerStatusCode = 200,
                Payload = AsPayload(Replacement()),
            },
            request => observed = request);

        PolicyElevationResult result = await Build(launcher)
            .ReplacePolicyAsync(Request(operation), CancellationToken.None);

        await launcher.Completion;

        Assert.Equal(PolicyElevationOutcome.Replaced, result.Outcome);
        Assert.NotNull(observed);
        Assert.Equal(operation, observed.Operation);
        Assert.Equal(operation, result.Request.Operation);
    }

    [Theory]
    [InlineData(PolicyElevationOperation.Update, "Update")]
    [InlineData(PolicyElevationOperation.ReplaceIdentity, "ReplaceIdentity")]
    [InlineData(PolicyElevationOperation.Create, "Create")]
    [InlineData(PolicyElevationOperation.Repair, "Repair")]
    public async Task EveryOperation_IsWrittenAsItsExactPascalCaseName(
        PolicyElevationOperation operation,
        string expected)
    {
        var message = new PolicyElevationRequestMessage
        {
            RequestId = new string('a', PolicyElevationProtocol.RequestIdCharacters),
            Operation = operation,
            ExpectedStoreToken = "store-token",
            ValidationReceipt = "validation-receipt",
            Draft = JsonDocument.Parse(DraftJson).RootElement.Clone(),
        };

        await using var stream = new MemoryStream();
        await PolicyElevationFrame.WriteRequestAsync(stream, message, CancellationToken.None);

        string json = System.Text.Encoding.UTF8.GetString(stream.ToArray()[4..]);
        Assert.Contains($"\"{expected}\"", json, StringComparison.Ordinal);

        stream.Position = 0;
        PolicyElevationRequestMessage round =
            await PolicyElevationFrame.ReadRequestAsync(stream, CancellationToken.None);

        Assert.Equal(operation, round.Operation);
    }

    // ---- Correction 7: credentials required and bounded for every operation ------------------

    [Theory]
    [InlineData(PolicyElevationOperation.Update)]
    [InlineData(PolicyElevationOperation.ReplaceIdentity)]
    [InlineData(PolicyElevationOperation.Create)]
    [InlineData(PolicyElevationOperation.Repair)]
    public void CredentialsAreRequired_ForEveryOperationIncludingCreateAndRepair(
        PolicyElevationOperation operation)
    {
        foreach ((string? token, string? receipt) in new (string?, string?)[]
                 {
                     (null, "receipt"),
                     ("", "receipt"),
                     ("token", null),
                     ("token", ""),
                 })
        {
            var message = new PolicyElevationRequestMessage
            {
                RequestId = new string('a', PolicyElevationProtocol.RequestIdCharacters),
                Operation = operation,
                ExpectedStoreToken = token!,
                ValidationReceipt = receipt!,
                Draft = JsonDocument.Parse(DraftJson).RootElement.Clone(),
            };

            Assert.Throws<PolicyElevationFrameException>(() => PolicyElevationFrame.ValidateRequest(message));
        }
    }

    [Fact]
    public void CredentialBounds_AreTheExactSharedMaximums()
    {
        Assert.Equal(512, PolicyElevationProtocol.MaxStoreTokenCharacters);
        Assert.Equal(2048, PolicyElevationProtocol.MaxValidationReceiptCharacters);
    }

    [Theory]
    [InlineData(512, 2048, true)]
    [InlineData(513, 2048, false)]
    [InlineData(512, 2049, false)]
    [InlineData(1, 1, true)]
    public void CredentialLengths_AreAcceptedExactlyUpToTheSharedMaximum(
        int tokenLength,
        int receiptLength,
        bool accepted)
    {
        var message = new PolicyElevationRequestMessage
        {
            RequestId = new string('a', PolicyElevationProtocol.RequestIdCharacters),
            Operation = PolicyElevationOperation.Repair,
            ExpectedStoreToken = new string('a', tokenLength),
            ValidationReceipt = new string('b', receiptLength),
            Draft = JsonDocument.Parse(DraftJson).RootElement.Clone(),
        };

        if (accepted)
        {
            PolicyElevationFrame.ValidateRequest(message);
            return;
        }

        Assert.Throws<PolicyElevationFrameException>(() => PolicyElevationFrame.ValidateRequest(message));
    }

    [Theory]
    // The shared grammar: printable ASCII, first character an ASCII alphanumeric.
    [InlineData("a", true)]
    [InlineData("0", true)]
    [InlineData("Z", true)]
    [InlineData("tok-1.2:3_4~5", true)]
    [InlineData("-leading", false)]
    [InlineData("_leading", false)]
    [InlineData(".leading", false)]
    [InlineData("~leading", false)]
    [InlineData(" leading", false)]
    [InlineData("tok en", false)]
    [InlineData("tok\ten", false)]
    [InlineData("tok\nen", false)]
    [InlineData("tokén", false)]
    [InlineData("token ", false)]
    public void CredentialGrammar_MirrorsTheSharedConverters(string credential, bool accepted)
    {
        static void Validate(string token, string receipt) => PolicyElevationFrame.ValidateRequest(
            new PolicyElevationRequestMessage
            {
                RequestId = new string('a', PolicyElevationProtocol.RequestIdCharacters),
                Operation = PolicyElevationOperation.Create,
                ExpectedStoreToken = token,
                ValidationReceipt = receipt,
                Draft = JsonDocument.Parse(DraftJson).RootElement.Clone(),
            });

        if (accepted)
        {
            Validate(credential, credential);
            return;
        }

        Assert.Throws<PolicyElevationFrameException>(() => Validate(credential, "receipt"));
        Assert.Throws<PolicyElevationFrameException>(() => Validate("token", credential));
    }

    // ---- Correction 8: the response budget holds the whole success document ------------------

    [Fact]
    public void ResponseBudget_HoldsEveryPolicyCopyASuccessCanCarry()
    {
        // A success carries Policy, Validation.CanonicalDraft and Management.Policy — three
        // independent documents, each of which the broker allows to reach the shared body limit —
        // plus bounded findings and diagnostics.
        Assert.Equal(3, PolicyElevationProtocol.MaxResponsePolicyCopies);

        long expected = ((long)PolicyElevationProtocol.MaxPolicyManagementBodyBytes
                * PolicyElevationProtocol.MaxResponsePolicyCopies)
            + PolicyElevationProtocol.MaxResponseDiagnosticsBytes
            + PolicyElevationProtocol.ResponseEnvelopeOverheadBytes;

        Assert.Equal(PolicyElevationProtocol.MaxResponseFrameBytes, checked((int)expected));

        // The arithmetic must not have overflowed into a smaller-than-shared limit.
        Assert.True(expected <= int.MaxValue);
        Assert.True(
            PolicyElevationProtocol.MaxResponseFrameBytes
            > PolicyElevationProtocol.MaxPolicyManagementBodyBytes);
        Assert.True(
            PolicyElevationProtocol.MaxResponseFrameBytes
            > PolicyElevationProtocol.MaxRequestFrameBytes);
    }

    [Fact]
    public async Task AFrameOfExactlyTheResponseBudget_IsAccepted()
    {
        byte[] body = BuildPaddedResponseBody(PolicyElevationProtocol.MaxResponseFrameBytes);
        Assert.Equal(PolicyElevationProtocol.MaxResponseFrameBytes, body.Length);

        await using var stream = new MemoryStream();
        await PolicyElevationFrame.WriteAsync(
            stream,
            body,
            PolicyElevationProtocol.MaxResponseFrameBytes,
            CancellationToken.None);

        stream.Position = 0;
        PolicyElevationResponseMessage response =
            await PolicyElevationFrame.ReadResponseAsync(stream, CancellationToken.None);

        Assert.Equal(PolicyElevationResponseStatus.BrokerRejected, response.Outcome);
    }

    [Fact]
    public async Task AFrameOneByteOverTheResponseBudget_IsRejectedBeforeAllocation()
    {
        byte[] header = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(
            header,
            PolicyElevationProtocol.MaxResponseFrameBytes + 1);

        await using var stream = new MemoryStream(header);

        PolicyElevationFrameException failure = await Assert.ThrowsAsync<PolicyElevationFrameException>(
            () => PolicyElevationFrame.ReadResponseAsync(stream, CancellationToken.None));

        Assert.Equal(PolicyElevationFrameError.Oversized, failure.Error);
    }

    private static byte[] BuildPaddedResponseBody(int totalBytes)
    {
        // The padding goes in the relayed payload, because that is where a real maximum-size
        // response puts its bulk: the message and error-code fields are separately bounded.
        static byte[] Serialize(int payloadCharacters)
        {
            using JsonDocument payload = JsonDocument.Parse(
                $$"""{"p":"{{new string('x', payloadCharacters)}}"}""");

            return JsonSerializer.SerializeToUtf8Bytes(
                new PolicyElevationResponseMessage
                {
                    RequestId = new string('a', PolicyElevationProtocol.RequestIdCharacters),
                    Outcome = PolicyElevationResponseStatus.BrokerRejected,
                    BrokerErrorCode = "StalePolicyStoreToken",
                    Payload = payload.RootElement.Clone(),
                },
                PolicyElevationJsonContext.Default.PolicyElevationResponseMessage);
        }

        // Every padding character is one ASCII byte, so one measurement fixes the length exactly.
        int padding = totalBytes - Serialize(0).Length;
        byte[] body = Serialize(padding);

        Assert.Equal(totalBytes, body.Length);
        return body;
    }

    // ---- Corrections 10 and 11: lossless relay, generic host-authored text -------------------

    [Fact]
    public async Task ASuccess_RelaysTheWholeSharedResponseDocument()
    {
        PolicyReplacementResponse expected = Replacement();

        FakeHelperLauncher launcher = Answering(request => new PolicyElevationResponseMessage
        {
            RequestId = request.RequestId,
            Outcome = PolicyElevationResponseStatus.Replaced,
            BrokerStatusCode = 200,
            Message = "helper said: C:\\Program Files\\UniGetUI raw broker text",
            Payload = AsPayload(expected),
        });

        PolicyElevationResult result = await Build(launcher)
            .ReplacePolicyAsync(Request(), CancellationToken.None);

        await launcher.Completion;

        Assert.Equal(PolicyElevationOutcome.Replaced, result.Outcome);
        Assert.NotNull(result.Response);

        // Nothing was summarised away: the relayed document is byte-identical to the broker's.
        Assert.Equal(BrokerSerializer.Serialize(expected), BrokerSerializer.Serialize(result.Response!));

        Assert.Equal("policy-id", result.Response!.Policy.Metadata.Id);
        Assert.NotNull(result.Response.Validation.CanonicalDraft);
        Assert.Equal("new-receipt", result.Response.Validation.ValidationReceipt);
        Assert.Equal(PolicyManagementState.Active, result.Response.Management.State);
        Assert.NotNull(result.Response.Management.Policy);

        // Correction 11: the helper's own text never reaches the caller.
        Assert.DoesNotContain("helper said", result.ErrorMessage ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\", result.ErrorMessage ?? string.Empty, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PolicyManagementState.Active)]
    [InlineData(PolicyManagementState.Missing)]
    [InlineData(PolicyManagementState.Invalid)]
    public async Task AStaleStoreTokenError_RelaysTheWholeSharedErrorDocument(PolicyManagementState state)
    {
        ErrorResponse expected = StaleTokenError(state);

        FakeHelperLauncher launcher = Answering(request => new PolicyElevationResponseMessage
        {
            RequestId = request.RequestId,
            Outcome = PolicyElevationResponseStatus.BrokerRejected,
            BrokerStatusCode = 409,
            BrokerErrorCode = "StalePolicyStoreToken",
            Message = "System.InvalidOperationException: raw exception text",
            Payload = AsPayload(expected),
        });

        PolicyElevationResult result = await Build(launcher)
            .ReplacePolicyAsync(Request(), CancellationToken.None);

        await launcher.Completion;

        Assert.Equal(PolicyElevationOutcome.BrokerRejected, result.Outcome);
        Assert.NotNull(result.Error);
        Assert.Equal(BrokerSerializer.Serialize(expected), BrokerSerializer.Serialize(result.Error!));

        Assert.Equal(ErrorCode.StalePolicyStoreToken, result.Error!.Code);
        Assert.NotNull(result.Error.Management);
        Assert.Equal(state, result.Error.Management!.State);

        // The broker's error code is relayed structurally for localisation, but the raw exception
        // text the helper reported is never surfaced.
        Assert.Equal("StalePolicyStoreToken", result.BrokerErrorCode);
        Assert.DoesNotContain("Exception", result.ErrorMessage ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("raw exception text", result.ErrorMessage ?? string.Empty, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"ResponseKind":"PolicyReplacementResponse"}""")]
    [InlineData("""{"responseKind":"PolicyReplacementResponse","ResponseVersion":"1.0"}""")]
    [InlineData("""{"ResponseKind":"ErrorResponse","ResponseVersion":"1.0","Server":{"ServerVersion":"x","Transport":"HttpNamedPipe"},"Code":"StalePolicyStoreToken","Message":"m"}""")]
    public async Task AMalformedOrIncompleteSuccessPayload_IsRejected(string payloadJson)
    {
        using JsonDocument payload = JsonDocument.Parse(payloadJson);
        JsonElement element = payload.RootElement.Clone();

        FakeHelperLauncher launcher = Answering(request => new PolicyElevationResponseMessage
        {
            RequestId = request.RequestId,
            Outcome = PolicyElevationResponseStatus.Replaced,
            BrokerStatusCode = 200,
            Payload = element,
        });

        PolicyElevationResult result = await Build(launcher)
            .ReplacePolicyAsync(Request(), CancellationToken.None);

        await launcher.Completion;

        Assert.Equal(PolicyElevationOutcome.MalformedResponse, result.Outcome);
        Assert.Null(result.Response);
    }

    [Fact]
    public async Task ASuccessWithNoPayload_IsRejected()
    {
        FakeHelperLauncher launcher = Answering(request => new PolicyElevationResponseMessage
        {
            RequestId = request.RequestId,
            Outcome = PolicyElevationResponseStatus.Replaced,
            BrokerStatusCode = 200,
        });

        PolicyElevationResult result = await Build(launcher)
            .ReplacePolicyAsync(Request(), CancellationToken.None);

        await launcher.Completion;

        Assert.Equal(PolicyElevationOutcome.MalformedResponse, result.Outcome);
        Assert.Null(result.Response);
    }

    [Fact]
    public async Task AStaleTokenErrorWithNoManagementSnapshot_IsRejected()
    {
        // The broker never reports a stale token without the snapshot the caller must reconcile
        // against, so a document missing it did not come from the broker.
        using JsonDocument payload = JsonDocument.Parse(
            """
            {"ResponseKind":"ErrorResponse","ResponseVersion":"1.0",
             "Server":{"ServerVersion":"2026.8.29","Transport":"HttpNamedPipe"},
             "Code":"StalePolicyStoreToken","Message":"stale"}
            """);

        JsonElement element = payload.RootElement.Clone();

        FakeHelperLauncher launcher = Answering(request => new PolicyElevationResponseMessage
        {
            RequestId = request.RequestId,
            Outcome = PolicyElevationResponseStatus.BrokerRejected,
            BrokerStatusCode = 409,
            Payload = element,
        });

        PolicyElevationResult result = await Build(launcher)
            .ReplacePolicyAsync(Request(), CancellationToken.None);

        await launcher.Completion;

        Assert.Equal(PolicyElevationOutcome.MalformedResponse, result.Outcome);
        Assert.Null(result.Error);
    }

    [Fact]
    public void TheRelayPath_IsSourceGeneratedAndNativeAotSafe()
    {
        // Both halves of the relay must resolve their metadata from source-generated contexts:
        // the elevation envelope through its own context, and the shared documents through the
        // broker's. A reflection-based resolver anywhere here would break the trimmed helper.
        Assert.NotNull(PolicyElevationJsonContext.Default.PolicyElevationResponseMessage);
        Assert.NotNull(PolicyElevationJsonContext.Default.PolicyElevationRequestMessage);
        Assert.False(PolicyElevationJsonContext.Default.Options.PropertyNameCaseInsensitive);

        Assert.NotNull(BrokerSerializer.Options.TypeInfoResolver);
        Assert.NotNull(BrokerSerializer.Options.TypeInfoResolver!.GetTypeInfo(
            typeof(PolicyReplacementResponse),
            BrokerSerializer.Options));
        Assert.NotNull(BrokerSerializer.Options.TypeInfoResolver.GetTypeInfo(
            typeof(ErrorResponse),
            BrokerSerializer.Options));

        // Round-tripping through exactly the calls the helper and host make must be lossless.
        PolicyReplacementResponse original = Replacement();
        PolicyReplacementResponse? relayed =
            BrokerSerializer.DeserializeStrict<PolicyReplacementResponse>(BrokerSerializer.Serialize(original));

        Assert.NotNull(relayed);
        Assert.Equal(BrokerSerializer.Serialize(original), BrokerSerializer.Serialize(relayed!));
    }

    [Fact]
    public async Task EveryUserFacingMessage_IsHostAuthored()
    {
        const string HelperText = @"broker said C:\Program Files\UniGetUI\Assets is unwritable";

        foreach (PolicyElevationResponseStatus status in new[]
                 {
                     PolicyElevationResponseStatus.BrokerRejected,
                     PolicyElevationResponseStatus.BrokerUnavailable,
                     PolicyElevationResponseStatus.BrokerInvalidResponse,
                     PolicyElevationResponseStatus.HelperRejected,
                 })
        {
            FakeHelperLauncher launcher = Answering(request => new PolicyElevationResponseMessage
            {
                RequestId = request.RequestId,
                Outcome = status,
                Message = HelperText,
            });

            PolicyElevationResult result = await Build(launcher)
                .ReplacePolicyAsync(Request(), CancellationToken.None);

            await launcher.Completion;

            Assert.NotNull(result.ErrorMessage);
            Assert.DoesNotContain(HelperText, result.ErrorMessage, StringComparison.Ordinal);
            Assert.DoesNotContain(@"C:\", result.ErrorMessage, StringComparison.Ordinal);
            Assert.DoesNotContain("broker said", result.ErrorMessage, StringComparison.Ordinal);
        }
    }
}
#endif
