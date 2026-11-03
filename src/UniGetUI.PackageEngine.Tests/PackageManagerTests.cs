using System.ComponentModel;
using System.Diagnostics;
using UniGetUI.Core.Data;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.SettingsEngine.SecureSettings;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.ManagerClasses.Classes;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.Tests.Infrastructure.Builders;
using UniGetUI.PackageEngine.Tests.Infrastructure.Fakes;

namespace UniGetUI.PackageEngine.Tests;

public sealed class PackageManagerTests : IDisposable
{
    private readonly string _testRoot;

    public PackageManagerTests()
    {
        _testRoot = Path.Combine(
            AppContext.BaseDirectory,
            nameof(PackageManagerTests),
            Guid.NewGuid().ToString("N")
        );
        var secureSettingsRoot = Path.Combine(_testRoot, "SecureSettings");

        CoreData.TEST_DataDirectoryOverride = Path.Combine(_testRoot, "Data");
        SecureSettings.TEST_SecureSettingsRootOverride = secureSettingsRoot;

        Directory.CreateDirectory(CoreData.UniGetUIUserConfigurationDirectory);
        Directory.CreateDirectory(secureSettingsRoot);

        Settings.ResetSettings();
        Settings.SetDictionary(Settings.K.DisabledManagers, new Dictionary<string, bool>());
        Settings.SetDictionary(Settings.K.ManagerPaths, new Dictionary<string, string>());
        SecureSettings.ApplyForUser(
            Environment.UserName,
            SecureSettings.ResolveKey(SecureSettings.K.AllowCustomManagerPaths),
            false
        );
    }

