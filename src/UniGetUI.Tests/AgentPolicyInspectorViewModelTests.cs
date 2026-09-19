using Avalonia.Automation;
using Devolutions.Now.Policy.Api;
using Devolutions.Now.Policy.Model;
using UniGetUI.Avalonia.ViewModels;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages.PolicyEditor;
using UniGetUI.PackageEngine.AgentBroker;
using UniGetUI.PackageEngine.AgentBroker.PolicyManagement;
using UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation;
using ApiTransport = Devolutions.Now.Policy.Api.Transport;
using PolicyDecision = Devolutions.Now.Policy.Model.Decision;
using PolicyManagerName = Devolutions.Now.Policy.Model.ManagerName;
using PolicyOperation = Devolutions.Now.Policy.Model.Operation;

namespace UniGetUI.Tests;

public class AgentPolicyInspectorViewModelTests
{
    [Fact]
    public async Task LoadPageAsync_PresentsActivePolicyInDocumentOrder()
    {
        PolicyResponse response = BuildFullResponse();
        string json = PolicySerializer.Serialize(response.Policy);
        (string? Message, AutomationLiveSetting LiveSetting)? announcement = null;
        using var viewModel = new AgentPolicyInspectorViewModel(
            new StubManagementService(ActiveManagement(response)),
            new StubWriteElevationEligibility(PolicyWriteElevationEligibilityStatus.Eligible),
            (message, liveSetting) => announcement = (message, liveSetting));

        await viewModel.LoadPageAsync();

        Assert.True(viewModel.HasPolicy);
        Assert.False(viewModel.HasNoRules);
        Assert.Equal(json, viewModel.RawJson);
        Assert.Equal(["first-rule", "second-rule"], viewModel.Rules.Select(rule => rule.Id));
        Assert.Equal(18, viewModel.Rules[0].MatchRows.Count);
        Assert.Equal(13, viewModel.Rules[0].ConstraintRows.Count);
        Assert.True(viewModel.Rules[0].HasConstraints);
        Assert.False(viewModel.Rules[1].HasConstraints);
        Assert.Empty(viewModel.Rules[1].ConstraintRows);
        Assert.Contains(
            viewModel.Rules[0].MatchRows,
            row => row.Label == "Exact package identifiers" && row.Value == "Contoso.App");
        Assert.Contains(
            viewModel.Rules[0].MatchRows,
            row => row.Label == "Exact versions" && row.Value == "1.2.3");
        Assert.Contains(
            viewModel.Rules[0].MatchRows,
            row => row.Label == "Package identifier patterns" && row.Value == "Any");
        Assert.Contains(
            viewModel.Rules[0].MatchRows,
            row => row.Label == "Version range" && row.Value == "Any");
        Assert.Contains(viewModel.MetadataRows, row => row.Label == "Server version" && row.Value == "2026.8-tests");
        Assert.Contains(
            viewModel.MetadataRows,
            row => row.Label == "Policy format version" && row.Value == "1.2.3");
        Assert.Contains(viewModel.EnforcementRows, row => row.Label == "Default decision" && row.Value == "Deny");
        Assert.All(viewModel.MetadataRows, row => Assert.False(string.IsNullOrWhiteSpace(row.HelpText)));
        Assert.All(viewModel.EnforcementRows, row => Assert.False(string.IsNullOrWhiteSpace(row.HelpText)));
        Assert.All(viewModel.Rules[0].MatchRows, row => Assert.False(string.IsNullOrWhiteSpace(row.HelpText)));
        Assert.All(viewModel.Rules[0].ConstraintRows, row => Assert.False(string.IsNullOrWhiteSpace(row.HelpText)));
        Assert.Equal(AutomationLiveSetting.Polite, announcement?.LiveSetting);
        Assert.Contains("Policy management is active", announcement?.Message);
    }

