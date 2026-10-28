using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Devolutions.AgentSkills;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.ViewModels;
using UniGetUI.Avalonia.ViewModels.Pages;
using UniGetUI.Avalonia.Views;
using UniGetUI.Avalonia.Views.Pages;
using UniGetUI.Avalonia.Views.Pages.SettingsPages;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Extensions.AgentSkills;
using UniGetUI.Interface;
using UniGetUI.PackageEngine.Classes.Packages.Classes;

namespace UniGetUI.Tests;

public sealed class AgentSkillsExtensionTests
{
    private readonly HeadlessUnitTestSession _session =
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(AgentSkillsExtensionTests).Assembly);

    [Fact]
    public Task ExtensionIsDefaultOffEvenWithLegacyManagerEnablementAndRetainsPreferences() => _session.Dispatch(() =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        fixture.Seed(["codex"], pin: "0.23.9");
        Settings.Set(Settings.K.SkillsDefaultEnablementApplied, true);
        Settings.SetDictionaryItem(Settings.K.DisabledManagers, "Skills", false);
        Settings.SetList(AgentSkillsService.SourcesListKey, new List<string> { "contoso/skills" });
        var extension = AppExtensions.AgentSkills;
        Assert.False(extension.IsEnabled);
        Assert.Equal("agent-skills", extension.Id);
        Assert.Equal(PageType.AgentSkills, extension.Page);
        Assert.Equal(typeof(AgentSkillsSettingsControl), extension.SettingsPage);
        Assert.Contains(extension.Id, IpcAppPages.SupportedPages);
        Assert.DoesNotContain(new SidebarViewModel().NavigationPages, page => page == PageType.AgentSkills);
        extension.SetEnabled(true);
        Assert.True(extension.IsEnabled);
        extension.SetEnabled(false);
        Assert.False(extension.IsEnabled);
        Assert.Equal(["codex"], AgentSkillsService.TargetAgents);
        Assert.Equal("contoso/skills", Assert.Single(AgentSkillsService.GetConfiguredSources()).Name);
        Assert.Equal("0.23.9", NotionCliTool.PinnedVersion);
        Assert.False(Settings.GetDictionaryItem<string, bool>(Settings.K.DisabledManagers, "Skills"));
    }, CancellationToken.None);

    [Fact]
    public Task SidebarToggleAndHiddenOrderRoundTripRequireNoRestart() => _session.Dispatch(() =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        var sidebar = new SidebarViewModel();
        sidebar.SetExtensionEnabled(PageType.AgentSkills, true);
        Assert.Contains(PageType.AgentSkills, sidebar.NavigationPages);
        var order = sidebar.NavigationPages.Reverse().ToArray();
        sidebar.ReorderNavigationPages(order);
        sidebar.SetExtensionEnabled(PageType.AgentSkills, false);
        Assert.DoesNotContain(PageType.AgentSkills, sidebar.NavigationPages);
        sidebar.SetExtensionEnabled(PageType.AgentSkills, true);
        Assert.Equal(order, sidebar.NavigationPages);
        Assert.Equal("agent-skills", IpcAppPages.NormalizePageName("agent-skills"));
    }, CancellationToken.None);

    [Fact]
    public Task ExtensionSessionCachesPageAndStopDisablesFurtherWork() => _session.Dispatch(async () =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        var runtime = AppExtensions.AgentSkills.Start(null!);
        var page = Assert.IsType<AgentSkillsPage>(runtime.Page);
        Assert.Same(page, runtime.Page);
        var vm = Assert.IsType<AgentSkillsViewModel>(page.DataContext);
        runtime.Stop();
        await vm.RefreshCommand.ExecuteAsync(null);
        await runtime.CheckForUpdatesAsync();
        Assert.Empty(vm.Skills);
        Assert.False(vm.IsBusy);
        Assert.False(vm.CanInstall);
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task StandalonePageDiscoversInstallsListsUpdatesAndRemovesThroughService() => _session.Dispatch(async () =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        Settings.SetList(AgentSkillsService.SourcesListKey, new List<string> { "contoso/skills" });
        AgentSkillsService.TargetAgents = ["codex"];
        var backend = new RecordingBackend();
        var vm = new AgentSkillsViewModel(new AgentSkillsService(backend));
        using var host = new AgentSkillsSettingsTestWindow(new AgentSkillsPage(vm), 1100);
        vm.Query = "review";
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedSkill = Assert.Single(vm.Skills);
        Assert.True(vm.CanInstall);
        Assert.Contains("contoso/skills", vm.SelectedSkill.Provenance);
        await vm.InstallCommand.ExecuteAsync(null);
        Assert.Equal(["codex"], backend.LastTargets);
        Assert.Equal("https://github.com/contoso/skills", backend.LastInstallSource);
        vm.Mode = 1;
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedSkill = Assert.Single(vm.Skills);
        Assert.True(vm.CanRemove);
        Assert.Contains("codex", vm.SelectedSkill.Provenance);
        Assert.Equal("1111111", vm.SelectedSkill.Version);
        vm.Mode = 2;
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedSkill = Assert.Single(vm.Skills);
        Assert.True(vm.CanUpdate);
        Assert.Equal("2222222", vm.SelectedSkill.NewVersion);
        await vm.UpdateCommand.ExecuteAsync(null);
        Assert.Equal(1, backend.UpdateCalls);
        Assert.Empty(vm.Skills);
        vm.Mode = 1;
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedSkill = Assert.Single(vm.Skills);
        await vm.RemoveCommand.ExecuteAsync(null);
        Assert.Equal(["review"], backend.Removed);
        Assert.Empty(vm.Skills);
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task OperationErrorsAreVisibleAndDoNotReportCompletion() => _session.Dispatch(async () =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        Settings.SetList(AgentSkillsService.SourcesListKey, new List<string> { "contoso/skills" });
        var backend = new RecordingBackend { FailInstall = true };
        var vm = new AgentSkillsViewModel(new AgentSkillsService(backend));
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedSkill = Assert.Single(vm.Skills);
        List<AccessibilityAnnouncement> announcements = [];
        void RecordAnnouncement(object? sender, AccessibilityAnnouncement announcement) => announcements.Add(announcement);
        AccessibilityAnnouncementService.AnnouncementRequested += RecordAnnouncement;
        try
        {
            await vm.InstallCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("install rejected", vm.Status);
            Assert.DoesNotContain("Completed", vm.Status);
            Assert.False(vm.IsBusy);
            Assert.Empty(backend.Installed);
            Assert.Contains(announcements, announcement => announcement.Message == vm.Status
                && announcement.LiveSetting == AutomationLiveSetting.Assertive);
        }
        finally
        {
            AccessibilityAnnouncementService.AnnouncementRequested -= RecordAnnouncement;
        }
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task StoppingExtensionCancelsInFlightWorkAndPreventsLateResults() => _session.Dispatch(async () =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        Settings.SetList(AgentSkillsService.SourcesListKey, new List<string> { "contoso/skills" });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new RecordingBackend
        {
            BeforeList = token =>
        {
            entered.TrySetResult();
            token.WaitHandle.WaitOne();
            token.ThrowIfCancellationRequested();
        }
        };
        var vm = new AgentSkillsViewModel(new AgentSkillsService(backend));
        var pending = vm.RefreshCommand.ExecuteAsync(null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(vm.IsBusy);
        vm.Stop();
        await pending.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(vm.IsBusy);
        Assert.Empty(vm.Skills);
        Assert.False(vm.CanInstall);
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Empty(vm.Skills);
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task BackgroundUpdatesRespectNotificationPreferenceAndDeduplicate() => _session.Dispatch(async () =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        Settings.SetList(AgentSkillsService.SourcesListKey, new List<string> { "contoso/skills" });
        var backend = new RecordingBackend();
        backend.Installed.Add(backend.Tracked());
        int notifications = 0;
        var vm = new AgentSkillsViewModel(new AgentSkillsService(backend)) { UpdatesAvailable = count => notifications += count };
        await vm.CheckForUpdatesInBackgroundAsync();
        await vm.CheckForUpdatesInBackgroundAsync();
        Assert.Equal(1, notifications);
        Settings.SetDictionaryItem(Settings.K.DisabledPackageManagerNotifications, "Skills", true);
        var muted = new AgentSkillsViewModel(new AgentSkillsService(backend)) { UpdatesAvailable = count => notifications += count };
        await muted.CheckForUpdatesInBackgroundAsync();
        Assert.Equal(1, notifications);
        Assert.Contains("1 skill updates", muted.Status);
        vm.Stop();
        muted.Stop();
        return true;
    }, CancellationToken.None);

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("1.2; whoami")]
    public Task InvalidNotionPinsPreserveSavedPinAndIgnoredPackages(string invalid) => _session.Dispatch(() =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        fixture.Seed(pin: "0.23.9");
        AgentSkillsDependencies.ApplyPinnedUpdateExclusions();
        if (invalid.Length == 0)
        {
            Assert.True(AgentSkillsDependencies.TrySetPinnedVersion(invalid));
            Assert.Null(NotionCliTool.PinnedVersion);
            Assert.Null(IgnoredUpdatesDatabase.GetIgnoredVersion("winget\\Notion.ntn"));
            Assert.Null(IgnoredUpdatesDatabase.GetIgnoredVersion("npm\\ntn"));
        }
        else
        {
            Assert.False(AgentSkillsDependencies.TrySetPinnedVersion(invalid));
            Assert.Equal("0.23.9", NotionCliTool.PinnedVersion);
            Assert.NotNull(IgnoredUpdatesDatabase.GetIgnoredVersion("winget\\Notion.ntn"));
            Assert.NotNull(IgnoredUpdatesDatabase.GetIgnoredVersion("npm\\ntn"));
        }
        AgentSkillsSettingsTestFixture.AssertOtherPreferences();
    }, CancellationToken.None);

    [Fact]
    public Task PersistedNotionPinIsRestoredAtStartupWithoutEnablingExtension() => _session.Dispatch(() =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        fixture.Seed(pin: "0.23.9");
        AppExtensions.InitializePreferences();
        Assert.False(AppExtensions.AgentSkills.IsEnabled);
        Assert.NotNull(IgnoredUpdatesDatabase.GetIgnoredVersion("winget\\Notion.ntn"));
        Assert.NotNull(IgnoredUpdatesDatabase.GetIgnoredVersion("npm\\ntn"));
        var dependency = AgentSkillsDependencies.CreateNotionDependency();
        Assert.Contains("0.23.9", dependency.FancyInstallCommand);
        AgentSkillsSettingsTestFixture.AssertOtherPreferences();
    }, CancellationToken.None);

    [Fact]
    public Task SourceSettingsAddAndRemoveUseExtensionServiceWithoutManagerOperations() => _session.Dispatch(async () =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        var backend = new RecordingBackend();
        var control = new AgentSkillsSourcesControl(new AgentSkillsService(backend));
        using var host = new AgentSkillsSettingsTestWindow(control);
        var input = Assert.Single(control.GetVisualDescendants().OfType<TextBox>());
        input.Text = "https://skills.contoso.com";
        var added = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        control.SourcesChanged += (_, _) => added.TrySetResult();
        host.Click(Assert.Single(control.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, "Add source")));
        await added.Task.WaitAsync(TimeSpan.FromSeconds(30));
        host.Pump();
        Assert.Equal("https://skills.contoso.com", Assert.Single(AgentSkillsService.GetConfiguredSources()).InstallSource);
        Assert.Equal("", input.Text);
        host.Click(Assert.Single(control.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, "Remove")));
        Assert.Empty(AgentSkillsService.GetConfiguredSources());
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task ExplicitCancelReturnsToIdleAndAllowsRetry() => _session.Dispatch(async () =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        Settings.SetList(AgentSkillsService.SourcesListKey, new List<string> { "contoso/skills" });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new RecordingBackend
        {
            BeforeList = token =>
        {
            entered.TrySetResult();
            token.WaitHandle.WaitOne();
            token.ThrowIfCancellationRequested();
        }
        };
        var vm = new AgentSkillsViewModel(new AgentSkillsService(backend));
        var pending = vm.RefreshCommand.ExecuteAsync(null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        vm.CancelCommand.Execute(null);
        await pending.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("Cancelled", vm.Status);
        Assert.False(vm.IsBusy);
        Assert.Empty(vm.Skills);
        backend.BeforeList = null;
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("review", Assert.Single(vm.Skills).Id);
        return true;
    }, CancellationToken.None);

    [Fact]
    public void ExtensionServiceHasNoPackageEngineDependencyOrManagerRegistration()
    {
        Assert.DoesNotContain(typeof(AgentSkillsService).Assembly.GetReferencedAssemblies(),
            assembly => assembly.Name?.StartsWith("UniGetUI.PackageEngine", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(UniGetUI.PackageEngine.PEInterface.Managers, manager => manager.Id == "skills");
    }

    private sealed class RecordingBackend : ISkillsBackend
    {
        public string Version => "test";
        public List<InstalledSkillInfo> Installed { get; } = [];
        public List<string> Removed { get; } = [];
        public string? LastInstallSource { get; private set; }
        public IReadOnlyList<string>? LastTargets { get; private set; }
        public int UpdateCalls { get; private set; }
        public bool FailInstall { get; init; }
        public Action<CancellationToken>? BeforeList { get; set; }
        public InstalledSkillInfo Tracked() => new()
        {
            Name = "review",
            Description = "Reviews code",
            Path = "unused",
            Scope = SkillScope.Global,
            Agents = ["codex"],
            Source = "contoso/skills",
            SourceType = "github",
            Hash = "1111111aaaa",
        };
        public IReadOnlyList<AgentInfo> GetAgents() => [];
        public IReadOnlyList<InstalledSkillInfo> GetInstalledSkills(CancellationToken cancellationToken) => Installed.ToArray();
        public IReadOnlyList<AvailableSkill> GetAvailableSkills(string source, IProgress<string>? progress, CancellationToken cancellationToken)
        {
            BeforeList?.Invoke(cancellationToken);
            return [new("review", "Reviews code", null)];
        }
        public SkillInstallResult Install(string source, string skill, IReadOnlyList<string>? agents, IProgress<string>? progress, CancellationToken cancellationToken)
        {
            LastInstallSource = source;
            LastTargets = agents;
            progress?.Report("Installing review");
            if (!FailInstall) Installed.Add(Tracked());
            return new([new() { Name = skill, Status = FailInstall ? SkillOperationStatus.Failed : SkillOperationStatus.Succeeded, Error = FailInstall ? "install rejected" : null }]);
        }
        public SkillUpdateCheckResult CheckForUpdates(IReadOnlyList<string>? skills, IProgress<string>? progress, CancellationToken cancellationToken) =>
            new([new() { Name = "review", Source = "contoso/skills", Scope = SkillScope.Global, CurrentHash = "1111111aaaa", LatestHash = "2222222bbbb" }], []);
        public SkillUpdateResult Update(IReadOnlyList<SkillUpdate> updates, IProgress<string>? progress, CancellationToken cancellationToken)
        {
            UpdateCalls++;
            return new([new("review", SkillScope.Global, SkillOperationStatus.Succeeded)]);
        }
        public SkillRemoveResult Remove(string skill, IProgress<string>? progress, CancellationToken cancellationToken)
        {
            Removed.Add(skill);
            Installed.Clear();
            return new([new(skill, SkillOperationStatus.Succeeded)]);
        }
        public NotionStatus GetNotionStatus(CancellationToken cancellationToken) => new(NotionCliState.NotInstalled, null, null, null);
        public NotionSignIn BeginNotionSignIn(CancellationToken cancellationToken) => throw new NotSupportedException();
        public void CompleteNotionSignIn(CancellationToken cancellationToken) => throw new NotSupportedException();
        public void SignOutOfNotion(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
