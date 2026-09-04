using Devolutions.Now.Policy.Api;
using Devolutions.Now.Policy.Model;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;
using UniGetUI.PackageEngine.AgentBroker;
using ApiTransport = Devolutions.Now.Policy.Api.Transport;
using PolicyDecision = Devolutions.Now.Policy.Model.Decision;
using PolicyOperation = Devolutions.Now.Policy.Model.Operation;

namespace UniGetUI.Tests;

public class AgentPolicyInspectorViewModelTests
{
    [Fact]
    public async Task LoadAsync_PresentsFullPolicyInDocumentOrder()
    {
        PolicyResponse response = BuildFullResponse();
        string json = PolicySerializer.Serialize(response.Policy);
        using var viewModel = new AgentPolicyInspectorViewModel(
            new StubInspector(new(BrokerPolicyInspectionStatus.Connected, response, json)));

        await viewModel.LoadAsync();

        Assert.True(viewModel.HasPolicy);
        Assert.False(viewModel.HasNoRules);
        Assert.Equal(json, viewModel.RawJson);
        Assert.Equal(["first-rule", "second-rule"], viewModel.Rules.Select(rule => rule.Id));
        Assert.Equal(18, viewModel.Rules[0].MatchRows.Count);
        Assert.Equal(13, viewModel.Rules[0].ConstraintRows.Count);
        Assert.Contains(viewModel.MetadataRows, row => row.Label == "Server version" && row.Value == "2026.8-tests");
        Assert.Contains(viewModel.EnforcementRows, row => row.Label == "Default decision" && row.Value == "Deny");
    }

    [Fact]
    public async Task LoadAsync_PreservesWhitespaceOnlyPolicyValues()
    {
        PolicyResponse response = BuildFullResponse();
        response.Policy.Metadata.Publisher = " ";
        response.Policy.Metadata.Description = " ";
        response.Policy.Rules[0].Match.Sources = [" "];
        response.Policy.Rules[0].Constraints!.AllowedCustomParameters = [" "];
        string json = PolicySerializer.Serialize(response.Policy);
        using var viewModel = new AgentPolicyInspectorViewModel(
            new StubInspector(new(
                BrokerPolicyInspectionStatus.Connected,
                response,
                json)));
        string? copied = null;
        viewModel.CopyTextRequested += (_, text) => copied = text;

        await viewModel.LoadAsync();

        PolicyDetailRow publisher = viewModel.MetadataRows.Single(row => row.Label == "Publisher");
        PolicyDetailRow description = viewModel.MetadataRows.Single(row => row.Label == "Description");
        PolicyDetailRow sources = viewModel.Rules[0].MatchRows.Single(row => row.Label == "Sources");
        PolicyDetailRow customParameters = viewModel.Rules[0].ConstraintRows.Single(
            row => row.Label == "Allowed custom parameters");
        Assert.Equal(" ", publisher.Value);
        Assert.Equal("Publisher:  ", publisher.AutomationName);
        Assert.Equal(" ", description.Value);
        Assert.Equal("Description:  ", description.AutomationName);
        Assert.Equal(" ", sources.Value);
        Assert.NotEqual("Any", sources.Value);
        Assert.Equal(" ", customParameters.Value);
        Assert.NotEqual("None", customParameters.Value);
        Assert.Equal(json, viewModel.RawJson);
        Assert.Contains("\"Publisher\": \" \"", viewModel.RawJson);
        Assert.Contains("\"Description\": \" \"", viewModel.RawJson);

        viewModel.CopyRawJsonCommand.Execute(null);

        Assert.Equal(json, copied);
    }