    [Fact]
    public async Task LoadPageAsync_PreservesWhitespaceOnlyPolicyValues()
    {
        PolicyResponse response = BuildFullResponse();
        response.Policy.Metadata.Publisher = " ";
        response.Policy.Metadata.Description = " ";
        response.Policy.Rules[0].Match.SourceNames = [" "];
        response.Policy.Rules[0].Constraints!.AllowedCustomParameters = [" "];
        string json = PolicySerializer.Serialize(response.Policy);
        using var viewModel = new AgentPolicyInspectorViewModel(
            new StubManagementService(ActiveManagement(response)),
            new StubWriteElevationEligibility(PolicyWriteElevationEligibilityStatus.Eligible),
            (_, _) => { });
        string? copied = null;
        viewModel.CopyTextRequested += (_, request) => copied = request.Text;

        await viewModel.LoadPageAsync();

        PolicyDetailRow publisher = viewModel.MetadataRows.Single(row => row.Label == "Publisher");
        PolicyDetailRow description = viewModel.MetadataRows.Single(row => row.Label == "Description");
        PolicyDetailRow sources = viewModel.Rules[0].MatchRows.Single(row => row.Label == "Source names");
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
        Assert.Contains("\"PolicyFormatVersion\": \"1.2.3\"", viewModel.RawJson);
        Assert.Contains("\"Publisher\": \" \"", viewModel.RawJson);
        Assert.Contains("\"Description\": \" \"", viewModel.RawJson);

        viewModel.CopyRawJsonCommand.Execute(null);

        Assert.Equal(json, copied);
        Assert.Contains("\"PolicyFormatVersion\": \"1.2.3\"", copied);
    }

    [Fact]
    public async Task LoadAsync_PresentsPatternAndRangeConditionsSeparately()
    {
        PolicyResponse response = BuildFullResponse();
        response.Policy.Rules[0].Match.PackageIdentifiers = PatternPackageIdentifiers("Contoso.*");
        response.Policy.Rules[0].Match.Version = RangeVersions("1.0.0", "2.0.0");
        using var viewModel = new AgentPolicyInspectorViewModel(
            new StubManagementService(ActiveManagement(response)),
            new StubWriteElevationEligibility(PolicyWriteElevationEligibilityStatus.Eligible),
            (_, _) => { });

        await viewModel.LoadPageAsync();

        Assert.Contains(
            viewModel.Rules[0].MatchRows,
            row => row.Label == "Exact package identifiers" && row.Value == "Any");
        Assert.Contains(
            viewModel.Rules[0].MatchRows,
            row => row.Label == "Package identifier patterns" && row.Value == "Contoso.*");
        Assert.Contains(
            viewModel.Rules[0].MatchRows,
            row => row.Label == "Exact versions" && row.Value == "Any");
        Assert.Contains(
            viewModel.Rules[0].MatchRows,
            row => row.Label == "Version range"
                && row.Value == "1.0.0 to 2.0.0; include prerelease: No");
    }

    [Fact]
    public async Task ReportCopyFailure_PreservesPolicyAndAnnouncesError()
    {
        PolicyResponse response = BuildFullResponse();
        string json = PolicySerializer.Serialize(response.Policy);
        (string? Message, AutomationLiveSetting LiveSetting)? announcement = null;
        using var viewModel = new AgentPolicyInspectorViewModel(
            new StubManagementService(ActiveManagement(response)),
            new StubWriteElevationEligibility(PolicyWriteElevationEligibilityStatus.Eligible),
            (message, liveSetting) => announcement = (message, liveSetting));
        await viewModel.LoadPageAsync();
        PolicyCopyRequest? copyRequest = null;
        viewModel.CopyTextRequested += (_, request) => copyRequest = request;
        viewModel.CopyRawJsonCommand.Execute(null);

        viewModel.ReportCopyFailure(Assert.IsType<PolicyCopyRequest>(copyRequest).PageGeneration);

        Assert.True(viewModel.HasPolicy);
        Assert.Equal(json, viewModel.RawJson);
        Assert.Equal("Could not copy policy JSON", viewModel.ManagementStatus.Title);
        Assert.Equal(InfoBarSeverity.Error, viewModel.ManagementStatus.Severity);
        Assert.Equal(AutomationLiveSetting.Assertive, announcement?.LiveSetting);
        Assert.Contains("Could not copy policy JSON", announcement?.Message);
    }

