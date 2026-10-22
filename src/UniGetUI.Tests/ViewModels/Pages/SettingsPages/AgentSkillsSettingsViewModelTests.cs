using Devolutions.AgentSkills;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;
using UniGetUI.Core.SettingsEngine;
using static UniGetUI.Tests.AgentSkillsSettingsTestFixture;
using CoreSettings = UniGetUI.Core.SettingsEngine.Settings;
using SkillsManager = UniGetUI.PackageEngine.Managers.SkillsManager.AgentSkills;

namespace UniGetUI.Tests;

public sealed class AgentSkillsSettingsViewModelTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TargetRow_ExposesMetadataAndNotifiesSelection(bool detected)
    {
        var row = new SkillTargetViewModel("stable-id", "Agent name", detected, true);
        var changed = new List<string?>();
        row.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        Assert.Equal("stable-id", row.Id);
        Assert.Equal("Agent name", row.Name);
        Assert.Equal(!detected, row.IsNotDetected);
        Assert.Equal(detected ? "" : "Not detected", row.DetectionLabel);
        Assert.True(row.IsSelected);
        row.IsSelected = false;
        row.IsSelected = false;
        Assert.False(row.IsSelected);
        Assert.Equal([nameof(SkillTargetViewModel.IsSelected)], changed);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Constructor_HydratesTargetsWithoutWrites(bool explicitTargets, bool disabled)
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        fixture.Seed(explicitTargets ? ["detected-b", "unavailable-u"] : [], disabled);
        var before = Snapshot();
        var service = fixture.Service(hybrid: true);
        var vm = new AgentSkillsSettingsViewModel(service);
        Assert.Equal(explicitTargets ? 1 : 0, vm.TargetMode);
        Assert.Equal(explicitTargets ? ["detected-b", "unavailable-u"] : new[] { "detected-a", "detected-b" }, Selected(vm));
        Assert.Equal(explicitTargets ? ["detected-a", "detected-b", "unavailable-u"] : new[] { "detected-a", "detected-b" },
            vm.Agents.Select(row => row.Id));
        Assert.Equal(!disabled, vm.NotificationsEnabled);
        Assert.Equal("0.23.8", vm.VersionSummary);
        service.AssertNoHydrationWrites();
        before.AssertUnchanged();
        AssertOtherPreferences();
    }

    [Fact]
    public void ChooseAllTargets_PersistsExplicitFullList()
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        fixture.Seed(["detected-a", "unavailable-u"]);
        var service = fixture.Service(true);
        var vm = new AgentSkillsSettingsViewModel(service);
        vm.TargetMode = 0;
        vm.TargetMode = 1;
        Assert.Equal(["detected-a", "unavailable-u"], Selected(vm));
        Row(vm, "detected-b").IsSelected = true;
        Assert.True(vm.ChooseAgents);
        Assert.Equal(1, vm.TargetMode);
        Assert.Equal(["detected-a", "detected-b", "unavailable-u"], service.TargetAgents);
        Assert.Equal(["detected-a", "detected-b", "unavailable-u"], service.TargetWrites[^1]);
        Assert.False(vm.HasTargetError);
    }

    [Theory]
    [InlineData("unavailable-u", "Unavailable U")]
    [InlineData("unknown-x", "unknown-x")]
    public void UnavailableSavedTarget_SurvivesEditingOtherRow(string id, string name)
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        fixture.Seed([id]);
        var service = fixture.Service(true);
        var vm = new AgentSkillsSettingsViewModel(service);
        var saved = Row(vm, id);
        Assert.True(saved.IsNotDetected);
        Assert.True(saved.IsSelected);
        Assert.Equal(name, saved.Name);
        Assert.Equal("Not detected", saved.DetectionLabel);
        Row(vm, "detected-a").IsSelected = true;
        Assert.True(saved.IsSelected);
        Assert.Equal(["detected-a", id], service.TargetAgents);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DiscoveryFailure_PreservesSavedTargetsOrSafeAutomatic(bool saved)
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        fixture.Seed(saved ? ["unavailable-u", "unknown-x"] : []);
        var service = fixture.Service(true);
        service.DiscoveryFailure = new InvalidOperationException("catalogue unavailable");
        var vm = new AgentSkillsSettingsViewModel(service);
        Assert.Equal("Could not load coding agents: catalogue unavailable", vm.TargetError);
        Assert.True(vm.HasTargetError);
        service.AssertNoHydrationWrites();
        if (saved)
        {
            Assert.Equal(["unavailable-u", "unknown-x"], Selected(vm));
            Assert.All(vm.Agents, row => Assert.True(row.IsNotDetected));
            Row(vm, "unavailable-u").IsSelected = false;
            Assert.True(Row(vm, "unknown-x").IsSelected);
            Assert.Equal(["unknown-x"], service.TargetAgents);
            Row(vm, "unavailable-u").IsSelected = true;
            Row(vm, "unknown-x").IsSelected = false;
            Assert.True(Row(vm, "unavailable-u").IsSelected);
            Assert.Equal(["unavailable-u"], service.TargetAgents);
            Row(vm, "unknown-x").IsSelected = true;
            Assert.Equal(["unavailable-u", "unknown-x"], service.TargetAgents);
        }
        else
        {
            Assert.Empty(vm.Agents);
            Assert.Empty(service.TargetAgents);
            Assert.Equal(0, vm.TargetMode);
            Assert.Equal(["Automatic (recommended)"], vm.TargetModes);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NoAvailableTargets_OffersOnlyAutomatic(bool knownUndetected)
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        var service = fixture.Service();
        service.Catalogue = knownUndetected ? [fixture.Catalogue[2]] : [];
        var vm = new AgentSkillsSettingsViewModel(service);
        Assert.Empty(vm.Agents);
        Assert.True(vm.NoDetectedAgents);
        Assert.False(vm.ChooseAgents);
        Assert.Equal(["Automatic (recommended)"], vm.TargetModes);
        service.AssertNoHydrationWrites();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyExplicitDraft_PreservesLastSaveUntilRecovery(bool recoverThroughAutomatic)
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        fixture.Seed(["detected-a"]);
        var service = fixture.Service(true);
        var vm = new AgentSkillsSettingsViewModel(service);
        Row(vm, "detected-a").IsSelected = false;
        Assert.Empty(Selected(vm));
        Assert.True(vm.HasTargetError);
        Assert.Equal(TargetMessage, vm.TargetError);
        Assert.Equal(["detected-a"], service.TargetAgents);
        Assert.Empty(service.TargetWrites);
        if (recoverThroughAutomatic)
        {
            vm.TargetMode = 0;
            Assert.Empty(service.TargetAgents);
            Assert.False(vm.HasTargetError);
            Assert.Equal(0, vm.TargetMode);
            Assert.Equal(["detected-a", "detected-b"], vm.Agents.Select(row => row.Id));
            vm.TargetMode = 1;
            Assert.Equal(["detected-a"], Selected(vm));
            Assert.Equal(["detected-a"], service.TargetAgents);
        }
        else
        {
            Row(vm, "detected-b").IsSelected = true;
            Assert.Equal(["detected-b"], Selected(vm));
            Assert.Equal(["detected-b"], service.TargetAgents);
        }
        Assert.True(vm.ChooseAgents);
        Assert.False(vm.HasTargetError);
        Assert.All(service.TargetWrites.Skip(recoverThroughAutomatic ? 1 : 0), Assert.NotEmpty);
    }

    [Fact]
    public void TargetEdits_PersistWithoutChangingInstalledTree()
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        fixture.Seed();
        fixture.SeedInstalledTree();
        string[] before = fixture.InstalledTree();
        var service = fixture.Service(true);
        service.TargetAgents = ["detected-b", "detected-a", "detected-b"];
        Assert.Equal(["detected-b", "detected-a"], SkillsManager.TargetAgents);
        var vm = new AgentSkillsSettingsViewModel(service);
        Row(vm, "detected-a").IsSelected = false;
        Assert.Equal(["detected-b"], SkillsManager.TargetAgents);
        Row(vm, "detected-a").IsSelected = true;
        Assert.Equal(["detected-a", "detected-b"], SkillsManager.TargetAgents);
        vm.TargetMode = 0;
        Assert.Empty(SkillsManager.TargetAgents);
        vm.TargetMode = 1;
        Assert.Equal(["detected-a", "detected-b"], SkillsManager.TargetAgents);
        Assert.Equal(before, fixture.InstalledTree());
        Assert.Equal(0, service.StatusReads);
        Assert.Equal(0, service.SourceRefreshes);
        Assert.Equal(0, service.SignOutCalls);
        Assert.Empty(service.Operations); // No install/remove/update callback is part of a target edit.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [InlineData(null)]
    public void NotificationPreference_UsesInverseStorageAndSurvivesReopen(bool? disabled)
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        fixture.Seed(disabled: disabled);
        var service = fixture.Service(true);
        var vm = new AgentSkillsSettingsViewModel(service);
        Assert.Equal(!(disabled ?? false), vm.NotificationsEnabled);
        service.AssertNoHydrationWrites();
        if (disabled.HasValue)
        {
            vm.NotificationsEnabled = disabled.Value;
            Assert.Equal(!disabled.Value, Disabled);
            Assert.Equal([disabled.Value], service.NotificationWrites);
        }
        for (int i = 0; i < 2; i++)
        {
            var reopenedService = fixture.Service(true);
            var reopened = new AgentSkillsSettingsViewModel(reopenedService);
            Assert.Equal(disabled ?? true, reopened.NotificationsEnabled);
            reopenedService.AssertNoHydrationWrites();
        }
        if (!disabled.HasValue)
            Assert.False(CoreSettings.DictionaryContainsKey<string, bool>(CoreSettings.K.DisabledPackageManagerNotifications, "Skills"));
        AssertOtherPreferences();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LatestPolicy_ClearsPinAndPreservesUnrelatedIgnores(bool invalidDraft)
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        fixture.Seed();
        var service = fixture.Service(true);
        var vm = new AgentSkillsSettingsViewModel(service);
        await vm.RefreshAsync();
        if (invalidDraft) vm.VersionText = "latest";
        vm.VersionPolicy = 0;
        Assert.Equal([""], service.VersionWrites);
        Assert.Equal("", service.SavedVersion);
        Assert.Equal("", CoreSettings.GetValue(CoreSettings.K.SkillsNotionCliVersion));
        Assert.False(vm.SpecificVersion);
        Assert.False(vm.HasVersionError);
        Assert.Equal("Latest available", vm.VersionSummary);
        Assert.True(vm.CanInstall);
        AssertOtherPreferences();
    }

    [Theory]
    [InlineData("0.23.9", "0.23.9")]
    [InlineData(" 0.23.9 ", "0.23.9")]
    [InlineData(" 0.23.9-beta.1 ", "0.23.9-beta.1")]
    public async Task ValidVersionEdit_TrimsPersistsAndEnablesInstall(string draft, string expected)
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        fixture.Seed(pin: "");
        var service = fixture.Service(true);
        var vm = new AgentSkillsSettingsViewModel(service);
        await vm.RefreshAsync();
        vm.VersionPolicy = 1;
        Assert.True(vm.SpecificVersion);
        Assert.True(vm.HasVersionError);
        Assert.Empty(service.VersionWrites);
        vm.VersionText = draft;
        Assert.Equal([expected], service.VersionWrites);
        Assert.Equal(expected, service.SavedVersion);
        Assert.Equal(expected, vm.VersionSummary);
        Assert.False(vm.HasVersionError);
        Assert.True(vm.CanInstall);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1")]
    [InlineData("latest")]
    [InlineData("!")]
    public async Task InvalidVersionDraft_PreservesPinAndIgnores(string draft)
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        fixture.Seed();
        var before = Snapshot();
        var service = fixture.Service(true);
        var vm = new AgentSkillsSettingsViewModel(service);
        await vm.RefreshAsync();
        vm.VersionText = draft;
        AssertInvalidVersion(vm);
        Assert.Equal(draft, vm.VersionText);
        Assert.Empty(service.VersionWrites);
        before.AssertUnchanged();
        Assert.False(vm.CanInstall);
    }

    [Fact]
    public async Task InvalidSavedVersion_IsReadOnlyUntilCorrected()
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        fixture.Seed(pin: "invalid saved pin");
        var before = Snapshot();
        var service = fixture.Service(true);
        var vm = new AgentSkillsSettingsViewModel(service);
        Assert.Equal("invalid saved pin", vm.VersionText);
        AssertInvalidVersion(vm);
        await vm.RefreshAsync();
        await vm.RefreshAsync();
        Assert.Equal(2, service.StatusReads);
        service.AssertNoHydrationWrites();
        before.AssertUnchanged();
        vm.VersionText = "0.24.0";
        Assert.Equal(["0.24.0"], service.VersionWrites);
        Assert.Equal("0.24.0", service.SavedVersion);
        Assert.False(vm.HasVersionError);
        Assert.True(vm.CanInstall);
    }

    [Fact]
    public async Task RefreshWithValidPin_DoesNotRewriteSettings()
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        fixture.Seed();
        var before = Snapshot();
        var service = fixture.Service(true);
        var vm = new AgentSkillsSettingsViewModel(service);
        await vm.RefreshAsync();
        await vm.RefreshAsync();
        Assert.Equal(2, service.StatusReads);
        Assert.Equal("0.23.8", vm.VersionSummary);
        Assert.True(vm.CanInstall);
        service.AssertNoHydrationWrites();
        before.AssertUnchanged();
    }

    [Fact]
    public async Task ValidCorrection_ClearsVersionErrorAndRestoresEligibility()
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        var service = fixture.Service();
        var vm = new AgentSkillsSettingsViewModel(service);
        await vm.RefreshAsync();
        vm.VersionPolicy = 1;
        vm.VersionText = "!";
        AssertInvalidVersion(vm);
        Assert.False(vm.CanInstall);
        vm.VersionText = "0.23.9";
        Assert.False(vm.HasVersionError);
        Assert.Equal("", vm.VersionError);
        Assert.Equal("0.23.9", vm.VersionSummary);
        Assert.Equal(["0.23.9"], service.VersionWrites);
        Assert.True(vm.CanInstall);
    }

    [Fact]
    public async Task RejectedVersionSave_BlocksInstallUntilAcceptedCorrection()
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        var service = fixture.Service();
        service.StoredVersion = "0.23.8";
        service.RejectVersion = true;
        var vm = new AgentSkillsSettingsViewModel(service);
        await vm.RefreshAsync();
        vm.VersionText = "0.23.9";
        AssertInvalidVersion(vm);
        Assert.False(vm.CanInstall);
        Assert.Equal("0.23.8", service.SavedVersion);
        service.RejectVersion = false;
        vm.VersionText = "0.24.0";
        Assert.Equal(["0.23.9", "0.24.0"], service.VersionWrites);
        Assert.Equal("0.24.0", service.SavedVersion);
        Assert.False(vm.HasVersionError);
        Assert.True(vm.CanInstall);
    }

    [Fact]
    public async Task InvalidVersionInstallRequest_DoesNotInvokeActionOrRefresh()
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        var service = fixture.Service();
        var vm = new AgentSkillsSettingsViewModel(service);
        await vm.RefreshAsync();
        vm.VersionPolicy = 1;
        vm.IsAdvancedExpanded = false;
        int callbacks = 0;
        await vm.RunNotionActionAsync(() => { callbacks++; return Task.CompletedTask; }, requiresValidVersion: true);
        Assert.Equal(0, callbacks);
        Assert.Equal(0, service.SourceRefreshes);
        Assert.Equal(1, service.StatusReads);
        Assert.True(vm.IsAdvancedExpanded);
        AssertInvalidVersion(vm);
    }

    [Fact]
    public async Task PendingStatus_DisablesActionsWithoutStartingDuplicates()
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        var service = fixture.Service();
        var vm = new AgentSkillsSettingsViewModel(service);
        int callbacks = 0;
        Task Action() { callbacks++; return Task.CompletedTask; }
        vm.Deactivate();
        await vm.RunNotionActionAsync(Action, true);
        await vm.RunNotionActionAsync(Action);
        await vm.SignOutAsync();
        await vm.RefreshAsync();
        Assert.False(vm.CanAct);
        Assert.False(vm.CanInstall);
        Assert.Equal(0, service.StatusReads);
        vm.Activate();
        var gate = Gate<NotionStatus>();
        service.StatusQueue.Enqueue(gate.Task);
        Task pending = vm.RefreshAsync();
        try
        {
            Assert.True(vm.IsBusy);
            Assert.Equal("Checking the Notion CLI…", vm.NotionSummary);
            Assert.False(vm.CanAct);
            Assert.False(vm.CanInstall);
            await vm.RunNotionActionAsync(Action, true);
            await vm.RunNotionActionAsync(Action);
            await vm.SignOutAsync();
            Assert.Equal(0, callbacks);
            Assert.Equal(0, service.SignOutCalls);
            Assert.Equal(0, service.SourceRefreshes);
            Assert.Equal(1, service.StatusReads);
        }
        finally { gate.TrySetResult(Status()); await pending; }
        Assert.False(vm.IsBusy);
        Assert.True(vm.CanAct);
        Assert.Equal(1, service.StatusReads);
    }

    public static IEnumerable<object?[]> StatusCases()
    {
        foreach (bool sources in new[] { false, true })
        {
            yield return [sources, NotionCliState.NotInstalled, null, null, "Not installed. Only Notion sources need this integration.", ""];
            yield return [sources, NotionCliState.SignedOut, null, null, "Notion CLI 0.23.9 installed. Sign in to use Notion sources.", ""];
            yield return [sources, NotionCliState.SignedIn, "Workspace A", null, "Connected to Workspace A, CLI 0.23.9", ""];
            yield return [sources, NotionCliState.Unknown, null, "authentication unavailable", "Could not check the Notion connection.", "authentication unavailable"];
            yield return [sources, NotionCliState.Unknown, null, null, "Could not check the Notion connection.", "Retry the connection check or view Agent Skills logs."];
            yield return [sources, NotionCliState.Unknown, null, "", "Could not check the Notion connection.", "Retry the connection check or view Agent Skills logs."];
            yield return [sources, NotionCliState.Unknown, null, "   ", "Could not check the Notion connection.", "Retry the connection check or view Agent Skills logs."];
        }
        yield return [false, NotionCliState.SignedIn, null, null, "Connected to Notion, CLI 0.23.9", ""];
        yield return [false, NotionCliState.SignedIn, "", null, "Connected to Notion, CLI 0.23.9", ""];
    }

    [Theory]
    [MemberData(nameof(StatusCases))]
    public async Task StatusResult_MapsSummaryActionsAndExpansion(bool sources, NotionCliState state,
        string? workspace, string? message, string summary, string error)
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        var service = fixture.Service();
        service.HasNotionSources = sources;
        service.DefaultStatus = Status(state, workspace, message);
        var vm = new AgentSkillsSettingsViewModel(service);
        Assert.False(vm.IsNotionExpanded);
        await vm.RefreshAsync();
        Assert.Equal(state, vm.NotionState);
        Assert.Equal(summary, vm.NotionSummary);
        Assert.Equal(error, vm.NotionError);
        Assert.Equal(error.Length > 0, vm.HasNotionError);
        Assert.Equal(state == NotionCliState.NotInstalled, vm.ShowInstall);
        Assert.Equal(state == NotionCliState.SignedOut, vm.ShowSignIn);
        Assert.Equal(state == NotionCliState.SignedIn, vm.ShowSignOut);
        Assert.Equal(sources && state != NotionCliState.SignedIn, vm.IsNotionExpanded);
        Assert.False(vm.IsBusy);
        Assert.True(vm.CanAct);
        Assert.Equal(1, service.StatusReads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StatusException_ShowsUnknownAndAllowsRetry(bool sources)
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        var service = fixture.Service();
        service.HasNotionSources = sources;
        service.StatusQueue.Enqueue(Task.FromException<NotionStatus>(new InvalidOperationException("status failed")));
        var vm = new AgentSkillsSettingsViewModel(service);
        await vm.RefreshAsync();
        Assert.Equal(NotionCliState.Unknown, vm.NotionState);
        Assert.Equal("Could not check the Notion connection.", vm.NotionSummary);
        Assert.Equal("status failed", vm.NotionError);
        Assert.True(vm.HasNotionError);
        Assert.False(vm.ShowSignIn);
        Assert.Equal(sources, vm.IsNotionExpanded);
        Assert.True(vm.CanAct);
        var gate = Gate<NotionStatus>();
        service.StatusQueue.Enqueue(gate.Task);
        Task retry = vm.RefreshAsync();
        try
        {
            Assert.Equal("", vm.NotionError);
            Assert.True(vm.IsBusy);
        }
        finally { gate.TrySetResult(Status(NotionCliState.SignedIn, "Recovered")); await retry; }
        Assert.False(vm.IsBusy);
        Assert.False(vm.HasNotionError);
        Assert.True(vm.ShowSignOut);
        Assert.Equal("Connected to Recovered, CLI 0.23.9", vm.NotionSummary);
        Assert.Equal(2, service.StatusReads);
    }

    [Fact]
    public async Task UserCollapse_AfterAutoExpansionSurvivesRefresh()
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        var service = fixture.Service();
        service.HasNotionSources = true;
        var vm = new AgentSkillsSettingsViewModel(service);
        await vm.RefreshAsync();
        Assert.True(vm.IsNotionExpanded);
        vm.IsNotionExpanded = false;
        service.DefaultStatus = Status(NotionCliState.Unknown, message: "retry");
        await vm.RefreshAsync();
        Assert.False(vm.IsNotionExpanded);
        Assert.Equal("retry", vm.NotionError);
        Assert.Equal(2, service.StatusReads);
    }

    internal static Task RunAction(AgentSkillsSettingsViewModel vm, int kind, Func<Task> action) =>
        kind == 2 ? vm.SignOutAsync() : vm.RunNotionActionAsync(action, requiresValidVersion: kind == 0);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task SuccessfulAction_RefreshesSourcesThenStatus(int kind)
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        var service = fixture.Service();
        var vm = new AgentSkillsSettingsViewModel(service);
        await vm.RefreshAsync();
        service.Operations.Clear();
        service.DefaultStatus = Status(kind == 2 ? NotionCliState.SignedOut : NotionCliState.SignedIn, "Action workspace");
        int calls = 0;
        Task Action() { calls++; service.Operations.Add("callback"); return Task.CompletedTask; }
        await RunAction(vm, kind, Action);
        Assert.Equal(kind == 2 ? 0 : 1, calls);
        Assert.Equal(kind == 2 ? 1 : 0, service.SignOutCalls);
        Assert.Equal([kind == 2 ? "sign-out" : "callback", "sources", "status"], service.Operations);
        Assert.Equal(1, service.SourceRefreshes);
        Assert.Equal(2, service.StatusReads);
        Assert.Equal(kind == 2, vm.ShowSignIn);
        Assert.Equal(kind != 2, vm.ShowSignOut);
        Assert.False(vm.IsBusy);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task FailedAction_ShowsErrorAndRecoversOnRetry(int kind)
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        var service = fixture.Service();
        service.DefaultStatus = Status(NotionCliState.SignedIn, "Original");
        var vm = new AgentSkillsSettingsViewModel(service);
        await vm.RefreshAsync();
        bool fail = true;
        int calls = 0;
        Task Action()
        {
            calls++;
            return fail ? Task.FromException(new InvalidOperationException("action failed")) : Task.CompletedTask;
        }
        service.SignOut = Action;
        await RunAction(vm, kind, Action);
        Assert.Equal("Could not complete the Notion action: action failed", vm.NotionError);
        Assert.True(vm.IsNotionExpanded);
        Assert.False(vm.IsBusy);
        Assert.True(vm.CanAct);
        Assert.Equal(1, service.StatusReads);
        Assert.Equal(0, service.SourceRefreshes);
        Assert.True(vm.ShowSignOut); // A failure is not reported as a successful sign-out.
        fail = false;
        service.DefaultStatus = Status(NotionCliState.SignedOut);
        await RunAction(vm, kind, Action);
        Assert.Equal(2, calls);
        Assert.Equal(kind == 2 ? 2 : 0, service.SignOutCalls);
        Assert.Equal(1, service.SourceRefreshes);
        Assert.Equal(2, service.StatusReads);
        Assert.False(vm.HasNotionError);
        Assert.True(vm.ShowSignIn);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task BusyAction_DropsDuplicateRequests(int kind)
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        var service = fixture.Service();
        var vm = new AgentSkillsSettingsViewModel(service);
        var gate = Gate<bool>();
        int calls = 0;
        Task Action() { calls++; return gate.Task; }
        service.SignOut = Action;
        Task pending = RunAction(vm, kind, Action);
        try
        {
            Assert.True(vm.IsBusy);
            await RunAction(vm, kind, Action);
            await vm.RunNotionActionAsync(Action, true);
            await vm.RunNotionActionAsync(Action);
            await vm.SignOutAsync();
            Assert.Equal(1, calls);
            Assert.Equal(kind == 2 ? 1 : 0, service.SignOutCalls);
            Assert.Equal(0, service.StatusReads);
            Assert.Equal(0, service.SourceRefreshes);
        }
        finally { gate.TrySetResult(true); await pending; }
        Assert.Equal(1, calls);
        Assert.Equal(1, service.StatusReads);
        Assert.Equal(1, service.SourceRefreshes);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task RefreshRequests_DuringCheckCoalesceToOneFollowUp()
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        var service = fixture.Service();
        var gate = Gate<NotionStatus>();
        service.StatusQueue.Enqueue(gate.Task);
        service.StatusQueue.Enqueue(Task.FromResult(Status(NotionCliState.SignedIn, "Final")));
        var vm = new AgentSkillsSettingsViewModel(service);
        Task pending = vm.RefreshAsync();
        try
        {
            await vm.RefreshAsync();
            await vm.RefreshAsync();
            await vm.RefreshAsync();
            Assert.Equal(1, service.StatusReads);
            Assert.True(vm.IsBusy);
        }
        finally { gate.TrySetResult(Status(NotionCliState.SignedOut)); await pending; }
        Assert.Equal(2, service.StatusReads);
        Assert.Equal("Connected to Final, CLI 0.23.9", vm.NotionSummary);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task RefreshRequests_DuringActionCoalesceAfterActionStatusRead()
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        var service = fixture.Service();
        service.StatusQueue.Enqueue(Task.FromResult(Status(NotionCliState.SignedOut)));
        service.StatusQueue.Enqueue(Task.FromResult(Status(NotionCliState.SignedIn, "Pending refresh")));
        var vm = new AgentSkillsSettingsViewModel(service);
        var gate = Gate<bool>();
        int calls = 0;
        Task action = vm.RunNotionActionAsync(() => { calls++; service.Operations.Add("callback"); return gate.Task; });
        try
        {
            await vm.RefreshAsync();
            await vm.RefreshAsync();
            await vm.RefreshAsync();
            Assert.Equal(0, service.StatusReads);
            Assert.Equal(1, calls);
        }
        finally { gate.TrySetResult(true); await action; }
        Assert.Equal(["callback", "sources", "status", "status"], service.Operations);
        Assert.Equal(2, service.StatusReads);
        Assert.Equal(1, service.SourceRefreshes);
        Assert.Equal(1, calls);
        Assert.Equal("Connected to Pending refresh, CLI 0.23.9", vm.NotionSummary);
        Assert.False(vm.IsBusy);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeactivateDuringStatus_DiscardsLateResultAndPendingRefresh(bool throws)
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        var service = fixture.Service();
        var vm = new AgentSkillsSettingsViewModel(service);
        await vm.RefreshAsync();
        var gate = Gate<NotionStatus>();
        service.StatusQueue.Enqueue(gate.Task);
        Task pending = vm.RefreshAsync();
        await vm.RefreshAsync();
        vm.Deactivate();
        string summary = vm.NotionSummary;
        var state = vm.NotionState;
        try
        {
            Assert.False(vm.IsBusy);
            Assert.False(vm.CanAct);
            Assert.False(vm.CanInstall);
        }
        finally { CompleteOld(gate, throws); await pending; }
        Assert.Equal(summary, vm.NotionSummary);
        Assert.Equal(state, vm.NotionState);
        Assert.False(vm.HasNotionError);
        Assert.Equal(2, service.StatusReads);
        Assert.Equal(0, service.SourceRefreshes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeactivateDuringAction_DiscardsLateWorkAndProtectsNewGeneration(bool throws)
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        var service = fixture.Service();
        var vm = new AgentSkillsSettingsViewModel(service);
        var oldGate = Gate<bool>();
        Task old = vm.RunNotionActionAsync(() => oldGate.Task);
        await vm.RefreshAsync();
        vm.Deactivate();
        vm.Activate();
        var newGate = Gate<NotionStatus>();
        service.StatusQueue.Enqueue(newGate.Task);
        Task current = vm.RefreshAsync();
        try
        {
            if (throws) oldGate.TrySetException(new InvalidOperationException("stale action"));
            else oldGate.TrySetResult(true);
            await old;
            Assert.True(vm.IsBusy);
            Assert.False(vm.CanAct);
            Assert.False(vm.HasNotionError);
            Assert.Equal(0, service.SourceRefreshes);
            Assert.Equal(1, service.StatusReads);
        }
        finally
        {
            oldGate.TrySetResult(true);
            newGate.TrySetResult(Status(NotionCliState.SignedIn, "New generation"));
            await old;
            await current;
        }
        Assert.Equal("Connected to New generation, CLI 0.23.9", vm.NotionSummary);
        Assert.False(vm.IsBusy);
        Assert.Equal(1, service.StatusReads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReactivateWhileOldCheckPending_KeepsNewStatusWhenOldCompletesLast(bool throws)
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        var service = fixture.Service();
        var gate = Gate<NotionStatus>();
        service.StatusQueue.Enqueue(gate.Task);
        var vm = new AgentSkillsSettingsViewModel(service);
        Task old = vm.RefreshAsync();
        vm.Deactivate();
        vm.Activate();
        service.DefaultStatus = Status(NotionCliState.SignedIn, "Current workspace");
        try
        {
            await vm.RefreshAsync();
            Assert.Equal("Connected to Current workspace, CLI 0.23.9", vm.NotionSummary);
            Assert.True(vm.CanAct);
        }
        finally { CompleteOld(gate, throws); await old; }
        Assert.Equal("Connected to Current workspace, CLI 0.23.9", vm.NotionSummary);
        Assert.True(vm.ShowSignOut);
        Assert.False(vm.HasNotionError);
        Assert.False(vm.IsBusy);
        Assert.Equal(2, service.StatusReads);
    }

    private static void CompleteOld(TaskCompletionSource<NotionStatus> gate, bool throws)
    {
        if (throws) gate.TrySetException(new InvalidOperationException("stale status"));
        else gate.TrySetResult(Status(NotionCliState.SignedOut));
    }

    private static void AssertInvalidVersion(AgentSkillsSettingsViewModel vm)
    {
        Assert.True(vm.SpecificVersion);
        Assert.True(vm.HasVersionError);
        Assert.True(vm.IsAdvancedExpanded);
        Assert.Equal(VersionMessage, vm.VersionError);
        Assert.Equal("Version needs attention", vm.VersionSummary);
    }
}
