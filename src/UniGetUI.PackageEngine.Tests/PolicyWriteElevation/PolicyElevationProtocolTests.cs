using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Devolutions.Now.Policy.Api;
using UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation;

namespace UniGetUI.PackageEngine.Tests.PolicyWriteElevation;

/// <summary>
/// Guards the frame budget arithmetic. The budgets must stay derived from the shared 16 MiB
/// policy-management body constant plus the exact envelope overhead, so a maximum-size draft can
/// always survive the elevation hop.
/// </summary>
public class PolicyElevationProtocolTests
{
    private const int SharedPolicyBodyBytes = 16 * 1024 * 1024;

    [Fact]
    public void MaxPolicyBody_TracksTheSharedBrokerConstant()
    {
        Assert.Equal(BrokerApi.MaxPolicyManagementBodyBytes, PolicyElevationProtocol.MaxPolicyManagementBodyBytes);
        Assert.Equal(SharedPolicyBodyBytes, PolicyElevationProtocol.MaxPolicyManagementBodyBytes);
    }

    [Fact]
    public void FrameBudgets_AreTheSharedBodyPlusExactEnvelopeOverhead()
    {
        Assert.Equal(
            PolicyElevationProtocol.MaxPolicyManagementBodyBytes + PolicyElevationProtocol.RequestEnvelopeOverheadBytes,
            PolicyElevationProtocol.MaxRequestFrameBytes);

        Assert.Equal(
            (PolicyElevationProtocol.MaxPolicyManagementBodyBytes
                * PolicyElevationProtocol.MaxResponsePolicyCopies)
            + PolicyElevationProtocol.MaxResponseDiagnosticsBytes
            + PolicyElevationProtocol.ResponseEnvelopeOverheadBytes,
            PolicyElevationProtocol.MaxResponseFrameBytes);
    }

    [Fact]
    public void FrameBudgets_AreNeverAnArbitrarySmallerLimit()
    {
        Assert.True(PolicyElevationProtocol.MaxRequestFrameBytes > SharedPolicyBodyBytes);
        Assert.True(PolicyElevationProtocol.MaxResponseFrameBytes > SharedPolicyBodyBytes);

        // The obvious wrong answers.
        Assert.NotEqual(256 * 1024, PolicyElevationProtocol.MaxRequestFrameBytes);
        Assert.NotEqual(256 * 1024, PolicyElevationProtocol.MaxResponseFrameBytes);
        Assert.NotEqual(SharedPolicyBodyBytes, PolicyElevationProtocol.MaxRequestFrameBytes);
        Assert.NotEqual(SharedPolicyBodyBytes, PolicyElevationProtocol.MaxResponseFrameBytes);
    }

    [Fact]
    public void RequestPropertyNameBudget_MatchesTheRealContract()
        => Assert.Equal(
            PolicyElevationProtocol.RequestPropertyNameCharacters,
            SumPropertyNameLengths<PolicyElevationRequestMessage>());

    [Fact]
    public void ResponsePropertyNameBudget_MatchesTheRealContract()
        => Assert.Equal(
            PolicyElevationProtocol.ResponsePropertyNameCharacters,
            SumPropertyNameLengths<PolicyElevationResponseMessage>());

    [Fact]
    public void RequestEnvelopeOverhead_CoversAWorstCaseEnvelope()
    {
        var request = new PolicyElevationRequestMessage
        {
            ProtocolVersion = PolicyElevationProtocol.Version,
            RequestId = new string('f', PolicyElevationProtocol.RequestIdCharacters),
            Operation = PolicyElevationOperation.ReplaceIdentity,
            ConflictHandling = PolicyElevationConflictHandling.ConfirmOverwrite,

            // Control characters are escaped as \uXXXX, the worst case the budget assumes.
            ExpectedStoreToken = new string('\u0001', PolicyElevationProtocol.MaxStoreTokenCharacters),
            ValidationReceipt = new string('\u0001', PolicyElevationProtocol.MaxValidationReceiptCharacters),
            WarningsAcknowledged = true,
            Draft = JsonDocument.Parse("{}").RootElement.Clone(),
        };

        byte[] serialized = JsonSerializer.SerializeToUtf8Bytes(
            request,
            PolicyElevationJsonContext.Default.PolicyElevationRequestMessage);

        // "{}" is the two-byte draft; everything else is envelope.
        Assert.True(
            serialized.Length - 2 <= PolicyElevationProtocol.RequestEnvelopeOverheadBytes,
            $"Envelope needed {serialized.Length - 2} bytes but only {PolicyElevationProtocol.RequestEnvelopeOverheadBytes} are budgeted.");
    }

