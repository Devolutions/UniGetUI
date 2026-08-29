using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation;

namespace UniGetUI.PackageEngine.Tests.PolicyWriteElevation;

/// <summary>
/// Framing behaviour: exactly one length-prefixed UTF-8 JSON frame per direction, with distinct
/// end-of-stream, oversized and malformed classifications.
/// </summary>
public class PolicyElevationFrameTests
{
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
            Outcome = PolicyElevationResponseStatus.Replaced,
            BrokerStatusCode = 200,
            Message = "ok",
            Payload = JsonDocument.Parse("""{"storeToken":"abc"}""").RootElement.Clone(),
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

        Assert.Equal(PolicyElevationResponseStatus.Replaced, received.Outcome);
        Assert.Equal(200, received.BrokerStatusCode);
        Assert.Equal("ok", received.Message);
        Assert.Equal(sent.Payload!.Value.GetRawText(), received.Payload!.Value.GetRawText());
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
                """{"protocolVersion":"9.9","requestId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","outcome":0}"""));

        Assert.Equal(PolicyElevationFrameError.Malformed, error.Error);
    }

    [Fact]
    public async Task ShortRequestId_IsReportedAsMalformed()
    {
        PolicyElevationFrameException error = await Assert.ThrowsAsync<PolicyElevationFrameException>(
            () => ReadResponseFromBodyAsync("""{"protocolVersion":"1.0","requestId":"abc","outcome":0}"""));

        Assert.Equal(PolicyElevationFrameError.Malformed, error.Error);
    }

    [Fact]
    public async Task UndefinedOutcome_IsReportedAsMalformed()
    {
        PolicyElevationFrameException error = await Assert.ThrowsAsync<PolicyElevationFrameException>(
            () => ReadResponseFromBodyAsync(
                """{"protocolVersion":"1.0","requestId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","outcome":99}"""));

        Assert.Equal(PolicyElevationFrameError.Malformed, error.Error);
    }

    [Fact]
    public async Task OverlongScalar_IsRejectedOnWrite()
    {
        using var stream = new MemoryStream();
        PolicyElevationResponseMessage response = ValidResponse();
        response.Message = new string('x', PolicyElevationProtocol.MaxMessageCharacters + 1);

        PolicyElevationFrameException error = await Assert.ThrowsAsync<PolicyElevationFrameException>(
            () => PolicyElevationFrame.WriteResponseAsync(stream, response, CancellationToken.None));

        Assert.Equal(PolicyElevationFrameError.Malformed, error.Error);
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
}
