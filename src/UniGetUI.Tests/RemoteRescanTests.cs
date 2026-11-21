using UniGetUI.Interface.AgentRegistration;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.PackageLoader;

namespace UniGetUI.Tests;

public sealed class RemoteRescanTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private sealed class GateLoader(IReadOnlyList<IPackageManager> managers)
        : AbstractPackageLoader(
            managers,
            "REMOTE_RESCAN_TEST",
            AllowMultiplePackageVersions: false,
            DisableReload: false,
            CheckedBydefault: false,
            RequiresInternet: false
        )
    {
        private readonly object _lock = new();
        private readonly List<TaskCompletionSource> _gates = [];
        private readonly List<bool> _listingOnly = [];

        public int Loads
        {
            get
            {
                lock (_lock)
                    return _gates.Count;
            }
        }

        public bool[] ListingOnly
        {
            get
            {
                lock (_lock)
                    return [.. _listingOnly];
            }
        }

        protected override IReadOnlyList<IPackage> LoadPackagesFromManager(IPackageManager manager)
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_lock)
            {
                _gates.Add(gate);
                _listingOnly.Add(PackageListingScope.IsListingOnly);
            }

            gate.Task.Wait(Timeout);
            return [];
        }

        public async Task WaitForLoadsAsync(int count)
        {
            using var cts = new CancellationTokenSource(Timeout);
            while (Loads < count)
                await Task.Delay(10, cts.Token);
        }

        public async Task ReleaseAsync(int load)
        {
            await WaitForLoadsAsync(load + 1);
            lock (_lock)
                _gates[load].SetResult();
        }

        protected override Task<bool> IsPackageValid(IPackage package) => Task.FromResult(true);

        protected override Task WhenAddingPackage(IPackage package) => Task.CompletedTask;
    }

    private static (GateLoader Installed, GateLoader Updates) Loaders()
    {
        var manager = new RemoteInventorySnapshotTests.SnapshotManager("a");
        return (new GateLoader([manager]), new GateLoader([manager]));
    }

    private static async Task AssertPendingAsync(Task task)
    {
        await Task.Delay(300);
        Assert.False(task.IsCompleted);
    }

    [Fact]
    public async Task ScanRunningWhenTheRequestArrivesIsWaitedOutAndANewListingOnlyScanAnswers()
    {
        var (installed, updates) = Loaders();
        Task userLoad = installed.ReloadPackages();
        await installed.WaitForLoadsAsync(1);
        long arrival = installed.LoadGeneration;

        Task<bool> answer = new RemoteRescan().RequestAsync(installed, updates, CancellationToken.None);
        await AssertPendingAsync(answer);
        Assert.Equal(1, installed.Loads);
        Assert.Equal(0, updates.Loads);

        await installed.ReleaseAsync(0);
        await userLoad;
        await AssertPendingAsync(answer);

        await installed.ReleaseAsync(1);
        await updates.ReleaseAsync(0);
        Assert.True(await answer.WaitAsync(Timeout));
        Assert.True(installed.LastCompletedScan!.StartGeneration > arrival);
        Assert.Equal([false, true], installed.ListingOnly);
        Assert.Equal([true], updates.ListingOnly);
        Assert.False(PackageListingScope.IsListingOnly);
    }

    [Fact]
    public async Task RequestsWhileAScanIsQueuedShareItAndALaterRequestQueuesTheNext()
    {
        var (installed, updates) = Loaders();
        var rescan = new RemoteRescan();
        Task userLoad = installed.ReloadPackages();
        await installed.WaitForLoadsAsync(1);

        Task<bool> first = rescan.RequestAsync(installed, updates, CancellationToken.None);
        Task<bool> second = rescan.RequestAsync(installed, updates, CancellationToken.None);
        await installed.ReleaseAsync(0);
        await userLoad;
        await installed.WaitForLoadsAsync(2);
        await updates.WaitForLoadsAsync(1);

        Task<bool> third = rescan.RequestAsync(installed, updates, CancellationToken.None);
        await installed.ReleaseAsync(1);
        await updates.ReleaseAsync(0);
        Assert.True(await first.WaitAsync(Timeout));
        Assert.True(await second.WaitAsync(Timeout));
        await AssertPendingAsync(third);

        await installed.ReleaseAsync(2);
        await updates.ReleaseAsync(1);
        Assert.True(await third.WaitAsync(Timeout));
        Assert.Equal(3, installed.Loads);
        Assert.Equal(2, updates.Loads);
    }

    [Fact]
    public async Task RequestJoiningAScanIsNotAnsweredByALoadThatStartedBeforeIt()
    {
        var (installed, updates) = Loaders();
        var rescan = new RemoteRescan();
        Task? userLoad = null;
        Task<bool>? second = null;
        long secondArrival = 0;
        rescan.BeforeScanStarts = () =>
        {
            rescan.BeforeScanStarts = null;
            userLoad = installed.ReloadPackages();
            secondArrival = installed.LoadGeneration;
            second = rescan.RequestAsync(installed, updates, CancellationToken.None);
        };

        Task<bool> first = rescan.RequestAsync(installed, updates, CancellationToken.None);
        await installed.WaitForLoadsAsync(1);
        using (var cts = new CancellationTokenSource(Timeout))
            while (second is null)
                await Task.Delay(10, cts.Token);

        await installed.ReleaseAsync(0);
        await userLoad!.WaitAsync(Timeout);
        await AssertPendingAsync(second);
        await installed.WaitForLoadsAsync(2);

        await installed.ReleaseAsync(1);
        await updates.ReleaseAsync(0);
        Assert.True(await first.WaitAsync(Timeout));
        Assert.True(await second.WaitAsync(Timeout));
        Assert.True(installed.LastCompletedScan!.StartGeneration > secondArrival);
        Assert.Equal([false, true], installed.ListingOnly);
    }

    [Fact]
    public async Task ScanThatPublishesNoResultFailsTheRescan()
    {
        var (installed, updates) = Loaders();
        Task<bool> answer = new RemoteRescan().RequestAsync(installed, updates, CancellationToken.None);
        await installed.WaitForLoadsAsync(1);
        installed.StopLoading();

        await installed.ReleaseAsync(0);
        await updates.ReleaseAsync(0);
        Assert.False(await answer.WaitAsync(Timeout));
    }

    [Fact]
    public async Task PeriodicReloadListsOnlyAndIsSkippedWhileALoadIsRunning()
    {
        var (installed, _) = Loaders();
        var rescan = new RemoteRescan();
        var seen = new List<(bool Args, bool Ambient)>();
        bool ambientAtStart = true;
        installed.StartedLoading += (_, _) => ambientAtStart = PackageListingScope.IsListingOnly;
        installed.FinishedLoading += (_, e) =>
        {
            lock (seen)
                seen.Add((e.ListingOnly, PackageListingScope.IsListingOnly));
        };

        Assert.True(rescan.TryStartInstalledReload(installed));
        await installed.WaitForLoadsAsync(1);
        Assert.False(rescan.TryStartInstalledReload(installed));

        await installed.ReleaseAsync(0);
        using var cts = new CancellationTokenSource(Timeout);
        while (installed.IsLoading || seen.Count == 0)
            await Task.Delay(10, cts.Token);
        Assert.Equal(1, installed.Loads);
        Assert.Equal([true], installed.ListingOnly);
        Assert.False(PackageListingScope.IsListingOnly);
        // The mode reaches handlers through the arguments, never as ambient state they would carry
        // into the work they start.
        Assert.False(ambientAtStart);
        lock (seen)
            Assert.Equal([(true, false)], seen);
    }

    [Fact]
    public async Task NormalReloadWaitsOutAListingOnlyLoadAndThenRunsItsOwn()
    {
        var (installed, _) = Loaders();
        Assert.True(new RemoteRescan().TryStartInstalledReload(installed));
        await installed.WaitForLoadsAsync(1);

        Task userReload = installed.ReloadPackages();
        await AssertPendingAsync(userReload);
        Assert.Equal(1, installed.Loads);

        await installed.ReleaseAsync(0);
        await installed.ReleaseAsync(1);
        await userReload.WaitAsync(Timeout);
        Assert.Equal([true, false], installed.ListingOnly);
        Assert.False(installed.LoadIsListingOnly);
    }

    [Fact]
    public async Task RescanWaitsUntilThePackageManagersHaveLoaded()
    {
        var managersLoaded = new TaskCompletionSource();
        var backend = new IpcRemoteInventoryBackend(managersLoaded: ct => managersLoaded.Task.WaitAsync(ct));
        using var cts = new CancellationTokenSource();

        Task<bool> answer = backend.RescanInventoryAsync(cts.Token);
        await AssertPendingAsync(answer);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => answer);
    }
}
