using System.Text.Json;
using Devolutions.Now.Policy.Api;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages.PolicyEditor;
using UniGetUI.PackageEngine.AgentBroker.PolicyManagement;
using UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation;

namespace UniGetUI.Tests.PolicyEditor;

public class PolicyEditorProductionAdaptersTests
{
    [Fact]
    public async Task WriteAsync_CommittedTokenMatchesRefresh_ReturnsAuthoritativeSuccess()
    {
        PolicyManagementSnapshot management = PolicyEditorTestFixtures.BuildActiveManagement(
            PolicyEditorTestFixtures.BuildDocument(),
            "committed-token");
        var service = new FakeManagementService(
            new(BrokerPolicyManagementStatus.Retrieved, management));
        var client = new WindowsPolicyEditorWriteClient(
            new FakeElevator(request => new(
                PolicyElevationOutcome.Replaced,
                request,
                CommittedStoreToken: "committed-token")),
            service);

        PolicyWriteOutcome outcome = await client.WriteAsync(BuildRequest(), CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.False(outcome.SavedThenSuperseded);
        Assert.Same(management.Policy, outcome.Response!.Policy);
        Assert.Same(management, outcome.Response.Management);
        Assert.Equal(1, service.ManagementCallCount);
    }

    [Fact]
    public async Task WriteAsync_CommittedTokenDiffersFromRefresh_ReportsSavedThenSuperseded()
    {
        PolicyManagementSnapshot management = PolicyEditorTestFixtures.BuildActiveManagement(
            PolicyEditorTestFixtures.BuildDocument(),
            "newer-token");
        var client = new WindowsPolicyEditorWriteClient(
            new FakeElevator(request => new(
                PolicyElevationOutcome.Replaced,
                request,
                CommittedStoreToken: "committed-token")),
            new FakeManagementService(
                new(BrokerPolicyManagementStatus.Retrieved, management)));

        PolicyWriteOutcome outcome = await client.WriteAsync(BuildRequest(), CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.True(outcome.SavedThenSuperseded);
        Assert.Equal("newer-token", outcome.Response!.Management.StoreToken);
    }

    [Theory]
    [InlineData(BrokerPolicyManagementStatus.AgentUnavailable)]
    [InlineData(BrokerPolicyManagementStatus.InvalidResponse)]
    public async Task WriteAsync_CommittedButRefreshUnavailable_ReturnsUnknown(
        BrokerPolicyManagementStatus refreshStatus)
    {
        var client = new WindowsPolicyEditorWriteClient(
            new FakeElevator(request => new(
                PolicyElevationOutcome.Replaced,
                request,
                CommittedStoreToken: "committed-token")),
            new FakeManagementService(new(refreshStatus)));

        PolicyWriteOutcome outcome = await client.WriteAsync(BuildRequest(), CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(PolicyWriteFailureKind.WriteResultUnknown, outcome.FailureKind);
    }

    [Fact]
    public async Task WriteAsync_CommittedButRefreshHangs_TimesOutAsUnknown()
    {
        var service = new NonCancellableBlockingManagementService();
        var client = new WindowsPolicyEditorWriteClient(
            new FakeElevator(request => new(
                PolicyElevationOutcome.Replaced,
                request,
                CommittedStoreToken: "committed-token")),
            service,
            TimeSpan.FromMilliseconds(10));

        PolicyWriteOutcome outcome = await client
            .WriteAsync(BuildRequest(), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(outcome.Succeeded);
        Assert.Equal(PolicyWriteFailureKind.WriteResultUnknown, outcome.FailureKind);
        Assert.Equal(PolicyWriteFailureKind.WriteResultUnknown, outcome.FailureKind);
    }

    [Theory]
    [InlineData(PolicyElevationManagementState.Active, "policy-id", PolicyReplacementOperation.Update)]
    [InlineData(PolicyElevationManagementState.Active, "different-id", PolicyReplacementOperation.ReplaceIdentity)]
    [InlineData(PolicyElevationManagementState.Missing, null, PolicyReplacementOperation.Create)]
    [InlineData(PolicyElevationManagementState.Invalid, null, PolicyReplacementOperation.Repair)]
    public async Task WriteAsync_StaleAcknowledgement_ReconstructsExactRetryDecision(
        PolicyElevationManagementState state,
        string? activePolicyId,
        PolicyReplacementOperation expectedOperation)
    {
        var client = new WindowsPolicyEditorWriteClient(
            new FakeElevator(request => new(
                PolicyElevationOutcome.BrokerRejected,
                request,
                BrokerErrorCode: nameof(ErrorCode.StalePolicyStoreToken),
                ConflictStoreToken: "conflict-token",
                ConflictState: state,
                ConflictPolicyId: activePolicyId)),
            new FakeManagementService(new(BrokerPolicyManagementStatus.AgentUnavailable)));

        PolicyWriteOutcome outcome = await client.WriteAsync(BuildRequest(), CancellationToken.None);

        Assert.Equal(PolicyWriteFailureKind.BrokerRejected, outcome.FailureKind);
        Assert.Equal(ErrorCode.StalePolicyStoreToken, outcome.Error!.Code);
        Assert.NotNull(outcome.ConflictDecision);
        Assert.Equal("conflict-token", outcome.ConflictDecision!.Token);
        Assert.Equal(expectedOperation, outcome.ConflictDecision.Operation);
        Assert.Equal(activePolicyId, outcome.ConflictDecision.ActivePolicyId);
    }

    [Fact]
    public async Task WriteAsync_PreDispatchCancelledWithCallerCancellation_ThrowsCallerToken()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var service = new FakeManagementService(
            new(BrokerPolicyManagementStatus.AgentUnavailable));
        var client = new WindowsPolicyEditorWriteClient(
            new FakeElevator(request => new(
                PolicyElevationOutcome.Cancelled,
                request)),
            service);

        OperationCanceledException error = await Assert.ThrowsAsync<OperationCanceledException>(
            () => client.WriteAsync(BuildRequest(), cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(0, service.ManagementCallCount);
    }

    [Fact]
    public async Task WriteAsync_CancelledWithoutCallerCancellation_FailsClosedAsProtocolFailure()
    {
        var service = new FakeManagementService(
            new(BrokerPolicyManagementStatus.AgentUnavailable));
        var client = new WindowsPolicyEditorWriteClient(
            new FakeElevator(request => new(
                PolicyElevationOutcome.Cancelled,
                request)),
            service);

        PolicyWriteOutcome outcome = await client.WriteAsync(
            BuildRequest(),
            CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(PolicyWriteFailureKind.ProtocolFailed, outcome.FailureKind);
        Assert.Null(outcome.Error);
        Assert.Equal(0, service.ManagementCallCount);
    }

    private static PolicyEditorWriteRequest BuildRequest()
    {
        PolicyEditorDraftDocument draft = PolicyEditorTemplates.CreateNew("policy-id", "Contoso");
        using JsonDocument document = JsonDocument.Parse(PolicyEditorRawSyntax.ToCanonicalRaw(draft));
        return new(
            PolicyReplacementOperation.Update,
            PolicyConflictHandling.Reject,
            "expected-token",
            document.RootElement.Clone(),
            "validation-receipt",
            WarningsAcknowledged: false);
    }

    private sealed class FakeElevator(
        Func<PolicyElevationWriteRequest, PolicyElevationResult> resultFactory)
        : IPolicyWriteElevator
    {
        public Task<PolicyElevationResult> ReplacePolicyAsync(
            PolicyElevationWriteRequest request,
            CancellationToken cancellationToken)
            => Task.FromResult(resultFactory(request));
    }

    private sealed class FakeManagementService(BrokerPolicyManagementResult managementResult)
        : IBrokerPolicyManagementService
    {
        public int ManagementCallCount { get; private set; }

        public Task<BrokerPolicyManagementResult> GetManagementAsync(CancellationToken cancellationToken)
        {
            ManagementCallCount++;
            return Task.FromResult(managementResult);
        }

        public Task<BrokerPolicyValidationOutcome> ValidateAsync(
            JsonElement draft,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class NonCancellableBlockingManagementService : IBrokerPolicyManagementService
    {
        private readonly TaskCompletionSource<BrokerPolicyManagementResult> _never =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<BrokerPolicyManagementResult> GetManagementAsync(
            CancellationToken cancellationToken)
            => _never.Task;

        public Task<BrokerPolicyValidationOutcome> ValidateAsync(
            JsonElement draft,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
