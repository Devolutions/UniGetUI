using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.PackageLoader;
using UniGetUI.PackageEngine.Serializable;
using UniGetUI.PackageEngine.Tests.Infrastructure.Assertions;
using UniGetUI.PackageEngine.Tests.Infrastructure.Builders;
using UniGetUI.PackageEngine.Tests.Infrastructure.Helpers;

namespace UniGetUI.PackageEngine.Tests;

public sealed class HarnessSmokeTests
{
    [Fact]
    public void PackageManagerBuilderCreatesReadyManagerWithDeterministicSources()
    {
        var manager = new PackageManagerBuilder()
            .WithName("Harness")
            .WithDisplayName("Harness Manager")
            .WithSources(manager =>
            [
                new SourceBuilder()
                    .WithManager(manager)
                    .WithName("community")
                    .WithUrl("https://example.test/community")
                    .WithPackageCount(42)
                    .Build(),
            ])
            .Build();

        Assert.True(manager.IsReady());
        Assert.Equal("Harness", manager.Name);
        Assert.Equal("Harness Manager", manager.DisplayName);
        var source = Assert.Single(manager.SourcesHelper.GetSources());
        Assert.Equal("community", source.Name);
        Assert.Same(source, manager.SourcesHelper.Factory.GetSourceIfExists("community"));
    }

    [Fact]
    public async Task PackageDetailsBuilderPopulatesPackageThroughFakeHelper()
    {
        var manager = new PackageManagerBuilder()
            .ConfigureDetails(helper =>
                helper.DetailsFactory = package =>
                    new PackageDetailsBuilder()
                        .WithDescription("Deterministic package description")
                        .WithPublisher("Contoso")
                        .WithAuthor("UniGetUI Tests")
                        .WithHomepage("https://example.test/package")
                        .WithLicense("MIT", "https://example.test/license")
                        .WithInstaller(
                            "https://example.test/installer.exe",
                            "abc123",
                            "exe",
                            2048
                        )
                        .WithManifest("https://example.test/manifest.json")
                        .WithReleaseNotes(
                            "Smoke-test release notes",
                            "https://example.test/release-notes"
                        )
                        .WithUpdateDate("2026-01-01")
                        .WithTag("smoke")
                        .WithDependency("dep.one", "1.0.0")
                        .Build(package))
            .Build();
        var package = new PackageBuilder().WithManager(manager).WithId("Contoso.Test").Build();

        await package.Details.Load();

        Assert.True(package.Details.IsPopulated);
        Assert.Equal("Deterministic package description", package.Details.Description);
        Assert.Equal("Contoso", package.Details.Publisher);
        Assert.Single(package.Details.Dependencies);
    }

    [Fact]
    public async Task InstalledPackagesLoaderLoadsConfiguredPackagesWithoutLiveProcesses()
    {
        var manager = new PackageManagerBuilder()
            .WithInstalledPackages(manager =>
            [
                new PackageBuilder()
                    .WithManager(manager)
                    .WithName("Contoso Tool")
                    .WithId("Contoso.Tool")
                    .WithVersion("1.2.3")
                    .Build(),
            ])
            .Build();
        _ = new DiscoverablePackagesLoader([manager]);
        _ = new UpgradablePackagesLoader([manager]);
        var loader = new InstalledPackagesLoader([manager]);
        var recorder = new LoaderEventRecorder(loader);

        await loader.ReloadPackagesSilently();

        Assert.True(recorder.StartedLoading);
        Assert.True(recorder.FinishedLoading);
        var package = Assert.Single(loader.Packages);
        PackageAssert.Matches(package, "Contoso Tool", "Contoso.Tool", "1.2.3");
        Assert.Contains(recorder.AddedPackages, candidate => candidate.Id == "Contoso.Tool");
    }

    [Fact]
    public void OperationHelperReturnsConfiguredParametersAndVeredict()
    {
        var manager = new PackageManagerBuilder()
            .ConfigureOperation(helper =>
            {
                helper.ParametersFactory = (package, options, operation) =>
                [
                    operation.ToString().ToLowerInvariant(),
                    package.Id,
                    "--scope",
                    options.InstallationScope,
                ];
                helper.ResultFactory = (_, _, _, returnCode) =>
                    returnCode == 0 ? OperationVeredict.Success : OperationVeredict.Failure;
            })
            .Build();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithName("Contoso Tool")
            .WithId("Contoso.Tool")
            .Build();
        var options = new InstallOptions { InstallationScope = PackageScope.User };

        var parameters = manager.OperationHelper.GetParameters(package, options, OperationType.Install);
        var veredict = manager.OperationHelper.GetResult(
            package,
            OperationType.Install,
            ["completed"],
            0
        );

        OperationAssert.HasParameters(parameters, "install", "Contoso.Tool", "--scope", PackageScope.User);
        OperationAssert.HasVeredict(veredict, OperationVeredict.Success);
    }

    [Fact]
    public void FixtureHelperReadsSampleFixture()
    {
        var contents = PackageEngineFixtureFiles.ReadAllText("sample-manager-output.txt");

        Assert.Contains("Contoso.Tool", contents);
        Assert.Contains("1.2.3", contents);
    }
}
