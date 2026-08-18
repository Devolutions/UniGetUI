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

    private static async Task AssertCompletesAsync(Task wait)
    {
        Task finished = await Task.WhenAny(wait, Task.Delay(WaitBudget));
        Assert.True(ReferenceEquals(finished, wait), "WaitForCurrentLoadAsync never completed");
        await wait;
    }
}
