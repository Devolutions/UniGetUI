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
        Assert.Equal(1, backend.AvailableReads);
        Assert.False(vm.NeedsSetup);
        Assert.Single(page.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Sources");
        Assert.Equal("", input.Text);
        host.Click(Assert.Single(control.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, "Remove")));
        Assert.Empty(AgentSkillsService.GetConfiguredSources());
        Assert.True(vm.NeedsSetup);
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
        var tabs = page.FindControl<TabStrip>("ModeTabs")!;
        Assert.Equal("Skill views", AutomationProperties.GetName(tabs));
        Assert.Equal(["Discover", "Installed", "Updates"], tabs.Items.Cast<TabStripItem>().Select(item => item.Content));
        Assert.All(tabs.Items.Cast<TabStripItem>(), item =>
        {
            Assert.True(item.IsEffectivelyVisible);
            Assert.True(item.Bounds.Width > 0);
        });
        Assert.Equal(0, backend.InstalledReads);
        await SelectModeAsync(page, host, vm, 1);
        Assert.Equal("review", Assert.Single(vm.Skills).Id);
        Assert.Equal(1, backend.InstalledReads);
        Assert.Equal("Installed (1)", ((TabStripItem)tabs.Items[1]!).Content);
        await SelectModeAsync(page, host, vm, 2);
        Assert.Equal("2222222", Assert.Single(vm.Skills).NewVersion);
        Assert.Equal(1, backend.CheckCalls);
        Assert.Equal("Updates (1)", ((TabStripItem)tabs.Items[2]!).Content);
        await vm.LoadCurrentModeAsync();
        Assert.Equal(1, backend.CheckCalls);
        await SelectModeAsync(page, host, vm, 0);
        Assert.Empty(vm.Skills);
        Assert.Equal(0, backend.AvailableReads);
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

        host.Click(page.FindControl<Button>("AddFirstSourceButton")!);
        host.Pump();
        Assert.True(sources.IsEffectivelyVisible);
        Assert.False(empty.IsEffectivelyVisible);
        Assert.False(results.IsEffectivelyVisible);
        Assert.False(toolbar.IsEffectivelyVisible);
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
        Assert.Equal(1, backend.InstalledReads);
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
    public Task DiscoverEnterSearchesAndSourceFilterDoesNotReadBackend() => _session.Dispatch(async () =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        Settings.SetList(AgentSkillsService.SourcesListKey, new List<string> { "contoso/skills", "other/skills" });
        var backend = new RecordingBackend();
        var vm = new AgentSkillsViewModel(new AgentSkillsService(backend));
        var page = new AgentSkillsPage(vm);
        using var host = new AgentSkillsSettingsTestWindow(page, 1100);
        var input = page.FindControl<TextBox>("SkillSearch")!;
        host.EnterText(input, "review");
        Assert.Equal(0, backend.AvailableReads);
        PressEnter(host, input);
        await AwaitRefreshAsync(vm);
        host.Pump();
        Assert.Equal(2, backend.AvailableReads);
        Assert.Equal(2, vm.Skills.Count);
        page.FindControl<ComboBox>("SourceFilter")!.SelectedItem = "other/skills";
        host.Pump();
        Assert.Equal("other/skills", Assert.Single(vm.Skills).SourceName);
        Assert.Equal(2, backend.AvailableReads);
        Assert.False(page.FindControl<ComboBox>("AgentFilter")!.IsVisible);
        return true;
    }, CancellationToken.None);

    [Theory]
    [InlineData(0, "InstallButton")]
    [InlineData(1, "RemoveButton")]
    [InlineData(2, "UpdateButton")]
    public Task DetailsShowOnlyContextualActionAndExactSource(int mode, string action) => _session.Dispatch(async () =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        Settings.SetList(AgentSkillsService.SourcesListKey, new List<string> { "contoso/skills" });
        AgentSkillsService.TargetAgents = ["codex", "claude-code"];
        var backend = new RecordingBackend();
        backend.Installed.Add(backend.Tracked());
        var vm = new AgentSkillsViewModel(new AgentSkillsService(backend));
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
        Assert.Equal("Install for your user account using automatic agent detection and the shared skills folder.",
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
    public Task AutomaticInstallUsesConfiguredSourceWithoutExplicitAgentTargets() => _session.Dispatch(async () =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        Settings.SetList(AgentSkillsService.SourcesListKey, new List<string> { "contoso/skills" });
        var backend = new RecordingBackend();
        var vm = new AgentSkillsViewModel(new AgentSkillsService(backend));
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedSkill = Assert.Single(vm.Skills);
        await vm.InstallCommand.ExecuteAsync(null);
        Assert.Equal("https://github.com/contoso/skills", backend.LastInstallSource);
        Assert.Null(backend.LastTargets);
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
        Assert.Equal("Choose where to find skills", vm.EmptyTitle);
        Assert.Contains("Add a repository or skills index", vm.EmptyDescription);
        host.Click(page.FindControl<Button>("AddFirstSourceButton")!);
        Assert.True(vm.ShowSources);
        Assert.IsType<AgentSkillsSourcesControl>(page.FindControl<ContentControl>("SourcesHost")!.Content);
        Settings.SetList(AgentSkillsService.SourcesListKey, new List<string> { "contoso/skills" });
        vm.RefreshPreferences();
        Assert.Equal("Find a skill for your agents", vm.EmptyTitle);
        vm.Query = "nothing matches";
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("No matching skills", vm.EmptyTitle);
        Assert.Contains("Try another search", vm.EmptyDescription);
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
        var vm = new AgentSkillsViewModel(new AgentSkillsService(backend));
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedSkill = Assert.Single(vm.Skills);
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
        Assert.Equal(skills, vm.Skills);
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
            token.ThrowIfCancellationRequested();
        };
        var vm = new AgentSkillsViewModel(new AgentSkillsService(backend));
        var page = new AgentSkillsPage(vm);
        using var host = new AgentSkillsSettingsTestWindow(page, 1100);
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
        public string? LastInstallSource { get; private set; }
        public string? LastInstallSkill { get; private set; }
        public IReadOnlyList<string>? LastTargets { get; private set; }
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
        public IReadOnlyList<AgentInfo> GetAgents() => [];
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
            return [new("review", "Reviews code", null)];
        }
        public SkillInstallResult Install(string source, string skill, IReadOnlyList<string>? agents, IProgress<string>? progress, CancellationToken cancellationToken)
        {
            LastInstallSource = source;
            LastInstallSkill = skill;
            LastTargets = agents;
            progress?.Report("Installing review");
            if (!FailInstall) Installed.Add(Tracked());
            return new([new() { Name = skill, Status = FailInstall ? SkillOperationStatus.Failed : SkillOperationStatus.Succeeded, Error = FailInstall ? "install rejected" : null }]);
        }
        public SkillUpdateCheckResult CheckForUpdates(IReadOnlyList<string>? skills, IProgress<string>? progress, CancellationToken cancellationToken)
        {
            CheckCalls++;
            BeforeCheck?.Invoke(cancellationToken);
            return new(Installed.Select(skill => new SkillUpdate
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
