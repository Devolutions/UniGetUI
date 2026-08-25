using UniGetUI.PackageEngine.Tests.Infrastructure.Builders;

namespace UniGetUI.PackageEngine.Tests;

public class PackageDetailsLoadTests
{
    [Fact]
    public async Task ConcurrentLoadsShareASingleFetch()
    {
        int fetches = 0;
        var gate = new TaskCompletionSource();
        var manager = new PackageManagerBuilder()
            .ConfigureDetails(helper =>
                helper.PopulateDetails = details =>
                {
                    Interlocked.Increment(ref fetches);
                    gate.Task.Wait();
                    details.Description = "Fetched once";
                })
            .Build();
        var package = new PackageBuilder().WithManager(manager).WithId("Contoso.Test").Build();

        Task first = package.Details.Load();
        Task second = package.Details.Load();
        gate.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, fetches);
        Assert.True(package.Details.IsPopulated);
        Assert.Equal("Fetched once", package.Details.Description);
    }

    [Fact]
    public async Task LoadRunsAgainOnceThePreviousLoadCompleted()
    {
        int fetches = 0;
        var manager = new PackageManagerBuilder()
            .ConfigureDetails(helper =>
                helper.PopulateDetails = _ => Interlocked.Increment(ref fetches))
            .Build();
        var package = new PackageBuilder().WithManager(manager).WithId("Contoso.Test").Build();

        await package.Details.Load();
        await package.Details.Load();

        Assert.Equal(2, fetches);
    }
}
