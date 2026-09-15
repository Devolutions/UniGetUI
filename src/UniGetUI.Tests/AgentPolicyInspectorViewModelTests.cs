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
        Assert.Equal(AutomationLiveSetting.Polite, announcement?.LiveSetting);
        Assert.Contains("Connected to Devolutions Agent", announcement?.Message);
    }

    [Fact]
    public async Task LoadAsync_PreservesWhitespaceOnlyPolicyValues()
    {
        PolicyResponse response = BuildFullResponse();
        response.Policy.Metadata.Publisher = " ";
        response.Policy.Metadata.Description = " ";
        response.Policy.Rules[0].Match.SourceNames = [" "];
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
        string json = PolicySerializer.Serialize(response.Policy);
        using var viewModel = new AgentPolicyInspectorViewModel(
            new StubInspector(new(BrokerPolicyInspectionStatus.Connected, response, json)));

        await viewModel.LoadAsync();

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
            new StubInspector(new(BrokerPolicyInspectionStatus.Connected, response, json)),
            (message, liveSetting) => announcement = (message, liveSetting));
        await viewModel.LoadAsync();

        viewModel.ReportCopyFailure();

        Assert.True(viewModel.HasPolicy);
        Assert.Equal(json, viewModel.RawJson);
        Assert.Equal("Could not copy policy JSON", viewModel.Status.Title);
        Assert.Equal(InfoBarSeverity.Error, viewModel.Status.Severity);
        Assert.Equal(AutomationLiveSetting.Assertive, announcement?.LiveSetting);
        Assert.Contains("Could not copy policy JSON", announcement?.Message);
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
            new StubInspector(new(BrokerPolicyInspectionStatus.Unsupported)),
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
            new StubInspector(new(BrokerPolicyInspectionStatus.AgentUnavailable)),
            new StubManagementService(new(BrokerPolicyManagementStatus.AgentUnavailable)));

        await viewModel.LoadAsync();
        await viewModel.LoadManagementAsync();

        Assert.Equal(expectedMessage, viewModel.Status.Message);
        Assert.Equal(expectedMessage, viewModel.ManagementStatus.Message);
        Assert.False(viewModel.HasPolicy);
    }

    [Theory]
    [InlineData(false, BrokerPolicyInspectionStatus.PolicyUnavailable)]
    [InlineData(true, BrokerPolicyInspectionStatus.PolicyUnavailable)]
    [InlineData(false, BrokerPolicyInspectionStatus.AgentUnavailable)]
    [InlineData(true, BrokerPolicyInspectionStatus.AgentUnavailable)]
    [InlineData(false, BrokerPolicyInspectionStatus.InvalidResponse)]
    [InlineData(true, BrokerPolicyInspectionStatus.InvalidResponse)]
    [InlineData(false, BrokerPolicyInspectionStatus.AccessDenied)]
    [InlineData(true, BrokerPolicyInspectionStatus.AccessDenied)]
    public async Task MissingManagement_ReplacesInspectionFailureInEitherCompletionOrder(
        bool managementFirst,
        BrokerPolicyInspectionStatus failure)
    {
        var inspection = new TaskCompletionSource<BrokerPolicyInspectionResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var management = new TaskCompletionSource<BrokerPolicyManagementResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using AgentPolicyInspectorViewModel viewModel = CreateQueuedViewModel(
            [inspection.Task], [management.Task]);
        Task inspect = viewModel.LoadAsync();
        Task manage = viewModel.LoadManagementAsync();

        if (managementFirst)
        {
            management.SetResult(ManagementSnapshot(PolicyManagementState.Missing));
            await manage;
            AssertMissingPolicy(viewModel);
            inspection.SetResult(new(failure));
            await inspect;
        }
        else
        {
            inspection.SetResult(new(failure));
            await inspect;
            Assert.Equal(InfoBarSeverity.Error, viewModel.Status.Severity);
            management.SetResult(ManagementSnapshot(PolicyManagementState.Missing));
            await manage;
        }

        AssertMissingPolicy(viewModel);
        Assert.Equal("No policy file exists", viewModel.ManagementStatus.Title);
        Assert.False(viewModel.CanCreate);
        Assert.Equal("Writable", viewModel.AgentWriteCapabilityText);
        Assert.Equal("Unavailable", viewModel.PolicyChangesFromThisAppText);
        Assert.Contains("helper is missing", viewModel.PolicyChangesReasonText);
    }

    [Theory]
    [InlineData(BrokerPolicyManagementStatus.AgentUnavailable)]
    [InlineData(BrokerPolicyManagementStatus.Unsupported)]
    [InlineData(BrokerPolicyManagementStatus.Retrieved)]
    public async Task ManagementRefresh_DoesNotKeepMissingStateOrAcceptStaleMissingResults(
        BrokerPolicyManagementStatus nextStatus)
    {
        var stale = new TaskCompletionSource<BrokerPolicyManagementResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var newest = new TaskCompletionSource<BrokerPolicyManagementResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using AgentPolicyInspectorViewModel viewModel = CreateQueuedViewModel(
            [Task.FromResult(new BrokerPolicyInspectionResult(BrokerPolicyInspectionStatus.PolicyUnavailable))],
            [Task.FromResult(ManagementSnapshot(PolicyManagementState.Missing)), stale.Task, newest.Task]);
        await viewModel.LoadAsync();
        await viewModel.LoadManagementAsync();
        AssertMissingPolicy(viewModel);

        Task oldRefresh = viewModel.LoadManagementAsync();
        Assert.Equal("The active policy is unavailable", viewModel.Status.Title);
        Task newRefresh = viewModel.LoadManagementAsync();
        newest.SetResult(nextStatus == BrokerPolicyManagementStatus.Retrieved
            ? ManagementSnapshot(PolicyManagementState.Active)
            : new(nextStatus));
        await newRefresh;
        stale.SetResult(ManagementSnapshot(PolicyManagementState.Missing));
        await oldRefresh;

        Assert.Equal("The active policy is unavailable", viewModel.Status.Title);
        Assert.Equal(InfoBarSeverity.Error, viewModel.Status.Severity);
        Assert.False(viewModel.IsManagementLoading);
        Assert.NotEqual("Missing", viewModel.ManagementStateText);
    }

    [Fact]
    public async Task InspectionRefresh_IgnoresStaleFailureAndRestoresNewestResultWhenManagementIsActive()
    {
        var stale = new TaskCompletionSource<BrokerPolicyInspectionResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        PolicyResponse policy = BuildFullResponse();
        using AgentPolicyInspectorViewModel viewModel = CreateQueuedViewModel(
            [
                stale.Task,
                Task.FromResult(new BrokerPolicyInspectionResult(
                    BrokerPolicyInspectionStatus.Connected, policy, PolicySerializer.Serialize(policy.Policy))),
            ],
            [
                Task.FromResult(ManagementSnapshot(PolicyManagementState.Missing)),
                Task.FromResult(ManagementSnapshot(PolicyManagementState.Active)),
            ]);
        Task oldRefresh = viewModel.LoadAsync();
        await viewModel.LoadManagementAsync();
        await viewModel.LoadAsync();
        AssertMissingPolicy(viewModel);
        await viewModel.LoadManagementAsync();
        stale.SetResult(new(BrokerPolicyInspectionStatus.PolicyUnavailable));
        await oldRefresh;

        Assert.True(viewModel.HasPolicy);
        Assert.Equal("Connected to Devolutions Agent", viewModel.Status.Title);
        Assert.Equal(InfoBarSeverity.Success, viewModel.Status.Severity);
        Assert.False(viewModel.IsLoading);
    }

    [Fact]
    public async Task MissingManagement_ClearsPreviouslyDisplayedPolicy()
    {
        PolicyResponse policy = BuildFullResponse();
        using AgentPolicyInspectorViewModel viewModel = CreateQueuedViewModel(
            [Task.FromResult(new BrokerPolicyInspectionResult(
                BrokerPolicyInspectionStatus.Connected, policy, PolicySerializer.Serialize(policy.Policy)))],
            [Task.FromResult(ManagementSnapshot(PolicyManagementState.Missing))]);
        await viewModel.LoadAsync();
        Assert.True(viewModel.HasPolicy);

        await viewModel.LoadManagementAsync();

        AssertMissingPolicy(viewModel);
    }

    private static void AssertMissingPolicy(AgentPolicyInspectorViewModel viewModel)
    {
        Assert.Equal("No active package policy", viewModel.Status.Title);
        Assert.Equal(
            "Devolutions Agent reports that no policy file exists at the configured path.",
            viewModel.Status.Message);
        Assert.Equal(InfoBarSeverity.Informational, viewModel.Status.Severity);
        Assert.False(viewModel.HasPolicy);
        Assert.False(viewModel.HasNoRules);
        Assert.Empty(viewModel.MetadataRows);
        Assert.Empty(viewModel.EnforcementRows);
        Assert.Empty(viewModel.Rules);
        Assert.Empty(viewModel.RawJson);
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
        });

    private static AgentPolicyInspectorViewModel CreateQueuedViewModel(
        Task<BrokerPolicyInspectionResult>[] inspections,
        Task<BrokerPolicyManagementResult>[] management,
        PolicyWriteElevationEligibilityStatus eligibilityStatus =
            PolicyWriteElevationEligibilityStatus.HelperMissing) =>
        new(
            new QueuedInspector(inspections),
            new QueuedManagementService(management),
            new StubWriteElevationEligibility(eligibilityStatus),
            (_, _) => { });

    private sealed class QueuedInspector(IEnumerable<Task<BrokerPolicyInspectionResult>> results)
        : IBrokerPolicyInspector
    {
        private readonly Queue<Task<BrokerPolicyInspectionResult>> _results = new(results);

        public Task<BrokerPolicyInspectionResult> InspectAsync(CancellationToken cancellationToken) =>
            _results.Dequeue();
    }

    private sealed class QueuedManagementService(IEnumerable<Task<BrokerPolicyManagementResult>> results)
        : IBrokerPolicyManagementService
    {
        private readonly Queue<Task<BrokerPolicyManagementResult>> _results = new(results);

        public Task<BrokerPolicyManagementResult> GetManagementAsync(CancellationToken cancellationToken) =>
            _results.Dequeue();

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
    public async Task WritableAgentAndEligibleApp_EnableOnlyStateAppropriateWriteActions(
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
        Assert.Equal("Writable", viewModel.AgentWriteCapabilityText);
        Assert.Equal("Available", viewModel.PolicyChangesFromThisAppText);
        Assert.Equal("Not applicable", viewModel.PolicyChangesReasonText);
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
        Assert.Equal(state != PolicyManagementState.Missing, viewModel.HasPolicy);
        Assert.Equal("Writable", viewModel.AgentWriteCapabilityText);
        Assert.Equal("Unavailable", viewModel.PolicyChangesFromThisAppText);
        Assert.Contains(expectedReason, viewModel.PolicyChangesReasonText);
        Assert.Contains("all users", viewModel.PolicyChangesReasonText);
        Assert.False(viewModel.CanEdit);
        Assert.False(viewModel.CanCreate);
        Assert.False(viewModel.CanRepair);
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
            new StubInspector(new(BrokerPolicyInspectionStatus.Unsupported)),
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
        Assert.Equal("Writable", viewModel.AgentWriteCapabilityText);
        Assert.Equal("Available", viewModel.PolicyChangesFromThisAppText);
        Assert.Equal("Not applicable", viewModel.PolicyChangesReasonText);
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
        Assert.Equal("Writable", viewModel.AgentWriteCapabilityText);
        Assert.Equal("Unavailable", viewModel.PolicyChangesFromThisAppText);
        Assert.Contains("not administrator-protected", viewModel.PolicyChangesReasonText);
    }

    [Fact]
    public async Task ManagementFailure_ClearsWritePresentationFromPreviousSnapshot()
    {
        using AgentPolicyInspectorViewModel viewModel = CreateQueuedViewModel(
            [Task.FromResult(new BrokerPolicyInspectionResult(BrokerPolicyInspectionStatus.Unsupported))],
            [
                Task.FromResult(ManagementSnapshot(PolicyManagementState.Missing)),
                Task.FromResult(new BrokerPolicyManagementResult(
                    BrokerPolicyManagementStatus.AgentUnavailable)),
            ],
            PolicyWriteElevationEligibilityStatus.Eligible);

        await viewModel.LoadManagementAsync();
        Assert.Equal("Writable", viewModel.AgentWriteCapabilityText);
        Assert.Equal("Available", viewModel.PolicyChangesFromThisAppText);

        await viewModel.LoadManagementAsync();

        Assert.False(viewModel.HasManagementSnapshot);
        Assert.Empty(viewModel.AgentWriteCapabilityText);
        Assert.Empty(viewModel.PolicyChangesFromThisAppText);
        Assert.Empty(viewModel.PolicyChangesReasonText);
        Assert.False(viewModel.CanCreate);
        Assert.False(viewModel.CanEdit);
        Assert.False(viewModel.CanRepair);
        Assert.False(viewModel.CanReplaceIdentity);
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
                    PolicyFormatVersion = source.Policy.PolicyFormatVersion,
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
