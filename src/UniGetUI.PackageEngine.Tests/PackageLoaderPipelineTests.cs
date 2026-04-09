using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Tests.Infrastructure.Builders;
using UniGetUI.PackageEngine.Tests.Infrastructure.Fakes;
using UniGetUI.PackageEngine.Tests.Infrastructure.Helpers;

namespace UniGetUI.PackageEngine.Tests;

public sealed class PackageLoaderPipelineTests
{
    [Fact]
    public async Task AddForeign_DeduplicatesByPackageHash_WhenVersionAwareIdentityIsDisabled()
    {
        var manager = new PackageManagerBuilder().Build();
        var loader = new TestPackageLoader([manager], allowMultiplePackageVersions: false);
        var packageV1 = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Tool")
            .WithVersion("1.0.0")
            .Build();
        var packageV2 = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Tool")
            .WithVersion("2.0.0")
            .Build();

        await loader.AddForeign(packageV1);
        await loader.AddForeign(packageV2);

        Assert.Equal(1, loader.Count());
        Assert.True(loader.Contains(packageV1));
        Assert.True(loader.Contains(packageV2));
        Assert.Same(packageV1, loader.GetEquivalentPackage(packageV2));
        Assert.Single(loader.GetEquivalentPackages(packageV1));
    }

    [Fact]
    public async Task AddForeign_KeepsDistinctVersions_WhenVersionAwareIdentityIsEnabled()
    {
        var manager = new PackageManagerBuilder().Build();
        var loader = new TestPackageLoader([manager], allowMultiplePackageVersions: true);
        var packageV1 = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Tool")
            .WithVersion("1.0.0")
            .Build();
        var packageV2 = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Tool")
            .WithVersion("2.0.0")
            .Build();

        await loader.AddForeign(packageV1);
        await loader.AddForeign(packageV2);

        Assert.Equal(2, loader.Count());
        Assert.Same(packageV1, loader.GetEquivalentPackage(packageV1));
        Assert.Same(packageV2, loader.GetEquivalentPackage(packageV2));
        Assert.Equal(
            ["1.0.0", "2.0.0"],
            loader.GetEquivalentPackages(packageV1).Select(package => package.VersionString).Order()
        );
    }

    [Fact]
    public async Task LookupAndRemovalApisUseLoaderIdentityAndSourceFiltering()
    {
        var manager = new PackageManagerBuilder()
            .WithSources(testManager =>
            [
                new SourceBuilder()
                    .WithManager(testManager)
                    .WithName("stable")
                    .WithUrl("https://example.test/stable")
                    .Build(),
                new SourceBuilder()
                    .WithManager(testManager)
                    .WithName("beta")
                    .WithUrl("https://example.test/beta")
                    .Build(),
            ])
            .Build();
        var stableSource = manager.SourcesHelper.GetSources().Single(source => source.Name == "stable");
        var betaSource = manager.SourcesHelper.GetSources().Single(source => source.Name == "beta");
        var loader = new TestPackageLoader([manager]);
        var uniquePackage = new PackageBuilder()
            .WithManager(manager)
            .WithSource(stableSource)
            .WithId("Contoso.Unique")
            .Build();
        var stablePackage = new PackageBuilder()
            .WithManager(manager)
            .WithSource(stableSource)
            .WithId("Contoso.Shared")
            .Build();
        var betaPackage = new PackageBuilder()
            .WithManager(manager)
            .WithSource(betaSource)
            .WithId("Contoso.Shared")
            .Build();
        var recorder = new LoaderEventRecorder(loader);

        await loader.AddForeign(uniquePackage);
        await loader.AddForeign(stablePackage);
        await loader.AddForeign(betaPackage);
        loader.Remove(betaPackage);

        Assert.True(loader.Contains(stablePackage));
        Assert.False(loader.Contains(betaPackage));
        Assert.Same(uniquePackage, loader.GetPackageForId("Contoso.Unique"));
        Assert.Same(stablePackage, loader.GetPackageForId("Contoso.Shared", "stable"));
        Assert.Null(loader.GetPackageForId("Contoso.Shared", "beta"));
        Assert.Single(loader.GetEquivalentPackages(stablePackage));
        Assert.Null(loader.GetEquivalentPackage(betaPackage));
        Assert.Contains(recorder.RemovedPackages, package => package.Id == "Contoso.Shared");
    }

    [Fact]
    public async Task ClearPackagesAndStopLoading_EmitExpectedEvents()
    {
        var manager = new PackageManagerBuilder().Build();
        var loader = new TestPackageLoader([manager]);
        var recorder = new LoaderEventRecorder(loader);
        var package = new PackageBuilder().WithManager(manager).WithId("Contoso.Tool").Build();

        await loader.AddForeign(package);
        loader.StopLoading(emitFinishSignal: false);
        loader.ClearPackages();

        Assert.Equal(0, loader.Count());
        Assert.Equal(1, recorder.FinishedLoadingCount);
        Assert.Equal(2, recorder.Changes.Count);
        Assert.False(recorder.Changes[1].ProceduralChange);
        Assert.Empty(recorder.Changes[1].AddedPackages);
        Assert.Empty(recorder.Changes[1].RemovedPackages);
    }

    [Fact]
    public async Task ReloadPackages_ClearsExistingPackages_FiltersInvalidOnes_AndRaisesLifecycleEvents()
    {
        var manager = new PackageManagerBuilder()
            .WithInstalledPackages(testManager =>
            [
                new PackageBuilder()
                    .WithManager(testManager)
                    .WithId("Contoso.Accepted")
                    .WithVersion("1.0.0")
                    .Build(),
                new PackageBuilder()
                    .WithManager(testManager)
                    .WithId("Contoso.Accepted")
                    .WithVersion("1.0.0")
                    .Build(),
                new PackageBuilder()
                    .WithManager(testManager)
                    .WithId("Contoso.Rejected")
                    .WithVersion("1.0.0")
                    .Build(),
            ])
            .Build();
        var loader = new TestPackageLoader(
            [manager],
            loadPackages: testManager => testManager.GetInstalledPackages(),
            isPackageValid: package => Task.FromResult(package.Id != "Contoso.Rejected")
        );
        var stalePackage = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Stale")
            .WithVersion("0.9.0")
            .Build();
        var recorder = new LoaderEventRecorder(loader);

        await loader.AddForeign(stalePackage);
        await loader.ReloadPackages();

        var changeEvents = recorder.Changes.Skip(1).ToArray();

        Assert.True(recorder.StartedLoading);
        Assert.True(recorder.FinishedLoading);
        Assert.Equal(1, recorder.StartedLoadingCount);
        Assert.Equal(1, recorder.FinishedLoadingCount);
        Assert.True(loader.IsLoaded);
        Assert.False(loader.IsLoading);
        Assert.Equal(["Contoso.Accepted"], loader.Packages.Select(package => package.Id).Distinct());
        Assert.Single(loader.Packages);
        Assert.Equal(2, changeEvents.Length);
        Assert.False(changeEvents[0].ProceduralChange);
        Assert.Empty(changeEvents[0].AddedPackages);
        Assert.True(changeEvents[1].ProceduralChange);
        Assert.Equal(["Contoso.Accepted"], changeEvents[1].AddedPackages.Select(package => package.Id).Distinct());
    }
}