    [Fact]
    public void ResponseEnvelopeOverhead_CoversAWorstCaseEnvelope()
    {
        var response = new PolicyElevationResponseMessage
        {
            ProtocolVersion = PolicyElevationProtocol.Version,
            RequestId = new string('f', PolicyElevationProtocol.RequestIdCharacters),
            Outcome = PolicyElevationResponseStatus.HelperRejected,
            Win32ErrorCode = int.MinValue,
            BrokerStatusCode = int.MinValue,
            BrokerErrorCode = new string('\u0001', PolicyElevationProtocol.MaxBrokerErrorCodeCharacters),
            Message = new string('\u0001', PolicyElevationProtocol.MaxMessageCharacters),
            Payload = JsonDocument.Parse("{}").RootElement.Clone(),
        };

        byte[] serialized = JsonSerializer.SerializeToUtf8Bytes(
            response,
            PolicyElevationJsonContext.Default.PolicyElevationResponseMessage);

        Assert.True(
            serialized.Length - 2 <= PolicyElevationProtocol.ResponseEnvelopeOverheadBytes,
            $"Envelope needed {serialized.Length - 2} bytes but only {PolicyElevationProtocol.ResponseEnvelopeOverheadBytes} are budgeted.");
    }

    [Fact]
    public void PackagedLayout_IsTheOnlyAcceptedHelperLocation()
    {
        Assert.Equal("UniGetUI.PolicyElevator.exe", PolicyElevationProtocol.HelperFileName);
        Assert.Equal("UniGetUI.exe", PolicyElevationProtocol.HostFileName);
        Assert.Equal("Assets", PolicyElevationProtocol.HelperRelativeDirectory);
        Assert.Equal("Utilities", PolicyElevationProtocol.HelperRelativeSubDirectory);
    }

    [Fact]
    public void UserDeclinedElevation_UsesTheDistinctWin32Code()
        => Assert.Equal(1223, PolicyElevationProtocol.ErrorCancelled);

    [Theory]
    [InlineData("""{"protocolVersion":"1.0","requestId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","operation":0,"conflictHandling":"Reject","expectedStoreToken":"token","validationReceipt":"receipt","warningsAcknowledged":false,"draft":{}}""")]
    [InlineData("""{"protocolVersion":"1.0","requestId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","operation":"update","conflictHandling":"Reject","expectedStoreToken":"token","validationReceipt":"receipt","warningsAcknowledged":false,"draft":{}}""")]
    [InlineData("""{"protocolVersion":"1.0","requestId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","operation":"Update","conflictHandling":"Reject","expectedStoreToken":"token","validationReceipt":"receipt","warningsAcknowledged":false,"draft":{},"extra":true}""")]
    public async Task RequestJson_RejectsNumericWrongCaseAndUnknownMembers(string json)
    {
        await using var stream = new MemoryStream();
        await PolicyElevationFrame.WriteAsync(
            stream,
            System.Text.Encoding.UTF8.GetBytes(json),
            PolicyElevationProtocol.MaxRequestFrameBytes,
            CancellationToken.None);
        stream.Position = 0;

        await Assert.ThrowsAsync<PolicyElevationFrameException>(
            () => PolicyElevationFrame.ReadRequestAsync(stream, CancellationToken.None));
    }

    [Theory]
    [InlineData("", "receipt")]
    [InlineData("token", "")]
    [InlineData("bad token", "receipt")]
    [InlineData("token", "bad\nreceipt")]
    public void RequestValidation_RequiresBoundedSafeAsciiCredentials(
        string token,
        string receipt)
    {
        var request = new PolicyElevationRequestMessage
        {
            RequestId = new string('a', PolicyElevationProtocol.RequestIdCharacters),
            ExpectedStoreToken = token,
            ValidationReceipt = receipt,
            Draft = JsonDocument.Parse("{}").RootElement.Clone(),
        };

        Assert.Throws<PolicyElevationFrameException>(
            () => PolicyElevationFrame.ValidateRequest(request));
    }

    private static int SumPropertyNameLengths<T>()
    {
        int total = 0;

        foreach (PropertyInfo property in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            JsonPropertyNameAttribute? name = property.GetCustomAttribute<JsonPropertyNameAttribute>();
            Assert.NotNull(name);
            total += name.Name.Length;
        }

        return total;
    }
}
