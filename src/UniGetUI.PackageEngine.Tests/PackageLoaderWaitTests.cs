using UniGetUI.PackageEngine.Tests.Infrastructure.Builders;
using UniGetUI.PackageEngine.Tests.Infrastructure.Fakes;

namespace UniGetUI.PackageEngine.Tests;

public sealed class PackageLoaderWaitTests
{
    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(10);

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
