using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation;

namespace UniGetUI.PackageEngine.Tests.PolicyWriteElevation;

/// <summary>
/// Framing behaviour: exactly one length-prefixed UTF-8 JSON frame per direction, with distinct
/// end-of-stream, oversized and malformed classifications.
/// </summary>
public class PolicyElevationFrameTests
{
    private const string RequestJson =
        """{"protocolVersion":"2.0","requestId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","operation":"Update","conflictHandling":"Reject","expectedStoreToken":"token","validationReceipt":"receipt","warningsAcknowledged":false,"draft":{"policy":1}}""";

    private const string CommittedResponseJson =
        """{"protocolVersion":"2.0","requestId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","disposition":"Committed","brokerStatusCode":null,"brokerErrorCode":null,"committedStoreToken":"token","conflictStoreToken":null,"conflictState":null,"conflictPolicyId":null}""";

    private const string RejectedResponseJson =
        """{"protocolVersion":"2.0","requestId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","disposition":"Rejected","brokerStatusCode":400,"brokerErrorCode":"InvalidPolicy","committedStoreToken":null,"conflictStoreToken":null,"conflictState":null,"conflictPolicyId":null}""";

    private const string StaleResponseJson =
        """{"protocolVersion":"2.0","requestId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","disposition":"Rejected","brokerStatusCode":409,"brokerErrorCode":"StalePolicyStoreToken","committedStoreToken":null,"conflictStoreToken":"current-token","conflictState":"Active","conflictPolicyId":"current-policy"}""";

    private static PolicyElevationRequestMessage ValidRequest(string draftJson = """{"policy":1}""")
        => new()
        {
            ProtocolVersion = PolicyElevationProtocol.Version,
            RequestId = new string('a', PolicyElevationProtocol.RequestIdCharacters),
            Operation = PolicyElevationOperation.Update,
            ConflictHandling = PolicyElevationConflictHandling.Reject,
            ExpectedStoreToken = "token",
            ValidationReceipt = "receipt",
            WarningsAcknowledged = true,
            Draft = JsonDocument.Parse(draftJson).RootElement.Clone(),
        };

    private static PolicyElevationResponseMessage ValidResponse()
        => new()
        {
            ProtocolVersion = PolicyElevationProtocol.Version,
            RequestId = new string('a', PolicyElevationProtocol.RequestIdCharacters),
            Disposition = PolicyElevationDisposition.Committed,
            CommittedStoreToken = "abc",
        };

    [Fact]
    public async Task Request_RoundTrips()
    {
        using var stream = new MemoryStream();
        PolicyElevationRequestMessage sent = ValidRequest();

        await PolicyElevationFrame.WriteRequestAsync(stream, sent, CancellationToken.None);
        stream.Position = 0;

        PolicyElevationRequestMessage received =
            await PolicyElevationFrame.ReadRequestAsync(stream, CancellationToken.None);

        Assert.Equal(sent.RequestId, received.RequestId);
        Assert.Equal(sent.Operation, received.Operation);
        Assert.Equal(sent.ConflictHandling, received.ConflictHandling);
        Assert.Equal(sent.ExpectedStoreToken, received.ExpectedStoreToken);
        Assert.Equal(sent.ValidationReceipt, received.ValidationReceipt);
        Assert.True(received.WarningsAcknowledged);
        Assert.Equal(sent.Draft.GetRawText(), received.Draft.GetRawText());
    }

    [Fact]
    public async Task Response_RoundTrips()
    {
        using var stream = new MemoryStream();
        PolicyElevationResponseMessage sent = ValidResponse();

        await PolicyElevationFrame.WriteResponseAsync(stream, sent, CancellationToken.None);
        stream.Position = 0;

        PolicyElevationResponseMessage received =
            await PolicyElevationFrame.ReadResponseAsync(stream, CancellationToken.None);

        Assert.Equal(PolicyElevationDisposition.Committed, received.Disposition);
        Assert.Equal("abc", received.CommittedStoreToken);
    }

    [Fact]
    public async Task Frame_UsesABigEndianLengthPrefix()
    {
        using var stream = new MemoryStream();
        await PolicyElevationFrame.WriteRequestAsync(stream, ValidRequest(), CancellationToken.None);

        byte[] written = stream.ToArray();
        uint declared = BinaryPrimitives.ReadUInt32BigEndian(written);

        Assert.Equal(PolicyElevationProtocol.FrameLengthPrefixBytes, 4);
        Assert.Equal((uint)(written.Length - PolicyElevationProtocol.FrameLengthPrefixBytes), declared);
    }