    [Fact]
    public async Task ReportCopyFailure_IgnoresRequestFromPreviousRefreshGeneration()
    {
        var announcements = new List<(string? Message, AutomationLiveSetting LiveSetting)>();
        var management = new QueuedManagementService(
        [
            Task.FromResult(ActiveManagement(BuildFullResponse())),
            Task.FromResult(new BrokerPolicyManagementResult(
                BrokerPolicyManagementStatus.AgentUnavailable)),
        ]);
        using var viewModel = new AgentPolicyInspectorViewModel(
            management,
            new StubWriteElevationEligibility(PolicyWriteElevationEligibilityStatus.Eligible),
            (message, liveSetting) => announcements.Add((message, liveSetting)));
        PolicyCopyRequest? copyRequest = null;
        viewModel.CopyTextRequested += (_, request) => copyRequest = request;

        await viewModel.LoadPageAsync();
        viewModel.CopyRawJsonCommand.Execute(null);
        PolicyCopyRequest staleRequest = Assert.IsType<PolicyCopyRequest>(copyRequest);
        await viewModel.LoadPageAsync();
        int announcementCount = announcements.Count;

        viewModel.ReportCopyFailure(staleRequest.PageGeneration);

        Assert.Equal("Devolutions Agent is unavailable", viewModel.ManagementStatus.Title);
        Assert.Equal(announcementCount, announcements.Count);
    }

