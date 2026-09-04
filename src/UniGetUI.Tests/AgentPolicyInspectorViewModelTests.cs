using Avalonia.Automation;
using Devolutions.Now.Policy.Api;
using Devolutions.Now.Policy.Model;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages.PolicyEditor;
using UniGetUI.PackageEngine.AgentBroker;
using UniGetUI.PackageEngine.AgentBroker.PolicyManagement;
using UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation;
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
        (string? Message, AutomationLiveSetting LiveSetting)? announcement = null;
        using var viewModel = new AgentPolicyInspectorViewModel(
            new StubInspector(new(BrokerPolicyInspectionStatus.Connected, response, json)),
            (message, liveSetting) => announcement = (message, liveSetting));

        await viewModel.LoadAsync();

        Assert.True(viewModel.HasPolicy);
        Assert.False(viewModel.HasNoRules);
        Assert.Equal(json, viewModel.RawJson);
        Assert.Equal(["first-rule", "second-rule"], viewModel.Rules.Select(rule => rule.Id));
        Assert.Equal(18, viewModel.Rules[0].MatchRows.Count);
        Assert.Equal(13, viewModel.Rules[0].ConstraintRows.Count);
        Assert.Contains(viewModel.MetadataRows, row => row.Label == "Server version" && row.Value == "2026.8-tests");
        Assert.Contains(viewModel.EnforcementRows, row => row.Label == "Default decision" && row.Value == "Deny");
        Assert.Equal(AutomationLiveSetting.Polite, announcement?.LiveSetting);
        Assert.Contains("Connected to Devolutions Agent", announcement?.Message);
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
    [InlineData(
        BrokerPolicyInspectionStatus.AgentUnavailable,
        "Devolutions Agent is unavailable",
        AutomationLiveSetting.Assertive)]
    [InlineData(
        BrokerPolicyInspectionStatus.Unsupported,
        "Policy inspection is unsupported",
        AutomationLiveSetting.Polite)]
    [InlineData(
        BrokerPolicyInspectionStatus.AccessDenied,
        "Access to the active policy was denied",
        AutomationLiveSetting.Assertive)]
    [InlineData(
        BrokerPolicyInspectionStatus.PolicyUnavailable,
        "The active policy is unavailable",
        AutomationLiveSetting.Assertive)]
    [InlineData(
        BrokerPolicyInspectionStatus.InvalidResponse,
        "The policy response is invalid",
        AutomationLiveSetting.Assertive)]
    [InlineData(
        BrokerPolicyInspectionStatus.UnsupportedPlatform,
        "Policy inspection is available on Windows only",
        AutomationLiveSetting.Polite)]
    public async Task LoadAsync_PresentsFailureState(
        BrokerPolicyInspectionStatus status,
        string expectedTitle,
        AutomationLiveSetting expectedLiveSetting)
    {
        (string? Message, AutomationLiveSetting LiveSetting)? announcement = null;
        using var viewModel = new AgentPolicyInspectorViewModel(
            new StubInspector(new(status)),
            (message, liveSetting) => announcement = (message, liveSetting));

        await viewModel.LoadAsync();

        Assert.False(viewModel.HasPolicy);
        Assert.Equal(expectedTitle, viewModel.Status.Title);
        Assert.Equal(expectedLiveSetting, announcement?.LiveSetting);
        Assert.Contains(expectedTitle, announcement?.Message);
    }

    [Theory]
    [InlineData(
        BrokerPolicyManagementStatus.AccessDenied,
        "Access to policy management was denied",
        AutomationLiveSetting.Assertive)]
    [InlineData(
        BrokerPolicyManagementStatus.Unsupported,
        "Policy management is unsupported",
        AutomationLiveSetting.Polite)]
    public async Task LoadManagementAsync_AnnouncesFinalStatus(
        BrokerPolicyManagementStatus status,
        string expectedTitle,
        AutomationLiveSetting expectedLiveSetting)
    {
        (string? Message, AutomationLiveSetting LiveSetting)? announcement = null;
        using var viewModel = new AgentPolicyInspectorViewModel(
            new StubInspector(new(BrokerPolicyInspectionStatus.Unsupported)),
            new StubManagementService(new(status)),
            (message, liveSetting) => announcement = (message, liveSetting));

        await viewModel.LoadManagementAsync();

        Assert.Equal(expectedTitle, viewModel.ManagementStatus.Title);
        Assert.Equal(expectedLiveSetting, announcement?.LiveSetting);
        Assert.Contains(expectedTitle, announcement?.Message);
    }

    [Fact]
    public async Task ReplaceIdentity_PreservesWhitespaceOnlyActivePublisher()
    {
        PolicyDocument policy = BuildFullResponse().Policy;
        policy.Metadata.Publisher = " ";
        policy.Metadata.Id =
            $"{new string('a', PolicyEditorTemplates.ResourceIdMaxLength - 4)}-new";
        var snapshot = new PolicyManagementSnapshot
        {
            State = PolicyManagementState.Active,
            StoreToken = "token-1",
            Policy = policy,
            WriteCapability = PolicyWriteCapability.Writable,
        };
        using var viewModel = new AgentPolicyInspectorViewModel(
            new StubInspector(new(BrokerPolicyInspectionStatus.Unsupported)),
            new StubManagementService(
                new BrokerPolicyManagementResult(
                    BrokerPolicyManagementStatus.Retrieved,
                    snapshot)),
            new StubWriteElevationEligibility(PolicyWriteElevationEligibilityStatus.Eligible),
            (_, _) => { });
        PolicyEditorLaunchRequest? launch = null;
        viewModel.OpenPolicyEditorRequested += (_, request) => launch = request;
        await viewModel.LoadManagementAsync();

        viewModel.ReplaceIdentityCommand.Execute(null);

        Assert.NotNull(launch);
        Assert.Equal(PolicyEditorOperationKind.ReplaceIdentity, launch.Operation);
        Assert.Equal(" ", launch.SeedDraft!.Metadata.Publisher);
        Assert.NotEqual(policy.Metadata.Id, launch.SeedDraft.Metadata.Id);
        Assert.InRange(
            launch.SeedDraft.Metadata.Id.Length,
            1,
            PolicyEditorTemplates.ResourceIdMaxLength);
    }

    [Fact]
    public async Task ReplaceIdentity_IsUnavailableWhenTheActiveIdentifierIsInvalid()
    {
        PolicyDocument policy = BuildFullResponse().Policy;
        policy.Metadata.Id = "invalid id";
        var snapshot = new PolicyManagementSnapshot
        {
            State = PolicyManagementState.Active,
            WriteCapability = PolicyWriteCapability.Writable,
            Policy = policy,
        };
        using var viewModel = new AgentPolicyInspectorViewModel(
            new StubInspector(new(BrokerPolicyInspectionStatus.Unsupported)),
            new StubManagementService(
                new BrokerPolicyManagementResult(
                    BrokerPolicyManagementStatus.Retrieved,
                    snapshot)),
            new StubWriteElevationEligibility(PolicyWriteElevationEligibilityStatus.Eligible),
            (_, _) => { });
        PolicyEditorLaunchRequest? launch = null;
        viewModel.OpenPolicyEditorRequested += (_, request) => launch = request;

        await viewModel.LoadManagementAsync();

        Assert.False(viewModel.CanReplaceIdentity);
        viewModel.ReplaceIdentityCommand.Execute(null);
        Assert.Null(launch);
    }

    [Theory]
    [InlineData(PolicyManagementState.Active, true, false, false, true)]
    [InlineData(PolicyManagementState.Missing, false, true, false, false)]
    [InlineData(PolicyManagementState.Invalid, false, false, true, false)]
    public async Task ProtectedPackagedInstall_EnablesOnlyTheStateAppropriateWriteActions(
        PolicyManagementState state,
        bool canEdit,
        bool canCreate,
        bool canRepair,
        bool canReplaceIdentity)
    {
        PolicyDocument? policy = state == PolicyManagementState.Active
            ? BuildFullResponse().Policy
            : null;
        using AgentPolicyInspectorViewModel viewModel = BuildManagementViewModel(
            state,
            new StubWriteElevationEligibility(PolicyWriteElevationEligibilityStatus.Eligible),
            policy);

        await viewModel.LoadManagementAsync();

        Assert.Equal(canEdit, viewModel.CanEdit);
        Assert.Equal(canCreate, viewModel.CanCreate);
        Assert.Equal(canRepair, viewModel.CanRepair);
        Assert.Equal(canReplaceIdentity, viewModel.CanReplaceIdentity);
    }

    [Theory]
    [InlineData(
        PolicyWriteElevationEligibilityStatus.HelperMissing,
        PolicyManagementState.Missing,
        "helper is missing")]
    [InlineData(
        PolicyWriteElevationEligibilityStatus.ProtectedInstallRequired,
        PolicyManagementState.Active,
        "not administrator-protected")]
    [InlineData(
        PolicyWriteElevationEligibilityStatus.ProtectedInstallRequired,
        PolicyManagementState.Missing,
        "not administrator-protected")]
    [InlineData(
        PolicyWriteElevationEligibilityStatus.ProtectedInstallRequired,
        PolicyManagementState.Invalid,
        "not administrator-protected")]
    [InlineData(
        PolicyWriteElevationEligibilityStatus.InvalidInstallation,
        PolicyManagementState.Active,
        "cannot securely launch")]
    public async Task IneligibleInstall_DisablesEveryWriteActionBeforePrompting(
        PolicyWriteElevationEligibilityStatus status,
        PolicyManagementState state,
        string expectedReason)
    {
        PolicyResponse inspection = BuildFullResponse();
        PolicyDocument? managedPolicy = state == PolicyManagementState.Active
            ? inspection.Policy
            : null;
        using var viewModel = new AgentPolicyInspectorViewModel(
            new StubInspector(new(
                BrokerPolicyInspectionStatus.Connected,
                inspection,
                PolicySerializer.Serialize(inspection.Policy))),
            new StubManagementService(new(
                BrokerPolicyManagementStatus.Retrieved,
                new PolicyManagementSnapshot
                {
                    State = state,
                    StoreToken = "token",
                    Policy = managedPolicy,
                    WriteCapability = PolicyWriteCapability.Writable,
                })),
            new StubWriteElevationEligibility(status),
            (_, _) => { });
        int launchCount = 0;
        viewModel.OpenPolicyEditorRequested += (_, _) => launchCount++;

        await viewModel.LoadManagementAsync();
        await viewModel.LoadAsync();
        viewModel.EditPolicyCommand.Execute(null);
        viewModel.CreatePolicyCommand.Execute(null);
        viewModel.RepairPolicyCommand.Execute(null);
        viewModel.ReplaceIdentityCommand.Execute(null);

        Assert.True(viewModel.HasManagementSnapshot);
        Assert.True(viewModel.HasPolicy);
        Assert.Equal("ReadOnly", viewModel.ManagementCapabilityText);
        Assert.Contains(expectedReason, viewModel.ManagementReadOnlyReasonText);
        Assert.Contains("all users", viewModel.ManagementReadOnlyReasonText);
        Assert.False(viewModel.CanEdit);
        Assert.False(viewModel.CanCreate);
        Assert.False(viewModel.CanRepair);
        Assert.False(viewModel.CanReplaceIdentity);
        Assert.Equal(0, launchCount);
    }

    [Fact]
    public async Task AgentReadOnlyCapability_DoesNotProbeLocalWriteEligibility()
    {
        var eligibility = new CountingWriteElevationEligibility();
        PolicyDocument policy = BuildFullResponse().Policy;
        using var viewModel = new AgentPolicyInspectorViewModel(
            new StubInspector(new(BrokerPolicyInspectionStatus.Unsupported)),
            new StubManagementService(new(
                BrokerPolicyManagementStatus.Retrieved,
                new PolicyManagementSnapshot
                {
                    State = PolicyManagementState.Active,
                    Policy = policy,
                    WriteCapability = PolicyWriteCapability.ReadOnly,
                })),
            eligibility,
            (_, _) => { });

        await viewModel.LoadManagementAsync();

        Assert.Equal(0, eligibility.Invocations);
        Assert.False(viewModel.CanEdit);
        Assert.True(viewModel.HasManagementSnapshot);
    }

    [Fact]
    public async Task ManagementRefresh_CancelsStaleEligibilityProbeAndAppliesNewestResult()
    {
        var eligibility = new CancelAwareRefreshWriteElevationEligibility();
        using AgentPolicyInspectorViewModel viewModel = BuildManagementViewModel(
            PolicyManagementState.Active,
            eligibility,
            BuildFullResponse().Policy);

        Task first = viewModel.LoadManagementAsync();
        await eligibility.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await viewModel.RefreshManagementCommand.ExecuteAsync(null);
        await first;

        Assert.True(eligibility.FirstCanceled);
        Assert.True(viewModel.CanEdit);
        Assert.False(viewModel.IsManagementLoading);
    }

    [Fact]
    public async Task ManagementRefresh_IgnoresEligibilityResultThatDoesNotHonorCancellation()
    {
        var eligibility = new NonCancelableRefreshWriteElevationEligibility();
        using AgentPolicyInspectorViewModel viewModel = BuildManagementViewModel(
            PolicyManagementState.Active,
            eligibility,
            BuildFullResponse().Policy);

        Task first = viewModel.LoadManagementAsync();
        await eligibility.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await viewModel.RefreshManagementCommand.ExecuteAsync(null);
        Assert.False(viewModel.CanEdit);

        eligibility.CompleteFirst();
        await first;

        Assert.False(viewModel.CanEdit);
        Assert.Contains("not administrator-protected", viewModel.ManagementReadOnlyReasonText);
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

    private sealed class StubManagementService(BrokerPolicyManagementResult result)
        : IBrokerPolicyManagementService
    {
        public Task<BrokerPolicyManagementResult> GetManagementAsync(CancellationToken cancellationToken) =>
            Task.FromResult(result);

        public Task<BrokerPolicyValidationOutcome> ValidateAsync(
            System.Text.Json.JsonElement draft,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private static AgentPolicyInspectorViewModel BuildManagementViewModel(
        PolicyManagementState state,
        IPolicyWriteElevationEligibility eligibility,
        PolicyDocument? policy) =>
        new(
            new StubInspector(new(BrokerPolicyInspectionStatus.Unsupported)),
            new StubManagementService(new(
                BrokerPolicyManagementStatus.Retrieved,
                new PolicyManagementSnapshot
                {
                    State = state,
                    StoreToken = "token",
                    Policy = policy,
                    WriteCapability = PolicyWriteCapability.Writable,
                })),
            eligibility,
            (_, _) => { });

    private sealed class StubWriteElevationEligibility(
        PolicyWriteElevationEligibilityStatus status)
        : IPolicyWriteElevationEligibility
    {
        public Task<PolicyWriteElevationEligibility> EvaluateAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(new PolicyWriteElevationEligibility(status));
    }

    private sealed class CountingWriteElevationEligibility : IPolicyWriteElevationEligibility
    {
        public int Invocations { get; private set; }

        public Task<PolicyWriteElevationEligibility> EvaluateAsync(
            CancellationToken cancellationToken)
        {
            Invocations++;
            return Task.FromResult(PolicyWriteElevationEligibility.Eligible);
        }
    }

    private sealed class CancelAwareRefreshWriteElevationEligibility
        : IPolicyWriteElevationEligibility
    {
        private int _invocations;

        public TaskCompletionSource FirstStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool FirstCanceled { get; private set; }

        public async Task<PolicyWriteElevationEligibility> EvaluateAsync(
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _invocations) == 1)
            {
                FirstStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    FirstCanceled = true;
                    throw;
                }
            }

            return PolicyWriteElevationEligibility.Eligible;
        }
    }

    private sealed class NonCancelableRefreshWriteElevationEligibility
        : IPolicyWriteElevationEligibility
    {
        private readonly TaskCompletionSource _firstCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _invocations;

        public TaskCompletionSource FirstStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<PolicyWriteElevationEligibility> EvaluateAsync(
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _invocations) == 1)
            {
                FirstStarted.TrySetResult();
                await _firstCompletion.Task;
                return PolicyWriteElevationEligibility.Eligible;
            }

            return new(
                PolicyWriteElevationEligibilityStatus.ProtectedInstallRequired);
        }

        public void CompleteFirst() => _firstCompletion.TrySetResult();
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