    [Fact]
    public async Task CleanEndOfStream_IsReportedAsEndOfStream()
    {
        using var stream = new MemoryStream();

        PolicyElevationFrameException error = await Assert.ThrowsAsync<PolicyElevationFrameException>(
            () => PolicyElevationFrame.ReadResponseAsync(stream, CancellationToken.None));

        Assert.Equal(PolicyElevationFrameError.EndOfStream, error.Error);
    }

    [Fact]
    public async Task TruncatedBody_IsReportedAsMalformed()
    {
        using var stream = new MemoryStream();
        await PolicyElevationFrame.WriteResponseAsync(stream, ValidResponse(), CancellationToken.None);

        byte[] truncated = stream.ToArray()[..^4];
        using var replay = new MemoryStream(truncated);

        PolicyElevationFrameException error = await Assert.ThrowsAsync<PolicyElevationFrameException>(
            () => PolicyElevationFrame.ReadResponseAsync(replay, CancellationToken.None));

        Assert.Equal(PolicyElevationFrameError.Malformed, error.Error);
    }

    [Fact]
    public async Task TruncatedHeader_IsReportedAsMalformed()
    {
        using var stream = new MemoryStream([0x00, 0x01]);

        PolicyElevationFrameException error = await Assert.ThrowsAsync<PolicyElevationFrameException>(
            () => PolicyElevationFrame.ReadResponseAsync(stream, CancellationToken.None));

        Assert.Equal(PolicyElevationFrameError.Malformed, error.Error);
    }

