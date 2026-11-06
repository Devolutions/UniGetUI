using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Media;
using Avalonia.Styling;
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
    public Task ExtensionSessionCachesPageAndStopDisablesFurtherWork() => _session.Dispatch<bool>(async () =>
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
    public Task StandalonePageDiscoversInstallsListsUpdatesAndRemovesThroughService() => _session.Dispatch<bool>(async () =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        Settings.SetList(AgentSkillsService.SourcesListKey, new List<string> { "contoso/skills" });
        AgentSkillsService.TargetAgents = ["codex"];
        var backend = new RecordingBackend();
        var vm = CreateInstallViewModel(backend);
        var page = new AgentSkillsPage(vm);
        page.FindControl<ComboBox>("TypeFilter")!.SelectedItem = AgentLibraryTypeFilter.Skills;
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
    public Task OperationErrorsAreVisibleAndDoNotReportCompletion() => _session.Dispatch<bool>(async () =>
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
    public Task StoppingExtensionCancelsInFlightWorkAndPreventsLateResults() => _session.Dispatch<bool>(async () =>
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
        var vm = new AgentSkillsViewModel(new AgentSkillsService(backend))
        {
            ContentType = AgentLibraryTypeFilter.Skills,
        };
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
    public Task BackgroundUpdatesRespectNotificationPreferenceAndDeduplicate() => _session.Dispatch<bool>(async () =>
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
        Assert.Equal("1 skill update", vm.BackgroundStatus);
        Assert.Equal("", vm.Status);
        Settings.SetDictionaryItem(Settings.K.DisabledPackageManagerNotifications, "Skills", true);
        var muted = new AgentSkillsViewModel(new AgentSkillsService(backend)) { UpdatesAvailable = count => notifications += count };
        await muted.CheckForUpdatesInBackgroundAsync();
        Assert.Equal(1, notifications);
        Assert.Equal("1 skill update", muted.BackgroundStatus);
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
    public Task SourceSettingsAddAndRemoveUseExtensionServiceWithoutManagerOperations() => _session.Dispatch<bool>(async () =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        var backend = new RecordingBackend();
        var vm = new AgentSkillsViewModel(new AgentSkillsService(backend))
        {
            ContentType = AgentLibraryTypeFilter.Skills,
        };
        var page = new AgentSkillsPage(vm);
        using var host = new AgentSkillsSettingsTestWindow(page, 1100);
        Assert.Equal(AgentLibraryView.Discover, vm.LibraryView);
        await SelectLibraryViewAsync(page, host, vm, (int)AgentLibraryView.Sources);
        Assert.True(vm.IsSourcesView);
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
        Assert.Single(page.FindControl<Grid>("SourcesWorkspace")!.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Skill sources");
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
    public Task ExplicitCancelReturnsToIdleAndAllowsRetry() => _session.Dispatch<bool>(async () =>
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
    public Task LibraryTabsAreVisibleAndLoadInventoryWithoutEagerBackendReads() => _session.Dispatch<bool>(async () =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        Settings.SetList(AgentSkillsService.SourcesListKey, new List<string> { "contoso/skills" });
        var backend = new RecordingBackend();
        backend.Installed.Add(backend.Tracked());
        var vm = new AgentSkillsViewModel(new AgentSkillsService(backend))
        {
            ContentType = AgentLibraryTypeFilter.Skills,
        };
        vm.Mode = 1;
        Assert.Equal(0, backend.InstalledReads);
        Assert.Empty(vm.Skills);
        vm.Mode = 0;
        var page = new AgentSkillsPage(vm);
        using var host = new AgentSkillsSettingsTestWindow(page, 1100);
        await AwaitRefreshAsync(vm);
        Assert.Equal(1, backend.AvailableReads);
        var tabs = page.FindControl<TabStrip>("LibraryTabs")!;
        Assert.Equal("Agent library views", AutomationProperties.GetName(tabs));
        Assert.Equal(4, tabs.Items.Count);
        Assert.Equal("Discover", ((TabStripItem)tabs.Items[0]!).Content);
        Assert.Equal("Installed", ((TabStripItem)tabs.Items[1]!).Content);
        Assert.Equal(vm.UpdatesHeader, ((TabStripItem)tabs.Items[2]!).Content);
        Assert.Equal("Sources", ((TabStripItem)tabs.Items[3]!).Content);
        Assert.Equal(["All", "Skills", "Plugins"],
            page.FindControl<ComboBox>("TypeFilter")!.Items.Cast<AgentLibraryTypeFilter>().Select(type => type.ToString()));
        Assert.All(tabs.Items.Cast<TabStripItem>(), item =>
        {
            Assert.True(item.IsEffectivelyVisible);
            Assert.True(item.Bounds.Width > 0);
        });
        Assert.Equal(1, backend.InstalledReads);
        await SelectLibraryViewAsync(page, host, vm, 1);
        Assert.Equal("review", Assert.Single(vm.Skills).Id);
        Assert.Equal(2, backend.InstalledReads);
        Assert.Equal("Installed", ((TabStripItem)tabs.Items[1]!).Content);
        await SelectLibraryViewAsync(page, host, vm, 2);
        Assert.Equal("2222222", Assert.Single(vm.Skills).NewVersion);
        Assert.Equal(1, backend.CheckCalls);
        Assert.Equal("Updates (1)", ((TabStripItem)tabs.Items[2]!).Content);
        await vm.LoadLibraryViewAsync();
        Assert.Equal(1, backend.CheckCalls);
        await SelectLibraryViewAsync(page, host, vm, 0);
        Assert.Equal("review", Assert.Single(vm.Skills).Id);
        Assert.Equal(1, backend.AvailableReads);
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task UnifiedPluginTypeAndSourcesViewUseTheSameLibraryAndParentStopDisablesChild() => _session.Dispatch<bool>(async () =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        var backend = new RecordingBackend();
        var vm = new AgentSkillsViewModel(new AgentSkillsService(backend));
        // The parent intentionally has no plugin-runner seam. Stop the default child before
        // page materialization/navigation so this test can never discover or invoke real Claude.
        vm.AgentPlugins.Stop();
        var page = new AgentSkillsPage(vm);
        using var host = new AgentSkillsSettingsTestWindow(page, 1100);
        var typeFilter = page.FindControl<ComboBox>("TypeFilter")!;
        typeFilter.SelectedItem = AgentLibraryTypeFilter.Plugins;
        host.Pump();
        Assert.Equal(AgentLibraryTypeFilter.Plugins, vm.ContentType);
        Assert.True(vm.IsLibraryView);
        Assert.False(vm.ShowSkillSources);
        Assert.True(vm.ShowPluginMarketplaces);
        await SelectLibraryViewAsync(page, host, vm, (int)AgentLibraryView.Sources);
        Assert.True(page.FindControl<Grid>("SourcesWorkspace")!.IsEffectivelyVisible);
        Assert.False(page.FindControl<Grid>("SkillSourcesPane")!.IsEffectivelyVisible);
        Assert.True(page.FindControl<Grid>("PluginMarketplacesPane")!.IsEffectivelyVisible);
        Assert.Equal(0, backend.AvailableReads);
        Assert.Equal(0, backend.InstalledReads);
        Assert.Equal(0, backend.CheckCalls);
        Assert.False(vm.AgentPlugins.HasError);
        await SelectLibraryViewAsync(page, host, vm, (int)AgentLibraryView.Discover);
        Assert.True(page.FindControl<Grid>("LibraryWorkspace")!.IsEffectivelyVisible);
        var other = new AgentSkillsViewModel(new AgentSkillsService(backend));
        Assert.True(other.AgentPlugins.CanWork);
        other.Stop();
        Assert.False(other.AgentPlugins.CanWork);
        await other.AgentPlugins.LoadAsync();
        Assert.Empty(other.AgentPlugins.Plugins);
        Assert.False(other.AgentPlugins.IsBusy);
        return true;
    }, CancellationToken.None);

    [Theory]
    [InlineData(720)]
    [InlineData(1200)]
    public Task UnifiedWorkspaceNavigatesBetweenLibraryAndSourcesAcrossWindowSizes(int width) => _session.Dispatch<bool>(async () =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        var backend = new RecordingBackend();
        var vm = new AgentSkillsViewModel(new AgentSkillsService(backend))
        {
            ContentType = AgentLibraryTypeFilter.Skills,
        };
        vm.AgentPlugins.Stop();
        var page = new AgentSkillsPage(vm);
        using var host = new AgentSkillsSettingsTestWindow(page, width);
        var tabs = page.FindControl<TabStrip>("LibraryTabs")!;
        var empty = page.FindControl<StackPanel>("EmptyState")!;
        var library = page.FindControl<Grid>("LibraryWorkspace")!;
        var sources = page.FindControl<Grid>("SourcesWorkspace")!;
        Assert.True(library.IsEffectivelyVisible);
        Assert.False(sources.IsEffectivelyVisible);
        Assert.True(page.FindControl<ComboBox>("TypeFilter")!.IsEffectivelyVisible);
        Assert.True(page.FindControl<ComboBox>("ToolFilter")!.IsEffectivelyVisible);
        Assert.True(page.FindControl<TextBox>("LibrarySearch")!.IsEffectivelyVisible);
        Assert.True(empty.IsEffectivelyVisible);
        Assert.Equal(global::Avalonia.Layout.HorizontalAlignment.Left, empty.HorizontalAlignment);
        Assert.All(tabs.Items.Cast<TabStripItem>(), tab =>
        {
            Assert.InRange(tab.Bounds.Height, 36, 52);
        });

        await SelectLibraryViewAsync(page, host, vm, (int)AgentLibraryView.Sources);
        Assert.True(sources.IsEffectivelyVisible);
        var sourceHost = page.FindControl<ContentControl>("SourcesHost")!;
        var address = Assert.Single(sourceHost.GetVisualDescendants().OfType<TextBox>());
        Assert.True(address.IsEffectivelyVisible);
        Assert.InRange(sources.Bounds.Right, 1, page.Bounds.Width);
        await SelectLibraryViewAsync(page, host, vm, (int)AgentLibraryView.Discover);
        Assert.True(empty.IsEffectivelyVisible);

        Settings.SetList(AgentSkillsService.SourcesListKey, new List<string> { "contoso/skills" });
        vm.RefreshPreferences();
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedSkill = Assert.Single(vm.Skills);
        host.Pump();
        Assert.True(library.IsEffectivelyVisible);
        Assert.False(empty.IsEffectivelyVisible);
        var selected = Assert.Single(vm.Items);
        vm.OpenLibraryItemCommand.Execute(selected);
        host.Pump();
        Assert.True(page.FindControl<Border>("DetailsPane")!.IsEffectivelyVisible);
        vm.BackCommand.Execute(null);
        host.Pump();
        await SelectLibraryViewAsync(page, host, vm, 1);
        Assert.False(sources.IsEffectivelyVisible);
        Assert.True(empty.IsEffectivelyVisible);
        Assert.Equal(1, backend.AvailableReads);
        Assert.Equal(2, backend.InstalledReads);
        return true;
    }, CancellationToken.None);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public Task InventoryFiltersAndEnterUseLoadedResultsWithoutBackendReads(int mode) => _session.Dispatch<bool>(async () =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        Settings.SetList(AgentSkillsService.SourcesListKey, new List<string> { "contoso/skills", "other/skills" });
        var backend = new RecordingBackend();
        backend.Installed.AddRange([
            backend.Tracked(),
            backend.Tracked() with { Name = "format", Description = "Formats text", Source = "other/skills", Agents = ["claude-code"] },
            backend.Tracked() with { Name = "audit", Description = "Security review", Agents = ["claude-code"] },
        ]);
        var vm = new AgentSkillsViewModel(new AgentSkillsService(backend))
        {
            ContentType = AgentLibraryTypeFilter.Skills,
        };
        var page = new AgentSkillsPage(vm);
        page.FindControl<ComboBox>("TypeFilter")!.SelectedItem = AgentLibraryTypeFilter.Skills;
        using var host = new AgentSkillsSettingsTestWindow(page, 1100);
        await SelectModeAsync(page, host, vm, mode);
        int reads = backend.InstalledReads;
        int checks = backend.CheckCalls;
        Assert.Equal(["review", "format", "audit"], vm.Skills.Select(skill => skill.Id));
        vm.SelectedSkill = vm.Skills[0];
        var search = page.FindControl<TextBox>("LibrarySearch")!;
        host.EnterText(search, "REVIEW");
        Assert.Equal(["review", "audit"], vm.Skills.Select(skill => skill.Id));
        Assert.Equal("review", vm.SelectedSkill?.Id);
        PressEnter(host, search);
        Assert.Equal(reads, backend.InstalledReads);
        Assert.Equal(checks, backend.CheckCalls);
        var toolFilter = page.FindControl<ComboBox>("ToolFilter")!;
        toolFilter.SelectedItem = vm.ToolOptions.Single(tool => tool.AgentId == "claude-code");
        host.Pump();
        Assert.Equal(["audit"], vm.Items.Select(item => item.Skill?.Id));
        Assert.Null(vm.SelectedItem);
        search.Text = "";
        host.Pump();
        Assert.Equal(["audit", "format"], vm.Items.Select(item => item.Skill?.Id));
        Assert.Equal("2 items", vm.LibraryResultCount);
        toolFilter.SelectedIndex = 0;
        host.Pump();
        Assert.Equal(3, vm.Items.Count);
        Assert.Equal(reads, backend.InstalledReads);
        Assert.Equal(checks, backend.CheckCalls);
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task UnifiedLibraryContentTypeAndToolFiltersRecomputeRowsAndCountLocally() => _session.Dispatch<bool>(async () =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        var backend = new RecordingBackend();
        backend.Installed.AddRange([
            backend.Tracked() with { Name = "review", Agents = ["codex"] },
            backend.Tracked() with { Name = "format", Agents = ["claude-code"] },
        ]);
        var vm = CreateInstallViewModel(backend);
        vm.ContentType = AgentLibraryTypeFilter.Skills;
        vm.LibraryView = AgentLibraryView.Installed;
        await vm.RefreshCommand.ExecuteAsync(null);
        int readsAfterLoad = backend.InstalledReads;

        vm.SelectedTool = vm.ToolOptions.Single(tool => tool.AgentId == "codex");

        Assert.Equal(["review"], vm.Items.Select(item => item.Skill?.Id));
        Assert.All(vm.Items, item => Assert.Equal(AgentLibraryItemType.Skill, item.Type));
        Assert.Equal("1 item", vm.LibraryResultCount);

        vm.SelectedTool = vm.ToolOptions[0];
        Assert.Equal(["format", "review"], vm.Items.Select(item => item.Skill?.Id));
        Assert.Equal("2 items", vm.LibraryResultCount);

        vm.ContentType = AgentLibraryTypeFilter.Plugins;
        Assert.Empty(vm.Items);
        Assert.Equal("0 items", vm.LibraryResultCount);
        Assert.Equal(readsAfterLoad, backend.InstalledReads);
        Assert.Equal(0, backend.UpdateCalls);
        vm.Stop();
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task ToolFilterListsOnlyRelevantToolsAndGrowsWithInstalledSkills() => _session.Dispatch<bool>(async () =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        AgentSkillsService.TargetAgents = ["targeted-agent"];
        var backend = new RecordingBackend
        {
            AgentCatalogue =
            [
                new("codex", "Codex", ".codex/skills", @"C:\test\codex\skills", false, false),
                new("zed", "Zed", ".agents/skills", @"C:\test\zed\skills", false, true),
                new("targeted-agent", "Targeted Agent", ".targeted/skills", @"C:\test\targeted\skills", false, false),
                new("used-agent", "Used Agent", ".used/skills", @"C:\test\used\skills", false, false),
                new("obscure-agent", "Obscure Agent", ".obscure/skills", @"C:\test\obscure\skills", false, false),
            ],
        };
        backend.Installed.Add(backend.Tracked() with { Agents = ["used-agent"] });
        var vm = CreateInstallViewModel(backend);

        Assert.Same(vm.ToolOptions[0], vm.SelectedTool);
        Assert.True(vm.IsAllTools);
        Assert.Contains(vm.ToolOptions, tool => tool.AgentId == "codex" && tool.PluginProvider is not null);
        Assert.Contains(vm.ToolOptions, tool => tool.AgentId == "zed");
        Assert.Contains(vm.ToolOptions, tool => tool.AgentId == "targeted-agent");
        Assert.DoesNotContain(vm.ToolOptions, tool => tool.AgentId is "used-agent" or "obscure-agent");
        var names = vm.ToolOptions.Skip(1).Select(tool => tool.Name).ToArray();
        Assert.Equal(names.Order(StringComparer.CurrentCultureIgnoreCase), names);

        vm.LibraryView = AgentLibraryView.Installed;
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Contains(vm.ToolOptions, tool => tool.AgentId == "used-agent");
        Assert.DoesNotContain(vm.ToolOptions, tool => tool.AgentId == "obscure-agent");
        names = vm.ToolOptions.Skip(1).Select(tool => tool.Name).ToArray();
        Assert.Equal(names.Order(StringComparer.CurrentCultureIgnoreCase), names);
        vm.SelectedTool = vm.ToolOptions.Single(tool => tool.AgentId == "used-agent");
        Assert.Equal(["review"], vm.Items.Select(item => item.Skill?.Id));
        vm.Stop();
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task DiscoverBrowsesSourcesOnOpenAndFiltersLocally() => _session.Dispatch<bool>(async () =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        Settings.SetList(AgentSkillsService.SourcesListKey, new List<string> { "contoso/skills", "other/skills" });
        var backend = new RecordingBackend();
        backend.Available.Add(new("format", "Formats text", null));
        var vm = new AgentSkillsViewModel(new AgentSkillsService(backend))
        {
            ContentType = AgentLibraryTypeFilter.Skills,
        };
        var page = new AgentSkillsPage(vm);
        using var host = new AgentSkillsSettingsTestWindow(page, 1100);
        await AwaitRefreshAsync(vm);
        host.Pump();
        Assert.Equal(2, backend.AvailableReads);
        Assert.Equal(4, vm.Skills.Count);
        var input = page.FindControl<TextBox>("LibrarySearch")!;
        host.EnterText(input, "FORMAT");
        Assert.Equal(["format", "format"], vm.Items.Select(item => item.Skill?.Id));
        Assert.Equal(2, backend.AvailableReads);
        page.FindControl<ComboBox>("ToolFilter")!.SelectedItem =
            vm.ToolOptions.Single(tool => tool.AgentId == "claude-code");
        host.Pump();
        Assert.Empty(vm.Items);
        page.FindControl<ComboBox>("ToolFilter")!.SelectedIndex = 0;
        host.Pump();
        Assert.Equal(["format", "format"], vm.Items.Select(item => item.Skill?.Id));
        Assert.Equal(2, backend.AvailableReads);
        PressEnter(host, input);
        await AwaitRefreshAsync(vm);
        Assert.Equal(2, backend.AvailableReads);
        return true;
    }, CancellationToken.None);

    [Theory]
    [InlineData("npx skills add contoso/skills")]
    [InlineData("npx -y skills add https://github.com/contoso/skills --global")]
    [InlineData("github.com/contoso/skills/")]
    public Task PastedSourceIsAddedAndItsSkillsShownReadyToInstall(string pasted) => _session.Dispatch<bool>(async () =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        var backend = new RecordingBackend();
        backend.Available.Add(new("format", "Formats text", null));
        var vm = new AgentSkillsViewModel(new AgentSkillsService(backend))
        {
            ContentType = AgentLibraryTypeFilter.Skills,
        };
        var page = new AgentSkillsPage(vm);
        using var host = new AgentSkillsSettingsTestWindow(page, 1100);
        Assert.True(vm.NeedsSetup);
        Assert.True(page.FindControl<Border>("AddSourceBar")!.IsEffectivelyVisible);
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
        Assert.True(page.FindControl<Grid>("LibraryWorkspace")!.IsEffectivelyVisible);
        var installAll = page.GetVisualDescendants().OfType<Button>()
            .Single(button => Equals(button.Content, "Install all 2"));
        Assert.True(installAll.IsEffectivelyVisible);
        Assert.Equal("Install all 2", installAll.Content);

        vm.ToggleAddSourceCommand.Execute(null);
        host.Pump();
        host.EnterText(source, "https://github.com/contoso/skills");
        await vm.AddFromSourceCommand.ExecuteAsync(null);
        Assert.Single(AgentSkillsService.GetConfiguredSources());
        Assert.False(vm.HasError);
        Assert.Equal("contoso/skills is already one of your sources. It offers 2 skills.", vm.Status);
        Assert.Equal(1, backend.AvailableReads);

        vm.ToggleAddSourceCommand.Execute(null);
        host.Pump();
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
    public Task InstallAllInstallsEveryShownSkillAndReportsFailures(bool rejectOne) => _session.Dispatch<bool>(async () =>
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
        Assert.Equal(rejectOne, vm.NeedsRefresh);
        Assert.True(vm.Skills.Single(skill => skill.Id == "format").IsInstalled);
        Assert.Equal(!rejectOne, vm.Skills.Single(skill => skill.Id == "review").IsInstalled);
        Assert.Equal(!rejectOne, vm.CanChange);
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task SingleSkillInstallHonorsConfirmedTargetSelection() => _session.Dispatch<bool>(async () =>
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
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task InstallAllHonorsConfirmedTargetSelectionForEverySkill() => _session.Dispatch<bool>(async () =>
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
        return true;
    }, CancellationToken.None);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public Task CancelledOrEmptyTargetSelectionDoesNotInstall(bool installAll, bool emptySelection) => _session.Dispatch<bool>(async () =>
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
        return true;
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task PreviewExposesMarkdownFilesAndContentDerivedRiskIndicatorsBeforeInstall(bool truncated) => _session.Dispatch<bool>(async () =>
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
                IsMarkdownTruncated: truncated),
        };
        var vm = new AgentSkillsViewModel(new AgentSkillsService(backend))
        {
            ContentType = AgentLibraryTypeFilter.Skills,
        };
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedSkill = Assert.Single(vm.Skills);
        var page = new AgentSkillsPage(vm);
        using var host = new AgentSkillsSettingsTestWindow(page, 1100);
        vm.OpenLibraryItemCommand.Execute(Assert.Single(vm.Items));
        host.Pump();

        host.Click(Assert.Single(page.FindControl<Border>("DetailsPane")!.GetVisualDescendants().OfType<Button>(),
            button => Equals(button.Content, "Preview SKILL.md and files")));
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
        var details = page.FindControl<Border>("DetailsPane")!;
        Assert.Equal(preview.Markdown,
            Assert.Single(details.GetVisualDescendants().OfType<SelectableTextBlock>(), text => text.Text == preview.Markdown).Text);
        Assert.Single(details.GetVisualDescendants().OfType<Expander>()).IsExpanded = true;
        host.Pump();
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
        Assert.Equal(truncated, details.GetVisualDescendants().OfType<TextBlock>()
            .Single(text => text.Text == "Preview truncated for display.").IsEffectivelyVisible);
        Assert.Contains(details.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == "This preview comes from the source and does not show local changes." && text.IsEffectivelyVisible);
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task PreviewDoesNotFlagOrdinaryFilesWithoutToolPermissions() => _session.Dispatch<bool>(async () =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        Settings.SetList(AgentSkillsService.SourcesListKey, new List<string> { "contoso/skills" });
        var backend = new RecordingBackend
        {
            Preview = new SkillPreview("review", "Safe instructions.", ["SKILL.md", "references/guide.md"],
                null, null, [], false),
        };
        var vm = new AgentSkillsViewModel(new AgentSkillsService(backend))
        {
            ContentType = AgentLibraryTypeFilter.Skills,
        };
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedSkill = Assert.Single(vm.Skills);

        await vm.LoadPreviewCommand.ExecuteAsync(null);

        var preview = Assert.IsType<SkillPreview>(vm.SkillPreview);
        Assert.False(preview.ContainsScripts);
        Assert.False(preview.ContainsExecutables);
        Assert.False(vm.HasPreviewWarnings);
        Assert.Equal("", vm.PreviewWarnings);
        Assert.Empty(backend.InstallCalls);
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task DiscoverRowsShowInstalledAndUpdateAvailableInsteadOfInstall() => _session.Dispatch<bool>(async () =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        Settings.SetList(AgentSkillsService.SourcesListKey, new List<string> { "contoso/skills" });
        var backend = new RecordingBackend();
        backend.Installed.Add(backend.Tracked());
        backend.Installed.Add(backend.Tracked() with { Name = "format", Description = "Formats text" });
        backend.Available.Add(new("format", "Formats text", null));
        backend.Available.Add(new("new-skill", "Not installed yet", null));
        backend.UpdatableSkills = ["format"];
        var vm = new AgentSkillsViewModel(new AgentSkillsService(backend))
        {
            ContentType = AgentLibraryTypeFilter.Skills,
        };
        vm.AgentPlugins.Stop();
        var page = new AgentSkillsPage(vm);
        page.FindControl<ComboBox>("TypeFilter")!.SelectedItem = AgentLibraryTypeFilter.Skills;
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
        var list = page.FindControl<ListBox>("LibraryList")!;
        var statusTexts = list.GetVisualDescendants().OfType<TextBlock>().Where(text =>
            text.IsEffectivelyVisible && (text.Text is "Installed" or "Update available")).Select(text => text.Text);
        Assert.Equal(["Update available", "Installed"], statusTexts);
        var rowButtons = list.GetVisualDescendants().OfType<Button>()
            .Where(button => Equals(button.Content, "Install")).ToArray();
        Assert.Equal(3, rowButtons.Length);
        Assert.False(Assert.Single(rowButtons, button =>
            button.CommandParameter is AgentLibraryItem { Skill.Id: "review" }).IsEffectivelyVisible);
        Assert.False(Assert.Single(rowButtons, button =>
            button.CommandParameter is AgentLibraryItem { Skill.Id: "format" }).IsEffectivelyVisible);
        Assert.True(Assert.Single(rowButtons, button =>
            button.CommandParameter is AgentLibraryItem { Skill.Id: "new-skill" }).IsEffectivelyVisible);
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task EachDiscoverRowInstallsItsOwnSkill() => _session.Dispatch<bool>(async () =>
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
        var rows = page.FindControl<ListBox>("LibraryList")!.GetVisualDescendants().OfType<Button>()
            .Where(button => Equals(button.Content, "Install")).ToArray();
        Assert.Equal(2, rows.Length);
        Assert.All(rows, row => Assert.True(row.IsEffectivelyVisible && row.IsEnabled));
        var format = Assert.Single(rows, row => row.CommandParameter is AgentLibraryItem { Skill.Id: "format" });
        vm.SelectedSkill = vm.Skills.Single(skill => skill.Id == "review");
        Assert.Equal("Install", format.Content);
        host.Click(format);
        if (vm.InstallSkillCommand.ExecutionTask is { } installing) await installing.WaitAsync(TimeSpan.FromSeconds(30));
        host.Pump();
        Assert.Equal(["format"], backend.InstalledSkillNames);
        Assert.Equal("format", vm.SelectedSkill?.Id);
        Assert.False(vm.HasError, vm.Status);

        await SelectModeAsync(page, host, vm, 1);
        Assert.DoesNotContain(page.FindControl<ListBox>("LibraryList")!.GetVisualDescendants().OfType<Button>(),
            button => Equals(button.Content, "Install") && button.IsEffectivelyVisible);
        return true;
    }, CancellationToken.None);
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public Task DetailsShowContextualActionsAndExactSource(int mode) => _session.Dispatch<bool>(async () =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        Settings.SetList(AgentSkillsService.SourcesListKey, new List<string> { "contoso/skills" });
        AgentSkillsService.TargetAgents = ["codex", "claude-code"];
        var backend = new RecordingBackend();
        if (mode != 0) backend.Installed.Add(backend.Tracked());
        if (mode == 2) backend.UpdatableSkills = ["review"];
        var vm = CreateInstallViewModel(backend);
        var page = new AgentSkillsPage(vm);
        page.FindControl<ComboBox>("TypeFilter")!.SelectedItem = AgentLibraryTypeFilter.Skills;
        using var host = new AgentSkillsSettingsTestWindow(page, 1100);
        if (mode == 0) await vm.RefreshCommand.ExecuteAsync(null);
        else await SelectModeAsync(page, host, vm, mode);
        Assert.False(page.FindControl<Border>("DetailsPane")!.IsVisible);
        var item = Assert.Single(vm.Items);
        var skill = item.Skill!;
        vm.OpenLibraryItemCommand.Execute(item);
        host.Pump();
        Assert.True(page.FindControl<Border>("DetailsPane")!.IsEffectivelyVisible);
        var details = page.FindControl<Border>("DetailsPane")!;
        foreach (string actionName in new[] { "Install", "Uninstall", "Update" })
        {
            var buttons = details.GetVisualDescendants().OfType<Button>()
                .Where(candidate => Equals(candidate.Content, actionName)).ToArray();
            bool expectedVisible = actionName switch
            {
                "Install" => mode == 0,
                "Uninstall" => true,
                "Update" => mode == 2,
                _ => false,
            };
            if (actionName == "Uninstall")
            {
                var visibleButtons = buttons.Where(candidate => candidate.IsEffectivelyVisible).ToArray();
                var button = Assert.Single(visibleButtons);
                Assert.Equal(mode != 0, button.IsEnabled);
            }
            else
            {
                var button = Assert.Single(buttons);
                Assert.Equal(expectedVisible, button.IsEffectivelyVisible);
                if (expectedVisible) Assert.True(button.IsEnabled);
            }
        }
        Assert.Contains(details.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == "https://github.com/contoso/skills" && text.IsEffectivelyVisible);
        Assert.Equal(mode == 2, details.GetVisualDescendants().OfType<TextBlock>()
            .Single(text => text.Text == "Available revision").IsEffectivelyVisible);
        Assert.True(vm.CanOpenSource);
        if (mode == 0)
        {
            Assert.Equal("Choose the agents for this install. Current default: codex, claude-code", vm.InstallationSummary);
            await vm.InstallLibraryItemCommand.ExecuteAsync(item);
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
        Assert.False(page.FindControl<Border>("DetailsPane")!.IsVisible);
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task InstallConfirmationDefaultsToDetectedAndUniversalAgents() => _session.Dispatch<bool>(async () =>
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
    public Task EmptyStatesOfferSetupSearchDiscoverRefreshAndErrorRecovery() => _session.Dispatch<bool>(async () =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        var backend = new RecordingBackend();
        var service = new AgentSkillsService(backend);
        var vm = new AgentSkillsViewModel(service)
        {
            ContentType = AgentLibraryTypeFilter.Skills,
        };
        var page = new AgentSkillsPage(vm);
        vm.AgentPlugins.Stop();
        page.FindControl<ComboBox>("TypeFilter")!.SelectedItem = AgentLibraryTypeFilter.Skills;
        using var host = new AgentSkillsSettingsTestWindow(page, 1100);
        Assert.Equal("Add skills from a repository", vm.EmptyTitle);
        Assert.Contains("Nothing is installed until you choose Install", vm.EmptyDescription);
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
        await SelectLibraryViewAsync(page, host, vm, (int)AgentLibraryView.Discover);
        Assert.Equal(AgentLibraryView.Discover, vm.LibraryView);
        await SelectModeAsync(page, host, vm, 2);
        Assert.Equal("No skill updates found", vm.EmptyTitle);
        Assert.Contains("configured sources", vm.EmptyDescription);
        await SelectModeAsync(page, host, vm, 0);
        backend.InstalledFailure = new InvalidOperationException("inventory denied");
        await SelectModeAsync(page, host, vm, 1);
        Assert.True(vm.HasError);
        Assert.Equal("Could not load skills", vm.EmptyTitle);
        Assert.Contains("inventory denied", vm.EmptyDescription);
        var retry = page.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, vm.RefreshLabel));
        Assert.True(retry.IsEffectivelyVisible);
        Assert.False(retry.IsEnabled);
        backend.InstalledFailure = null;
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.False(vm.HasError);
        Assert.Equal("No skills installed", vm.EmptyTitle);
        return true;
    }, CancellationToken.None);

    [Theory]
    [InlineData(700, 500, false)]
    [InlineData(1000, 700, false)]
    [InlineData(1450, 900, false)]
    [InlineData(700, 500, true)]
    [InlineData(1000, 700, true)]
    [InlineData(1450, 900, true)]
    public Task DetailsUseFullWidthAndKeepActionsPinnedAcrossWindowSizes(int width, int height, bool dark) => _session.Dispatch<bool>(async () =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        Settings.SetList(AgentSkillsService.SourcesListKey, new List<string> { "contoso/skills" });
        var backend = new RecordingBackend();
        backend.Available.Clear();
        backend.Available.Add(new("review", string.Join("\n", Enumerable.Repeat("Long description with content to scroll.", 100)), null));
        backend.Available.Add(new("rust-port", "Port code to idiomatic Rust.", null));
        backend.Available.Add(new("rust-best-practices", "Review Rust idioms and safety.", null));
        backend.Installed.Add(backend.Tracked() with { Name = "rust-port" });
        backend.Installed.Add(backend.Tracked() with { Name = "rust-best-practices", Agents = ["claude-code", "codex", "cursor"] });
        backend.UpdatableSkills = ["rust-best-practices"];
        var vm = CreateInstallViewModel(backend);
        vm.UpdatesAvailable = _ => { };
        await vm.CheckForUpdatesInBackgroundAsync();
        await vm.RefreshCommand.ExecuteAsync(null);
        var page = new AgentSkillsPage(vm);
        AgentLibraryTestEvidence.AddApplicationResources(page);
        using var host = new AgentSkillsSettingsTestWindow(page, width, dark ? ThemeVariant.Dark : ThemeVariant.Light, height);
        vm.SelectedSkill = vm.Skills.Single(skill => skill.Id == "review");
        host.Pump();
        string size = $"{width}x{height}-{(dark ? "dark" : "light")}";
        AgentLibraryTestEvidence.Capture(host, "skills-list-" + size);
        var details = page.FindControl<Border>("DetailsPane")!;
        Assert.False(details.IsEffectivelyVisible);
        vm.OpenLibraryItemCommand.Execute(Assert.Single(vm.Items, item => item.Skill?.Id == "review"));
        host.Pump();
        Assert.True(details.IsEffectivelyVisible);
        bool sideBySide = width >= 1000;
        Assert.Equal(sideBySide, page.FindControl<Grid>("LibraryListPane")!.IsEffectivelyVisible);
        Assert.InRange(details.Bounds.Width, sideBySide ? 380 : page.Bounds.Width - 40,
            sideBySide ? 420 : page.Bounds.Width);
        Assert.Equal(double.PositiveInfinity, details.MaxHeight);
        var install = details.GetVisualDescendants().OfType<Button>()
            .Single(button => Equals(button.Content, "Install"));
        var scroll = Assert.Single(details.GetVisualDescendants().OfType<ScrollViewer>());
        Assert.DoesNotContain(scroll, install.GetVisualAncestors());
        Point before = install.TranslatePoint(default, page)!.Value;
        Assert.True(install.IsEffectivelyVisible && install.IsEnabled);
        Assert.InRange(before.Y + install.Bounds.Height, 1, height);
        Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
        AgentLibraryTestEvidence.Capture(host, "skills-details-" + size);
        scroll.Offset = new Vector(0, scroll.Extent.Height);
        host.Pump();
        Assert.Equal(before, install.TranslatePoint(default, page)!.Value);
        host.Window.Width = width == 700 ? 1450 : 700;
        host.Window.Height = width == 700 ? 900 : 500;
        host.Pump();
        Assert.InRange(details.Bounds.Width, host.Window.Width >= 1000 ? 380 : page.Bounds.Width - 40,
            host.Window.Width >= 1000 ? 420 : page.Bounds.Width);
        Point resized = install.TranslatePoint(default, page)!.Value;
        Assert.InRange(resized.Y + install.Bounds.Height, 1, host.Window.Height);
        host.Click(page.FindControl<Button>("BackButton")!);
        Assert.False(vm.ShowDetails);
        Assert.Equal("review", vm.SelectedSkill?.Id);
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task DetailsBackRestoresSelectedLibraryItemQueryToolFilterScrollAndFocus() => _session.Dispatch<bool>(async () =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        var backend = new RecordingBackend();
        for (int i = 0; i < 40; i++)
            backend.Installed.Add(backend.Tracked() with { Name = $"review-{i:D2}" });
        backend.Installed.Add(backend.Tracked() with { Name = "review-20", Source = "other/skills" });
        var vm = new AgentSkillsViewModel(new AgentSkillsService(backend))
        {
            ContentType = AgentLibraryTypeFilter.Skills,
            LibraryView = AgentLibraryView.Installed,
        };
        await vm.RefreshCommand.ExecuteAsync(null);
        var page = new AgentSkillsPage(vm);
        using var host = new AgentSkillsSettingsTestWindow(page, 1000, height: 700);
        vm.Query = "review";
        vm.SelectedTool = vm.ToolOptions.Single(tool => tool.AgentId == "codex");
        var selected = Assert.Single(vm.Items,
            item => item.Skill?.Id == "review-20" && item.Source == "contoso/skills");
        vm.SelectedItem = selected;
        var list = page.FindControl<ListBox>("LibraryList")!;
        list.ScrollIntoView(selected);
        host.Pump();
        var scroll = list.GetVisualDescendants().OfType<ScrollViewer>().First();
        Vector offset = scroll.Offset;
        int readsBeforeDetails = backend.InstalledReads;
        Assert.True(offset.Y > 0);
        Assert.False(vm.ShowDetails);
        vm.OpenLibraryItemCommand.Execute(selected);
        host.Pump();
        Assert.True(page.FindControl<Button>("BackButton")!.IsFocused);
        host.Click(page.FindControl<Button>("BackButton")!);
        host.Pump();
        Assert.Equal("review", vm.Query);
        Assert.Same(selected, vm.SelectedItem);
        Assert.Equal(("review-20", "contoso/skills"), (vm.SelectedItem!.Skill!.Id, vm.SelectedItem.Source));
        Assert.InRange(Math.Abs(scroll.Offset.Y - offset.Y), 0, 1);
        Assert.True(list.ContainerFromItem(vm.SelectedItem)!.GetVisualDescendants().OfType<Button>().First().IsFocused);
        Assert.Equal(readsBeforeDetails, backend.InstalledReads);
        Assert.Empty(backend.InstallCalls);
        return true;
    }, CancellationToken.None);

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public Task RowUpdateTargetsItsParameterAndClearsKnownUpdateState(int mode) => _session.Dispatch<bool>(async () =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        Settings.SetList(AgentSkillsService.SourcesListKey, new List<string> { "contoso/skills" });
        var backend = new RecordingBackend();
        backend.Installed.AddRange([backend.Tracked(), backend.Tracked() with { Name = "format" }]);
        backend.Available.Add(new("format", "Formats text", null));
        var vm = CreateInstallViewModel(backend);
        vm.UpdatesAvailable = _ => { };
        await vm.CheckForUpdatesInBackgroundAsync();
        vm.Mode = mode;
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedSkill = vm.Skills.Single(skill => skill.Id == "review");
        var target = vm.Skills.Single(skill => skill.Id == "format");
        await vm.UpdateSkillCommand.ExecuteAsync(target);
        Assert.False(vm.HasError, vm.Status);
        var applied = Assert.Single(backend.LastUpdates!);
        Assert.Equal(("format", "contoso/skills", "2222222bbbb"),
            (applied.Name, applied.Source, applied.LatestHash));
        Assert.True(vm.Skills.Single(skill => skill.Id == "review").HasUpdate);
        if (mode == 2)
        {
            Assert.Equal(["review"], vm.Skills.Select(skill => skill.Id));
            Assert.Equal(1, vm.UpdateCount);
        }
        else
        {
            var updated = vm.Skills.Single(skill => skill.Id == "format");
            Assert.True(updated.IsInstalled);
            Assert.False(updated.HasUpdate);
            Assert.False(updated.CanInstallFromDiscover);
        }
        Assert.Equal(1, vm.UpdateCount);
        Assert.False(vm.NeedsRefresh);
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task KnownSkillUpdatesAreDetectedBeforeAnExplicitUpdateMutatesTheSkill() => _session.Dispatch<bool>(async () =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        Settings.SetList(AgentSkillsService.SourcesListKey, new List<string> { "contoso/skills" });
        var backend = new RecordingBackend();
        backend.Installed.Add(backend.Tracked());
        var vm = CreateInstallViewModel(backend);

        await vm.CheckForUpdatesInBackgroundAsync();

        Assert.Equal(1, vm.UpdateCount);
        Assert.Equal(1, backend.CheckCalls);
        Assert.Equal(0, backend.UpdateCalls);
        Assert.Equal("1111111aaaa", backend.Installed.Single().Hash);

        vm.LibraryView = AgentLibraryView.Updates;
        await vm.RefreshCommand.ExecuteAsync(null);
        var knownUpdate = Assert.Single(vm.Skills);
        Assert.Equal("review", knownUpdate.Id);
        Assert.True(knownUpdate.HasUpdate);
        Assert.Equal("2222222", knownUpdate.NewVersion);
        Assert.Equal(0, backend.UpdateCalls);

        await vm.UpdateSkillCommand.ExecuteAsync(knownUpdate);

        Assert.Equal(1, backend.UpdateCalls);
        var applied = Assert.Single(backend.LastUpdates!);
        Assert.Equal(("review", "contoso/skills", "2222222bbbb"),
            (applied.Name, applied.Source, applied.LatestHash));
        Assert.Equal("2222222bbbb", backend.Installed.Single().Hash);
        Assert.Empty(vm.Skills);
        Assert.Equal(0, vm.UpdateCount);
        Assert.False(vm.HasError, vm.Status);
        vm.Stop();
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task PendingTargetSelectionRejectsDuplicateInstallAndKeepsOriginalRowIdentity() => _session.Dispatch<bool>(async () =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        Settings.SetList(AgentSkillsService.SourcesListKey, new List<string> { "contoso/skills" });
        var backend = new RecordingBackend();
        backend.Available.Add(new("format", "Formats text", null));
        var selection = new TaskCompletionSource<AgentSkillsInstallSelection?>(TaskCreationOptions.RunContinuationsAsynchronously);
        int prompts = 0;
        var vm = new AgentSkillsViewModel(new AgentSkillsService(backend), selectInstallTargets: (_, _, _) =>
        {
            prompts++;
            return selection.Task;
        });
        await vm.RefreshCommand.ExecuteAsync(null);
        var target = vm.Skills.Single(skill => skill.Id == "format");
        Task installing = vm.InstallSkillCommand.ExecuteAsync(target);
        try
        {
            Assert.True(vm.IsChoosingTargets);
            Assert.False(vm.CanNavigate);
            vm.SelectedSkill = vm.Skills.Single(skill => skill.Id == "review");
            await vm.InstallCommand.ExecuteAsync(null);
            await vm.InstallAllCommand.ExecuteAsync(null);
            Assert.Equal(1, prompts);
            Assert.Empty(backend.InstallCalls);
        }
        finally { selection.TrySetResult(new(["claude-code"], false)); }
        await installing.WaitAsync(TimeSpan.FromSeconds(30));
        var call = Assert.Single(backend.InstallCalls);
        Assert.Equal("format", call.Skill);
        Assert.Equal(["claude-code"], call.Agents);
        Assert.True(vm.Skills.Single(skill => skill.Id == "format").IsInstalled);
        Assert.False(vm.Skills.Single(skill => skill.Id == "review").IsInstalled);
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task LocalFilterAndSourceRefreshNeverQueryPublicCatalogAndExplicitSearchHonorsGates() => _session.Dispatch<bool>(async () =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        Settings.SetList(AgentSkillsService.SourcesListKey, new List<string> { "contoso/skills" });
        var backend = new RecordingBackend();
        var registry = new RecordingRegistry();
        var service = new AgentSkillsService(backend, registry) { SkillsShDiscoveryEnabled = true };
        var vm = new AgentSkillsViewModel(service);
        await vm.LoadCurrentModeAsync();
        vm.Query = "review";
        vm.SourceFilter = "contoso/skills";
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Empty(registry.Queries);
        Assert.Equal(2, backend.AvailableReads);
        vm.SearchPublicCatalog = true;
        vm.CatalogQuery = " a ";
        await vm.SearchCatalogCommand.ExecuteAsync(null);
        Assert.True(vm.HasError);
        Assert.Empty(registry.Queries);
        vm.CatalogQuery = "review";
        await vm.SearchCatalogCommand.ExecuteAsync(null);
        Assert.False(vm.HasError, vm.Status);
        Assert.Equal(["review"], registry.Queries);
        Assert.Equal("public-review", Assert.Single(vm.Skills).Id);
        Assert.Equal(2, backend.AvailableReads);
        vm.Query = "missing";
        Assert.Empty(vm.Skills);
        Assert.Equal(["review"], registry.Queries);
        service.SkillsShDiscoveryEnabled = false;
        await vm.SearchCatalogCommand.ExecuteAsync(null);
        Assert.True(vm.HasError);
        Assert.Equal(["review"], registry.Queries);
        Assert.Empty(backend.InstallCalls);
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task FailedSourceRefreshRetainsCatalogAndBlocksMutationUntilSuccessfulReload() => _session.Dispatch<bool>(async () =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        Settings.SetList(AgentSkillsService.SourcesListKey, new List<string> { "contoso/skills" });
        var backend = new RecordingBackend();
        var vm = CreateInstallViewModel(backend);
        await vm.RefreshCommand.ExecuteAsync(null);
        var previous = Assert.Single(vm.Skills);
        vm.SelectedSkill = previous;
        backend.BeforeList = _ => throw new SkillsException("fixture source unavailable");
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.True(vm.HasError);
        Assert.Equal(["review"], vm.Skills.Select(skill => skill.Id));
        Assert.Contains("Could not refresh 1 sources", vm.Status);
        Assert.False(vm.CanChange);
        await vm.InstallSkillCommand.ExecuteAsync(previous);
        Assert.Empty(backend.InstallCalls);
        backend.BeforeList = null;
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.False(vm.HasError, vm.Status);
        Assert.True(vm.CanInstall);
        Assert.Equal("review", vm.SelectedSkill?.Id);
        return true;
    }, CancellationToken.None);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public Task LatePreviewCannotPopulateAfterSelectionBackSectionChangeOrStop(int navigation) => _session.Dispatch<bool>(async () =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        Settings.SetList(AgentSkillsService.SourcesListKey, new List<string> { "contoso/skills" });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var backend = new RecordingBackend();
        backend.Available.Add(new("format", "Formats text", null));
        backend.BeforePreview = _ =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(30), CancellationToken.None)) throw new TimeoutException("Preview was not released");
        };
        var vm = CreateInstallViewModel(backend);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.OpenDetailsCommand.Execute(vm.Skills.Single(skill => skill.Id == "review"));
        Task preview = vm.LoadPreviewCommand.ExecuteAsync(null);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(vm.IsLoadingPreview);
            switch (navigation)
            {
                case 0: vm.SelectedSkill = vm.Skills.Single(skill => skill.Id == "format"); break;
                case 1: vm.BackCommand.Execute(null); break;
                case 2: vm.Section = AgentExtensionSection.Plugins; break;
                default: vm.Stop(); break;
            }
        }
        finally { release.Set(); }
        await preview.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Null(vm.SkillPreview);
        Assert.False(vm.HasPreviewError);
        Assert.False(vm.IsLoadingPreview);
        Assert.Empty(backend.InstallCalls);
        Assert.Equal(1, backend.PreviewCalls);
        return true;
    }, CancellationToken.None);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task CancellationOfUncooperativeInstallRequiresReconciliationWithoutFalseCompletion(bool bulk) => _session.Dispatch<bool>(async () =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        Settings.SetList(AgentSkillsService.SourcesListKey, new List<string> { "contoso/skills" });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var backend = new RecordingBackend();
        backend.Available.Add(new("format", "Formats text", null));
        backend.BeforeInstall = _ =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(30), CancellationToken.None)) throw new TimeoutException("Install was not released");
        };
        var vm = CreateInstallViewModel(backend);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedSkill = vm.Skills[0];
        Task installing = (bulk ? vm.InstallAllCommand : vm.InstallCommand).ExecuteAsync(null);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.False(vm.CanNavigate);
            vm.CancelCommand.Execute(null);
        }
        finally { release.Set(); }
        await installing.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(vm.NeedsRefresh);
        Assert.Contains("Cancelled", vm.Status);
        Assert.DoesNotContain("Completed", vm.Status);
        Assert.False(vm.CanChange);
        Assert.Single(backend.InstallCalls);
        backend.BeforeInstall = null;
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.False(vm.NeedsRefresh);
        Assert.True(vm.Skills.Single(skill => skill.Id == "review").IsInstalled);
        Assert.False(vm.Skills.Single(skill => skill.Id == "format").IsInstalled);
        return true;
    }, CancellationToken.None);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task StatusResourcesResolveForSelectedAndUnselectedRowsInBothThemes(bool dark) => _session.Dispatch<bool>(async () =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        Settings.SetList(AgentSkillsService.SourcesListKey, new List<string> { "contoso/skills" });
        var backend = new RecordingBackend();
        backend.Installed.Add(backend.Tracked());
        var vm = CreateInstallViewModel(backend);
        await vm.RefreshCommand.ExecuteAsync(null);
        var page = new AgentSkillsPage(vm);
        AgentLibraryTestEvidence.AddApplicationResources(page);
        using var host = new AgentSkillsSettingsTestWindow(page, 1000, dark ? ThemeVariant.Dark : ThemeVariant.Light, 700);
        foreach (bool selected in new[] { false, true })
        {
            vm.SelectedItem = selected ? Assert.Single(vm.Items) : null;
            host.Pump();
            var text = page.FindControl<ListBox>("LibraryList")!.GetVisualDescendants().OfType<TextBlock>()
                .Single(item => item.Text == "Installed");
            var border = text.GetVisualAncestors().OfType<Border>().First(item => item.Classes.Contains("item-status"));
            Assert.True(text.TryFindResource("StatusSuccessForeground", text.ActualThemeVariant, out var foreground));
            Assert.True(text.TryFindResource("StatusSuccessBackground", text.ActualThemeVariant, out var background));
            Assert.Equal(Assert.IsAssignableFrom<ISolidColorBrush>(foreground).Color,
                Assert.IsAssignableFrom<ISolidColorBrush>(text.Foreground).Color);
            Assert.Equal(Assert.IsAssignableFrom<ISolidColorBrush>(background).Color,
                Assert.IsAssignableFrom<ISolidColorBrush>(border.Background).Color);
            if (dark) Assert.NotEqual(Colors.Black, Assert.IsAssignableFrom<ISolidColorBrush>(text.Foreground).Color);
        }
        foreach (string key in new[] { "StatusWarningForeground", "WarningBannerBackground", "StatusErrorForeground", "StatusErrorBackground" })
        {
            Assert.True(page.TryFindResource(key, page.ActualThemeVariant, out var resource), key);
            Assert.NotEqual(Colors.Transparent, Assert.IsAssignableFrom<ISolidColorBrush>(resource).Color);
        }
        return true;
    }, CancellationToken.None);

    [Theory]
    [InlineData(700, false)]
    [InlineData(1200, false)]
    [InlineData(700, true)]
    [InlineData(1200, true)]
    public Task LibraryRowsKeepMetadataTogetherAndActionsHorizontal(int width, bool dark) => _session.Dispatch<bool>(async () =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        var backend = new RecordingBackend();
        backend.Installed.Add(backend.Tracked());
        var vm = CreateInstallViewModel(backend);
        vm.LibraryView = AgentLibraryView.Installed;
        await vm.RefreshCommand.ExecuteAsync(null);
        var page = new AgentSkillsPage(vm);
        AgentLibraryTestEvidence.AddApplicationResources(page);
        using var host = new AgentSkillsSettingsTestWindow(page, width, dark ? ThemeVariant.Dark : ThemeVariant.Light, 700);
        var provider = vm.AgentPlugins.Providers.Single(item => item.Name == "GitHub Copilot");
        var enabled = new AgentPluginItem("diagram-viewer@awesome-copilot", "", "1", true, true);
        var disabled = new AgentPluginItem("browser-use", "", "1", true, false) { CanManage = false };
        AgentLibraryItem[] items =
        [
            new("enabled", enabled.Name, "", AgentLibraryItemType.Plugin, "awesome-copilot", enabled.Status,
                provider.Name, true, false, false, true, null, enabled, provider),
            new("disabled", disabled.Name, " ", AgentLibraryItemType.Plugin, provider.Name, disabled.Status,
                provider.Name, true, false, false, false, null, disabled, provider),
            new("long", new string('n', 200), "", AgentLibraryItemType.Plugin, new string('s', 200), enabled.Status,
                provider.Name, true, false, false, true, null, enabled, provider),
        ];
        vm.Items.Clear();
        foreach (var item in items) vm.Items.Add(item);
        host.Pump();
        var list = page.FindControl<ListBox>("LibraryList")!;

        foreach (var item in items)
        {
            var container = Assert.IsType<ListBoxItem>(list.ContainerFromItem(item));
            var row = Assert.Single(container.GetVisualDescendants().OfType<Grid>(), grid => grid.Classes.Contains("library-row"));
            var metadata = Assert.Single(row.GetVisualDescendants().OfType<Grid>(), grid => grid.Classes.Contains("row-metadata"));
            var title = Assert.Single(row.GetVisualDescendants().OfType<Button>(), button => button.Classes.Contains("item-title"));
            var description = Assert.Single(row.GetVisualDescendants().OfType<TextBlock>(), text => text.Classes.Contains("description"));
            Assert.False(description.IsEffectivelyVisible);
            Assert.InRange(container.Bounds.Height, 50, 84);
            Assert.True(metadata.TranslatePoint(default, row)!.Value.Y >= title.TranslatePoint(default, row)!.Value.Y + title.Bounds.Height);
            Assert.Equal(title.TranslatePoint(default, row)!.Value.X, metadata.TranslatePoint(default, row)!.Value.X);
            Assert.InRange(title.Bounds.Width, 1, row.Bounds.Width);
            var source = Assert.Single(metadata.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == item.SourceSummary);
            Assert.InRange(source.TranslatePoint(default, metadata)!.Value.X + source.Bounds.Width, 1, metadata.Bounds.Width + 1);
            var status = Assert.Single(metadata.GetVisualDescendants().OfType<Border>(),
                border => border.IsEffectivelyVisible && border.Child is TextBlock text && text.Text == item.Status);
            Assert.Equal(item.Plugin!.Enabled, status.Classes.Contains("installed"));
            var actions = Assert.Single(row.GetVisualDescendants().OfType<WrapPanel>(), panel => panel.Classes.Contains("row-actions"));
            var buttons = actions.Children.OfType<Button>().Where(button => button.IsEffectivelyVisible).ToArray();
            if (item == items[1])
            {
                Assert.Empty(buttons);
                Assert.Equal(provider.Name, source.Text);
            }
            else
            {
                Assert.Equal(["Update", "Disable"], buttons.Select(button => button.Content));
                Assert.Equal(buttons[0].Bounds.Y, buttons[1].Bounds.Y);
                Assert.True(buttons[1].Bounds.X >= buttons[0].Bounds.Right + 6);
                Assert.InRange(actions.TranslatePoint(default, row)!.Value.X + actions.Bounds.Width, 1, row.Bounds.Width + 1);
                Assert.Same(vm.UpdateLibraryItemCommand, buttons[0].Command);
                Assert.Same(vm.ToggleLibraryItemCommand, buttons[1].Command);
                Assert.All(buttons, button =>
                {
                    Assert.Same(item, button.CommandParameter);
                    Assert.True(button.Focus());
                });
            }
        }
        AgentLibraryTestEvidence.Capture(host, $"compact-library-rows-{width}-{(dark ? "dark" : "light")}");
        vm.Stop();
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task InstallAllTargetsOnlyVisibleUninstalledSkillsFromOneSource() => _session.Dispatch<bool>(async () =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        Settings.SetList(AgentSkillsService.SourcesListKey, new List<string> { "contoso/skills", "other/skills" });
        var backend = new RecordingBackend();
        backend.Available.AddRange([
            new("format-one", "Formats text", null), new("format-two", "Formats code", null),
            new("hidden", "Not part of this filter", null),
        ]);
        backend.Installed.Add(backend.Tracked());
        var vm = CreateInstallViewModel(backend);
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.False(vm.ShowInstallAll);
        vm.SourceFilter = "contoso/skills";
        vm.Query = "format";
        Assert.Equal("Install all 2", vm.InstallAllLabel);
        Assert.True(vm.CanInstallAll);
        await vm.InstallAllCommand.ExecuteAsync(null);
        Assert.False(vm.HasError, vm.Status);
        Assert.Equal(["format-one", "format-two"], backend.InstallCalls.Select(call => call.Skill));
        Assert.All(backend.InstallCalls, call => Assert.Equal(["codex", "shared-agent"], call.Agents));
        Assert.All(vm.Skills, skill =>
        {
            Assert.Equal("contoso/skills", skill.SourceName);
            Assert.True(skill.IsInstalled);
            Assert.False(skill.CanInstallFromDiscover);
        });
        Assert.False(vm.ShowInstallAll);
        Assert.Equal("contoso/skills", vm.SourceFilter);
        Assert.Equal("format", vm.Query);
        return true;
    }, CancellationToken.None);

    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public Task MutationRemovingDetailItemReturnsToNextRowOrEmptySearch(int mode, bool hasNext) => _session.Dispatch<bool>(async () =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        Settings.SetList(AgentSkillsService.SourcesListKey, new List<string> { "contoso/skills" });
        var backend = new RecordingBackend();
        backend.Installed.Add(backend.Tracked());
        if (hasNext) backend.Installed.Add(backend.Tracked() with { Name = "format" });
        backend.UpdatableSkills = hasNext ? ["review", "format"] : ["review"];
        var vm = CreateInstallViewModel(backend);
        var page = new AgentSkillsPage(vm);
        using var host = new AgentSkillsSettingsTestWindow(page, 1000, height: 700);
        await AwaitRefreshAsync(vm);
        await SelectModeAsync(page, host, vm, mode);
        vm.SelectedTool = vm.ToolOptions.Single(tool => tool.AgentId == "codex");
        vm.Query = "Reviews";
        vm.OpenLibraryItemCommand.Execute(Assert.Single(vm.Items, item => item.Skill?.Id == "review"));
        host.Pump();
        await (mode == 1 ? vm.RemoveCommand : vm.UpdateCommand).ExecuteAsync(null);
        host.Pump();
        Assert.False(vm.ShowDetails);
        Assert.False(vm.HasError, vm.Status);
        Assert.Equal("Reviews", vm.Query);
        Assert.Equal("codex", vm.SelectedTool?.AgentId);
        Assert.Equal(hasNext ? 1 : 0, mode == 1 ? vm.InstalledCount : vm.UpdateCount);
        if (hasNext)
        {
            Assert.Null(vm.SelectedItem);
            Assert.Equal("format", Assert.Single(vm.Items).Skill?.Id);
            Assert.False(page.FindControl<Border>("DetailsPane")!.IsEffectivelyVisible);
        }
        else
        {
            Assert.Empty(vm.Items);
            Assert.Null(vm.SelectedItem);
            Assert.True(page.FindControl<TextBox>("LibrarySearch")!.IsFocused);
        }
        return true;
    }, CancellationToken.None);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public Task InstalledAndUpdatesKeepLocalReloadAfterPublicSearch(int mode) => _session.Dispatch<bool>(async () =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        Settings.SetList(AgentSkillsService.SourcesListKey, new List<string> { "contoso/skills" });
        var backend = new RecordingBackend();
        backend.Installed.Add(backend.Tracked());
        var registry = new RecordingRegistry();
        var service = new AgentSkillsService(backend, registry) { SkillsShDiscoveryEnabled = true };
        var vm = new AgentSkillsViewModel(service)
        {
            ContentType = AgentLibraryTypeFilter.Skills,
            SearchPublicCatalog = true,
            CatalogQuery = "review",
        };
        await vm.SearchCatalogCommand.ExecuteAsync(null);
        var page = new AgentSkillsPage(vm);
        vm.AgentPlugins.Stop();
        using var host = new AgentSkillsSettingsTestWindow(page, 1000, height: 700);
        await SelectModeAsync(page, host, vm, mode);
        var reload = page.GetVisualDescendants().OfType<Button>()
            .Single(button => Equals(button.Content, vm.RefreshLabel));
        Assert.True(reload.IsEffectivelyVisible);
        host.Click(reload);
        await AwaitRefreshAsync(vm);
        Assert.Equal("review", Assert.Single(vm.Skills).Id);
        Assert.Equal(["review"], registry.Queries);
        Assert.Equal(0, backend.AvailableReads);
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task ExplicitPublicSearchAnnotatesInstalledSourceIdentityAndFriendlyAgents() => _session.Dispatch<bool>(async () =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        var backend = new RecordingBackend();
        backend.Installed.Add(backend.Tracked() with { Agents = ["claude-code", "codex", "cursor"] });
        var registry = new RecordingRegistry
        {
            Results = [
                new("review", SkillSourceLocator.Parse("contoso/skills")!),
                new("review", SkillSourceLocator.Parse("other/skills")!),
            ],
        };
        var service = new AgentSkillsService(backend, registry) { SkillsShDiscoveryEnabled = true };
        var vm = new AgentSkillsViewModel(service) { SearchPublicCatalog = true, CatalogQuery = "review" };
        await vm.SearchCatalogCommand.ExecuteAsync(null);
        var installed = vm.Skills.Single(skill => skill.SourceName == "contoso/skills");
        var fresh = vm.Skills.Single(skill => skill.SourceName == "other/skills");
        Assert.True(installed.IsInstalled);
        Assert.False(installed.CanInstallFromDiscover);
        Assert.Equal("Claude Code +2", installed.AgentSummary);
        Assert.Equal("Claude Code, Codex, Cursor", installed.AgentNames);
        Assert.False(fresh.IsInstalled);
        Assert.True(fresh.CanInstallFromDiscover);
        Assert.Empty(backend.InstallCalls);
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task LatePublicSearchCannotReplaceTheInstalledView() => _session.Dispatch<bool>(async () =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        var backend = new RecordingBackend();
        backend.Installed.Add(backend.Tracked());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var registry = new RecordingRegistry
        {
            BeforeSearch = () =>
            {
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("Registry search was not released");
            },
        };
        var service = new AgentSkillsService(backend, registry) { SkillsShDiscoveryEnabled = true };
        var vm = new AgentSkillsViewModel(service) { SearchPublicCatalog = true, CatalogQuery = "review" };
        Task search = vm.SearchCatalogCommand.ExecuteAsync(null);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            vm.LibraryView = AgentLibraryView.Installed;
        }
        finally { release.Set(); }
        await search.WaitAsync(TimeSpan.FromSeconds(30));
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(AgentLibraryView.Installed, vm.LibraryView);
        Assert.Equal("review", Assert.Single(vm.Skills).Id);
        Assert.DoesNotContain(vm.Skills, skill => skill.Id == "public-review");
        Assert.False(vm.HasError, vm.Status);
        Assert.Equal(["review"], registry.Queries);
        Assert.Empty(backend.InstallCalls);
        return true;
    }, CancellationToken.None);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task BackgroundResultsAndErrorsLeaveForegroundStateUntouched(bool fails) => _session.Dispatch<bool>(async () =>
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
        Assert.Equal(fails ? "Could not check skill updates: background denied" : "1 skill update", vm.BackgroundStatus);
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
    public Task ForegroundCancelsBackgroundAndSerializesUntilGateReleased(bool cancelForeground) => _session.Dispatch<bool>(async () =>
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
            Assert.False(vm.CanNavigate);
            Assert.Equal(4, page.FindControl<TabStrip>("LibraryTabs")!.Items.Count);
            Assert.False(page.GetVisualDescendants().OfType<Button>()
                .Single(button => Equals(button.Content, vm.RefreshLabel)).IsEnabled);
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
    public Task StopCancelsBackgroundAndDiscardsUncooperativeLateResults() => _session.Dispatch<bool>(async () =>
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

    [Fact]
    public Task WarmStartupAndModeNavigationReuseMetadataUntilExplicitRefresh() => _session.Dispatch<bool>(async () =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        Settings.SetList(AgentSkillsService.SourcesListKey, new List<string> { "contoso/skills" });
        var backend = new RecordingBackend();
        backend.Installed.Add(backend.Tracked());
        string cache = Path.Combine(fixture.Root, "cache");
        var first = new AgentSkillsViewModel(new AgentSkillsService(backend, cacheDirectory: cache))
        {
            ContentType = AgentLibraryTypeFilter.Skills,
        };
        await first.LoadCurrentModeAsync();
        Assert.Equal(1, backend.AvailableReads);
        first.Stop();
        var vm = new AgentSkillsViewModel(new AgentSkillsService(backend, cacheDirectory: cache))
        {
            ContentType = AgentLibraryTypeFilter.Skills,
        };
        await vm.LoadCurrentModeAsync();
        Assert.Equal("review", Assert.Single(vm.Skills).Id);
        Assert.True(vm.ShowCachedSourceNotice);
        Assert.Equal(1, backend.AvailableReads);
        vm.Mode = 2;
        await vm.LoadCurrentModeAsync();
        Assert.Equal(1, backend.CheckCalls);
        vm.Mode = 0;
        await vm.LoadCurrentModeAsync();
        vm.Mode = 2;
        await vm.LoadCurrentModeAsync();
        Assert.Equal(1, backend.CheckCalls);
        Assert.Equal(1, backend.AvailableReads);
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(2, backend.CheckCalls);
        vm.Mode = 0;
        await vm.LoadCurrentModeAsync();
        backend.Available.Add(new("new-skill", "New after refresh", null));
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(2, backend.AvailableReads);
        Assert.Equal(["review", "new-skill"], vm.Skills.Select(skill => skill.Id));
        var page = new AgentSkillsPage(vm);
        AgentLibraryTestEvidence.AddApplicationResources(page);
        using var host = new AgentSkillsSettingsTestWindow(page, 1000, height: 700);
        await AwaitRefreshAsync(vm);
        host.Pump();
        Assert.Equal(2, backend.AvailableReads);
        Assert.Contains(page.GetVisualDescendants().OfType<TextBlock>(), text =>
            text.IsEffectivelyVisible && text.Text == "Saved source listings. Use Refresh sources to fetch changes.");
        AgentLibraryTestEvidence.Capture(host, "skills-cached-sources");
        vm.Stop();
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task UpdateAllUsesVisibleSnapshotAndBlocksDuplicateConfirmation() => _session.Dispatch<bool>(async () =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        Settings.SetList(AgentSkillsService.SourcesListKey, new List<string> { "contoso/skills", "fabrikam/skills" });
        var backend = BulkUpdateBackend();
        backend.Installed.Add(backend.Tracked() with { Name = "hidden-agent", Agents = ["claude-code"] });
        backend.Installed.Add(backend.Tracked() with { Name = "hidden-source", Source = "fabrikam/skills" });
        backend.Installed.Add(backend.Tracked() with { Name = "unrelated", Description = "Does not match" });
        var confirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        List<string> prompts = [];
        var vm = new AgentSkillsViewModel(new AgentSkillsService(backend), confirm: message =>
        {
            prompts.Add(message);
            return confirmation.Task;
        })
        { Mode = 2 };
        await vm.LoadCurrentModeAsync();
        vm.Query = "Reviews";
        vm.SourceFilter = "contoso/skills";
        vm.AgentFilter = "codex";
        Assert.Equal(["review", "format", "third"], vm.Skills.Select(skill => skill.Id));
        Assert.Equal("Update all 3", vm.UpdateAllLabel);
        Task updating = vm.UpdateAllCommand.ExecuteAsync(null);
        try
        {
            Assert.True(vm.IsChoosingTargets);
            Assert.False(vm.CanNavigate);
            Assert.False(vm.CanUpdateAll);
            vm.Query = "unrelated";
            await vm.UpdateAllCommand.ExecuteAsync(null);
            Assert.Single(prompts);
            Assert.Empty(backend.AppliedUpdateHistory);
            Assert.Contains("3 skills", prompts[0]);
            Assert.Contains("Review (contoso/skills)", prompts[0]);
            Assert.Contains("Format (contoso/skills)", prompts[0]);
            Assert.DoesNotContain("hidden", prompts[0]);
        }
        finally { confirmation.TrySetResult(true); }
        await updating.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(["review", "format", "third"], backend.AppliedUpdateHistory.Select(update => update.Name));
        Assert.All(backend.AppliedUpdateHistory, update =>
            Assert.Equal(("contoso/skills", "1111111aaaa", "2222222bbbb"),
                (update.Source, update.CurrentHash, update.LatestHash)));
        Assert.Equal(3, vm.UpdateCount);
        Assert.Equal(6, vm.InstalledCount);
        Assert.Equal("Updated 3 skills", vm.Status);
        Assert.False(vm.HasError);
        Assert.False(vm.NeedsRefresh);
        vm.Query = "";
        vm.SourceFilter = vm.Sources[0];
        vm.AgentFilter = vm.Agents[0];
        Assert.Equal(["hidden-agent", "hidden-source", "unrelated"], vm.Skills.Select(skill => skill.Id));
        vm.Stop();
        return true;
    }, CancellationToken.None);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public Task UpdateAllPartialFailureRetainsOnlyFailedKnownUpdatesAndConcreteVersions(int mode) =>
        _session.Dispatch<bool>(async () =>
        {
            using var fixture = new AgentSkillsSettingsTestFixture();
            Settings.SetList(AgentSkillsService.SourcesListKey, new List<string> { "contoso/skills" });
            var backend = BulkUpdateBackend();
            backend.RejectedUpdates.Add("format");
            var vm = new AgentSkillsViewModel(new AgentSkillsService(backend), confirm: _ => Task.FromResult(true))
            {
                ContentType = AgentLibraryTypeFilter.Skills,
            };
            vm.AgentPlugins.Stop();
            vm.UpdatesAvailable = _ => { };
            await vm.CheckForUpdatesInBackgroundAsync();
            vm.Mode = mode;
            await vm.LoadCurrentModeAsync();
            await vm.UpdateAllCommand.ExecuteAsync(null);
            Assert.Equal(["review", "format", "third"], backend.AppliedUpdateHistory.Select(update => update.Name));
            Assert.True(vm.HasError);
            Assert.True(vm.NeedsRefresh);
            Assert.False(vm.CanUpdateAll);
            Assert.Contains("2 skills updated; 1 failed", vm.Status);
            Assert.Contains("Format: fixture update rejected", vm.Status);
            Assert.Equal(1, vm.UpdateCount);
            Assert.Equal(3, vm.InstalledCount);
            Assert.True(vm.Skills.Single(skill => skill.Id == "format").HasUpdate);
            if (mode == 2) Assert.Equal("format", Assert.Single(vm.Skills).Id);
            else
            {
                foreach (string id in new[] { "review", "third" })
                {
                    var item = vm.Skills.Single(skill => skill.Id == id);
                    Assert.False(item.HasUpdate);
                    Assert.True(item.IsInstalled);
                    Assert.Equal("2222222", item.Version);
                }
            }
            vm.Stop();
            return true;
        }, CancellationToken.None);

    [Fact]
    public Task UpdateAllCancellationPreservesFailedAndUnprocessedBadgesWithoutFalseCompletion() =>
        _session.Dispatch<bool>(async () =>
        {
            using var fixture = new AgentSkillsSettingsTestFixture();
            Settings.SetList(AgentSkillsService.SourcesListKey, new List<string> { "contoso/skills" });
            var backend = BulkUpdateBackend();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new ManualResetEventSlim();
            backend.BeforeUpdate = (updates, _) =>
            {
                if (updates[0].Name != "format") return;
                started.TrySetResult();
                Assert.True(release.Wait(TimeSpan.FromSeconds(30), CancellationToken.None));
            };
            var vm = new AgentSkillsViewModel(new AgentSkillsService(backend), confirm: _ => Task.FromResult(true)) { Mode = 2 };
            await vm.LoadCurrentModeAsync();
            Task updating = vm.UpdateAllCommand.ExecuteAsync(null);
            try
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
                Assert.Equal(["review", "format"], backend.AppliedUpdateHistory.Select(update => update.Name));
                Assert.Equal(2, vm.UpdateCount);
                vm.CancelCommand.Execute(null);
            }
            finally { release.Set(); }
            await updating.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(["review", "format"], backend.AppliedUpdateHistory.Select(update => update.Name));
            Assert.Equal(["format", "third"], vm.Skills.Select(skill => skill.Id));
            Assert.All(vm.Skills, skill => Assert.True(skill.HasUpdate));
            Assert.True(vm.NeedsRefresh);
            Assert.False(vm.CanUpdateAll);
            Assert.Contains("Cancelled. Reload", vm.Status);
            Assert.DoesNotContain("Updated 3", vm.Status);
            vm.Stop();
            return true;
        }, CancellationToken.None);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task UpdateAllDeclineOrStopDuringConfirmationNeverMutates(bool stop) => _session.Dispatch<bool>(async () =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        Settings.SetList(AgentSkillsService.SourcesListKey, new List<string> { "contoso/skills" });
        var backend = BulkUpdateBackend();
        var choice = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = new AgentSkillsViewModel(new AgentSkillsService(backend), confirm: _ => choice.Task) { Mode = 2 };
        await vm.LoadCurrentModeAsync();
        Task update = vm.UpdateAllCommand.ExecuteAsync(null);
        if (stop) vm.Stop();
        choice.SetResult(stop);
        await update;
        Assert.Empty(backend.AppliedUpdateHistory);
        Assert.Equal(3, vm.UpdateCount);
        Assert.All(vm.Skills, item => Assert.True(item.HasUpdate));
        Assert.False(vm.IsChoosingTargets);
        Assert.False(vm.NeedsRefresh);
        vm.Stop();
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task AttachedUpdateAllButtonTracksFilteredCountAndClearsSuccessfulBadges() => _session.Dispatch<bool>(async () =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        Settings.SetList(AgentSkillsService.SourcesListKey, new List<string> { "contoso/skills" });
        var backend = BulkUpdateBackend();
        var vm = new AgentSkillsViewModel(new AgentSkillsService(backend), confirm: _ => Task.FromResult(true))
        {
            ContentType = AgentLibraryTypeFilter.Skills,
        };
        vm.AgentPlugins.Stop();
        var page = new AgentSkillsPage(vm);
        page.FindControl<ComboBox>("TypeFilter")!.SelectedItem = AgentLibraryTypeFilter.Skills;
        AgentLibraryTestEvidence.AddApplicationResources(page);
        using var host = new AgentSkillsSettingsTestWindow(page, 1000, height: 700);
        await AwaitRefreshAsync(vm);
        await SelectModeAsync(page, host, vm, 2);
        var button = page.GetVisualDescendants().OfType<Button>()
            .Single(candidate => Equals(candidate.Content, "Update all 3"));
        Assert.True(button.IsEffectivelyVisible);
        Assert.True(button.IsEnabled);
        Assert.Equal("Update all 3", button.Content);
        AgentLibraryTestEvidence.Capture(host, "skills-update-all");
        vm.Query = "format";
        host.Pump();
        Assert.Equal("Update all 1", button.Content);
        host.Click(button);
        if (vm.UpdateAllCommand.ExecutionTask is { } task) await task;
        host.Pump();
        Assert.Equal("format", Assert.Single(backend.AppliedUpdateHistory).Name);
        Assert.Equal(2, vm.UpdateCount);
        Assert.Empty(vm.Skills);
        Assert.False(button.IsEffectivelyVisible);
        vm.Query = "";
        host.Pump();
        Assert.Equal("Update all 2", button.Content);
        Assert.Equal(["review", "third"], vm.Skills.Select(skill => skill.Id));
        vm.Stop();
        return true;
    }, CancellationToken.None);

    private static RecordingBackend BulkUpdateBackend()
    {
        var backend = new RecordingBackend();
        backend.Installed.AddRange([backend.Tracked(), backend.Tracked() with { Name = "format" }, backend.Tracked() with { Name = "third" }]);
        backend.Available.AddRange([new("format", "Reviews formatting", null), new("third", "Reviews third", null)]);
        return backend;
    }

    private static async Task SelectModeAsync(AgentSkillsPage page, AgentSkillsSettingsTestWindow host,
        AgentSkillsViewModel vm, int mode)
    {
        var tabs = page.FindControl<TabStrip>("LibraryTabs")!;
        host.Click((TabStripItem)tabs.Items[mode]!);
        await AwaitRefreshAsync(vm);
        host.Pump();
        Assert.Equal((AgentLibraryView)mode, vm.LibraryView);
        Assert.Equal(mode, tabs.SelectedIndex);
    }

    private static Task SelectLibraryViewAsync(AgentSkillsPage page, AgentSkillsSettingsTestWindow host,
        AgentSkillsViewModel vm, int view) => SelectModeAsync(page, host, vm, view);

    private static async Task AwaitRefreshAsync(AgentSkillsViewModel vm)
    {
        if (vm.RefreshCommand.ExecutionTask is { } task) await task.WaitAsync(TimeSpan.FromSeconds(30));
        // Automatic cached loads deliberately bypass the explicit-refresh command.
        await AgentSkillsSettingsTestFixture.When(vm, () => !vm.IsBusy).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(vm.IsBusy);
    }

    private static AgentSkillsViewModel CreateInstallViewModel(RecordingBackend backend) =>
        new(new AgentSkillsService(backend), selectInstallTargets: SelectInstallDefaults)
        {
            ContentType = AgentLibraryTypeFilter.Skills,
        };

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
        public IReadOnlyList<AgentInfo> AgentCatalogue { get; init; } =
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
        public List<SkillUpdate> AppliedUpdateHistory { get; } = [];
        public HashSet<string> RejectedUpdates { get; } = [];
        public Action<IReadOnlyList<SkillUpdate>, CancellationToken>? BeforeUpdate { get; set; }
        public Exception? InstalledFailure { get; set; }
        public Action<CancellationToken>? BeforeCheck { get; set; }
        public bool FailInstall { get; init; }
        public Action<CancellationToken>? BeforeList { get; set; }
        public Action<CancellationToken>? BeforePreview { get; set; }
        public Action<CancellationToken>? BeforeInstall { get; set; }
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
            BeforePreview?.Invoke(cancellationToken);
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
            BeforeInstall?.Invoke(cancellationToken);
            progress?.Report("Installing review");
            bool fail = FailInstall || RejectedInstalls.Contains(skill);
            if (!fail)
            {
                Installed.Add(Tracked() with { Name = skill, Agents = agents?.ToArray() ?? ["codex"] });
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
            AppliedUpdateHistory.AddRange(updates);
            BeforeUpdate?.Invoke(updates, cancellationToken);
            foreach (var update in updates)
            {
                if (RejectedUpdates.Contains(update.Name)) continue;
                int index = Installed.FindIndex(skill => skill.Name == update.Name);
                if (index >= 0) Installed[index] = Installed[index] with { Hash = update.LatestHash };
            }
            return new(updates.Select(update => new SkillUpdateOutcome(update.Name, SkillScope.Global,
                RejectedUpdates.Contains(update.Name) ? SkillOperationStatus.Failed : SkillOperationStatus.Succeeded,
                RejectedUpdates.Contains(update.Name) ? "fixture update rejected" : null)).ToArray());
        }
        public SkillRemoveResult Remove(string skill, IProgress<string>? progress, CancellationToken cancellationToken)
        {
            Removed.Add(skill);
            Installed.RemoveAll(item => item.Name == skill);
            return new([new(skill, SkillOperationStatus.Succeeded)]);
        }
        public NotionStatus GetNotionStatus(CancellationToken cancellationToken) => new(NotionCliState.NotInstalled, null, null, null);
        public NotionSignIn BeginNotionSignIn(CancellationToken cancellationToken) => throw new NotSupportedException();
        public void CompleteNotionSignIn(CancellationToken cancellationToken) => throw new NotSupportedException();
        public void SignOutOfNotion(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingRegistry : ISkillsShClient
    {
        public List<string> Queries { get; } = [];
        public IReadOnlyList<SkillsShResult> Results { get; init; } =
            [new("public-review", SkillSourceLocator.Parse("fabrikam/skills")!)];
        public Action? BeforeSearch { get; init; }
        public IReadOnlyList<SkillsShResult> Search(string query)
        {
            Queries.Add(query);
            BeforeSearch?.Invoke();
            return Results;
        }
    }
}
