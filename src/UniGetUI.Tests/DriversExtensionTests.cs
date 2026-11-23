using Avalonia.Headless;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.ViewModels;
using UniGetUI.Avalonia.ViewModels.Pages;
using UniGetUI.Avalonia.Views;
using UniGetUI.Avalonia.Views.Pages.SettingsPages;
using UniGetUI.Core.Data;
using UniGetUI.Extensions.Drivers;
using UniGetUI.Interface;
using CoreSettings = UniGetUI.Core.SettingsEngine.Settings;

namespace UniGetUI.Tests;

public sealed class DriversExtensionTests : IDisposable
{
    private const string FirstId = "8c3e5f0a-6b1d-4e2f-9a7b-1c2d3e4f5a6b";
    private const string SecondId = "1d2e3f4a-5b6c-4d7e-8f90-a1b2c3d4e5f6";

    private readonly HeadlessUnitTestSession _session =
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(DriversExtensionTests).Assembly);
    private readonly string _testRoot = Path.Combine(Path.GetTempPath(), nameof(DriversExtensionTests), Guid.NewGuid().ToString("N"));

    public DriversExtensionTests()
    {
        CoreData.TEST_DataDirectoryOverride = Path.Combine(_testRoot, "Data");
        Directory.CreateDirectory(CoreData.UniGetUIUserConfigurationDirectory);
        CoreSettings.ResetSettings();
    }

    public void Dispose()
    {
        CoreSettings.ResetSettings();
        CoreData.TEST_DataDirectoryOverride = null;
        try { Directory.Delete(_testRoot, recursive: true); }
        catch (IOException) { }
    }

    private sealed class FakeRunner : IDriverScriptRunner
    {
        public Queue<Func<string, bool, ScriptOutput>> Responses { get; } = new();
        public List<(string Script, bool Elevated)> Calls { get; } = [];

        public Task<ScriptOutput> RunAsync(string script, bool elevated, CancellationToken cancellationToken)
        {
            Calls.Add((script, elevated));
            return Task.FromResult(Responses.Dequeue()(script, elevated));
        }

        public void Respond(string json) => Responses.Enqueue((_, _) => new ScriptOutput(0, "UNIGETUI-DRIVERS:" + json, ""));

        public void Fail(DriversException exception) => Responses.Enqueue((_, _) => throw exception);

        public void RespondRaw(string output) => Responses.Enqueue((_, _) => new ScriptOutput(1, output, ""));
    }

    private DriverScanCache NewCache() => new(Path.Combine(_testRoot, "cache", "drivers.json"));

    private static string Update(string id, string title, int rebootBehavior = 0) =>
        $$"""{"id":"{{id}}","revision":1,"title":"{{title}}","description":"","driverClass":"Display","manufacturer":"Contoso","model":"Contoso GPU","provider":"Contoso","hardwareId":"PCI\\VEN_1234","driverDate":"2025-01-02","size":2048,"rebootBehavior":{{rebootBehavior}},"supportUrl":"","eulaAccepted":true,"deviceName":"Contoso Graphics","installedVersion":"1.0.0.0","installedDate":"2023-05-06"}""";

    private static string Scan(params string[] updates) => $$"""{"updates":[{{string.Join(',', updates)}}]}""";

    [Fact]
    public void TheExtensionIsWindowsOnlyAndOffByDefault()
    {
        var extension = AppExtensions.Drivers;

        Assert.Equal("drivers", extension.Id);
        Assert.Equal(PageType.Drivers, extension.Page);
        Assert.Equal(typeof(DriversSettings), extension.SettingsPage);
        Assert.False(extension.IsEnabled);
        Assert.Equal(OperatingSystem.IsWindows(), AppExtensions.All.Contains(extension));
        Assert.Equal(OperatingSystem.IsWindows(), IpcAppPages.SupportedPages.Contains("drivers"));
        Assert.Equal("drivers", IpcAppPages.ToPageName(nameof(PageType.Drivers)));
    }

    [WindowsFact]
    public Task TheSidebarShowsThePageOnlyWhileEnabled() => _session.Dispatch(() =>
    {
        var sidebar = new SidebarViewModel();
        Assert.DoesNotContain(PageType.Drivers, sidebar.NavigationPages);

        CoreSettings.Set(CoreSettings.K.EnableDrivers, true);
        sidebar.SetExtensionEnabled(PageType.Drivers, true);
        Assert.Equal([PageType.Drivers], sidebar.MorePages);
        sidebar.PinPageToSidebar(PageType.Drivers);
        Assert.Contains(PageType.Drivers, sidebar.NavigationPages);
        PageType[] order = [.. sidebar.NavigationPages.Reverse()];
        sidebar.ReorderNavigationPages(order);

        sidebar.DriversBadgeCount = 3;
        CoreSettings.Set(CoreSettings.K.EnableDrivers, false);
        sidebar.SetExtensionEnabled(PageType.Drivers, false);
        Assert.DoesNotContain(PageType.Drivers, sidebar.NavigationPages);
        Assert.Equal(0, sidebar.DriversBadgeCount);

        CoreSettings.Set(CoreSettings.K.EnableDrivers, true);
        sidebar.SetExtensionEnabled(PageType.Drivers, true);
        Assert.Equal(order, sidebar.NavigationPages);
    }, CancellationToken.None);

    [WindowsFact]
    public Task ScanListsUpdatesAndCountsThemForTheBadge() => _session.Dispatch<bool>(async () =>
    {
        var runner = new FakeRunner();
        runner.Respond(Scan(Update(FirstId, "Contoso - Display - 2.0", rebootBehavior: 1), Update(SecondId, "Contoso - Display - 3.0")));
        var vm = new DriversViewModel(new DriverUpdateService(runner));

        await vm.CheckForUpdatesAsync();

        Assert.Equal(2, vm.UpdateCount);
        Assert.Equal(2, vm.SelectedCount);
        Assert.False(vm.ShowEmptyState);
        Assert.False(vm.IsBusy);
        DriverUpdateItemViewModel first = vm.Updates.Single(item => item.Id == FirstId);
        Assert.Equal("Contoso Graphics", first.DeviceText);
        Assert.True(first.MayRequireRestart);
        Assert.Contains("1.0.0.0", first.InstalledText);
        Assert.False(Assert.Single(runner.Calls).Elevated);
        Assert.True(vm.InstallSelectedCommand.CanExecute(null));
        return true;
    }, CancellationToken.None);

    [WindowsFact]
    public Task AnEmptyScanShowsTheUpToDateState() => _session.Dispatch<bool>(async () =>
    {
        var runner = new FakeRunner();
        runner.Respond(Scan());
        var vm = new DriversViewModel(new DriverUpdateService(runner));

        await vm.CheckForUpdatesAsync();

        Assert.True(vm.ShowEmptyState);
        Assert.Equal(0, vm.UpdateCount);
        Assert.False(vm.InstallSelectedCommand.CanExecute(null));
        return true;
    }, CancellationToken.None);

    [WindowsFact]
    public Task AFailedScanExplainsWhy() => _session.Dispatch<bool>(async () =>
    {
        var runner = new FakeRunner();
        runner.Respond("""{"error":"WSUS says no","hResult":-2145124306}""");
        var vm = new DriversViewModel(new DriverUpdateService(runner));

        await vm.CheckForUpdatesAsync();

        Assert.True(vm.HasError);
        Assert.True(vm.ErrorNotice.IsOpen);
        Assert.Contains("organization", vm.ErrorText);
        Assert.False(vm.ShowEmptyState);
        return true;
    }, CancellationToken.None);

    [WindowsFact]
    public Task InstallingSelectedDriversElevatesAndReportsEachResult() => _session.Dispatch<bool>(async () =>
    {
        var runner = new FakeRunner();
        runner.Respond(Scan(Update(FirstId, "First"), Update(SecondId, "Second")));
        runner.Respond($$"""{"results":[{"id":"{{FirstId}}","resultCode":2,"hResult":0,"rebootRequired":true},{"id":"{{SecondId}}","resultCode":4,"hResult":-2145099769,"rebootRequired":false}],"rebootRequired":true}""");
        var vm = new DriversViewModel(new DriverUpdateService(runner));
        await vm.CheckForUpdatesAsync();

        await vm.InstallSelectedCommand.ExecuteAsync(null);

        (string script, bool elevated) = runner.Calls[1];
        Assert.True(elevated);
        Assert.Contains(FirstId, script);
        Assert.Contains(SecondId, script);
        DriverUpdateItemViewModel first = vm.Updates.Single(item => item.Id == FirstId);
        DriverUpdateItemViewModel second = vm.Updates.Single(item => item.Id == SecondId);
        Assert.Equal(DriverItemState.RestartRequired, first.State);
        Assert.False(first.CanInstall);
        Assert.Equal(DriverItemState.Failed, second.State);
        Assert.Contains("0x80246007", second.StateText);
        Assert.True(second.CanInstall);
        Assert.True(vm.RestartRequired);
        Assert.True(vm.RestartNotice.IsOpen);
        Assert.Equal(1, vm.UpdateCount);
        return true;
    }, CancellationToken.None);

    [WindowsFact]
    public Task OnlyTheChosenDriverIsInstalled() => _session.Dispatch<bool>(async () =>
    {
        var runner = new FakeRunner();
        runner.Respond(Scan(Update(FirstId, "First"), Update(SecondId, "Second")));
        runner.Respond($$"""{"results":[{"id":"{{SecondId}}","resultCode":2,"hResult":0,"rebootRequired":false}],"rebootRequired":false}""");
        var vm = new DriversViewModel(new DriverUpdateService(runner));
        await vm.CheckForUpdatesAsync();
        vm.Updates.Single(item => item.Id == FirstId).IsSelected = false;

        Assert.Equal(1, vm.SelectedCount);
        await vm.InstallSelectedCommand.ExecuteAsync(null);

        Assert.DoesNotContain(FirstId, runner.Calls[1].Script);
        Assert.Equal(DriverItemState.Available, vm.Updates.Single(item => item.Id == FirstId).State);
        Assert.Equal(DriverItemState.Installed, vm.Updates.Single(item => item.Id == SecondId).State);
        Assert.False(vm.RestartRequired);
        return true;
    }, CancellationToken.None);

    [WindowsFact]
    public Task ACancelledAdministratorPromptLeavesTheDriversInstallable() => _session.Dispatch<bool>(async () =>
    {
        var runner = new FakeRunner();
        runner.Respond(Scan(Update(FirstId, "First")));
        runner.Fail(new DriversException(DriversErrorKind.ElevationCancelled, "cancelled"));
        var vm = new DriversViewModel(new DriverUpdateService(runner));
        await vm.CheckForUpdatesAsync();

        await vm.InstallOneCommand.ExecuteAsync(vm.Updates[0]);

        Assert.True(vm.Updates[0].CanInstall);
        Assert.Contains("cancelled", vm.ErrorText);
        Assert.False(vm.IsBusy);
        return true;
    }, CancellationToken.None);

    [WindowsFact]
    public async Task ElevationIsRefusedWhenProhibited()
    {
        var runner = new PowerShellDriverScriptRunner(() => new DriversElevation(false, true, @"C:\elevator.exe", []));

        var ex = await Assert.ThrowsAsync<DriversException>(() => runner.RunAsync("exit", elevated: true, CancellationToken.None));

        Assert.Equal(DriversErrorKind.ElevationProhibited, ex.Kind);
    }

    [WindowsFact]
    public Task BackgroundChecksAreThrottled() => _session.Dispatch<bool>(async () =>
    {
        var now = new DateTimeOffset(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);
        var runner = new FakeRunner();
        runner.Respond(Scan(Update(FirstId, "First")));
        runner.Respond(Scan());
        var vm = new DriversViewModel(new DriverUpdateService(runner), NewCache(), () => now);

        await vm.CheckForUpdatesAsync();
        await vm.CheckForUpdatesAsync();
        Assert.Single(runner.Calls);

        now += DriversViewModel.BackgroundCheckInterval + TimeSpan.FromMinutes(1);
        await vm.CheckForUpdatesAsync();
        Assert.Equal(2, runner.Calls.Count);
        Assert.True(vm.ShowEmptyState);
        return true;
    }, CancellationToken.None);

    [WindowsFact]
    public Task AFreshCachedScanIsShownWithoutAskingWindowsUpdate() => _session.Dispatch<bool>(async () =>
    {
        var now = new DateTimeOffset(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);
        var seed = new FakeRunner();
        seed.Respond(Scan(Update(FirstId, "First")));
        await new DriversViewModel(new DriverUpdateService(seed), NewCache(), () => now).CheckForUpdatesAsync();

        var runner = new FakeRunner();
        var vm = new DriversViewModel(new DriverUpdateService(runner), NewCache(), () => now.AddHours(1));
        await vm.CheckForUpdatesAsync();

        Assert.Empty(runner.Calls);
        Assert.True(vm.HasScanned);
        Assert.Equal(1, vm.UpdateCount);
        Assert.Equal(FirstId, Assert.Single(vm.Updates).Id);
        return true;
    }, CancellationToken.None);

    [WindowsFact]
    public Task ARescanKeepsInstallResultsAndSelections() => _session.Dispatch<bool>(async () =>
    {
        const string ThirdId = "0f1e2d3c-4b5a-4968-8776-655443322110";
        var runner = new FakeRunner();
        runner.Respond(Scan(Update(FirstId, "First"), Update(SecondId, "Second"), Update(ThirdId, "Third")));
        runner.Respond($$"""{"results":[{"id":"{{FirstId}}","resultCode":2,"hResult":0,"rebootRequired":true}],"rebootRequired":true}""");
        runner.Respond(Scan(Update(SecondId, "Second"), Update(ThirdId, "Third")));
        var vm = new DriversViewModel(new DriverUpdateService(runner), NewCache());
        await vm.CheckForUpdatesAsync();
        vm.Updates.Single(item => item.Id == ThirdId).IsSelected = false;

        await vm.InstallOneCommand.ExecuteAsync(vm.Updates.Single(item => item.Id == FirstId));
        await vm.ScanCommand.ExecuteAsync(null);

        Assert.Equal(DriverItemState.RestartRequired, vm.Updates.Single(item => item.Id == FirstId).State);
        Assert.False(vm.Updates.Single(item => item.Id == ThirdId).IsSelected);
        Assert.True(vm.Updates.Single(item => item.Id == SecondId).IsSelected);
        Assert.Equal(2, vm.UpdateCount);
        Assert.True(vm.RestartRequired);
        return true;
    }, CancellationToken.None);

    [WindowsFact]
    public Task InstalledDriversAreLeftOutOfTheCache() => _session.Dispatch<bool>(async () =>
    {
        var runner = new FakeRunner();
        runner.Respond(Scan(Update(FirstId, "First"), Update(SecondId, "Second")));
        runner.Respond($$"""{"results":[{"id":"{{FirstId}}","resultCode":2,"hResult":0,"rebootRequired":false}],"rebootRequired":false}""");
        DriverScanCache cache = NewCache();
        var vm = new DriversViewModel(new DriverUpdateService(runner), cache);
        await vm.CheckForUpdatesAsync();

        await vm.InstallOneCommand.ExecuteAsync(vm.Updates.Single(item => item.Id == FirstId));

        Assert.Equal(SecondId, Assert.Single(cache.Load()!.Updates).Id);
        return true;
    }, CancellationToken.None);

    [WindowsFact]
    public Task AnUnreadableInstallResultChecksWindowsUpdateAgain() => _session.Dispatch<bool>(async () =>
    {
        var runner = new FakeRunner();
        runner.Respond(Scan(Update(FirstId, "First"), Update(SecondId, "Second")));
        runner.RespondRaw("");
        runner.Respond(Scan(Update(SecondId, "Second")));
        var vm = new DriversViewModel(new DriverUpdateService(runner), NewCache());
        await vm.CheckForUpdatesAsync();

        await vm.InstallSelectedCommand.ExecuteAsync(null);

        Assert.Equal(3, runner.Calls.Count);
        Assert.False(runner.Calls[2].Elevated);
        DriverUpdateItemViewModel first = vm.Updates.Single(item => item.Id == FirstId);
        DriverUpdateItemViewModel second = vm.Updates.Single(item => item.Id == SecondId);
        Assert.Equal(DriverItemState.Installed, first.State);
        Assert.False(first.CanInstall);
        Assert.Equal(DriverItemState.Available, second.State);
        Assert.True(second.CanInstall);
        Assert.True(second.IsSelected);
        Assert.True(vm.InstallSelectedCommand.CanExecute(null));
        Assert.Contains("checked Windows Update again", vm.ErrorText);
        Assert.False(vm.IsBusy);
        return true;
    }, CancellationToken.None);

    [WindowsFact]
    public Task DriversStayInstallableWhenTheRecheckAlsoFails() => _session.Dispatch<bool>(async () =>
    {
        var runner = new FakeRunner();
        runner.Respond(Scan(Update(FirstId, "First")));
        runner.RespondRaw("");
        runner.Fail(new DriversException(DriversErrorKind.Offline, "offline", unchecked((int)0x8024402C)));
        var vm = new DriversViewModel(new DriverUpdateService(runner), NewCache());
        await vm.CheckForUpdatesAsync();

        await vm.InstallSelectedCommand.ExecuteAsync(null);

        DriverUpdateItemViewModel first = Assert.Single(vm.Updates);
        Assert.Equal(DriverItemState.Unknown, first.State);
        Assert.True(first.CanInstall);
        Assert.True(first.IsSelected);
        Assert.Equal(1, vm.UpdateCount);
        Assert.True(vm.InstallSelectedCommand.CanExecute(null));
        Assert.True(vm.HasError);
        return true;
    }, CancellationToken.None);

    [WindowsFact]
    public Task AFailedScanIsRetriedOnlyAfterABackOff() => _session.Dispatch<bool>(async () =>
    {
        var now = new DateTimeOffset(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);
        var runner = new FakeRunner();
        runner.Respond("""{"error":"blocked","hResult":-2145124306}""");
        runner.Respond(Scan());
        var vm = new DriversViewModel(new DriverUpdateService(runner), NewCache(), () => now);

        await vm.CheckForUpdatesAsync();
        Assert.True(vm.HasError);
        await vm.CheckForUpdatesAsync();
        now += TimeSpan.FromMinutes(30);
        await vm.CheckForUpdatesAsync();
        Assert.Single(runner.Calls);

        now += DriversViewModel.FailedCheckRetryInterval;
        await vm.CheckForUpdatesAsync();
        Assert.Equal(2, runner.Calls.Count);
        Assert.False(vm.HasError);
        return true;
    }, CancellationToken.None);

    [WindowsFact]
    public Task ACachedScanFromTheFutureIsTreatedAsExpired() => _session.Dispatch<bool>(async () =>
    {
        var now = new DateTimeOffset(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);
        NewCache().Save(new DriverScanSnapshot([new DriverUpdate { Id = FirstId, Title = "First" }], now.AddDays(1)));
        var runner = new FakeRunner();
        runner.Respond(Scan());
        var vm = new DriversViewModel(new DriverUpdateService(runner), NewCache(), () => now);

        await vm.CheckForUpdatesAsync();

        Assert.Single(runner.Calls);
        Assert.Empty(vm.Updates);
        return true;
    }, CancellationToken.None);

    [WindowsFact]
    public Task AFailureAfterASuccessfulScanUsesTheShorterRetry() => _session.Dispatch<bool>(async () =>
    {
        var now = new DateTimeOffset(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);
        var runner = new FakeRunner();
        runner.Respond(Scan(Update(FirstId, "First")));
        runner.Respond("""{"error":"offline","hResult":-2145107924}""");
        runner.Respond(Scan());
        var vm = new DriversViewModel(new DriverUpdateService(runner), NewCache(), () => now);
        await vm.CheckForUpdatesAsync();

        now += TimeSpan.FromMinutes(5);
        await vm.ScanCommand.ExecuteAsync(null);
        Assert.True(vm.HasError);

        now += TimeSpan.FromMinutes(30);
        await vm.CheckForUpdatesAsync();
        Assert.Equal(2, runner.Calls.Count);

        now += DriversViewModel.FailedCheckRetryInterval;
        await vm.CheckForUpdatesAsync();
        Assert.Equal(3, runner.Calls.Count);
        Assert.False(vm.HasError);
        return true;
    }, CancellationToken.None);

    [WindowsFact]
    public Task ANewRevisionOfAnInstalledDriverIsOfferedAgain() => _session.Dispatch<bool>(async () =>
    {
        var runner = new FakeRunner();
        runner.Respond(Scan(Update(FirstId, "First")));
        runner.Respond($$"""{"results":[{"id":"{{FirstId}}","resultCode":2,"hResult":0,"rebootRequired":true}],"rebootRequired":true}""");
        runner.Respond(Scan(Update(FirstId, "First (revised)").Replace("\"revision\":1", "\"revision\":2")));
        var vm = new DriversViewModel(new DriverUpdateService(runner), NewCache());
        await vm.CheckForUpdatesAsync();
        await vm.InstallOneCommand.ExecuteAsync(vm.Updates[0]);
        Assert.Equal(DriverItemState.RestartRequired, vm.Updates[0].State);

        await vm.ScanCommand.ExecuteAsync(null);

        DriverUpdateItemViewModel item = Assert.Single(vm.Updates);
        Assert.Equal(2, item.Update.Revision);
        Assert.Equal("First (revised)", item.Title);
        Assert.Equal(DriverItemState.Available, item.State);
        Assert.True(item.CanInstall);
        return true;
    }, CancellationToken.None);

    [WindowsFact]
    public Task FailuresAndRestartsAreAnnouncedToScreenReaders() => _session.Dispatch<bool>(async () =>
    {
        List<AccessibilityAnnouncement> announcements = [];
        void Collect(object? _, AccessibilityAnnouncement announcement) => announcements.Add(announcement);
        AccessibilityAnnouncementService.AnnouncementRequested += Collect;
        try
        {
            var runner = new FakeRunner();
            runner.Respond("""{"error":"blocked","hResult":-2145124306}""");
            runner.Respond(Scan(Update(FirstId, "First")));
            runner.Respond($$"""{"results":[{"id":"{{FirstId}}","resultCode":2,"hResult":0,"rebootRequired":true}],"rebootRequired":true}""");
            var vm = new DriversViewModel(new DriverUpdateService(runner), NewCache());

            await vm.ScanCommand.ExecuteAsync(null);
            await vm.ScanCommand.ExecuteAsync(null);
            await vm.InstallOneCommand.ExecuteAsync(vm.Updates[0]);
            global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            Assert.Contains(announcements, a => a.LiveSetting == global::Avalonia.Automation.AutomationLiveSetting.Assertive && a.Message.Contains("organization"));
            Assert.DoesNotContain(announcements, a => a.Message.Length == 0);
            Assert.Contains(announcements, a => a.Message == vm.RestartNotice.Message && a.LiveSetting == global::Avalonia.Automation.AutomationLiveSetting.Polite);
        }
        finally
        {
            AccessibilityAnnouncementService.AnnouncementRequested -= Collect;
        }
        return true;
    }, CancellationToken.None);

    [WindowsFact]
    public Task AStoppedSessionStartsNoMoreWork() => _session.Dispatch<bool>(async () =>
    {
        var runner = new FakeRunner();
        var vm = new DriversViewModel(new DriverUpdateService(runner), NewCache());

        vm.Stop();
        await vm.CheckForUpdatesAsync();
        await vm.ScanCommand.ExecuteAsync(null);

        Assert.Empty(runner.Calls);
        Assert.False(vm.IsBusy);
        return true;
    }, CancellationToken.None);
}
