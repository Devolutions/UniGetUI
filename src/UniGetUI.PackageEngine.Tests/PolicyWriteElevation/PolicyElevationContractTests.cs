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
                Disposition = PolicyElevationDisposition.Committed,
                CommittedStoreToken = "new-token",
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

    // ---- Protocol v2: bounded post-commit acknowledgement ------------------------------------

    [Fact]
    public async Task AMaximumStaleAcknowledgementExactlyFitsTheResponseBudget()
    {
        static string WorstCaseSafeAscii(int length) =>
            "a" + new string('"', length - 1);

        var response = new PolicyElevationResponseMessage
        {
            RequestId = new string('a', PolicyElevationProtocol.RequestIdCharacters),
            Disposition = PolicyElevationDisposition.Rejected,
            BrokerStatusCode = int.MinValue,
            BrokerErrorCode = ErrorCode.StalePolicyStoreToken.ToString(),
            ConflictStoreToken = WorstCaseSafeAscii(
                PolicyElevationProtocol.MaxStoreTokenCharacters),
            ConflictState = PolicyElevationManagementState.Active,
            ConflictPolicyId = WorstCaseSafeAscii(
                PolicyElevationProtocol.MaxConflictPolicyIdCharacters),
        };

        await using var stream = new MemoryStream();
        await PolicyElevationFrame.WriteResponseAsync(
            stream,
            response,
            CancellationToken.None);

        Assert.Equal(
            PolicyElevationProtocol.FrameLengthPrefixBytes
            + PolicyElevationProtocol.MaxResponseFrameBytes,
            stream.Length);
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

    [Theory]
    [InlineData(PolicyElevationManagementState.Active, "current-policy")]
    [InlineData(PolicyElevationManagementState.Missing, null)]
    [InlineData(PolicyElevationManagementState.Invalid, null)]
    public async Task AStaleStoreTokenError_RelaysOnlyAtomicBoundedConflictContext(
        PolicyElevationManagementState state,
        string? policyId)
    {
        FakeHelperLauncher launcher = Answering(request => new PolicyElevationResponseMessage
        {
            RequestId = request.RequestId,
            Disposition = PolicyElevationDisposition.Rejected,
            BrokerStatusCode = 409,
            BrokerErrorCode = ErrorCode.StalePolicyStoreToken.ToString(),
            ConflictStoreToken = "current-token",
            ConflictState = state,
            ConflictPolicyId = policyId,
        });

        PolicyElevationResult result = await Build(launcher)
            .ReplacePolicyAsync(Request(), CancellationToken.None);
        await launcher.Completion;

        Assert.Equal(PolicyElevationOutcome.BrokerRejected, result.Outcome);
        Assert.Equal("current-token", result.ConflictStoreToken);
        Assert.Equal(state, result.ConflictState);
        Assert.Equal(policyId, result.ConflictPolicyId);
        Assert.Null(result.Payload);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task ACommittedAcknowledgement_CarriesOnlyTheBoundedStoreToken()
    {
        FakeHelperLauncher launcher = Answering(request => new PolicyElevationResponseMessage
        {
            RequestId = request.RequestId,
            Disposition = PolicyElevationDisposition.Committed,
            CommittedStoreToken = "committed-token",
        });

        PolicyElevationResult result = await Build(launcher)
            .ReplacePolicyAsync(Request(), CancellationToken.None);
        await launcher.Completion;

        Assert.Equal(PolicyElevationOutcome.Replaced, result.Outcome);
        Assert.Equal("committed-token", result.CommittedStoreToken);
        Assert.Null(result.Payload);
        Assert.Null(result.Response);
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
        Assert.All(
            PolicyElevationJsonContext.Default.PolicyElevationRequestMessage.Properties,
            property => Assert.True(property.IsRequired, $"{property.Name} must be required."));
        Assert.All(
            PolicyElevationJsonContext.Default.PolicyElevationResponseMessage.Properties,
            property => Assert.True(property.IsRequired, $"{property.Name} must be required."));

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
    public async Task UnknownResult_UsesOnlyHostAuthoredText()
    {
        FakeHelperLauncher launcher = Answering(request => new PolicyElevationResponseMessage
        {
            RequestId = request.RequestId,
            Disposition = PolicyElevationDisposition.Unknown,
            BrokerErrorCode = "Timeout",
        });

        PolicyElevationResult result = await Build(launcher)
            .ReplacePolicyAsync(Request(), CancellationToken.None);
        await launcher.Completion;

        Assert.Equal(PolicyElevationOutcome.WriteResultUnknown, result.Outcome);
        Assert.NotNull(result.ErrorMessage);
        Assert.DoesNotContain(@"C:\", result.ErrorMessage, StringComparison.Ordinal);
    }
}
#endif