    [Fact]
    public async Task LoadAsync_PresentsEmptyOptionalPolicy()
    {
        var response = new PolicyResponse
        {
            Server = new ServerContext { ServerVersion = "tests", Transport = ApiTransport.HttpNamedPipe },
            Policy = new PolicyDocument
            {
                Metadata = new PolicyMetadata
                {
                    Id = "empty",
                    Publisher = "Contoso",
                    Revision = 1,
                    PublishedAt = DateTimeOffset.Parse("2026-08-18T00:00:00Z"),
                },
                Enforcement = new PolicyEnforcement
                {
                    DefaultDecision = PolicyDecision.Allow,
                    RulePrecedence = RulePrecedence.PriorityThenDeny,
                },
            },
        };
        using var viewModel = new AgentPolicyInspectorViewModel(
            new StubInspector(new(
                BrokerPolicyInspectionStatus.Connected,
                response,
                PolicySerializer.Serialize(response.Policy))));

        await viewModel.LoadAsync();

        Assert.True(viewModel.HasPolicy);
        Assert.True(viewModel.HasNoRules);
        Assert.Empty(viewModel.Rules);
        Assert.Contains(viewModel.MetadataRows, row => row.Label == "Valid until" && row.Value == "Not set");
    }

    [Theory]
    [InlineData(BrokerPolicyInspectionStatus.AgentUnavailable, "Devolutions Agent is unavailable")]
    [InlineData(BrokerPolicyInspectionStatus.Unsupported, "Policy inspection is unsupported")]
    [InlineData(BrokerPolicyInspectionStatus.AccessDenied, "Access to the active policy was denied")]
    [InlineData(BrokerPolicyInspectionStatus.PolicyUnavailable, "The active policy is unavailable")]
    [InlineData(BrokerPolicyInspectionStatus.InvalidResponse, "The policy response is invalid")]
    [InlineData(BrokerPolicyInspectionStatus.UnsupportedPlatform, "Policy inspection is available on Windows only")]
    public async Task LoadAsync_PresentsFailureState(
        BrokerPolicyInspectionStatus status,
        string expectedTitle)
    {
        using var viewModel = new AgentPolicyInspectorViewModel(
            new StubInspector(new(status)));

        await viewModel.LoadAsync();

        Assert.False(viewModel.HasPolicy);
        Assert.Equal(expectedTitle, viewModel.Status.Title);
    }

    [Fact]
    public async Task Refresh_CancelsStaleRequestAndKeepsNewestResult()
    {
        var inspector = new RefreshInspector(BuildFullResponse());
        using var viewModel = new AgentPolicyInspectorViewModel(inspector);

        Task first = viewModel.LoadAsync();
        Assert.True(viewModel.IsLoading);

        await viewModel.RefreshCommand.ExecuteAsync(null);
        await first;

        Assert.True(inspector.FirstRequestCanceled);
        Assert.True(viewModel.HasPolicy);
        Assert.Equal("contoso.full", viewModel.MetadataRows.Single(row => row.Label == "Policy ID").Value);
    }

    [Fact]
    public async Task Refresh_IgnoresStaleResultWhenInspectorDoesNotHonorCancellation()
    {
        var inspector = new NonCancelableRefreshInspector(BuildFullResponse());
        using var viewModel = new AgentPolicyInspectorViewModel(inspector);

        Task first = viewModel.LoadAsync();
        await inspector.FirstRequestStarted.Task;
        await viewModel.RefreshCommand.ExecuteAsync(null);
        inspector.CompleteFirstRequest();
        await first;

        Assert.Equal(
            "newest",
            viewModel.MetadataRows.Single(row => row.Label == "Policy ID").Value);
    }

    [Fact]
    public async Task Dispose_CancelsInFlightRequest()
    {
        var inspector = new BlockingInspector();
        var viewModel = new AgentPolicyInspectorViewModel(inspector);
        Task pending = viewModel.LoadAsync();

        viewModel.Dispose();
        await pending;

        Assert.True(inspector.Canceled);
    }

    [Fact]
    public async Task CopyRawJson_RaisesDisplayedCanonicalJson()
    {
        PolicyResponse response = BuildFullResponse();
        string json = PolicySerializer.Serialize(response.Policy);
        using var viewModel = new AgentPolicyInspectorViewModel(
            new StubInspector(new(BrokerPolicyInspectionStatus.Connected, response, json)));
        string? copied = null;
        viewModel.CopyTextRequested += (_, text) => copied = text;
        await viewModel.LoadAsync();

        viewModel.CopyRawJsonCommand.Execute(null);

        Assert.Equal(json, copied);
    }