    [Fact]
    public async Task LoadPageAsync_PresentsEmptyOptionalPolicy()
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
                },
            },
        };
        using var viewModel = new AgentPolicyInspectorViewModel(
            new StubManagementService(ActiveManagement(response)),
            new StubWriteElevationEligibility(PolicyWriteElevationEligibilityStatus.Eligible),
            (_, _) => { });

        await viewModel.LoadPageAsync();

        Assert.True(viewModel.HasPolicy);
        Assert.True(viewModel.HasNoRules);
        Assert.Empty(viewModel.Rules);
        Assert.Contains(viewModel.MetadataRows, row => row.Label == "Valid until" && row.Value == "Not set");
    }

    [Theory]
    [InlineData(
        BrokerPolicyManagementStatus.AgentUnavailable,
        "Devolutions Agent is unavailable",
        AutomationLiveSetting.Assertive)]
    [InlineData(
        BrokerPolicyManagementStatus.InvalidResponse,
        "The policy management response is invalid",
        AutomationLiveSetting.Assertive)]
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
            new StubManagementService(new(status)),
            (message, liveSetting) => announcement = (message, liveSetting));

        await viewModel.LoadManagementAsync();

        Assert.Equal(expectedTitle, viewModel.ManagementStatus.Title);
        Assert.Equal(expectedLiveSetting, announcement?.LiveSetting);
        Assert.Contains(expectedTitle, announcement?.Message);
    }

    [Fact]
    public async Task UnavailableCopy_DoesNotInferAResponseOrAuthenticationDenial()
    {
        const string expectedMessage =
            "Communication with the package broker could not be completed. Verify that Devolutions Agent is installed and running. If the problem persists, check the Agent logs, then refresh.";
        using var viewModel = new AgentPolicyInspectorViewModel(
            new StubManagementService(new(BrokerPolicyManagementStatus.AgentUnavailable)),
            new StubWriteElevationEligibility(PolicyWriteElevationEligibilityStatus.Eligible),
            (_, _) => { });

        await viewModel.LoadPageAsync();

        Assert.Equal(expectedMessage, viewModel.ManagementStatus.Message);
        Assert.False(viewModel.HasPolicy);
    }

    [Theory]
    [InlineData(PolicyManagementState.Active)]
    [InlineData(PolicyManagementState.Missing)]
    [InlineData(PolicyManagementState.Invalid)]
    public async Task PageRefresh_UsesExactlyOneManagementRequestAndNoInspection(
        PolicyManagementState state)
    {
        var management = new QueuedManagementService(
            [Task.FromResult(ManagementSnapshot(state))]);
        using AgentPolicyInspectorViewModel viewModel = CreatePageViewModel(
            management,
            PolicyWriteElevationEligibilityStatus.Eligible);

        await viewModel.RefreshPageCommand.ExecuteAsync(null);

        Assert.Equal(1, management.Invocations);
        Assert.Equal(state == PolicyManagementState.Active, viewModel.HasActivePolicyDetails);
        Assert.Equal(state == PolicyManagementState.Active, viewModel.HasPolicy);
        Assert.Equal(state.ToString(), viewModel.ManagementStateText);
    }

    [Theory]
    [InlineData(BrokerPolicyManagementStatus.AgentUnavailable, "Devolutions Agent is unavailable")]
    [InlineData(BrokerPolicyManagementStatus.AccessDenied, "Access to policy management was denied")]
    [InlineData(BrokerPolicyManagementStatus.Unsupported, "Policy management is unsupported")]
    [InlineData(BrokerPolicyManagementStatus.InvalidResponse, "The policy management response is invalid")]
    [InlineData(BrokerPolicyManagementStatus.UnsafePolicyPath, "The configured policy path is unsafe")]
    [InlineData(BrokerPolicyManagementStatus.PolicyUnavailable, "The policy management state is unavailable")]
    public async Task PageRefresh_ManagementFailureDoesNotCallInspection(
        BrokerPolicyManagementStatus status,
        string expectedTitle)
    {
        var management = new QueuedManagementService(
            [Task.FromResult(new BrokerPolicyManagementResult(status))]);
        var announcements = new List<(string? Message, AutomationLiveSetting LiveSetting)>();
        using AgentPolicyInspectorViewModel viewModel = CreatePageViewModel(
            management,
            PolicyWriteElevationEligibilityStatus.Eligible,
            (message, liveSetting) => announcements.Add((message, liveSetting)));

        await viewModel.RefreshPageCommand.ExecuteAsync(null);

        Assert.Equal(1, management.Invocations);
        Assert.Equal(expectedTitle, viewModel.ManagementStatus.Title);
        Assert.False(viewModel.HasActivePolicyDetails);
        Assert.False(viewModel.HasPolicy);
        Assert.Single(announcements);
    }

    [Fact]
    public async Task PageRefresh_ActiveSnapshotRendersCanonicalPolicyInDocumentOrder()
    {
        BrokerPolicyManagementResult active = ManagementSnapshot(PolicyManagementState.Active);
        using AgentPolicyInspectorViewModel viewModel = CreatePageViewModel(
            new QueuedManagementService([Task.FromResult(active)]),
            PolicyWriteElevationEligibilityStatus.Eligible);

        await viewModel.RefreshPageCommand.ExecuteAsync(null);

        string expected = PolicySerializer.Serialize(active.Snapshot!.Policy!);
        Assert.Equal(expected, viewModel.RawJson);
        Assert.Equal(["first-rule", "second-rule"], viewModel.Rules.Select(rule => rule.Id));
        Assert.Equal("contoso.full", viewModel.MetadataRows.Single(row => row.Label == "Policy ID").Value);
        Assert.Contains(
            viewModel.MetadataRows,
            row => row.Label == "Server version" && row.Value == "2026.9-tests");
    }

    [Fact]
    public async Task PageRefresh_IsNonReentrantAndCancelsStaleGeneration()
    {
        var stale = new TaskCompletionSource<BrokerPolicyManagementResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var newest = new TaskCompletionSource<BrokerPolicyManagementResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var management = new QueuedManagementService([stale.Task, newest.Task]);
        using AgentPolicyInspectorViewModel viewModel = CreatePageViewModel(
            management,
            PolicyWriteElevationEligibilityStatus.Eligible);

        Task first = viewModel.LoadPageAsync();
        Assert.True(viewModel.IsPageRefreshActive);
        Assert.False(viewModel.RefreshPageCommand.CanExecute(null));
        viewModel.RefreshPageCommand.Execute(null);
        Assert.Equal(1, management.Invocations);
        Task second = viewModel.LoadPageAsync();
        newest.SetResult(ManagementSnapshot(PolicyManagementState.Active));
        await second;
        stale.SetResult(ManagementSnapshot(PolicyManagementState.Missing));
        await first;

        Assert.Equal(2, management.Invocations);
        Assert.Equal("Active", viewModel.ManagementStateText);
        Assert.True(viewModel.HasActivePolicyDetails);
        Assert.True(viewModel.HasPolicy);
    }

    [Fact]
    public async Task PageRefresh_AnnouncesSupportedManagementStateOnce()
    {
        var announcements = new List<(string? Message, AutomationLiveSetting LiveSetting)>();
        using AgentPolicyInspectorViewModel viewModel = CreatePageViewModel(
            new QueuedManagementService(
                [Task.FromResult(ManagementSnapshot(PolicyManagementState.Active))]),
            PolicyWriteElevationEligibilityStatus.Eligible,
            (message, liveSetting) => announcements.Add((message, liveSetting)));

        await viewModel.RefreshPageCommand.ExecuteAsync(null);

        (string? message, AutomationLiveSetting liveSetting) = Assert.Single(announcements);
        Assert.Contains("Policy management is active", message);
        Assert.Equal(AutomationLiveSetting.Polite, liveSetting);
    }

    [Fact]
    public async Task PageRefresh_DisposeCancelsManagementRequest()
    {
        var management = new CancelAwareManagementService();
        var viewModel = new AgentPolicyInspectorViewModel(
            management,
            new StubWriteElevationEligibility(PolicyWriteElevationEligibilityStatus.Eligible),
            (_, _) => { });
        Task pending = viewModel.LoadPageAsync();

        viewModel.Dispose();
        await pending;

        Assert.True(management.Canceled);
    }

    [Fact]
    public async Task PageRefresh_TransitionsActiveMissingInvalidAndFailure()
    {
        var management = new QueuedManagementService(
        [
            Task.FromResult(ManagementSnapshot(PolicyManagementState.Active)),
            Task.FromResult(ManagementSnapshot(PolicyManagementState.Missing)),
            Task.FromResult(ManagementSnapshot(PolicyManagementState.Invalid)),
            Task.FromResult(new BrokerPolicyManagementResult(BrokerPolicyManagementStatus.AgentUnavailable)),
        ]);
        using AgentPolicyInspectorViewModel viewModel = CreatePageViewModel(
            management,
            PolicyWriteElevationEligibilityStatus.Eligible);

        await viewModel.RefreshPageCommand.ExecuteAsync(null);
        Assert.True(viewModel.HasActivePolicyDetails);
        await viewModel.RefreshPageCommand.ExecuteAsync(null);
        Assert.False(viewModel.HasActivePolicyDetails);
        Assert.Equal("Missing", viewModel.ManagementStateText);
        await viewModel.RefreshPageCommand.ExecuteAsync(null);
        Assert.False(viewModel.HasActivePolicyDetails);
        Assert.Equal("Invalid", viewModel.ManagementStateText);
        Assert.Equal(
            @"C:\ProgramData\Devolutions\Agent\policy.json",
            viewModel.ManagementConfiguredPath);
        await viewModel.RefreshPageCommand.ExecuteAsync(null);
        Assert.False(viewModel.HasManagementSnapshot);
        Assert.Equal("Devolutions Agent is unavailable", viewModel.ManagementStatus.Title);
    }

    private static BrokerPolicyManagementResult ManagementSnapshot(PolicyManagementState state) =>
        new(BrokerPolicyManagementStatus.Retrieved, new PolicyManagementSnapshot
        {
            State = state,
            ConfiguredPath = @"C:\ProgramData\Devolutions\Agent\policy.json",
            Source = PolicyConfigurationSource.DefaultPath,
            StoreToken = "token",
            WriteCapability = PolicyWriteCapability.Writable,
            Policy = state == PolicyManagementState.Active ? BuildFullResponse().Policy : null,
        }, Server: new ServerContext
        {
            ServerVersion = "2026.9-tests",
            Transport = ApiTransport.HttpNamedPipe,
        });

    private static BrokerPolicyManagementResult ActiveManagement(PolicyResponse response) =>
        new(
            BrokerPolicyManagementStatus.Retrieved,
            new PolicyManagementSnapshot
            {
                State = PolicyManagementState.Active,
                ConfiguredPath = @"C:\ProgramData\Devolutions\Agent\policy.json",
                Source = PolicyConfigurationSource.ConfiguredPath,
                StoreToken = "token",
                WriteCapability = PolicyWriteCapability.Writable,
                Policy = response.Policy,
            },
            Server: response.Server);

    private static AgentPolicyInspectorViewModel CreatePageViewModel(
        IBrokerPolicyManagementService management,
        PolicyWriteElevationEligibilityStatus eligibilityStatus,
        Action<string?, AutomationLiveSetting>? announce = null) =>
        new(
            management,
            new StubWriteElevationEligibility(eligibilityStatus),
            announce ?? ((_, _) => { }));

    private sealed class CancelAwareManagementService : IBrokerPolicyManagementService
    {
        public bool Canceled { get; private set; }

        public async Task<BrokerPolicyManagementResult> GetManagementAsync(
            CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new(BrokerPolicyManagementStatus.AgentUnavailable);
            }
            catch (OperationCanceledException)
            {
                Canceled = true;
                throw;
            }
        }

        public Task<BrokerPolicyValidationOutcome> ValidateAsync(
            System.Text.Json.JsonElement draft,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private static AgentPolicyInspectorViewModel CreateQueuedViewModel(
        Task<BrokerPolicyManagementResult>[] management,
        PolicyWriteElevationEligibilityStatus eligibilityStatus =
            PolicyWriteElevationEligibilityStatus.HelperMissing) =>
        new(
            new QueuedManagementService(management),
            new StubWriteElevationEligibility(eligibilityStatus),
            (_, _) => { });

    private sealed class QueuedManagementService(IEnumerable<Task<BrokerPolicyManagementResult>> results)
        : IBrokerPolicyManagementService
    {
        private readonly Queue<Task<BrokerPolicyManagementResult>> _results = new(results);
        public int Invocations { get; private set; }

        public Task<BrokerPolicyManagementResult> GetManagementAsync(CancellationToken cancellationToken)
        {
            Invocations++;
            return _results.Dequeue();
        }

        public Task<BrokerPolicyValidationOutcome> ValidateAsync(
            System.Text.Json.JsonElement draft, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
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
    [InlineData(PolicyManagementState.Active, true, false, true)]
    [InlineData(PolicyManagementState.Missing, false, true, false)]
    public async Task WritableAgentAndEligibleApp_EnableOnlyStateAppropriateWriteActions(
        PolicyManagementState state,
        bool canEdit,
        bool canCreate,
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
        Assert.Equal(canReplaceIdentity, viewModel.CanReplaceIdentity);
        Assert.Equal("Writable", viewModel.AgentWriteCapabilityText);
        Assert.Equal("Available", viewModel.PolicyChangesFromThisAppText);
        Assert.Equal("Not applicable", viewModel.PolicyChangesReasonText);
        Assert.False(viewModel.HasPolicyChangesReason);
    }

    [Fact]
    public async Task InvalidPolicy_RemainsDiagnosticAndOffersNoWriteAction()
    {
        var eligibility = new CountingWriteElevationEligibility(
            PolicyWriteElevationEligibilityStatus.Eligible);
        using AgentPolicyInspectorViewModel viewModel = BuildManagementViewModel(
            PolicyManagementState.Invalid,
            eligibility,
            policy: null);
        int launchCount = 0;
        viewModel.OpenPolicyEditorRequested += (_, _) => launchCount++;

        await viewModel.LoadManagementAsync();
        viewModel.EditPolicyCommand.Execute(null);
        viewModel.CreatePolicyCommand.Execute(null);
        viewModel.ReplaceIdentityCommand.Execute(null);

        Assert.Equal("Invalid", viewModel.ManagementStateText);
        Assert.Equal("The configured policy file is invalid", viewModel.ManagementStatus.Title);
        Assert.Contains("administrator", viewModel.ManagementStatus.Message);
        Assert.Contains("outside UniGetUI", viewModel.ManagementStatus.Message);
        Assert.Equal("Unavailable", viewModel.PolicyChangesFromThisAppText);
        Assert.Contains("outside this app", viewModel.PolicyChangesReasonText);
        Assert.True(viewModel.HasPolicyChangesReason);
        Assert.False(viewModel.CanEdit);
        Assert.False(viewModel.CanCreate);
        Assert.False(viewModel.CanReplaceIdentity);
        Assert.Equal(0, eligibility.Invocations);
        Assert.Equal(0, launchCount);
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
            new StubManagementService(new(
                BrokerPolicyManagementStatus.Retrieved,
                new PolicyManagementSnapshot
                {
                    State = state,
                    ConfiguredPath = @"C:\ProgramData\Devolutions\PackageBroker\policy.json",
                    StoreToken = "token",
                    Policy = managedPolicy,
                    WriteCapability = PolicyWriteCapability.Writable,
                })),
            new StubWriteElevationEligibility(status),
            (_, _) => { });
        int launchCount = 0;
        viewModel.OpenPolicyEditorRequested += (_, _) => launchCount++;

        await viewModel.LoadManagementAsync();
        viewModel.EditPolicyCommand.Execute(null);
        viewModel.CreatePolicyCommand.Execute(null);
        viewModel.ReplaceIdentityCommand.Execute(null);

        Assert.True(viewModel.HasManagementSnapshot);
        Assert.Equal(state == PolicyManagementState.Active, viewModel.HasPolicy);
        Assert.Equal("Writable", viewModel.AgentWriteCapabilityText);
        Assert.Equal("Unavailable", viewModel.PolicyChangesFromThisAppText);
        Assert.Contains(expectedReason, viewModel.PolicyChangesReasonText);
        Assert.Contains("all users", viewModel.PolicyChangesReasonText);
        Assert.True(viewModel.HasPolicyChangesReason);
        Assert.False(viewModel.CanEdit);
        Assert.False(viewModel.CanCreate);
        Assert.False(viewModel.CanReplaceIdentity);
        Assert.Equal(0, launchCount);
    }

    [Theory]
    [InlineData(
        PolicyReadOnlyReason.ManagementDisabled,
        "Policy management is disabled in Devolutions Agent.")]
    [InlineData(
        PolicyReadOnlyReason.PathNotConfigured,
        "No policy path is configured in Devolutions Agent.")]
    [InlineData(
        PolicyReadOnlyReason.UnsupportedFormat,
        "Devolutions Agent does not support the configured policy format.")]
    [InlineData(
        PolicyReadOnlyReason.UnsafePath,
        "Devolutions Agent considers the configured policy path unsafe.")]
    [InlineData(
        PolicyReadOnlyReason.InsufficientPermissions,
        "Devolutions Agent does not have permission to change the policy file.")]
    [InlineData(
        PolicyReadOnlyReason.UnsupportedFileSystem,
        "Devolutions Agent does not support the policy file system.")]
    public async Task AgentReadOnlyReason_WinsOverUnavailableLocalHelper(
        PolicyReadOnlyReason readOnlyReason,
        string expectedReason)
    {
        var eligibility = new CountingWriteElevationEligibility(
            PolicyWriteElevationEligibilityStatus.HelperMissing);
        PolicyDocument policy = BuildFullResponse().Policy;
        using var viewModel = new AgentPolicyInspectorViewModel(
            new StubManagementService(new(
                BrokerPolicyManagementStatus.Retrieved,
                new PolicyManagementSnapshot
                {
                    State = PolicyManagementState.Active,
                    Policy = policy,
                    WriteCapability = PolicyWriteCapability.ReadOnly,
                    ReadOnlyReason = readOnlyReason,
                })),
            eligibility,
            (_, _) => { });

        await viewModel.LoadManagementAsync();

        Assert.Equal(0, eligibility.Invocations);
        Assert.False(viewModel.CanEdit);
        Assert.True(viewModel.HasManagementSnapshot);
        Assert.Equal("Read-only", viewModel.AgentWriteCapabilityText);
        Assert.Equal("Unavailable", viewModel.PolicyChangesFromThisAppText);
        Assert.Equal(expectedReason, viewModel.PolicyChangesReasonText);
        Assert.DoesNotContain("helper", viewModel.PolicyChangesReasonText, StringComparison.OrdinalIgnoreCase);
        Assert.True(viewModel.HasPolicyChangesReason);
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
        await viewModel.LoadPageAsync();
        await first;

        Assert.True(eligibility.FirstCanceled);
        Assert.True(viewModel.CanEdit);
        Assert.Equal("Writable", viewModel.AgentWriteCapabilityText);
        Assert.Equal("Available", viewModel.PolicyChangesFromThisAppText);
        Assert.Equal("Not applicable", viewModel.PolicyChangesReasonText);
        Assert.False(viewModel.HasPolicyChangesReason);
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
        await viewModel.LoadPageAsync();
        Assert.False(viewModel.CanEdit);

        eligibility.CompleteFirst();
        await first;

        Assert.False(viewModel.CanEdit);
        Assert.Equal("Writable", viewModel.AgentWriteCapabilityText);
        Assert.Equal("Unavailable", viewModel.PolicyChangesFromThisAppText);
        Assert.Contains("not administrator-protected", viewModel.PolicyChangesReasonText);
    }

    [Fact]
    public async Task ManagementFailure_ClearsWritePresentationFromPreviousSnapshot()
    {
        using AgentPolicyInspectorViewModel viewModel = CreateQueuedViewModel(
            [
                Task.FromResult(ManagementSnapshot(PolicyManagementState.Missing)),
                Task.FromResult(new BrokerPolicyManagementResult(
                    BrokerPolicyManagementStatus.AgentUnavailable)),
            ],
            PolicyWriteElevationEligibilityStatus.Eligible);

        await viewModel.LoadManagementAsync();
        Assert.Equal("Writable", viewModel.AgentWriteCapabilityText);
        Assert.Equal("Available", viewModel.PolicyChangesFromThisAppText);
        Assert.False(viewModel.HasActivePolicyDetails);

        await viewModel.LoadManagementAsync();

        Assert.False(viewModel.HasManagementSnapshot);
        Assert.False(viewModel.HasActivePolicyDetails);
        Assert.Equal("Devolutions Agent is unavailable", viewModel.ManagementStatus.Title);
        Assert.Empty(viewModel.AgentWriteCapabilityText);
        Assert.Empty(viewModel.PolicyChangesFromThisAppText);
        Assert.Empty(viewModel.PolicyChangesReasonText);
        Assert.False(viewModel.HasPolicyChangesReason);
        Assert.False(viewModel.CanCreate);
        Assert.False(viewModel.CanEdit);
        Assert.False(viewModel.CanReplaceIdentity);
    }

    [Fact]
    public async Task CopyRawJson_RaisesDisplayedCanonicalJson()
    {
        PolicyResponse response = BuildFullResponse();
        string json = PolicySerializer.Serialize(response.Policy);
        using var viewModel = new AgentPolicyInspectorViewModel(
            new StubManagementService(ActiveManagement(response)),
            new StubWriteElevationEligibility(PolicyWriteElevationEligibilityStatus.Eligible),
            (_, _) => { });
        string? copied = null;
        viewModel.CopyTextRequested += (_, request) => copied = request.Text;
        await viewModel.LoadPageAsync();

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
                PolicyFormatVersion =
                    Devolutions.Now.Policy.Model.PolicyFormatVersion.Parse("1.2.3"),
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
                            Managers = [PolicyManagerName.Winget],
                            SourceNames = ["community"],
                            PackageIdentifiers = ExactPackageIdentifiers("Contoso.App"),
                            Version = ExactVersions("1.2.3"),
                            Interactive = false,
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
                        Match = new PolicyMatch
                        {
                            Operations = [PolicyOperation.Uninstall],
                        },
                    },
                ],
            },
        };

    private static PackageIdentifierCondition ExactPackageIdentifiers(params string[] identifiers)
    {
        var condition = new PackageIdentifierCondition();
        condition.UseExact([.. identifiers]);
        return condition;
    }

    private static VersionCondition ExactVersions(params string[] versions)
    {
        var condition = new VersionCondition();
        condition.UseExact([.. versions]);
        return condition;
    }

    private static PackageIdentifierCondition PatternPackageIdentifiers(params string[] patterns)
    {
        var condition = new PackageIdentifierCondition();
        condition.UsePatterns([.. patterns]);
        return condition;
    }

    private static VersionCondition RangeVersions(string minVersion, string maxVersion)
    {
        var condition = new VersionCondition();
        condition.UseRange(new VersionRange
        {
            MinVersion = minVersion,
            MaxVersion = maxVersion,
        });
        return condition;
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

    private sealed class CountingWriteElevationEligibility(
        PolicyWriteElevationEligibilityStatus status =
            PolicyWriteElevationEligibilityStatus.Eligible)
        : IPolicyWriteElevationEligibility
    {
        public int Invocations { get; private set; }

        public Task<PolicyWriteElevationEligibility> EvaluateAsync(
            CancellationToken cancellationToken)
        {
            Invocations++;
            return Task.FromResult(new PolicyWriteElevationEligibility(status));
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

}
