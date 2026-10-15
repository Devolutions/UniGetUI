using UniGetUI.Core.Data;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.Serializable;
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

    [Fact]
    public async Task ALoadJoinedAfterAnOptionsSaveReturnsDetailsFetchedUnderTheNewOptions()
    {
        string testRoot = Path.Combine(Path.GetTempPath(), nameof(PackageDetailsLoadTests), Guid.NewGuid().ToString("N"));
        CoreData.TEST_DataDirectoryOverride = Path.Combine(testRoot, "Data");
        try
        {
            Directory.CreateDirectory(CoreData.UniGetUIInstallationOptionsDirectory);
            int fetches = 0;
            var firstFetchStarted = new TaskCompletionSource();
            var releaseFirstFetch = new TaskCompletionSource();
            var manager = new PackageManagerBuilder()
                .ConfigureDetails(helper =>
                    helper.PopulateDetails = details =>
                    {
                        int fetch = Interlocked.Increment(ref fetches);
                        if (fetch == 1)
                        {
                            firstFetchStarted.SetResult();
                            releaseFirstFetch.Task.Wait();
                        }
                        details.InstallerUrl = new Uri($"https://example.test/fetch-{fetch}.exe");
                    })
                .Build();
            var package = new PackageBuilder().WithManager(manager).WithId("Contoso.Test").Build();

            Task first = package.Details.Load();
            await firstFetchStarted.Task;
            InstallOptionsFactory.SaveForPackage(new InstallOptions { InstallationScope = PackageScope.Machine }, package);
            Task joined = package.Details.Load();
            releaseFirstFetch.SetResult();
            await Task.WhenAll(first, joined);

            Assert.Same(first, joined);
            Assert.Equal(2, fetches);
            Assert.Equal("https://example.test/fetch-2.exe", package.Details.InstallerUrl?.ToString());
            Assert.True(package.Details.IsPopulated);
        }
        finally
        {
            CoreData.TEST_DataDirectoryOverride = null;
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task SavingInstallOptionsMarksLoadedDetailsAsStale()
    {
        string testRoot = Path.Combine(Path.GetTempPath(), nameof(PackageDetailsLoadTests), Guid.NewGuid().ToString("N"));
        CoreData.TEST_DataDirectoryOverride = Path.Combine(testRoot, "Data");
        try
        {
            Directory.CreateDirectory(CoreData.UniGetUIInstallationOptionsDirectory);
            var manager = new PackageManagerBuilder().Build();
            var package = new PackageBuilder().WithManager(manager).WithId("Contoso.Test").Build();

            await package.Details.Load();
            Assert.True(package.Details.IsPopulated);

            InstallOptionsFactory.SaveForPackage(new InstallOptions { InstallationScope = PackageScope.Machine }, package);
            Assert.False(package.Details.IsPopulated);

            await package.Details.Load();
            Assert.True(package.Details.IsPopulated);
        }
        finally
        {
            CoreData.TEST_DataDirectoryOverride = null;
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }
}