    private static PolicyResponse BuildFullResponse() =>
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
                    Id = "contoso.full",
                    Publisher = "Contoso",
                    Revision = 7,
                    PublishedAt = DateTimeOffset.Parse("2026-08-18T00:00:00Z"),
                    ValidFrom = DateTimeOffset.Parse("2026-08-18T01:00:00Z"),
                    ValidUntil = DateTimeOffset.Parse("2027-08-18T01:00:00Z"),
                    Description = "Full test policy",
                    SupportUrl = "https://contoso.example/policy",
                },
                Enforcement = new PolicyEnforcement
                {
                    DefaultDecision = PolicyDecision.Deny,
                    RulePrecedence = RulePrecedence.PriorityThenDeny,
                    AuditMode = true,
                },
                Rules =
                [
                    new PolicyRule
                    {
                        Id = "first-rule",
                        Priority = 10,
                        Decision = PolicyDecision.Allow,
                        Reason = "approved",
                        Match = new PolicyMatch
                        {
                            Operations = [PolicyOperation.Install],
                            PackageIdentifiers = ["Contoso.App"],
                            Interactive = [false],
                        },
                        Constraints = new PolicyConstraints
                        {
                            AllowInteractive = false,
                            AllowedCustomParameters = ["--silent"],
                            DeniedCustomParameters = ["--unsafe"],
                        },
                    },
                    new PolicyRule
                    {
                        Id = "second-rule",
                        Enabled = false,
                        Priority = 20,
                        Decision = PolicyDecision.Deny,
                    },
                ],
            },
        };

    private sealed class StubInspector(BrokerPolicyInspectionResult result) : IBrokerPolicyInspector
    {
        public Task<BrokerPolicyInspectionResult> InspectAsync(CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }

    private sealed class RefreshInspector(PolicyResponse response) : IBrokerPolicyInspector
    {
        private int _calls;
        public bool FirstRequestCanceled { get; private set; }

        public async Task<BrokerPolicyInspectionResult> InspectAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    FirstRequestCanceled = true;
                    throw;
                }
            }

            return new(
                BrokerPolicyInspectionStatus.Connected,
                response,
                PolicySerializer.Serialize(response.Policy));
        }
    }

    private sealed class BlockingInspector : IBrokerPolicyInspector
    {
        public bool Canceled { get; private set; }

        public async Task<BrokerPolicyInspectionResult> InspectAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Canceled = true;
                throw;
            }

            throw new InvalidOperationException();
        }
    }

    private sealed class NonCancelableRefreshInspector(PolicyResponse response) : IBrokerPolicyInspector
    {
        private readonly TaskCompletionSource _completeFirstRequest =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public TaskCompletionSource FirstRequestStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void CompleteFirstRequest() => _completeFirstRequest.SetResult();

        public async Task<BrokerPolicyInspectionResult> InspectAsync(CancellationToken cancellationToken)
        {
            PolicyResponse requestResponse;
            if (Interlocked.Increment(ref _calls) == 1)
            {
                FirstRequestStarted.SetResult();
                await _completeFirstRequest.Task;
                requestResponse = WithPolicyId(response, "stale");
            }
            else
            {
                requestResponse = WithPolicyId(response, "newest");
            }

            return new(
                BrokerPolicyInspectionStatus.Connected,
                requestResponse,
                PolicySerializer.Serialize(requestResponse.Policy));
        }

        private static PolicyResponse WithPolicyId(PolicyResponse source, string policyId) =>
            new()
            {
                Server = source.Server,
                Policy = new PolicyDocument
                {
                    PolicyVersion = source.Policy.PolicyVersion,
                    Metadata = new PolicyMetadata
                    {
                        Id = policyId,
                        Publisher = source.Policy.Metadata.Publisher,
                        Revision = source.Policy.Metadata.Revision,
                        PublishedAt = source.Policy.Metadata.PublishedAt,
                    },
                    Enforcement = source.Policy.Enforcement,
                },
            };
    }
}
