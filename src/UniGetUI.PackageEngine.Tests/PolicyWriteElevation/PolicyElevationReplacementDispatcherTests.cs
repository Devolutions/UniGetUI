using System.Text.Json;
using Devolutions.Now.Policy.Api;
using UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation;

namespace UniGetUI.PackageEngine.Tests.PolicyWriteElevation;

public class PolicyElevationReplacementDispatcherTests
{
    [Theory]
    [InlineData(PolicyElevationOperation.Update, PolicyReplacementOperation.Update)]
    [InlineData(
        PolicyElevationOperation.ReplaceIdentity,
        PolicyReplacementOperation.ReplaceIdentity)]
    [InlineData(PolicyElevationOperation.Create, PolicyReplacementOperation.Create)]
    [InlineData(PolicyElevationOperation.Repair, PolicyReplacementOperation.Repair)]
    public async Task DispatchAsync_MapsEveryOperationExplicitly(
        PolicyElevationOperation wire,
        PolicyReplacementOperation expected)
    {
        PolicyReplacementRequest? observed = null;

        await PolicyElevationReplacementDispatcher.DispatchAsync(
            Request(operation: wire),
            (request, _) =>
            {
                observed = request;
                return Task.FromResult(Response());
            },
            CancellationToken.None);

        Assert.Equal(expected, observed!.Operation);
    }

    [Theory]
    [InlineData(PolicyElevationConflictHandling.Reject, PolicyConflictHandling.Reject)]
    [InlineData(
        PolicyElevationConflictHandling.ConfirmOverwrite,
        PolicyConflictHandling.ConfirmOverwrite)]
    public async Task DispatchAsync_MapsEveryConflictHandlingExplicitly(
        PolicyElevationConflictHandling wire,
        PolicyConflictHandling expected)
    {
        PolicyReplacementRequest? observed = null;

        await PolicyElevationReplacementDispatcher.DispatchAsync(
            Request(conflictHandling: wire),
            (request, _) =>
            {
                observed = request;
                return Task.FromResult(Response());
            },
            CancellationToken.None);

        Assert.Equal(expected, observed!.ConflictHandling);
    }

    [Fact]
    public async Task DispatchAsync_InvalidOperationFailsBeforeBrokerCall()
    {
        int calls = 0;

        await Assert.ThrowsAsync<InvalidDataException>(
            () => PolicyElevationReplacementDispatcher.DispatchAsync(
                Request(operation: (PolicyElevationOperation)int.MaxValue),
                (_, _) =>
                {
                    calls++;
                    return Task.FromResult(Response());
                },
                CancellationToken.None));

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task DispatchAsync_InvalidConflictHandlingFailsBeforeBrokerCall()
    {
        int calls = 0;

        await Assert.ThrowsAsync<InvalidDataException>(
            () => PolicyElevationReplacementDispatcher.DispatchAsync(
                Request(conflictHandling: (PolicyElevationConflictHandling)int.MaxValue),
                (_, _) =>
                {
                    calls++;
                    return Task.FromResult(Response());
                },
                CancellationToken.None));

        Assert.Equal(0, calls);
    }

    [Fact]
    public void BrokerRequestBodyLimit_CountsTheCompleteReplacementEnvelope()
    {
        PolicyElevationRequestMessage empty = Request("""{"padding":""}""");
        int emptySize = PolicyElevationReplacementDispatcher.GetBrokerRequestBodyByteCount(empty);
        int paddingLength = BrokerApi.MaxPolicyManagementBodyBytes - emptySize;

        PolicyElevationRequestMessage atLimit = Request(
            $$"""{"padding":"{{new string('a', paddingLength)}}"}""");
        PolicyElevationRequestMessage overLimit = Request(
            $$"""{"padding":"{{new string('a', paddingLength + 1)}}"}""");

        Assert.Equal(
            BrokerApi.MaxPolicyManagementBodyBytes,
            PolicyElevationReplacementDispatcher.GetBrokerRequestBodyByteCount(atLimit));
        Assert.True(PolicyElevationReplacementDispatcher.IsBrokerRequestWithinLimit(atLimit));
        Assert.False(PolicyElevationReplacementDispatcher.IsBrokerRequestWithinLimit(overLimit));
    }

    [Fact]
    public async Task DispatchAsync_OversizedBrokerRequestFailsBeforeBrokerCall()
    {
        int calls = 0;
        PolicyElevationRequestMessage empty = Request("""{"padding":""}""");
        int paddingLength =
            BrokerApi.MaxPolicyManagementBodyBytes
            - PolicyElevationReplacementDispatcher.GetBrokerRequestBodyByteCount(empty)
            + 1;
        PolicyElevationRequestMessage request = Request(
            $$"""{"padding":"{{new string('a', paddingLength)}}"}""");

        await Assert.ThrowsAsync<InvalidDataException>(
            () => PolicyElevationReplacementDispatcher.DispatchAsync(
                request,
                (_, _) =>
                {
                    calls++;
                    return Task.FromResult(Response());
                },
                CancellationToken.None));

        Assert.Equal(0, calls);
    }

    private static PolicyElevationRequestMessage Request(
        string draftJson = "{}",
        PolicyElevationOperation operation = PolicyElevationOperation.Update,
        PolicyElevationConflictHandling conflictHandling =
            PolicyElevationConflictHandling.Reject)
    {
        using JsonDocument draft = JsonDocument.Parse(draftJson);
        return new PolicyElevationRequestMessage
        {
            RequestId = new string('a', PolicyElevationProtocol.RequestIdCharacters),
            Operation = operation,
            ConflictHandling = conflictHandling,
            ExpectedStoreToken = "token",
            ValidationReceipt = "receipt",
            Draft = draft.RootElement.Clone(),
        };
    }

    private static PolicyReplacementResponse Response() =>
        new()
        {
            Management = new PolicyManagementSnapshot
            {
                State = PolicyManagementState.Active,
                StoreToken = "new-token",
            },
        };

}
