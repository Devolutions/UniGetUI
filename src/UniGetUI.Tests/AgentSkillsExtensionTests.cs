using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Devolutions.AgentSkills;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.ViewModels;
using UniGetUI.Avalonia.ViewModels.Pages;
using UniGetUI.Avalonia.Views;
using UniGetUI.Avalonia.Views.DialogPages;
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
        var vm = CreateInstallViewModel(backend);
        var page = new AgentSkillsPage(vm);
        using var host = new AgentSkillsSettingsTestWindow(page, 1100);
        vm.Query = "review";
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedSkill = Assert.Single(vm.Skills);
        Assert.True(vm.CanInstall);
        Assert.Contains("contoso/skills", vm.SelectedSkill.Provenance);
        await vm.InstallCommand.ExecuteAsync(null);
        Assert.Equal(["codex"], backend.LastTargets);
        Assert.Equal("https://github.com/contoso/skills", backend.LastInstallSource);
        await SelectModeAsync(page, host, vm, 1);
        vm.SelectedSkill = Assert.Single(vm.Skills);
        Assert.True(vm.CanRemove);
        Assert.Contains("codex", vm.SelectedSkill.Provenance);
        Assert.Equal("1111111", vm.SelectedSkill.Version);
        await SelectModeAsync(page, host, vm, 2);
        vm.SelectedSkill = Assert.Single(vm.Skills);
        Assert.True(vm.CanUpdate);
        Assert.Equal("2222222", vm.SelectedSkill.NewVersion);
        await vm.UpdateCommand.ExecuteAsync(null);
        Assert.Equal(1, backend.UpdateCalls);
        Assert.Empty(vm.Skills);
        await SelectModeAsync(page, host, vm, 1);
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
        var vm = CreateInstallViewModel(backend);
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
        Assert.Equal("1 skill updates", vm.BackgroundStatus);
        Assert.Equal("", vm.Status);
        Settings.SetDictionaryItem(Settings.K.DisabledPackageManagerNotifications, "Skills", true);
        var muted = new AgentSkillsViewModel(new AgentSkillsService(backend)) { UpdatesAvailable = count => notifications += count };
        await muted.CheckForUpdatesInBackgroundAsync();
        Assert.Equal(1, notifications);
        Assert.Equal("1 skill updates", muted.BackgroundStatus);
        Assert.Equal("", muted.Status);
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
        var vm = new AgentSkillsViewModel(new AgentSkillsService(backend));
        var page = new AgentSkillsPage(vm);
        using var host = new AgentSkillsSettingsTestWindow(page, 1100);
        Assert.False(vm.ShowSources);
        host.Click(page.FindControl<ToggleButton>("ManageSourcesButton")!);
        Assert.True(vm.ShowSources);
        var control = Assert.IsType<AgentSkillsSourcesControl>(page.FindControl<ContentControl>("SourcesHost")!.Content);
        var input = Assert.Single(control.GetVisualDescendants().OfType<TextBox>());
        input.Text = "https://skills.contoso.com";
        var added = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        control.SourcesChanged += (_, _) => added.TrySetResult();
        host.Click(Assert.Single(control.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, "Add source")));
        await added.Task.WaitAsync(TimeSpan.FromSeconds(30));
        host.Pump();
        Assert.Equal("https://skills.contoso.com", Assert.Single(AgentSkillsService.GetConfiguredSources()).InstallSource);
        await AwaitRefreshAsync(vm);
        Assert.Equal(1, backend.AvailableReads);
        Assert.False(vm.NeedsSetup);
        Assert.Single(page.FindControl<Border>("SourcesPanel")!.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Sources");
        Assert.Equal("", input.Text);
        host.Click(Assert.Single(control.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, "Remove")));
        await AwaitRefreshAsync(vm);
        Assert.Empty(AgentSkillsService.GetConfiguredSources());
        Assert.True(vm.NeedsSetup);
        Assert.Empty(vm.Skills);
        Assert.Equal(1, backend.AvailableReads);
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

    [Fact]
    public Task TabsAreVisibleAndLoadInventoryButDirectModeChangesDoNotReadBackend() => _session.Dispatch(async () =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        Settings.SetList(AgentSkillsService.SourcesListKey, new List<string> { "contoso/skills" });
        var backend = new RecordingBackend();
        backend.Installed.Add(backend.Tracked());
        var vm = new AgentSkillsViewModel(new AgentSkillsService(backend));
        vm.Mode = 1;
        Assert.Equal(0, backend.InstalledReads);
        Assert.Empty(vm.Skills);
        vm.Mode = 0;
        var page = new AgentSkillsPage(vm);
        using var host = new AgentSkillsSettingsTestWindow(page, 1100);
        await AwaitRefreshAsync(vm);
        Assert.Equal(1, backend.AvailableReads);
        var tabs = page.FindControl<TabStrip>("ModeTabs")!;
        Assert.Equal("Skill views", AutomationProperties.GetName(tabs));
        Assert.Equal(["Discover", "Installed", "Updates"], tabs.Items.Cast<TabStripItem>().Select(item => item.Content));
        Assert.All(tabs.Items.Cast<TabStripItem>(), item =>
        {
            Assert.True(item.IsEffectivelyVisible);
            Assert.True(item.Bounds.Width > 0);
        });
        Assert.Equal(1, backend.InstalledReads);
        await SelectModeAsync(page, host, vm, 1);
        Assert.Equal("review", Assert.Single(vm.Skills).Id);
        Assert.Equal(2, backend.InstalledReads);
        Assert.Equal("Installed (1)", ((TabStripItem)tabs.Items[1]!).Content);
        await SelectModeAsync(page, host, vm, 2);
        Assert.Equal("2222222", Assert.Single(vm.Skills).NewVersion);
        Assert.Equal(1, backend.CheckCalls);
        Assert.Equal("Updates (1)", ((TabStripItem)tabs.Items[2]!).Content);
        await vm.LoadCurrentModeAsync();
        Assert.Equal(1, backend.CheckCalls);
        await SelectModeAsync(page, host, vm, 0);
        Assert.Equal("review", Assert.Single(vm.Skills).Id);
        Assert.Equal(1, backend.AvailableReads);
        return true;
    }, CancellationToken.None);

    [Theory]
    [InlineData(720)]
    [InlineData(1200)]
    public Task CompactWorkspaceSeparatesSetupSourcesAndResultsAndRestoresFocus(int width) => _session.Dispatch(async () =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        var backend = new RecordingBackend();
        var vm = new AgentSkillsViewModel(new AgentSkillsService(backend));
        var page = new AgentSkillsPage(vm);
        using var host = new AgentSkillsSettingsTestWindow(page, width);
        var toolbar = page.FindControl<WrapPanel>("SearchToolbar")!;
        var empty = page.FindControl<StackPanel>("EmptyState")!;
        var results = page.FindControl<Border>("ResultsPanel")!;
        var sources = page.FindControl<Border>("SourcesPanel")!;
        var manage = page.FindControl<ToggleButton>("ManageSourcesButton")!;
        var close = page.FindControl<Button>("CloseSourcesButton")!;
        var tabs = page.FindControl<TabStrip>("ModeTabs")!;
        Assert.False(toolbar.IsEffectivelyVisible);
        Assert.True(page.FindControl<StackPanel>("AddSourceBar")!.IsEffectivelyVisible);
        Assert.True(empty.IsEffectivelyVisible);
        Assert.False(results.IsEffectivelyVisible);
        Assert.False(sources.IsEffectivelyVisible);
        Assert.Equal(global::Avalonia.Layout.HorizontalAlignment.Left, empty.HorizontalAlignment);
        Assert.Equal(global::Avalonia.Layout.VerticalAlignment.Top, empty.VerticalAlignment);
        Assert.DoesNotContain(results, empty.GetVisualAncestors());
        Assert.All(tabs.Items.Cast<TabStripItem>(), tab =>
        {
            Assert.Equal(14, tab.FontSize);
            Assert.InRange(tab.Bounds.Height, 40, 52);
        });

        host.Click(manage);
        host.Pump();
        Assert.True(sources.IsEffectivelyVisible);
        Assert.False(empty.IsEffectivelyVisible);
        Assert.False(results.IsEffectivelyVisible);
        Assert.False(toolbar.IsEffectivelyVisible);
        Assert.False(page.FindControl<StackPanel>("AddSourceBar")!.IsEffectivelyVisible);
        var sourceHost = page.FindControl<ContentControl>("SourcesHost")!;
        var address = Assert.Single(sourceHost.GetVisualDescendants().OfType<TextBox>());
        Assert.True(address.IsFocused);
        Assert.InRange(address.TranslatePoint(default, sourceHost)!.Value.X, 0, 1);
        Assert.InRange(sources.Bounds.Right, 1, page.Bounds.Width);
        Assert.Equal(0, sources.BorderThickness.Left);
        host.Click(close);
        host.Pump();
        Assert.False(vm.ShowSources);
        Assert.True(manage.IsFocused);
        Assert.True(empty.IsEffectivelyVisible);

        Settings.SetList(AgentSkillsService.SourcesListKey, new List<string> { "contoso/skills" });
        vm.RefreshPreferences();
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedSkill = Assert.Single(vm.Skills);
        host.Pump();
        Assert.True(toolbar.IsEffectivelyVisible);
        Assert.True(results.IsEffectivelyVisible);
        Assert.False(empty.IsEffectivelyVisible);
        var selected = vm.SelectedSkill;
        host.Click(manage);
        host.Pump();
        Assert.True(sources.IsEffectivelyVisible);
        Assert.False(results.IsEffectivelyVisible);
        Assert.False(page.FindControl<Border>("DetailsHost")!.IsEffectivelyVisible);
        Assert.False(toolbar.IsEffectivelyVisible);
        host.Click(close);
        host.Pump();
        Assert.Same(selected, vm.SelectedSkill);
        Assert.True(results.IsEffectivelyVisible);
        Assert.True(page.FindControl<Border>("DetailsHost")!.IsEffectivelyVisible);
        host.Click(manage);
        await SelectModeAsync(page, host, vm, 1);
        Assert.False(vm.ShowSources);
        Assert.False(sources.IsEffectivelyVisible);
        Assert.True(empty.IsEffectivelyVisible);
        Assert.False(results.IsEffectivelyVisible);
        Assert.Equal(1, backend.AvailableReads);
        Assert.Equal(2, backend.InstalledReads);
        return true;
    }, CancellationToken.None);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public Task InventoryFiltersAndEnterUseLoadedResultsWithoutBackendReads(int mode) => _session.Dispatch(async () =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        Settings.SetList(AgentSkillsService.SourcesListKey, new List<string> { "contoso/skills", "other/skills" });
        var backend = new RecordingBackend();
        backend.Installed.AddRange([
            backend.Tracked(),
            backend.Tracked() with { Name = "format", Description = "Formats text", Source = "other/skills", Agents = ["claude-code"] },
            backend.Tracked() with { Name = "audit", Description = "Security review", Agents = ["claude-code"] },
        ]);
        var vm = new AgentSkillsViewModel(new AgentSkillsService(backend));
        var page = new AgentSkillsPage(vm);
        using var host = new AgentSkillsSettingsTestWindow(page, 1100);
        await SelectModeAsync(page, host, vm, mode);
        int reads = backend.InstalledReads;
        int checks = backend.CheckCalls;
        Assert.Equal(["review", "format", "audit"], vm.Skills.Select(skill => skill.Id));
        vm.SelectedSkill = vm.Skills[0];
        var search = page.FindControl<TextBox>("SkillSearch")!;
        host.EnterText(search, "REVIEW");
        Assert.Equal(["review", "audit"], vm.Skills.Select(skill => skill.Id));
        Assert.Equal("review", vm.SelectedSkill?.Id);
        PressEnter(host, search);
        Assert.Equal(reads, backend.InstalledReads);
        Assert.Equal(checks, backend.CheckCalls);
        page.FindControl<ComboBox>("AgentFilter")!.SelectedItem = "claude-code";
        host.Pump();
        Assert.Equal("audit", Assert.Single(vm.Skills).Id);
        Assert.Null(vm.SelectedSkill);
        page.FindControl<ComboBox>("SourceFilter")!.SelectedItem = "other/skills";
        host.Pump();
        Assert.Empty(vm.Skills);
        Assert.Equal("No matching skills", vm.EmptyTitle);
        Assert.Contains("clear the source and agent filters", vm.EmptyDescription);
        search.Text = "";
        host.Pump();
        Assert.Equal("format", Assert.Single(vm.Skills).Id);
        page.FindControl<ComboBox>("SourceFilter")!.SelectedIndex = 0;
        page.FindControl<ComboBox>("AgentFilter")!.SelectedIndex = 0;
        host.Pump();
        Assert.Equal(3, vm.Skills.Count);
        Assert.Equal(reads, backend.InstalledReads);
        Assert.Equal(checks, backend.CheckCalls);
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task DiscoverBrowsesSourcesOnOpenAndFiltersLocally() => _session.Dispatch(async () =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        Settings.SetList(AgentSkillsService.SourcesListKey, new List<string> { "contoso/skills", "other/skills" });
        var backend = new RecordingBackend();
        backend.Available.Add(new("format", "Formats text", null));
        var vm = new AgentSkillsViewModel(new AgentSkillsService(backend));
        var page = new AgentSkillsPage(vm);
        using var host = new AgentSkillsSettingsTestWindow(page, 1100);
        await AwaitRefreshAsync(vm);
        host.Pump();
        Assert.Equal(2, backend.AvailableReads);
        Assert.Equal(4, vm.Skills.Count);
        var input = page.FindControl<TextBox>("SkillSearch")!;
        host.EnterText(input, "FORMAT");
        Assert.Equal(["format", "format"], vm.Skills.Select(skill => skill.Id));
        Assert.Equal(2, backend.AvailableReads);
        page.FindControl<ComboBox>("SourceFilter")!.SelectedItem = "other/skills";
        host.Pump();
        Assert.Equal("other/skills", Assert.Single(vm.Skills).SourceName);
        Assert.Equal(2, backend.AvailableReads);
        Assert.False(page.FindControl<ComboBox>("AgentFilter")!.IsVisible);
        PressEnter(host, input);
        await AwaitRefreshAsync(vm);
        Assert.Equal(4, backend.AvailableReads);
        return true;
    }, CancellationToken.None);

    [Theory]
    [InlineData("npx skills add contoso/skills")]
    [InlineData("npx -y skills add https://github.com/contoso/skills --global")]
    [InlineData("github.com/contoso/skills/")]
    public Task PastedSourceIsAddedAndItsSkillsShownReadyToInstall(string pasted) => _session.Dispatch(async () =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        var backend = new RecordingBackend();
        backend.Available.Add(new("format", "Formats text", null));
        var vm = new AgentSkillsViewModel(new AgentSkillsService(backend));
        var page = new AgentSkillsPage(vm);
        using var host = new AgentSkillsSettingsTestWindow(page, 1100);
        Assert.True(vm.NeedsSetup);
        Assert.True(page.FindControl<StackPanel>("AddSourceBar")!.IsEffectivelyVisible);
        var source = page.FindControl<TextBox>("SourceInput")!;
        host.EnterText(source, pasted);
        PressEnter(host, source);
        if (vm.AddFromSourceCommand.ExecutionTask is { } adding) await adding.WaitAsync(TimeSpan.FromSeconds(30));
        host.Pump();
        Assert.False(vm.HasError, vm.Status);
        Assert.Single(AgentSkillsService.GetConfiguredSources());
        Assert.Equal(["review", "format"], vm.Skills.Select(skill => skill.Id));
        Assert.Equal("Added contoso/skills. It offers 2 skills.", vm.Status);
        Assert.Equal("", vm.SourceInput);
        Assert.False(vm.NeedsSetup);
        Assert.True(page.FindControl<WrapPanel>("SearchToolbar")!.IsEffectivelyVisible);
        var installAll = page.FindControl<Button>("InstallAllButton")!;
        Assert.True(installAll.IsEffectivelyVisible);
        Assert.Equal("Install all 2", installAll.Content);

        host.EnterText(source, "https://github.com/contoso/skills");
        await vm.AddFromSourceCommand.ExecuteAsync(null);
        Assert.Single(AgentSkillsService.GetConfiguredSources());
        Assert.False(vm.HasError);
        Assert.Equal("contoso/skills is already one of your sources. It offers 2 skills.", vm.Status);
        Assert.Equal(1, backend.AvailableReads);

        host.EnterText(source, "this is not a source");
        await vm.AddFromSourceCommand.ExecuteAsync(null);
        Assert.True(vm.HasSourceInputMessage);
        Assert.Contains("owner/repo", vm.SourceInputMessage);
        Assert.Single(AgentSkillsService.GetConfiguredSources());
        return true;
    }, CancellationToken.None);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task InstallAllInstallsEveryShownSkillAndReportsFailures(bool rejectOne) => _session.Dispatch(async () =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        Settings.SetList(AgentSkillsService.SourcesListKey, new List<string> { "contoso/skills" });
        var backend = new RecordingBackend();
        backend.Available.Add(new("format", "Formats text", null));
        if (rejectOne) backend.RejectedInstalls.Add("review");
        var vm = CreateInstallViewModel(backend);
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.True(vm.CanInstallAll);
        await vm.InstallAllCommand.ExecuteAsync(null);
        Assert.Equal(rejectOne ? ["format"] : ["review", "format"], backend.InstalledSkillNames);
        Assert.Equal(rejectOne, vm.HasError);
        Assert.Contains(rejectOne ? "1 of 2 skills could not be installed" : "Installed 2 skills", vm.Status);
        Assert.False(vm.IsBusy);
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task SingleSkillInstallHonorsConfirmedTargetSelection() => _session.Dispatch(async () =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        Settings.SetList(AgentSkillsService.SourcesListKey, new List<string> { "contoso/skills" });
        AgentSkillsService.TargetAgents = ["codex"];
        var backend = new RecordingBackend();
        IReadOnlyList<string>? offeredDefaults = null;
        var vm = new AgentSkillsViewModel(new AgentSkillsService(backend),
            selectInstallTargets: (skills, agents, defaults) =>
            {
                Assert.Equal(["review"], skills.Select(skill => skill.Id));
                Assert.Contains(agents, agent => agent.Id == "claude-code");
                offeredDefaults = defaults;
                return Task.FromResult<AgentSkillsInstallSelection?>(
                    new(["claude-code"], RememberAsDefault: false));
            });
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedSkill = Assert.Single(vm.Skills);

        await vm.InstallCommand.ExecuteAsync(null);

        var install = Assert.Single(backend.InstallCalls);
        Assert.Equal("review", install.Skill);
        Assert.Equal(["claude-code"], install.Agents);
        Assert.Equal(["codex"], offeredDefaults);
        Assert.Equal(["codex"], AgentSkillsService.TargetAgents);
        Assert.False(vm.HasError, vm.Status);
    }, CancellationToken.None);

    [Fact]
    public Task InstallAllHonorsConfirmedTargetSelectionForEverySkill() => _session.Dispatch(async () =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        Settings.SetList(AgentSkillsService.SourcesListKey, new List<string> { "contoso/skills" });
        AgentSkillsService.TargetAgents = ["codex"];
        var backend = new RecordingBackend();
        backend.Available.Add(new("format", "Formats text", null));
        IReadOnlyList<string>? offeredDefaults = null;
        var vm = new AgentSkillsViewModel(new AgentSkillsService(backend),
            selectInstallTargets: (skills, _, defaults) =>
            {
                Assert.Equal(["review", "format"], skills.Select(skill => skill.Id));
                offeredDefaults = defaults;
                return Task.FromResult<AgentSkillsInstallSelection?>(
                    new(["claude-code"], RememberAsDefault: false));
            });
        await vm.RefreshCommand.ExecuteAsync(null);

        await vm.InstallAllCommand.ExecuteAsync(null);

        Assert.Equal(["review", "format"], backend.InstallCalls.Select(call => call.Skill));
        Assert.All(backend.InstallCalls, call => Assert.Equal(["claude-code"], call.Agents));
        Assert.Equal(["codex"], offeredDefaults);
        Assert.Equal(["codex"], AgentSkillsService.TargetAgents);
        Assert.False(vm.HasError, vm.Status);
    }, CancellationToken.None);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public Task CancelledOrEmptyTargetSelectionDoesNotInstall(bool installAll, bool emptySelection) => _session.Dispatch(async () =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        Settings.SetList(AgentSkillsService.SourcesListKey, new List<string> { "contoso/skills" });
        var backend = new RecordingBackend();
        backend.Available.Add(new("format", "Formats text", null));
        var vm = new AgentSkillsViewModel(new AgentSkillsService(backend),
            selectInstallTargets: (_, _, _) => Task.FromResult<AgentSkillsInstallSelection?>(
                emptySelection ? new([], RememberAsDefault: false) : null));
        await vm.RefreshCommand.ExecuteAsync(null);
        if (!installAll) vm.SelectedSkill = Assert.Single(vm.Skills, skill => skill.Id == "review");

        if (installAll) await vm.InstallAllCommand.ExecuteAsync(null);
        else await vm.InstallCommand.ExecuteAsync(null);

        Assert.Empty(backend.InstallCalls);
        Assert.Empty(backend.InstalledSkillNames);
        Assert.False(vm.IsBusy);
    }, CancellationToken.None);

    [Fact]
    public Task InstallDialogDefaultsToSavedOrDetectedAndUniversalAgents() => _session.Dispatch(() =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        var backend = new RecordingBackend();
        var skill = new SkillItem("review", "Reviews code", SkillSourceLocator.Parse("contoso/skills"));

        var savedDefaults = new AgentSkillsInstallDialog([skill], backend.AgentCatalogue, ["claude-code"]);
        Assert.Equal(["claude-code"], savedDefaults.Targets.Where(target => target.IsSelected).Select(target => target.Id));

        var automaticDefaults = new AgentSkillsInstallDialog([skill], backend.AgentCatalogue, null);
        Assert.Equal(["codex", "shared-agent"],
            automaticDefaults.Targets.Where(target => target.IsSelected).Select(target => target.Id));
        Assert.Contains(automaticDefaults.Targets, target => target.Id == "claude-code" && !target.IsSelected);
        return Task.CompletedTask;
    }, CancellationToken.None);

    [Fact]
    public Task PreviewExposesMarkdownFilesAndContentDerivedRiskIndicatorsBeforeInstall() => _session.Dispatch(async () =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        Settings.SetList(AgentSkillsService.SourcesListKey, new List<string> { "contoso/skills" });
        var backend = new RecordingBackend
        {
            Preview = new SkillPreview(
                "review",
                "---\nname: review\n---\nReview source before running commands.",
                ["SKILL.md", "scripts/setup.ps1", "bin/validator.exe", "references/guide.md"],
                "MIT",
                "Works with local repositories",
                ["Bash", "Read", "Write"],
                IsMarkdownTruncated: false),
        };
        var vm = new AgentSkillsViewModel(new AgentSkillsService(backend));
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedSkill = Assert.Single(vm.Skills);
        var page = new AgentSkillsPage(vm);
        using var host = new AgentSkillsSettingsTestWindow(page, 1100);
        host.Pump();

        host.Click(page.FindControl<Button>("PreviewSkillButton")!);
        if (vm.LoadPreviewCommand.ExecutionTask is { } previewing)
            await previewing.WaitAsync(TimeSpan.FromSeconds(30));
        host.Pump();

        var preview = Assert.IsType<SkillPreview>(vm.SkillPreview);
        Assert.Equal("---\nname: review\n---\nReview source before running commands.", preview.Markdown);
        Assert.Equal(["SKILL.md", "scripts/setup.ps1", "bin/validator.exe", "references/guide.md"], preview.Files);
        Assert.Equal(["Bash", "Read", "Write"], preview.AllowedTools);
        Assert.Equal("MIT", preview.License);
        Assert.Equal("Works with local repositories", preview.Compatibility);
        Assert.True(preview.ContainsScripts);
        Assert.True(preview.ContainsExecutables);
        Assert.True(vm.HasPreview);
        Assert.Equal(1, backend.PreviewCalls);
        Assert.Empty(backend.InstallCalls);
        var details = page.FindControl<Border>("DetailsHost")!;
        Assert.Equal(preview.Markdown,
            Assert.Single(details.GetVisualDescendants().OfType<SelectableTextBlock>()).Text);
        Assert.Contains(details.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == "scripts/setup.ps1" && text.IsEffectivelyVisible);
        Assert.Contains(details.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == "bin/validator.exe" && text.IsEffectivelyVisible);
        Assert.Contains(details.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == vm.PreviewWarnings && text.IsEffectivelyVisible);
        Assert.Contains("Contains files under scripts/", vm.PreviewWarnings);
        Assert.Contains("Contains executable or script files.", vm.PreviewWarnings);
        Assert.Contains("Requests tool access: Bash, Read, Write", vm.PreviewWarnings);
        Assert.Equal("https://github.com/contoso/skills", backend.LastPreviewSource);
        Assert.Equal("review", backend.LastPreviewSkill);
    }, CancellationToken.None);

    [Fact]
    public Task PreviewDoesNotFlagOrdinaryFilesWithoutToolPermissions() => _session.Dispatch(async () =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        Settings.SetList(AgentSkillsService.SourcesListKey, new List<string> { "contoso/skills" });
        var backend = new RecordingBackend
        {
            Preview = new SkillPreview("review", "Safe instructions.", ["SKILL.md", "references/guide.md"],
                null, null, [], false),
        };
        var vm = new AgentSkillsViewModel(new AgentSkillsService(backend));
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedSkill = Assert.Single(vm.Skills);

        await vm.LoadPreviewCommand.ExecuteAsync(null);

        var preview = Assert.IsType<SkillPreview>(vm.SkillPreview);
        Assert.False(preview.ContainsScripts);
        Assert.False(preview.ContainsExecutables);
        Assert.False(vm.HasPreviewWarnings);
        Assert.Equal("", vm.PreviewWarnings);
        Assert.Empty(backend.InstallCalls);
    }, CancellationToken.None);

    [Fact]
    public Task DiscoverRowsShowInstalledAndUpdateAvailableInsteadOfInstall() => _session.Dispatch(async () =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        Settings.SetList(AgentSkillsService.SourcesListKey, new List<string> { "contoso/skills" });
        var backend = new RecordingBackend();
        backend.Installed.Add(backend.Tracked());
        backend.Installed.Add(backend.Tracked() with { Name = "format", Description = "Formats text" });
        backend.Available.Add(new("format", "Formats text", null));
        backend.Available.Add(new("new-skill", "Not installed yet", null));
        backend.UpdatableSkills = ["format"];
        var vm = new AgentSkillsViewModel(new AgentSkillsService(backend));
        var page = new AgentSkillsPage(vm);
        using var host = new AgentSkillsSettingsTestWindow(page, 1100);
        await AwaitRefreshAsync(vm);
        await SelectModeAsync(page, host, vm, 2);
        await SelectModeAsync(page, host, vm, 0);

        var installed = Assert.Single(vm.Skills, skill => skill.Id == "review");
        var update = Assert.Single(vm.Skills, skill => skill.Id == "format");
        var fresh = Assert.Single(vm.Skills, skill => skill.Id == "new-skill");
        Assert.Equal("Installed", installed.DiscoverStatus);
        Assert.True(installed.IsInstalled);
        Assert.False(installed.HasUpdate);
        Assert.False(installed.CanInstallFromDiscover);
        Assert.Equal("Update available", update.DiscoverStatus);
        Assert.True(update.IsInstalled);
        Assert.True(update.HasUpdate);
        Assert.False(update.CanInstallFromDiscover);
        Assert.Equal("", fresh.DiscoverStatus);
        Assert.True(fresh.CanInstallFromDiscover);
        Assert.Empty(backend.InstallCalls);
        host.Pump();
        var list = page.FindControl<ListBox>("SkillList")!;
        var statusTexts = list.GetVisualDescendants().OfType<TextBlock>().Where(text =>
            text.IsEffectivelyVisible && (text.Text is "Installed" or "Update available")).Select(text => text.Text);
        Assert.Equal(["Installed", "Update available"], statusTexts);
        var rowButtons = list.GetVisualDescendants().OfType<Button>()
            .Where(button => button.Name == "RowInstallButton").ToArray();
        Assert.Equal(3, rowButtons.Length);
        Assert.False(Assert.Single(rowButtons, button =>
            button.CommandParameter is SkillItem { Id: "review" }).IsEffectivelyVisible);
        Assert.False(Assert.Single(rowButtons, button =>
            button.CommandParameter is SkillItem { Id: "format" }).IsEffectivelyVisible);
        Assert.True(Assert.Single(rowButtons, button =>
            button.CommandParameter is SkillItem { Id: "new-skill" }).IsEffectivelyVisible);
    }, CancellationToken.None);

    [Fact]
    public Task EachDiscoverRowInstallsItsOwnSkill() => _session.Dispatch(async () =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        Settings.SetList(AgentSkillsService.SourcesListKey, new List<string> { "contoso/skills" });
        var backend = new RecordingBackend();
        backend.Available.Add(new("format", "Formats text", null));
        var vm = CreateInstallViewModel(backend);
        var page = new AgentSkillsPage(vm);
        using var host = new AgentSkillsSettingsTestWindow(page, 1100);
        await AwaitRefreshAsync(vm);
        host.Pump();
        var rows = page.FindControl<ListBox>("SkillList")!.GetVisualDescendants().OfType<Button>()
            .Where(button => button.Name == "RowInstallButton").ToArray();
        Assert.Equal(2, rows.Length);
        Assert.All(rows, row => Assert.True(row.IsEffectivelyVisible && row.IsEnabled));
        var format = Assert.Single(rows, row => row.CommandParameter is SkillItem { Id: "format" });
        Assert.Equal("format", AutomationProperties.GetHelpText(format), StringComparer.OrdinalIgnoreCase);
        host.Click(format);
        if (vm.InstallSkillCommand.ExecutionTask is { } installing) await installing.WaitAsync(TimeSpan.FromSeconds(30));
        host.Pump();
        Assert.Equal(["format"], backend.InstalledSkillNames);
        Assert.Equal("format", vm.SelectedSkill?.Id);
        Assert.False(vm.HasError, vm.Status);

        await SelectModeAsync(page, host, vm, 1);
        Assert.DoesNotContain(page.FindControl<ListBox>("SkillList")!.GetVisualDescendants().OfType<Button>(),
            button => button.Name == "RowInstallButton" && button.IsEffectivelyVisible);
        return true;
    }, CancellationToken.None);
    [InlineData(1, "RemoveButton")]
    [InlineData(2, "UpdateButton")]
    public Task DetailsShowOnlyContextualActionAndExactSource(int mode, string action) => _session.Dispatch(async () =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        Settings.SetList(AgentSkillsService.SourcesListKey, new List<string> { "contoso/skills" });
        AgentSkillsService.TargetAgents = ["codex", "claude-code"];
        var backend = new RecordingBackend();
        backend.Installed.Add(backend.Tracked());
        var vm = CreateInstallViewModel(backend);
        var page = new AgentSkillsPage(vm);
        using var host = new AgentSkillsSettingsTestWindow(page, 1100);
        if (mode == 0) await vm.RefreshCommand.ExecuteAsync(null);
        else await SelectModeAsync(page, host, vm, mode);
        Assert.False(page.FindControl<Border>("DetailsHost")!.IsVisible);
        vm.SelectedSkill = Assert.Single(vm.Skills);
        host.Pump();
        Assert.True(page.FindControl<Border>("DetailsHost")!.IsEffectivelyVisible);
        foreach (string name in new[] { "InstallButton", "RemoveButton", "UpdateButton" })
        {
            var button = page.FindControl<Button>(name)!;
            Assert.Equal(name == action, button.IsEffectivelyVisible);
            if (name == action) Assert.True(button.IsEnabled);
        }
        var details = page.FindControl<Border>("DetailsHost")!;
        Assert.Contains(details.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == "https://github.com/contoso/skills" && text.IsEffectivelyVisible);
        Assert.Equal(mode == 2, details.GetVisualDescendants().OfType<TextBlock>()
            .Single(text => text.Text == "Available version").IsEffectivelyVisible);
        Assert.True(vm.CanOpenSource);
        if (mode == 0)
        {
            Assert.Equal("Install for your user account in: codex, claude-code", vm.InstallationSummary);
            await vm.InstallCommand.ExecuteAsync(null);
            Assert.Equal(["codex", "claude-code"], backend.LastTargets);
            Assert.Equal("https://github.com/contoso/skills", backend.LastInstallSource);
            Assert.Equal("review", backend.LastInstallSkill);
        }
        else if (mode == 1)
        {
            await vm.RemoveCommand.ExecuteAsync(null);
            Assert.Equal(["review"], backend.Removed);
            Assert.Empty(vm.Skills);
        }
        else
        {
            await vm.UpdateCommand.ExecuteAsync(null);
            var update = Assert.Single(backend.LastUpdates!);
            Assert.Equal("review", update.Name);
            Assert.Equal("contoso/skills", update.Source);
            Assert.Equal(SkillScope.Global, update.Scope);
            Assert.Equal("2222222bbbb", update.LatestHash);
            Assert.Empty(vm.Skills);
        }
        return true;
    }, CancellationToken.None);

    [Theory]
    [InlineData(null, false)]
    [InlineData("ssh://git@git.example.com:2222/team/repo.git", true)]
    [InlineData("https://skills.contoso.com", true)]
    public Task AutomaticTargetsExplainUserScopeAndSourceLinkMatchesScheme(string? source, bool canOpen) => _session.Dispatch(() =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        var vm = new AgentSkillsViewModel(new AgentSkillsService(new RecordingBackend()));
        var page = new AgentSkillsPage(vm);
        using var host = new AgentSkillsSettingsTestWindow(page, 1100);
        Assert.Equal("Choose the agents for this install. Detected agents and the shared skills folder are selected by default.",
            vm.InstallationSummary);
        vm.SelectedSkill = new SkillItem("local", "Local skill", SkillSourceLocator.Parse(source ?? ""));
        host.Pump();
        Assert.Equal(canOpen, vm.CanOpenSource);
        Assert.Equal(canOpen, page.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Open source")).IsEnabled);
        vm.SelectedSkill = new SkillItem("ssh-only", "No browser URL",
            new SkillSourceLocator(SkillSourceKind.Repository, "git.example.com/team/repo",
                "ssh://git@git.example.com/team/repo.git", new Uri("ssh://git@git.example.com/team/repo.git")));
        host.Pump();
        Assert.False(vm.CanOpenSource);
        Assert.False(page.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Open source")).IsEnabled);
        vm.SelectedSkill = null;
        host.Pump();
        Assert.False(vm.CanOpenSource);
        Assert.False(page.FindControl<Border>("DetailsHost")!.IsVisible);
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task InstallConfirmationDefaultsToDetectedAndUniversalAgents() => _session.Dispatch(async () =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        Settings.SetList(AgentSkillsService.SourcesListKey, new List<string> { "contoso/skills" });
        var backend = new RecordingBackend();
        var vm = CreateInstallViewModel(backend);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedSkill = Assert.Single(vm.Skills);
        await vm.InstallCommand.ExecuteAsync(null);
        Assert.Equal("https://github.com/contoso/skills", backend.LastInstallSource);
        Assert.Equal(["codex", "shared-agent"], backend.LastTargets);
        Assert.Equal("review", backend.LastInstallSkill);
        Assert.Contains("Completed", vm.Status);
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task EmptyStatesOfferSetupSearchDiscoverRefreshAndErrorRecovery() => _session.Dispatch(async () =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        var backend = new RecordingBackend();
        var service = new AgentSkillsService(backend);
        var vm = new AgentSkillsViewModel(service);
        var page = new AgentSkillsPage(vm);
        using var host = new AgentSkillsSettingsTestWindow(page, 1100);
        Assert.Equal("Add skills from a repository", vm.EmptyTitle);
        Assert.Contains("owner/repo", vm.EmptyDescription);
        Assert.True(page.FindControl<TextBox>("SourceInput")!.IsEffectivelyVisible);
        Settings.SetList(AgentSkillsService.SourcesListKey, new List<string> { "contoso/skills" });
        vm.RefreshPreferences();
        Assert.Equal("Browse your sources", vm.EmptyTitle);
        vm.Query = "nothing matches";
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("No matching skills", vm.EmptyTitle);
        Assert.Contains("Try a different filter", vm.EmptyDescription);
        await SelectModeAsync(page, host, vm, 1);
        Assert.Equal("No skills installed", vm.EmptyTitle);
        Assert.Contains("Open Discover", vm.EmptyDescription);
        host.Click(page.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Discover skills")));
        Assert.Equal(0, vm.Mode);
        await SelectModeAsync(page, host, vm, 2);
        Assert.Equal("No skill updates found", vm.EmptyTitle);
        Assert.Contains("configured sources", vm.EmptyDescription);
        await SelectModeAsync(page, host, vm, 0);
        backend.InstalledFailure = new InvalidOperationException("inventory denied");
        await SelectModeAsync(page, host, vm, 1);
        Assert.True(vm.HasError);
        Assert.Equal("Could not load skills", vm.EmptyTitle);
        Assert.Contains("inventory denied", vm.EmptyDescription);
        var retry = page.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Retry"));
        Assert.True(retry.IsEffectivelyVisible);
        backend.InstalledFailure = null;
        host.Click(retry);
        await AwaitRefreshAsync(vm);
        Assert.False(vm.HasError);
        Assert.Equal("No skills installed", vm.EmptyTitle);
        return true;
    }, CancellationToken.None);

    [Theory]
    [InlineData(949, true)]
    [InlineData(950, false)]
    [InlineData(1200, false)]
    public Task DetailsReflowAcrossBreakpointAndWindowResize(int width, bool narrow) => _session.Dispatch(() =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        var vm = new AgentSkillsViewModel(new AgentSkillsService(new RecordingBackend()));
        var page = new AgentSkillsPage(vm);
        using var host = new AgentSkillsSettingsTestWindow(page, width);
        vm.SelectedSkill = new SkillItem("review", "A long skill description that must wrap without clipping.", null);
        host.Pump();
        var details = page.FindControl<Border>("DetailsHost")!;
        Assert.Equal(narrow ? 0 : 1, Grid.GetColumn(details));
        Assert.Equal(narrow ? 1 : 0, Grid.GetRow(details));
        Assert.Equal(narrow ? 250 : double.PositiveInfinity, details.MaxHeight);
        if (narrow)
        {
            Assert.True(double.IsNaN(details.Width));
            Assert.InRange(details.Bounds.Height, 1, 250);
        }
        else Assert.Equal(340, details.Bounds.Width);
        Assert.True(details.Bounds.Width > 0);
        Assert.True(details.Bounds.Height > 0);
        Assert.True(details.Bounds.Right <= page.Bounds.Width);
        host.Window.Width = narrow ? 1200 : 720;
        host.Pump();
        Assert.Equal(narrow ? 1 : 0, Grid.GetColumn(details));
        Assert.Equal(narrow ? 0 : 1, Grid.GetRow(details));
        Assert.True(details.IsEffectivelyVisible);
    }, CancellationToken.None);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task BackgroundResultsAndErrorsLeaveForegroundStateUntouched(bool fails) => _session.Dispatch(async () =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        Settings.SetList(AgentSkillsService.SourcesListKey, new List<string> { "contoso/skills" });
        var backend = new RecordingBackend();
        backend.Installed.Add(backend.Tracked());
        backend.Available.Add(new("format", "Formats text", null));
        var vm = new AgentSkillsViewModel(new AgentSkillsService(backend));
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedSkill = Assert.Single(vm.Skills, skill => skill.Id == "format");
        vm.Status = "Foreground search complete";
        vm.Output = "Foreground activity";
        var selection = vm.SelectedSkill;
        var skills = vm.Skills.ToArray();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        backend.BeforeCheck = _ =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(30), CancellationToken.None)) throw new TimeoutException("Test did not release background check.");
            if (fails) throw new InvalidOperationException("background denied");
        };
        var pending = vm.CheckForUpdatesInBackgroundAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(vm.IsCheckingUpdates);
            Assert.False(vm.IsBusy);
            Assert.True(vm.CanInstall);
        }
        finally
        {
            release.Set();
            await pending.WaitAsync(TimeSpan.FromSeconds(30));
        }
        Assert.Equal(fails ? "Could not check skill updates: background denied" : "1 skill updates", vm.BackgroundStatus);
        Assert.Equal(fails ? null : (int?)1, vm.UpdateCount);
        Assert.Equal("Foreground search complete", vm.Status);
        Assert.Equal("Foreground activity", vm.Output);
        Assert.Equal(["review", "format"], vm.Skills.Select(skill => skill.Id));
        Assert.Equal(!fails, Assert.Single(vm.Skills, skill => skill.Id == "review").HasUpdate);
        Assert.Same(selection, vm.SelectedSkill);
        Assert.False(vm.HasError);
        Assert.False(vm.IsBusy);
        Assert.False(vm.IsCheckingUpdates);
        return true;
    }, CancellationToken.None);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ForegroundCancelsBackgroundAndSerializesUntilGateReleased(bool cancelForeground) => _session.Dispatch(async () =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        var backend = new RecordingBackend();
        backend.Installed.Add(backend.Tracked());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        backend.BeforeCheck = token =>
        {
            using var registration = token.Register(() => cancelled.TrySetResult());
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(30), CancellationToken.None)) throw new TimeoutException("Test did not release background check.");
            token.ThrowIfCancellationRequested();
        };
        var vm = new AgentSkillsViewModel(new AgentSkillsService(backend));
        var page = new AgentSkillsPage(vm);
        using var host = new AgentSkillsSettingsTestWindow(page, 1100);
        Settings.SetList(AgentSkillsService.SourcesListKey, new List<string> { "contoso/skills" });
        vm.ShowSources = true;
        host.Pump();
        var background = vm.CheckForUpdatesInBackgroundAsync();
        Task foreground = Task.CompletedTask;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            foreground = vm.RefreshCommand.ExecuteAsync(null);
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(30));
            host.Pump();
            Assert.True(vm.IsBusy);
            Assert.Equal(0, backend.AvailableReads);
            Assert.False(page.FindControl<ToggleButton>("ManageSourcesButton")!.IsEnabled);
            Assert.False(page.FindControl<ContentControl>("SourcesHost")!.GetVisualAncestors()
                .OfType<Border>().First(border => border.Classes.Contains("skills-panel")).IsEnabled);
            await vm.CheckForUpdatesInBackgroundAsync();
            Assert.Equal(1, backend.CheckCalls);
            if (cancelForeground)
            {
                vm.CancelCommand.Execute(null);
                await foreground.WaitAsync(TimeSpan.FromSeconds(30));
                Assert.Equal("Cancelled", vm.Status);
                Assert.Equal(0, backend.AvailableReads);
            }
        }
        finally
        {
            release.Set();
            await background.WaitAsync(TimeSpan.FromSeconds(30));
            await foreground.WaitAsync(TimeSpan.FromSeconds(30));
        }
        Assert.Null(vm.UpdateCount);
        Assert.Equal("", vm.BackgroundStatus);
        if (cancelForeground) await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("review", Assert.Single(vm.Skills).Id);
        Assert.Equal(1, backend.AvailableReads);
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task StopCancelsBackgroundAndDiscardsUncooperativeLateResults() => _session.Dispatch(async () =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        Settings.SetList(AgentSkillsService.SourcesListKey, new List<string> { "contoso/skills" });
        var backend = new RecordingBackend();
        backend.Installed.Add(backend.Tracked());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        backend.BeforeCheck = token =>
        {
            using var registration = token.Register(() => cancelled.TrySetResult());
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(30), CancellationToken.None)) throw new TimeoutException("Test did not release background check.");
            // Deliberately return stale success despite cancellation.
        };
        int notifications = 0;
        var vm = new AgentSkillsViewModel(new AgentSkillsService(backend)) { UpdatesAvailable = count => notifications += count };
        vm.Status = "Keep foreground status";
        var background = vm.CheckForUpdatesInBackgroundAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            vm.Stop();
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            release.Set();
            await background.WaitAsync(TimeSpan.FromSeconds(30));
        }
        Assert.Null(vm.UpdateCount);
        Assert.Equal("", vm.BackgroundStatus);
        Assert.Equal("Keep foreground status", vm.Status);
        Assert.Equal(0, notifications);
        Assert.False(vm.IsCheckingUpdates);
        await vm.CheckForUpdatesInBackgroundAsync();
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(1, backend.CheckCalls);
        Assert.Equal(0, backend.AvailableReads);
        return true;
    }, CancellationToken.None);

    private static async Task SelectModeAsync(AgentSkillsPage page, AgentSkillsSettingsTestWindow host,
        AgentSkillsViewModel vm, int mode)
    {
        var tabs = page.FindControl<TabStrip>("ModeTabs")!;
        host.Click((TabStripItem)tabs.Items[mode]!);
        await AwaitRefreshAsync(vm);
        host.Pump();
        Assert.Equal(mode, vm.Mode);
        Assert.Equal(mode, tabs.SelectedIndex);
    }

    private static async Task AwaitRefreshAsync(AgentSkillsViewModel vm)
    {
        if (vm.RefreshCommand.ExecutionTask is { } task) await task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(vm.IsBusy);
    }

    private static AgentSkillsViewModel CreateInstallViewModel(RecordingBackend backend) =>
        new(new AgentSkillsService(backend), selectInstallTargets: SelectInstallDefaults);

    private static Task<AgentSkillsInstallSelection?> SelectInstallDefaults(IReadOnlyList<SkillItem> skills,
        IReadOnlyList<AgentInfo> agents, IReadOnlyList<string>? defaults)
    {
        IReadOnlyList<string> selected = defaults is { Count: > 0 }
            ? defaults
            : agents.Where(agent => agent.IsDetected || agent.IsUniversal)
                .Select(agent => agent.Id).ToArray();
        return Task.FromResult<AgentSkillsInstallSelection?>(
            selected.Count == 0 ? null : new AgentSkillsInstallSelection(selected, RememberAsDefault: false));
    }

    private static void PressEnter(AgentSkillsSettingsTestWindow host, TextBox input)
    {
        Assert.True(input.Focus());
        host.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        host.Window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        host.Pump();
    }

    private sealed class RecordingBackend : ISkillsBackend
    {
        public string Version => "test";
        public List<InstalledSkillInfo> Installed { get; } = [];
        public List<string> Removed { get; } = [];
        public IReadOnlyList<AgentInfo> AgentCatalogue { get; } =
        [
            new("codex", "Codex", ".codex/skills", @"C:\test\codex\skills", false, true),
            new("claude-code", "Claude Code", ".claude/skills", @"C:\test\claude\skills", false, false),
            new("shared-agent", "Shared Agent", ".agents/skills", @"C:\test\shared\skills", true, false),
        ];
        public SkillPreview Preview { get; set; } = new(
            "review", "---\nname: review\n---\nReview code.", ["SKILL.md"], null, null, [], false);
        public int PreviewCalls { get; private set; }
        public string? LastPreviewSource { get; private set; }
        public string? LastPreviewSkill { get; private set; }
        public HashSet<string>? UpdatableSkills { get; set; }
        public string? LastInstallSource { get; private set; }
        public string? LastInstallSkill { get; private set; }
        public IReadOnlyList<string>? LastTargets { get; private set; }
        public List<(string Skill, string[]? Agents)> InstallCalls { get; } = [];
        public int UpdateCalls { get; private set; }
        public int InstalledReads { get; private set; }
        public int AvailableReads { get; private set; }
        public int CheckCalls { get; private set; }
        public IReadOnlyList<SkillUpdate>? LastUpdates { get; private set; }
        public Exception? InstalledFailure { get; set; }
        public Action<CancellationToken>? BeforeCheck { get; set; }
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
        public IReadOnlyList<AgentInfo> GetAgents() => AgentCatalogue;
        public IReadOnlyList<InstalledSkillInfo> GetInstalledSkills(CancellationToken cancellationToken)
        {
            InstalledReads++;
            if (InstalledFailure is { } error) throw error;
            return Installed.ToArray();
        }
        public IReadOnlyList<AvailableSkill> GetAvailableSkills(string source, IProgress<string>? progress, CancellationToken cancellationToken)
        {
            AvailableReads++;
            BeforeList?.Invoke(cancellationToken);
            return Available.ToArray();
        }
        public SkillPreview PreviewSkill(string source, string skill, CancellationToken cancellationToken)
        {
            PreviewCalls++;
            LastPreviewSource = source;
            LastPreviewSkill = skill;
            return Preview;
        }
        public List<AvailableSkill> Available { get; } = [new("review", "Reviews code", null)];
        public List<string> InstalledSkillNames { get; } = [];
        public HashSet<string> RejectedInstalls { get; } = [];
        public SkillInstallResult Install(string source, string skill, IReadOnlyList<string>? agents, IProgress<string>? progress, CancellationToken cancellationToken)
        {
            LastInstallSource = source;
            LastInstallSkill = skill;
            LastTargets = agents;
            InstallCalls.Add((skill, agents?.ToArray()));
            progress?.Report("Installing review");
            bool fail = FailInstall || RejectedInstalls.Contains(skill);
            if (!fail)
            {
                Installed.Add(Tracked() with { Name = skill });
                InstalledSkillNames.Add(skill);
            }
            return new([new() { Name = skill, Status = fail ? SkillOperationStatus.Failed : SkillOperationStatus.Succeeded, Error = fail ? "install rejected" : null }]);
        }
        public SkillUpdateCheckResult CheckForUpdates(IReadOnlyList<string>? skills, IProgress<string>? progress, CancellationToken cancellationToken)
        {
            CheckCalls++;
            BeforeCheck?.Invoke(cancellationToken);
            return new(Installed.Where(skill => UpdatableSkills is null || UpdatableSkills.Contains(skill.Name))
                .Select(skill => new SkillUpdate
                {
                    Name = skill.Name,
                    Source = skill.Source!,
                    Scope = SkillScope.Global,
                    CurrentHash = "1111111aaaa",
                    LatestHash = "2222222bbbb",
                }).ToArray(), []);
        }
        public SkillUpdateResult Update(IReadOnlyList<SkillUpdate> updates, IProgress<string>? progress, CancellationToken cancellationToken)
        {
            UpdateCalls++;
            LastUpdates = updates;
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