    [Fact]
    public async Task OversizedDeclaredLength_IsRejectedBeforeAllocation()
    {
        byte[] header = new byte[PolicyElevationProtocol.FrameLengthPrefixBytes];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)PolicyElevationProtocol.MaxResponseFrameBytes + 1);

        using var stream = new MemoryStream(header);

        PolicyElevationFrameException error = await Assert.ThrowsAsync<PolicyElevationFrameException>(
            () => PolicyElevationFrame.ReadResponseAsync(stream, CancellationToken.None));

        Assert.Equal(PolicyElevationFrameError.Oversized, error.Error);
    }

    [Fact]
    public async Task ZeroLengthFrame_IsReportedAsMalformed()
    {
        using var stream = new MemoryStream(new byte[PolicyElevationProtocol.FrameLengthPrefixBytes]);

        PolicyElevationFrameException error = await Assert.ThrowsAsync<PolicyElevationFrameException>(
            () => PolicyElevationFrame.ReadResponseAsync(stream, CancellationToken.None));

        Assert.Equal(PolicyElevationFrameError.Malformed, error.Error);
    }

    [Fact]
    public async Task NonJsonBody_IsReportedAsMalformed()
    {
        PolicyElevationFrameException error = await Assert.ThrowsAsync<PolicyElevationFrameException>(
            () => ReadResponseFromBodyAsync("this is not json"));

        Assert.Equal(PolicyElevationFrameError.Malformed, error.Error);
    }

    [Fact]
    public async Task ForeignProtocolVersion_IsReportedAsMalformed()
    {
        PolicyElevationFrameException error = await Assert.ThrowsAsync<PolicyElevationFrameException>(
            () => ReadResponseFromBodyAsync(
                """{"protocolVersion":"9.9","requestId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","disposition":"Unknown","brokerStatusCode":null,"brokerErrorCode":null,"committedStoreToken":null,"conflictStoreToken":null,"conflictState":null,"conflictPolicyId":null}"""));

        Assert.Equal(PolicyElevationFrameError.Malformed, error.Error);
    }

    [Fact]
    public async Task ShortRequestId_IsReportedAsMalformed()
    {
        PolicyElevationFrameException error = await Assert.ThrowsAsync<PolicyElevationFrameException>(
            () => ReadResponseFromBodyAsync("""{"protocolVersion":"2.0","requestId":"abc","disposition":"Unknown","brokerStatusCode":null,"brokerErrorCode":null,"committedStoreToken":null,"conflictStoreToken":null,"conflictState":null,"conflictPolicyId":null}"""));

        Assert.Equal(PolicyElevationFrameError.Malformed, error.Error);
    }

    [Fact]
    public async Task UndefinedDisposition_IsReportedAsMalformed()
    {
        PolicyElevationFrameException error = await Assert.ThrowsAsync<PolicyElevationFrameException>(
            () => ReadResponseFromBodyAsync(
                """{"protocolVersion":"2.0","requestId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","disposition":99,"brokerStatusCode":null,"brokerErrorCode":null,"committedStoreToken":null,"conflictStoreToken":null,"conflictState":null,"conflictPolicyId":null}"""));

        Assert.Equal(PolicyElevationFrameError.Malformed, error.Error);
    }

    [Theory]
    [InlineData("protocolVersion")]
    [InlineData("requestId")]
    [InlineData("operation")]
    [InlineData("conflictHandling")]
    [InlineData("expectedStoreToken")]
    [InlineData("validationReceipt")]
    [InlineData("warningsAcknowledged")]
    [InlineData("draft")]
    public async Task HelperRequestDeserialization_RejectsEveryOmittedMandatoryField(
        string propertyName)
    {
        PolicyElevationFrameException error = await Assert.ThrowsAsync<PolicyElevationFrameException>(
            () => ReadRequestFromBodyAsync(RemoveProperty(RequestJson, propertyName)));

        Assert.Equal(PolicyElevationFrameError.Malformed, error.Error);
        Assert.IsType<JsonException>(error.InnerException);
    }

    [Theory]
    [InlineData("protocolVersion")]
    [InlineData("requestId")]
    [InlineData("disposition")]
    [InlineData("brokerStatusCode")]
    [InlineData("brokerErrorCode")]
    [InlineData("committedStoreToken")]
    [InlineData("conflictStoreToken")]
    [InlineData("conflictState")]
    [InlineData("conflictPolicyId")]
    public async Task HostResponseDeserialization_RejectsEveryOmittedFixedWireField(
        string propertyName)
    {
        PolicyElevationFrameException error = await Assert.ThrowsAsync<PolicyElevationFrameException>(
            () => ReadResponseFromBodyAsync(RemoveProperty(StaleResponseJson, propertyName)));

        Assert.Equal(PolicyElevationFrameError.Malformed, error.Error);
        Assert.IsType<JsonException>(error.InnerException);
    }

    [Fact]
    public async Task HostResponseDeserialization_RejectsCommittedResponseWithoutStoreToken()
    {
        PolicyElevationFrameException error = await Assert.ThrowsAsync<PolicyElevationFrameException>(
            () => ReadResponseFromBodyAsync(
                RemoveProperty(CommittedResponseJson, "committedStoreToken")));

        Assert.Equal(PolicyElevationFrameError.Malformed, error.Error);
    }

    [Fact]
    public async Task HostResponseDeserialization_RejectsRejectedResponseWithoutErrorCode()
    {
        PolicyElevationFrameException error = await Assert.ThrowsAsync<PolicyElevationFrameException>(
            () => ReadResponseFromBodyAsync(
                RemoveProperty(RejectedResponseJson, "brokerErrorCode")));

        Assert.Equal(PolicyElevationFrameError.Malformed, error.Error);
    }

    [Theory]
    [InlineData("conflictStoreToken")]
    [InlineData("conflictState")]
    [InlineData("conflictPolicyId")]
    public async Task HostResponseDeserialization_RejectsStaleResponseWithoutAtomicConflictField(
        string propertyName)
    {
        PolicyElevationFrameException error = await Assert.ThrowsAsync<PolicyElevationFrameException>(
            () => ReadResponseFromBodyAsync(
                RemoveProperty(StaleResponseJson, propertyName)));

        Assert.Equal(PolicyElevationFrameError.Malformed, error.Error);
    }

    [Fact]
    public void ValidateResponse_RejectsDefaultInvalidDisposition()
    {
        var response = new PolicyElevationResponseMessage
        {
            RequestId = new string('a', PolicyElevationProtocol.RequestIdCharacters),
        };

        Assert.Equal(PolicyElevationDisposition.Invalid, response.Disposition);
        Assert.Throws<PolicyElevationFrameException>(
            () => PolicyElevationFrame.ValidateResponse(response));
    }

    [Fact]
    public void ValidateResponse_RequiresAnErrorCodeForUnknownDisposition()
    {
        var response = new PolicyElevationResponseMessage
        {
            RequestId = new string('a', PolicyElevationProtocol.RequestIdCharacters),
            Disposition = PolicyElevationDisposition.Unknown,
        };

        Assert.Throws<PolicyElevationFrameException>(
            () => PolicyElevationFrame.ValidateResponse(response));
    }

    [Fact]
    public void ValidateResponse_RejectsFieldsOutsideTheDispositionShape()
    {
        string requestId = new('a', PolicyElevationProtocol.RequestIdCharacters);
        PolicyElevationResponseMessage[] invalidResponses =
        [
            new()
            {
                RequestId = requestId,
                Disposition = PolicyElevationDisposition.Committed,
                CommittedStoreToken = "token",
                BrokerStatusCode = 200,
            },
            new()
            {
                RequestId = requestId,
                Disposition = PolicyElevationDisposition.Rejected,
                BrokerErrorCode = "InvalidPolicy",
                CommittedStoreToken = "token",
            },
            new()
            {
                RequestId = requestId,
                Disposition = PolicyElevationDisposition.Rejected,
                BrokerErrorCode = "InvalidPolicy",
                ConflictStoreToken = "token",
            },
            new()
            {
                RequestId = requestId,
                Disposition = PolicyElevationDisposition.Unknown,
                BrokerErrorCode = "Timeout",
                ConflictState = PolicyElevationManagementState.Invalid,
            },
        ];

        Assert.All(
            invalidResponses,
            response => Assert.Throws<PolicyElevationFrameException>(
                () => PolicyElevationFrame.ValidateResponse(response)));
    }

    [Fact]
    public async Task OverlongScalar_IsRejectedOnWrite()
    {
        using var stream = new MemoryStream();
        PolicyElevationResponseMessage response = ValidResponse();
        response.CommittedStoreToken =
            new string('x', PolicyElevationProtocol.MaxStoreTokenCharacters + 1);

        PolicyElevationFrameException error = await Assert.ThrowsAsync<PolicyElevationFrameException>(
            () => PolicyElevationFrame.WriteResponseAsync(stream, response, CancellationToken.None));

        Assert.Equal(PolicyElevationFrameError.Malformed, error.Error);
    }

    [Fact]
    public async Task StaleConflictContext_RoundTripsAtomicallyWithoutPolicyPayload()
    {
        using var stream = new MemoryStream();
        var sent = new PolicyElevationResponseMessage
        {
            RequestId = new string('a', PolicyElevationProtocol.RequestIdCharacters),
            Disposition = PolicyElevationDisposition.Rejected,
            BrokerStatusCode = 409,
            BrokerErrorCode = "StalePolicyStoreToken",
            ConflictStoreToken = "current-token",
            ConflictState = PolicyElevationManagementState.Active,
            ConflictPolicyId = "current-policy",
        };

        await PolicyElevationFrame.WriteResponseAsync(stream, sent, CancellationToken.None);
        stream.Position = 0;
        PolicyElevationResponseMessage received =
            await PolicyElevationFrame.ReadResponseAsync(stream, CancellationToken.None);

        Assert.Equal("current-token", received.ConflictStoreToken);
        Assert.Equal(PolicyElevationManagementState.Active, received.ConflictState);
        Assert.Equal("current-policy", received.ConflictPolicyId);
    }

    [Fact]
    public async Task DraftlessRequest_IsRejectedOnWrite()
    {
        using var stream = new MemoryStream();
        var request = new PolicyElevationRequestMessage
        {
            RequestId = new string('a', PolicyElevationProtocol.RequestIdCharacters),
        };

        PolicyElevationFrameException error = await Assert.ThrowsAsync<PolicyElevationFrameException>(
            () => PolicyElevationFrame.WriteRequestAsync(stream, request, CancellationToken.None));

        Assert.Equal(PolicyElevationFrameError.Malformed, error.Error);
    }

    private static async Task ReadResponseFromBodyAsync(string body)
    {
        byte[] payload = Encoding.UTF8.GetBytes(body);
        byte[] frame = new byte[PolicyElevationProtocol.FrameLengthPrefixBytes + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)payload.Length);
        payload.CopyTo(frame, PolicyElevationProtocol.FrameLengthPrefixBytes);

        using var stream = new MemoryStream(frame);
        await PolicyElevationFrame.ReadResponseAsync(stream, CancellationToken.None);
    }

    private static async Task ReadRequestFromBodyAsync(string body)
    {
        byte[] payload = Encoding.UTF8.GetBytes(body);
        byte[] frame = new byte[PolicyElevationProtocol.FrameLengthPrefixBytes + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)payload.Length);
        payload.CopyTo(frame, PolicyElevationProtocol.FrameLengthPrefixBytes);

        using var stream = new MemoryStream(frame);
        await PolicyElevationFrame.ReadRequestAsync(stream, CancellationToken.None);
    }

    private static string RemoveProperty(string json, string propertyName)
    {
        JsonObject root = JsonNode.Parse(json)?.AsObject()
            ?? throw new InvalidOperationException("The test JSON was not an object.");
        Assert.True(root.Remove(propertyName), $"The test JSON did not contain '{propertyName}'.");
        return root.ToJsonString();
    }
}
