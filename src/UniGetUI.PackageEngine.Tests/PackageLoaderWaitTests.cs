using UniGetUI.Core.SettingsEngine;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.PackageLoader;
using UniGetUI.PackageEngine.Tests.Infrastructure.Builders;
using UniGetUI.PackageEngine.Tests.Infrastructure.Fakes;

namespace UniGetUI.PackageEngine.Tests;

public sealed class PackageLoaderWaitTests
{
    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(10);

    private sealed class InventoryLoader(IReadOnlyList<IPackageManager> managers) : InstalledPackagesLoader(managers)
    {
        protected override bool WillQueryAnyManager() => false;
        protected override Task WhenAddingPackage(IPackage package) => Task.CompletedTask;
    }

    private sealed class GatedManagers(IPackageManager manager) : IReadOnlyList<IPackageManager>, IDisposable
    {
        private int _enumerations;
        public readonly ManualResetEventSlim Release = new(false);
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource EnteredTwice = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Count => 1;
        public IPackageManager this[int index] => index == 0 ? manager : throw new IndexOutOfRangeException();

        public IEnumerator<IPackageManager> GetEnumerator()
        {
            if (Interlocked.Increment(ref _enumerations) == 1)
            {
                Entered.SetResult();
                if (!Release.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("Load initialization was not released");
            }
            else
                EnteredTwice.TrySetResult();
            yield return manager;
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        public void Dispose() => Release.Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentReloadsCannotStartWhileAnotherLoadIsPublishingItsState(bool silent)
    {
        Settings.Set(Settings.K.DisableWaitForInternetConnection, true);
        using var releaseListing = new ManualResetEventSlim(false);
        using var secondCalled = new ManualResetEventSlim(false);
        int listings = 0;
        var manager = new PackageManagerBuilder().WithInstalledPackages(_ =>
        {
            Interlocked.Increment(ref listings);
            releaseListing.Wait(TimeSpan.FromSeconds(10));
            return [];
        }).Build();
        using var managers = new GatedManagers(manager);
        var previousLoader = InstalledPackagesLoader.Instance;
        var loader = new InventoryLoader(managers);
        Task first = Task.Run(loader.ReloadPackages);
        Task? second = null;
        try
        {
            await managers.Entered.Task.WaitAsync(WaitBudget);
            second = Task.Factory.StartNew(() =>
            {
                secondCalled.Set();
                return silent ? loader.ReloadPackagesSilently() : loader.ReloadPackagesListingOnly();
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
            Assert.True(secondCalled.Wait(WaitBudget));
            await Task.WhenAny(managers.EnteredTwice.Task, Task.Delay(300));
            Assert.False(managers.EnteredTwice.Task.IsCompleted,
                "Another reload entered initialization before the first published its load state");

            managers.Release.Set();
            await second.WaitAsync(WaitBudget);
            Assert.True(loader.IsLoading);
        }
        finally
        {
            managers.Release.Set();
            releaseListing.Set();
            await first.WaitAsync(WaitBudget);
            if (second is not null)
                await second.WaitAsync(WaitBudget);
            InstalledPackagesLoader.Instance = previousLoader;
        }
        Assert.Equal(1, listings);
        Assert.Equal(1, loader.LoadGeneration);
    }

    [Fact]
    public async Task WaitForCurrentLoadAsync_CompletesWhenNoLoadIsRunning()
    {
        var manager = new PackageManagerBuilder().Build();
        var loader = new TestPackageLoader([manager], allowMultiplePackageVersions: false);

        await AssertCompletesAsync(loader.WaitForCurrentLoadAsync());
    }

    [Fact]
    public async Task WaitForCurrentLoadAsync_CompletesForAWaiterThatSubscribedFromFinishedLoading()
    {
        var manager = new PackageManagerBuilder().Build();
        var loader = new TestPackageLoader([manager], allowMultiplePackageVersions: false);

        Task? waitStartedInsideTheEvent = null;
        loader.FinishedLoading += (_, _) => waitStartedInsideTheEvent ??= loader.WaitForCurrentLoadAsync();

        await loader.ReloadPackages();

        Assert.NotNull(waitStartedInsideTheEvent);
        await AssertCompletesAsync(waitStartedInsideTheEvent);
    }

    [Fact]
    public async Task ReloadPackages_DuringASilentReloadAfterAListingOnlyLoadReturnsInsteadOfRecursing()
    {
        Settings.Set(Settings.K.DisableWaitForInternetConnection, true);
        int listings = 0;
        using var gate = new ManualResetEventSlim(true);
        var manager = new PackageManagerBuilder()
            .WithInstalledPackages(_ =>
            {
                Interlocked.Increment(ref listings);
                gate.Wait(TimeSpan.FromSeconds(30));
                return [];
            })
            .Build();
        var loader = new InstalledPackagesLoader([manager]);
        await AssertCompletesAsync(loader.ReloadPackagesListingOnly());
        Assert.True(loader.LoadIsListingOnly);

        gate.Reset();
        Task silent;
        try
        {
            silent = loader.ReloadPackagesSilently();
            Assert.True(loader.IsLoading);

            // A silent reload is a normal load: a reload requested during it is not left waiting
            // for, or spinning on, a listing-only load that is not running.
            await AssertCompletesAsync(Task.Run(loader.ReloadPackages));
            Assert.False(loader.LoadIsListingOnly);
        }
        finally
        {
            gate.Set();
        }

        await AssertCompletesAsync(silent);
        Assert.Equal(2, listings);
    }

    [Fact]
    public async Task NormalReloadKeepsPartialRowsButDoesNotPublishThemAsACompleteScan()
    {
        Settings.Set(Settings.K.DisableWaitForInternetConnection, true);
        int calls = 0;
        var manager = new PackageManagerBuilder()
            .WithName($"PartialListing-{Guid.NewGuid():N}")
            .WithInstalledPackages(m =>
            {
                calls++;
                PackageListingScope.ReportIncomplete(new InvalidOperationException("one source failed"));
                return [new PackageBuilder().WithManager(m).WithId("partial").Build()];
            })
            .Build();
        Assert.True(manager.IsReady(), manager.Status.Version);
        var previousLoader = InstalledPackagesLoader.Instance;
        try
        {
            var loader = new InventoryLoader([manager]);

            await AssertCompletesAsync(loader.ReloadPackages());

            Assert.Equal("partial", Assert.Single(loader.Packages).Id);
            Assert.Equal("partial", Assert.Single(loader.LastCompletedScan!.Packages).Id);
            Assert.DoesNotContain(manager, loader.LastCompletedScan.SucceededManagers);
            Assert.True(loader.LastLoadReportedFailures);
            Assert.Equal(1, calls);
            Assert.Equal(0, manager.AttemptFastRepairCalls);
        }
        finally
        {
            InstalledPackagesLoader.Instance = previousLoader;
        }
    }

    [Fact]
    public async Task ListingThatFailsIsNotCompleteWhenAnotherListingClearsTheSharedFlag()
    {
        Settings.Set(Settings.K.DisableWaitForInternetConnection, true);
        int failingCalls = 0;
        TestPackageManager? failing = null;
        failing = new PackageManagerBuilder()
            .WithName("failing")
            .WithInstalledPackages(_ =>
                Interlocked.Increment(ref failingCalls) <= 2
                    ? throw new InvalidOperationException("listing failed")
                    : [])
            .Build();
        var other = new PackageManagerBuilder()
            .WithName("other")
            .WithInstalledPackages(_ =>
            {
                // Lists the failing manager again once its scan call has failed, as another
                // listing (a UI page, a CLI request) can while this load is still running.
                SpinWait.SpinUntil(() => failing.LastInstalledListingFailed, TimeSpan.FromSeconds(10));
                failing.GetInstalledPackages();
                return [];
            })
            .Build();
        var loader = new InstalledPackagesLoader([failing, other]);

        await AssertCompletesAsync(loader.ReloadPackages());

        Assert.False(failing.LastInstalledListingFailed);
        Assert.True(loader.LastLoadReportedFailures);
        var scan = loader.LastCompletedScan;
        Assert.NotNull(scan);
        Assert.DoesNotContain(failing, scan.SucceededManagers);
        Assert.Contains(other, scan.SucceededManagers);
    }

    [Fact]
    public async Task WaitForCurrentLoadAsync_CompletesWhenLoadingIsStoppedWithoutASignal()
    {
        var manager = new PackageManagerBuilder().Build();
        var loader = new TestPackageLoader([manager], allowMultiplePackageVersions: false);

        Task? waitStartedInsideTheEvent = null;
        loader.FinishedLoading += (_, _) => waitStartedInsideTheEvent ??= loader.WaitForCurrentLoadAsync();

        await loader.ReloadPackages();
        loader.StopLoading(emitFinishSignal: false);

        Assert.NotNull(waitStartedInsideTheEvent);
        await AssertCompletesAsync(waitStartedInsideTheEvent);
    }

    [Fact]
    public async Task WaitForCurrentLoadAsync_KeepsWaiting_WhenARedundantReloadIsRequested()
    {
        using var release = new ManualResetEventSlim(false);
        var manager = new PackageManagerBuilder().Build();
        var loader = new TestPackageLoader(
            [manager],
            loadPackages: _ =>
            {
                release.Wait();
                return [];
            }
        );

        Task load = loader.ReloadPackages();
        Assert.True(loader.IsLoading);

        Task wait = loader.WaitForCurrentLoadAsync();
        await loader.ReloadPackages();

        Assert.False(wait.IsCompleted);

        release.Set();
        await load;
        await AssertCompletesAsync(wait);
    }

    [Fact]
    public async Task WaitForCurrentLoadAsync_TracksTheNextLoad_WhenAReloadStartsFromFinishedLoading()
    {
        using var release = new ManualResetEventSlim(true);
        var manager = new PackageManagerBuilder().Build();
        var loader = new TestPackageLoader(
            [manager],
            loadPackages: _ =>
            {
                release.Wait();
                return [];
            }
        );

        Task? queued = null;
        loader.FinishedLoading += (_, _) =>
        {
            if (queued is not null) return;
            release.Reset();
            queued = loader.ReloadPackages();
        };

        await loader.ReloadPackages();

        Assert.NotNull(queued);
        Assert.False(queued.IsCompleted);
        Assert.False(loader.WaitForCurrentLoadAsync().IsCompleted);

        release.Set();
        await queued;
    }

    private static async Task AssertCompletesAsync(Task wait)
    {
        Task finished = await Task.WhenAny(wait, Task.Delay(WaitBudget));
        Assert.True(ReferenceEquals(finished, wait), "WaitForCurrentLoadAsync never completed");
        await wait;
    }
}