    public void Dispose()
    {
        Settings.SetDictionary(Settings.K.DisabledManagers, new Dictionary<string, bool>());
        Settings.SetDictionary(Settings.K.ManagerPaths, new Dictionary<string, string>());
        SecureSettings.ApplyForUser(
            Environment.UserName,
            SecureSettings.ResolveKey(SecureSettings.K.AllowCustomManagerPaths),
            false
        );

        CoreData.TEST_DataDirectoryOverride = null;
        SecureSettings.TEST_SecureSettingsRootOverride = null;

        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, true);
        }
    }

    [Fact]
    public void InitializeDisabledManagerSkipsLoadingAndStaysNotReady()
    {
        var manager = CreateManager();
        Settings.SetDictionaryItem<string, bool>(Settings.K.DisabledManagers, manager.Name, true);

        manager.Initialize();

        Assert.False(manager.IsEnabled());
        Assert.False(manager.IsReady());
        Assert.False(manager.Status.Found);
        Assert.Equal(string.Empty, manager.Status.ExecutablePath);
        Assert.Equal(
            CoreTools.Translate("{0} is disabled", manager.DisplayName),
            manager.Status.Version
        );
    }

    [Fact]
    public void InitializeEnabledManagerWithoutExecutableStaysNotReady()
    {
        var manager = CreateManager();
        manager.ExecutableFound = false;
        manager.ExecutablePath = CreateExecutable("missing-manager.exe");
        Settings.SetDictionaryItem<string, bool>(Settings.K.DisabledManagers, manager.Name, false);

        manager.Initialize();

        Assert.True(manager.IsEnabled());
        Assert.False(manager.IsReady());
        Assert.False(manager.Status.Found);
        Assert.Equal(manager.ExecutablePath, manager.Status.ExecutablePath);
        Assert.Equal(
            CoreTools.Translate("{pm} was not found!").Replace("{pm}", manager.DisplayName).Trim('!'),
            manager.Status.Version
        );
    }

    [Fact]
    public void FirstInitializeDisablesManagerWhoseExecutableIsMissing()
    {
        var manager = CreateManager();
        manager.ExecutableFound = false;
        manager.ExecutablePath = CreateExecutable("never-installed-manager.exe");

        manager.Initialize();

        Assert.False(manager.IsEnabled());
        Assert.False(manager.IsReady());
        Assert.False(manager.Status.Found);
        Assert.True(Settings.GetDictionaryItem<string, bool>(Settings.K.DisabledManagers, manager.Name));
        Assert.Equal(
            CoreTools.Translate("{0} is disabled", manager.DisplayName),
            manager.Status.Version
        );
    }

    [Fact]
    public void FirstInitializeKeepsFoundManagerEnabledAfterItsExecutableDisappears()
    {
        var manager = CreateManager();
        manager.ExecutablePath = CreateExecutable("installed-manager.exe");

        manager.Initialize();

        Assert.True(manager.IsReady());
        Assert.True(Settings.DictionaryContainsKey<string, bool>(Settings.K.DisabledManagers, manager.Name));
        Assert.False(Settings.GetDictionaryItem<string, bool>(Settings.K.DisabledManagers, manager.Name));

        manager.ExecutableFound = false;
        manager.Initialize();

        Assert.True(manager.IsEnabled());
        Assert.False(manager.IsReady());
        Assert.False(manager.Status.Found);
    }

    [Fact]
    public void FirstInitializeDisablesOnlyMissingManagersWhenInitializedConcurrently()
    {
        var managers = Enumerable.Range(0, 16).Select(i =>
        {
            var manager = CreateManager();
            manager.ExecutableFound = i % 2 == 0;
            manager.ExecutablePath = CreateExecutable($"{manager.Name}.exe");
            return manager;
        }).ToList();

        Parallel.ForEach(managers, manager => manager.Initialize());

        foreach (var manager in managers)
        {
            Assert.True(Settings.DictionaryContainsKey<string, bool>(Settings.K.DisabledManagers, manager.Name));
            Assert.Equal(manager.ExecutableFound, manager.IsEnabled());
            Assert.Equal(manager.ExecutableFound, manager.IsReady());
        }
    }

    [Fact]
    public void InitializeEnabledManagerWithExecutableBecomesReady()
    {
        var manager = CreateManager();
        manager.ExecutablePath = CreateExecutable("ready-manager.exe");
        manager.LoadedVersion = "9.9.9-test";

        manager.Initialize();

        Assert.True(manager.IsEnabled());
        Assert.True(manager.IsReady());
        Assert.True(manager.Status.Found);
        Assert.Equal(manager.ExecutablePath, manager.Status.ExecutablePath);
        Assert.Equal("9.9.9-test", manager.Status.Version);
    }

    [Fact]
    public void InitializeEnabledManagerThatCannotBeStartedIsFoundButNotReady()
    {
        var manager = CreateManager();
        manager.ExecutablePath = CreateExecutable("unstartable-manager.exe");
        manager.VersionLoadFailure = new Win32Exception(
            unchecked((int)0xC0EA0001),
            "No applicable app licenses found"
        );

        manager.Initialize();

        Assert.True(manager.IsEnabled());
        Assert.True(manager.Status.Found);
        Assert.False(manager.IsReady());
    }

    [Fact]
    public void IsReadyTracksEnablementChangesAfterInitialization()
    {
        var manager = CreateManager();
        manager.ExecutablePath = CreateExecutable("toggle-manager.exe");

        Assert.True(manager.IsEnabled());
        Assert.False(manager.IsReady());

        manager.Initialize();

        Assert.True(manager.IsReady());

        Settings.SetDictionaryItem<string, bool>(Settings.K.DisabledManagers, manager.Name, true);

        Assert.False(manager.IsEnabled());
        Assert.False(manager.IsReady());

        Settings.SetDictionaryItem<string, bool>(Settings.K.DisabledManagers, manager.Name, false);

        Assert.True(manager.IsEnabled());
        Assert.True(manager.IsReady());
    }

    [Fact]
    public void GetExecutableFileReturnsFalseWhenNoCandidatesExist()
    {
        var manager = CreateManager();
        manager.SetCandidateExecutableFiles();

        var executable = manager.GetExecutableFile();

        Assert.False(executable.Item1);
        Assert.Equal(string.Empty, executable.Item2);
    }

    [Fact]
    public void GetExecutableFileUsesSavedPathWhenNoCandidatesExistAndCustomPathsAreEnabled()
    {
        var manager = CreateManager();
        var customPath = CreateExecutable("custom-only.exe");
        manager.SetCandidateExecutableFiles();
        EnableCustomManagerPaths();
        Settings.SetDictionaryItem<string, string>(Settings.K.ManagerPaths, manager.Name, customPath);

        var executable = manager.GetExecutableFile();

        Assert.True(executable.Item1);
        Assert.Equal(customPath, executable.Item2);
    }

    [Fact]
    public void GetExecutableFileIgnoresSavedPathWhenCustomPathsAreDisabled()
    {
        var manager = CreateManager();
        var first = CreateExecutable("candidate-a.exe");
        var second = CreateExecutable("candidate-b.exe");
        manager.SetCandidateExecutableFiles(first, second);
        Settings.SetDictionaryItem<string, string>(Settings.K.ManagerPaths, manager.Name, second);

        var executable = manager.GetExecutableFile();

        Assert.True(executable.Item1);
        Assert.Equal(first, executable.Item2);
    }

    [Fact]
    public void GetExecutableFileUsesSavedCandidateWhenCustomPathsAreEnabled()
    {
        var manager = CreateManager();
        var first = CreateExecutable("enabled-a.exe");
        var second = CreateExecutable("enabled-b.exe");
        manager.SetCandidateExecutableFiles(first, second);
        EnableCustomManagerPaths();
        Settings.SetDictionaryItem<string, string>(Settings.K.ManagerPaths, manager.Name, second);

        var executable = manager.GetExecutableFile();

        Assert.True(executable.Item1);
        Assert.Equal(second, executable.Item2);
    }

    [Fact]
    public void GetExecutableFileFallsBackWhenSavedPathDoesNotExist()
    {
        var manager = CreateManager();
        var first = CreateExecutable("fallback-a.exe");
        var second = CreateExecutable("fallback-b.exe");
        manager.SetCandidateExecutableFiles(first, second);
        EnableCustomManagerPaths();
        Settings.SetDictionaryItem<string, string>(
            Settings.K.ManagerPaths,
            manager.Name,
            Path.Combine(_testRoot, "missing.exe")
        );

        var executable = manager.GetExecutableFile();

        Assert.True(executable.Item1);
        Assert.Equal(first, executable.Item2);
    }

    [Fact]
    public void GetExecutableFileUsesSavedPathWhenSavedPathIsNotACandidate()
    {
        var manager = CreateManager();
        var first = CreateExecutable("outside-a.exe");
        var second = CreateExecutable("outside-b.exe");
        var outsideCandidateList = CreateExecutable("outside-c.exe");
        manager.SetCandidateExecutableFiles(first, second);
        EnableCustomManagerPaths();
        Settings.SetDictionaryItem<string, string>(
            Settings.K.ManagerPaths,
            manager.Name,
            outsideCandidateList
        );

        var executable = manager.GetExecutableFile();

        Assert.True(executable.Item1);
        Assert.Equal(outsideCandidateList, executable.Item2);
    }

    [Fact]
    public void FindPackagesReturnsEmptyWhenManagerIsNotReady()
    {
        var manager = CreateManager();

        var packages = manager.FindPackages("tool");

        Assert.Empty(packages);
        Assert.Null(manager.LastQuery);
        Assert.Equal(0, manager.AttemptFastRepairCalls);
    }

    [Fact]
    public void FindPackagesRetriesOnceAfterFailure()
    {
        var manager = CreateReadyManager();
        var attempts = 0;
        manager.SetFindPackages(query =>
        {
            attempts++;
            return attempts == 1
                ? throw new InvalidOperationException("search failed")
                : [CreatePackage(manager, "Contoso.Search", "Contoso Search")];
        });

        var packages = manager.FindPackages("Contoso");

        var package = Assert.Single(packages);
        Assert.Equal("Contoso.Search", package.Id);
        Assert.Equal("Contoso", manager.LastQuery);
        Assert.Equal(1, manager.AttemptFastRepairCalls);
    }

    [Fact]
    public void GetAvailableUpdatesRetriesOnceWithoutRefreshingIndexesAgain()
    {
        var manager = CreateReadyManager();
        var attempts = 0;
        manager.SetAvailableUpdates(() =>
        {
            attempts++;
            return attempts == 1
                ? throw new InvalidOperationException("updates failed")
                : [CreatePackage(manager, "Contoso.Update", "Contoso Update", "1.0.0", "2.0.0")];
        });

        var packages = manager.GetAvailableUpdates();

        var package = Assert.Single(packages);
        Assert.Equal("Contoso.Update", package.Id);
        Assert.Equal(1, manager.AttemptFastRepairCalls);
        Assert.Equal(1, manager.RefreshPackageIndexesCalls);
    }

    [Fact]
    public void GetInstalledPackagesReturnsEmptyAfterSecondFailure()
    {
        var manager = CreateReadyManager();
        manager.SetInstalledPackages(() => throw new InvalidOperationException("installed failed"));

        var packages = manager.GetInstalledPackages();

        Assert.Empty(packages);
        Assert.Equal(1, manager.AttemptFastRepairCalls);
    }

    [Fact]
    public void FailedSearchIsReportedAndRecorded()
    {
        var manager = CreateReadyManager();
        manager.SetFindPackages(_ => throw new InvalidOperationException("search is down"));

        var packages = manager.FindPackages("tool", out bool failed);

        Assert.Empty(packages);
        Assert.True(failed);
        var search = manager.GetHealthRecord().Search;
        Assert.Equal(1, search.ConsecutiveFailures);
        Assert.Contains("search is down", search.LastFailureReason);
        Assert.NotNull(search.LastFastRepairUtc);
        Assert.Null(search.LastSuccessUtc);
    }

    [Fact]
    public void SearchWithoutMatchesIsNotAFailure()
    {
        var manager = CreateReadyManager();

        var packages = manager.FindPackages("tool", out bool failed);

        Assert.Empty(packages);
        Assert.False(failed);
        var search = manager.GetHealthRecord().Search;
        Assert.Equal(0, search.ConsecutiveFailures);
        Assert.NotNull(search.LastSuccessUtc);
        Assert.Equal(0, search.LastPackageCount);
        Assert.Single(search.RecentDurationsMs);
    }

    [Fact]
    public void ListingOnANotReadyManagerIsNotRecorded()
    {
        var manager = CreateManager();

        manager.FindPackages("tool", out bool failed);
        manager.GetInstalledPackages();

        Assert.False(failed);
        var record = manager.GetHealthRecord();
        Assert.Null(record.Search.LastSuccessUtc);
        Assert.Null(record.Installed.LastFailureUtc);
    }

    [Fact]
    public void ListingRecoveredByFastRepairCountsAsSuccess()
    {
        var manager = CreateReadyManager();
        var attempts = 0;
        manager.SetAvailableUpdates(() =>
        {
            attempts++;
            return attempts == 1
                ? throw new InvalidOperationException("updates failed")
                : [CreatePackage(manager, "Contoso.Update", "Contoso Update", "1.0.0", "2.0.0")];
        });

        manager.GetAvailableUpdates();

        var updates = manager.GetHealthRecord().Updates;
        Assert.Equal(0, updates.ConsecutiveFailures);
        Assert.Equal(1, updates.LastPackageCount);
        Assert.Contains("updates failed", updates.LastFastRepairReason);
        Assert.False(manager.LastUpdatesListingFailed);
    }

    [Fact]
    public void ConsecutiveFailuresAccumulateAndResetOnSuccess()
    {
        var manager = CreateReadyManager();
        var fail = true;
        manager.SetInstalledPackages(() =>
            fail ? throw new TimeoutException("too slow") : [CreatePackage(manager, "Contoso.App", "Contoso App")]
        );

        manager.GetInstalledPackages();
        manager.GetInstalledPackages();
        var failing = manager.GetHealthRecord().Installed;
        Assert.Equal(2, failing.ConsecutiveFailures);
        Assert.True(failing.LastFailureWasTimeout);
        Assert.True(manager.LastInstalledListingFailed);

        fail = false;
        manager.GetInstalledPackages();
        var recovered = manager.GetHealthRecord().Installed;
        Assert.Equal(0, recovered.ConsecutiveFailures);
        Assert.NotNull(recovered.LastFailureUtc);
        Assert.Equal(1, recovered.LastPackageCount);
    }

    [Fact]
    public void HealthRecordSurvivesARestart()
    {
        var manager = CreateReadyManager();
        manager.SetInstalledPackages(() => throw new InvalidOperationException("broken"));
        manager.GetInstalledPackages();

        ManagerHealthStore.Flush();
        ManagerHealthStore.ResetForTests();

        var installed = manager.GetHealthRecord().Installed;
        Assert.Equal(1, installed.ConsecutiveFailures);
        Assert.Contains("broken", installed.LastFailureReason);
    }

    [Fact]
    public void RepeatedFailuresNeedAttention()
    {
        var manager = CreateReadyManager();
        manager.SetAvailableUpdates(() => throw new InvalidOperationException("down"));

        manager.GetAvailableUpdates();
        var once = ManagerHealthEvaluator.Evaluate(manager);
        Assert.Equal(ManagerHealthState.Failing, once.State);
        Assert.False(once.NeedsAttention);

        manager.GetAvailableUpdates();
        var twice = ManagerHealthEvaluator.Evaluate(manager);
        Assert.True(twice.NeedsAttention);
        Assert.Equal(ManagerListingKind.Updates, twice.FailingListing);
        Assert.Equal(2, twice.ConsecutiveFailures);
    }

    [Fact]
    public void EmptyInstalledListIsAMalfunctionOnlyWhenTheManagerExpectsPackages()
    {
        var expecting = CreateReadyManager();
        expecting.ExpectsInstalled = true;
        var other = CreateReadyManager();

        expecting.GetInstalledPackages();
        other.GetInstalledPackages();

        var report = ManagerHealthEvaluator.Evaluate(expecting);
        Assert.Equal(ManagerHealthState.Malfunctioning, report.State);
        Assert.True(report.NeedsAttention);
        Assert.Equal(ManagerHealthState.Healthy, ManagerHealthEvaluator.Evaluate(other).State);
    }

    [Fact]
    public void WinGetMalfunctionDetectorSettingStillSilencesWinGet()
    {
        var winget = new PackageManagerBuilder().WithName("WinGet").Build(initialize: false);
        winget.ExecutablePath = CreateExecutable("winget-test.exe");
        winget.Initialize();
        winget.ExpectsInstalled = true;
        Settings.Set(Settings.K.DisableWinGetMalfunctionDetector, true);
        try
        {
            winget.GetInstalledPackages();

            Assert.NotEqual(
                ManagerHealthState.Malfunctioning,
                ManagerHealthEvaluator.Evaluate(winget).State
            );
        }
        finally
        {
            Settings.Set(Settings.K.DisableWinGetMalfunctionDetector, false);
        }
    }

    [Fact]
    public void EveryManagerOffersReloadAndStopStuckListings()
    {
        var actions = CreateReadyManager().GetMaintenanceActions();

        Assert.Contains(actions, action => action.Id == "reload" && !action.RequiresConfirmation);
        Assert.Contains(actions, action => action.Id == "stop-stuck-listings" && action.RequiresConfirmation);
    }

    [Fact]
    public async Task StopRunningListingsKillsAHungListingProcess()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var manager = CreateReadyManager();
        using var started = new ManualResetEventSlim();
        manager.SetFindPackages(_ =>
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.SystemDirectory, "ping.exe"),
                    Arguments = "-n 120 127.0.0.1",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                },
            };
            manager.TaskLogger.CreateNew(LoggableTaskType.FindPackages, process);
            process.Start();
            started.Set();
            process.WaitForExit();
            return [];
        });

        var search = Task.Run(() => manager.FindPackages("tool"));
        Assert.True(started.Wait(TimeSpan.FromSeconds(20)));

        Assert.Equal(1, manager.StopRunningListings());
        await search.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(0, manager.AttemptFastRepairCalls);
        var listing = manager.GetHealthRecord().Search;
        Assert.Equal(1, listing.ConsecutiveFailures);
        Assert.Null(listing.LastSuccessUtc);
        Assert.Contains("stopped", listing.LastFailureReason);
    }

    [Fact]
    public async Task StoppedListingThatThrowsIsNotRetried()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var manager = CreateReadyManager();
        using var started = new ManualResetEventSlim();
        int attempts = 0;
        manager.SetInstalledPackages(() =>
        {
            attempts++;
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.SystemDirectory, "ping.exe"),
                    Arguments = "-n 120 127.0.0.1",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                },
            };
            manager.TaskLogger.CreateNew(LoggableTaskType.ListInstalledPackages, process);
            process.Start();
            started.Set();
            process.WaitForExit();
            throw new InvalidOperationException($"The listing process exited with {process.ExitCode}");
        });

        var listing = Task.Run(manager.GetInstalledPackages);
        Assert.True(started.Wait(TimeSpan.FromSeconds(20)));

        Assert.Equal(1, manager.StopRunningListings());
        await listing.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(1, attempts);
        Assert.Equal(0, manager.AttemptFastRepairCalls);
        Assert.True(manager.LastInstalledListingFailed);
        Assert.Equal(1, manager.GetHealthRecord().Installed.ConsecutiveFailures);
    }

    [Fact]
    public void ANewerSingleFailureDoesNotHideALongerStreak()
    {
        var manager = CreateReadyManager();
        manager.SetAvailableUpdates(() => throw new InvalidOperationException("updates down"));
        manager.SetFindPackages(_ => throw new InvalidOperationException("search down"));

        manager.GetAvailableUpdates();
        manager.GetAvailableUpdates();
        manager.FindPackages("tool");

        var report = ManagerHealthEvaluator.Evaluate(manager);
        Assert.True(report.NeedsAttention);
        Assert.Equal(ManagerListingKind.Updates, report.FailingListing);
        Assert.Equal(2, report.ConsecutiveFailures);
    }

    [Fact]
    public void OldFailuresAreShownButDoNotNeedAttention()
    {
        var manager = CreateReadyManager();
        manager.SetFindPackages(_ => throw new InvalidOperationException("search down"));
        using (ManagerHealthStore.UseClock(() => DateTime.UtcNow - TimeSpan.FromDays(3)))
        {
            manager.FindPackages("tool");
            manager.FindPackages("tool");
        }

        var report = ManagerHealthEvaluator.Evaluate(manager);
        Assert.Equal(ManagerHealthState.Failing, report.State);
        Assert.Equal(2, report.ConsecutiveFailures);
        Assert.False(report.NeedsAttention);
    }

    [Fact]
    public void ManagerIsLoadingUntilInitialized()
    {
        var manager = CreateManager();
        Assert.Equal(ManagerHealthState.Loading, ManagerHealthEvaluator.Evaluate(manager).State);

        manager.ExecutablePath = CreateExecutable($"{manager.Name}.exe");
        manager.Initialize();

        Assert.Equal(ManagerHealthState.Unknown, ManagerHealthEvaluator.Evaluate(manager).State);
    }

    [Fact]
    public async Task MaintenanceCommandWithoutExecutableFailsClearly()
    {
        var manager = CreateReadyManager();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.RunMaintenanceCommandForTestAsync("", "cache clean")
        );

        Assert.Contains("could not be found on this system", error.Message);
    }

    [Fact]
    public async Task HungMaintenanceCommandTimesOut()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var manager = CreateReadyManager();
        manager.MaintenanceTimeout = TimeSpan.FromSeconds(1);

        await Assert.ThrowsAsync<TimeoutException>(() =>
            manager
                .RunMaintenanceCommandForTestAsync(
                    Path.Combine(Environment.SystemDirectory, "ping.exe"),
                    "-n 120 127.0.0.1"
                )
                .WaitAsync(TimeSpan.FromSeconds(30))
        );
    }

    [Fact]
    public async Task StopRunningListingsAlsoStopsMaintenanceCommands()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var manager = CreateReadyManager();
        var command = manager.RunMaintenanceCommandForTestAsync(
            Path.Combine(Environment.SystemDirectory, "ping.exe"),
            "-n 120 127.0.0.1"
        );

        int killed = 0;
        for (int attempt = 0; attempt < 100 && killed == 0; attempt++)
        {
            await Task.Delay(100);
            killed = manager.StopRunningListings();
        }

        Assert.Equal(1, killed);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            command.WaitAsync(TimeSpan.FromSeconds(30))
        );
    }

    [Fact]
    public void FailureReasonKeepsHostsWhenAtSignIsInTheQuery()
    {
        var manager = CreateReadyManager();
        manager.SetInstalledPackages(() =>
            throw new InvalidOperationException("404 from https://feed.example.test?owner=dev@corp.test")
        );

        manager.GetInstalledPackages();

        Assert.Contains(
            "https://feed.example.test?owner=dev@corp.test",
            manager.GetHealthRecord().Installed.LastFailureReason
        );
    }

    [Fact]
    public void FailureReasonHidesUrlCredentials()
    {
        var manager = CreateReadyManager();
        manager.SetInstalledPackages(() =>
            throw new InvalidOperationException("401 from https://builder:s3cret@feed.example.test/v3/index.json")
        );

        manager.GetInstalledPackages();

        var reason = manager.GetHealthRecord().Installed.LastFailureReason;
        Assert.DoesNotContain("s3cret", reason);
        Assert.Contains("https://****@feed.example.test/v3/index.json", reason);
    }

    [Fact]
    public void MalfunctionIsNotHiddenByANonUrgentFailure()
    {
        var manager = CreateReadyManager();
        manager.ExpectsInstalled = true;
        manager.SetFindPackages(_ => throw new InvalidOperationException("search down"));

        manager.GetInstalledPackages();
        manager.FindPackages("tool");

        var report = ManagerHealthEvaluator.Evaluate(manager);
        Assert.Equal(ManagerHealthState.Malfunctioning, report.State);
        Assert.True(report.NeedsAttention);
    }

    [Fact]
    public void FinishingInitializationRaisesHealthChanged()
    {
        var manager = CreateManager();
        manager.ExecutablePath = CreateExecutable($"{manager.Name}.exe");
        var raised = new List<string>();
        void Observe(object? sender, string name) => raised.Add(name);
        ManagerHealthStore.HealthChanged += Observe;
        try
        {
            manager.Initialize();
        }
        finally
        {
            ManagerHealthStore.HealthChanged -= Observe;
        }

        Assert.Contains(manager.Name, raised);
    }

    [Fact]
    public async Task MaintenanceTimeoutCoversOutputHeldByAChildProcess()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var manager = CreateReadyManager();
        manager.MaintenanceTimeout = TimeSpan.FromSeconds(2);

        await Assert.ThrowsAsync<TimeoutException>(() =>
            manager
                .RunMaintenanceCommandForTestAsync(
                    Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                    "/c start /b ping -n 60 127.0.0.1"
                )
                .WaitAsync(TimeSpan.FromSeconds(30))
        );
    }

    private static TestPackageManager CreateManager()
    {
        return new PackageManagerBuilder()
            .WithName($"TestManager-{Guid.NewGuid():N}")
            .WithDisplayName("Test Manager")
            .Build(initialize: false);
    }

    private TestPackageManager CreateReadyManager()
    {
        var manager = CreateManager();
        manager.ExecutablePath = CreateExecutable($"{manager.Name}.exe");
        manager.Initialize();
        Assert.True(manager.IsReady());
        return manager;
    }

    private static void EnableCustomManagerPaths()
    {
        Assert.Equal(
            0,
            SecureSettings.ApplyForUser(
                Environment.UserName,
                SecureSettings.ResolveKey(SecureSettings.K.AllowCustomManagerPaths),
                true
            )
        );
    }

    private string CreateExecutable(string fileName)
    {
        var path = Path.Combine(_testRoot, "Executables", fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
        return path;
    }

    private static Package CreatePackage(
        TestPackageManager manager,
        string id,
        string name,
        string version = "1.0.0",
        string? newVersion = null
    )
    {
        var builder = new PackageBuilder().WithManager(manager).WithId(id).WithName(name).WithVersion(version);
        return newVersion is null ? builder.Build() : builder.WithNewVersion(newVersion).Build();
    }
}
