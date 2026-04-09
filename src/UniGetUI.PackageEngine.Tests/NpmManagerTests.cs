using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Managers.NpmManager;
using UniGetUI.PackageEngine.Serializable;
using UniGetUI.PackageEngine.Structs;
using UniGetUI.PackageEngine.Tests.Infrastructure.Assertions;
using UniGetUI.PackageEngine.Tests.Infrastructure.Builders;
using UniGetUI.PackageEngine.Tests.Infrastructure.Helpers;

namespace UniGetUI.PackageEngine.Tests;

public sealed class NpmManagerTests
{
    [Fact]
    public void ParseSearchOutputParsesJsonArrayAfterWarningPrefix()
    {
        var manager = new Npm();

        var packages = Npm.ParseSearchOutput(
            PackageEngineFixtureFiles.ReadAllText(@"Npm\search-array-with-warning.txt"),
            manager.DefaultSource,
            manager
        );

        var packageList = packages.ToArray();
        Assert.Equal(2, packageList.Length);
        PackageAssert.BelongsTo(packageList[0], manager, manager.DefaultSource);
        Assert.Equal("left-pad", packageList[0].Id);
        Assert.Equal("1.3.0", packageList[0].VersionString);
        PackageAssert.BelongsTo(packageList[1], manager, manager.DefaultSource);
        Assert.Equal("@types/node", packageList[1].Id);
        Assert.Equal("24.0.0", packageList[1].VersionString);
    }

    [Fact]
    public void ParseSearchOutputFallsBackToNdjsonAndSkipsInvalidEntries()
    {
        var manager = new Npm();

        var packages = Npm.ParseSearchOutput(
            PackageEngineFixtureFiles.ReadAllText(@"Npm\search-ndjson.txt"),
            manager.DefaultSource,
            manager
        );

        var packageList = packages.ToArray();
        Assert.Equal(2, packageList.Length);
        Assert.Equal("chalk", packageList[0].Id);
        Assert.Equal("5.4.1", packageList[0].VersionString);
        Assert.Equal("npm-check-updates", packageList[1].Id);
        Assert.Equal("17.1.1", packageList[1].VersionString);
    }

    [Fact]
    public void ParseAvailableUpdatesOutputCreatesPackagesWithRequestedScope()
    {
        var manager = new Npm();

        var packages = Npm.ParseAvailableUpdatesOutput(
            PackageEngineFixtureFiles.ReadAllText(@"Npm\outdated.json"),
            manager.DefaultSource,
            manager,
            new OverridenInstallationOptions(PackageScope.Global)
        );

        var package = Assert.Single(packages);
        PackageAssert.BelongsTo(package, manager, manager.DefaultSource);
        Assert.Equal("npm", package.Id);
        Assert.Equal("10.9.0", package.VersionString);
        Assert.Equal("11.0.0", package.NewVersionString);
        Assert.Equal(PackageScope.Global, package.OverridenOptions.Scope);
    }

    [Fact]
    public void ParseInstalledPackagesOutputCreatesPackagesWithRequestedScope()
    {
        var manager = new Npm();

        var packages = Npm.ParseInstalledPackagesOutput(
            PackageEngineFixtureFiles.ReadAllText(@"Npm\installed.json"),
            manager.DefaultSource,
            manager,
            new OverridenInstallationOptions(PackageScope.Local)
        );

        var package = Assert.Single(packages);
        PackageAssert.BelongsTo(package, manager, manager.DefaultSource);
        Assert.Equal("rimraf", package.Id);
        Assert.Equal("6.0.1", package.VersionString);
        Assert.Equal(PackageScope.Local, package.OverridenOptions.Scope);
    }

    [Fact]
    public void OperationHelperBuildsInstallParametersFromOptions()
    {
        var manager = new Npm();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("contoso-tool")
            .WithVersion("1.0.0")
            .Build();
        var options = new InstallOptions
        {
            Version = "2.0.0",
            InstallationScope = PackageScope.Global,
            PreRelease = true,
        };
        options.CustomParameters_Install.Add("--foreground-scripts");

        var parameters = manager.OperationHelper.GetParameters(package, options, OperationType.Install);

        Assert.Equal(
            [
                "install",
                OperatingSystem.IsWindows() ? "'contoso-tool@2.0.0'" : "contoso-tool@2.0.0",
                "--global",
                "--include",
                "dev",
                "--foreground-scripts",
            ],
            parameters
        );
    }

    [Fact]
    public void OperationHelperLetsPackageScopeOverrideUpdateScope()
    {
        var manager = new Npm();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("contoso-tool")
            .WithVersion("1.0.0")
            .WithNewVersion("3.0.0")
            .WithOptions(new OverridenInstallationOptions(PackageScope.Global))
            .Build();
        var options = new InstallOptions
        {
            InstallationScope = PackageScope.Local,
        };
        options.CustomParameters_Update.Add("--audit");

        var parameters = manager.OperationHelper.GetParameters(package, options, OperationType.Update);

        Assert.Equal(
            [
                "install",
                OperatingSystem.IsWindows() ? "'contoso-tool@3.0.0'" : "contoso-tool@3.0.0",
                "--global",
                "--audit",
            ],
            parameters
        );
    }

    [Fact]
    public void OperationHelperReturnsSuccessOnlyForZeroExitCode()
    {
        var manager = new Npm();
        var package = new PackageBuilder().WithManager(manager).Build();

        var success = manager.OperationHelper.GetResult(package, OperationType.Install, [], 0);
        var failure = manager.OperationHelper.GetResult(package, OperationType.Install, [], 1);

        Assert.Equal(OperationVeredict.Success, success);
        Assert.Equal(OperationVeredict.Failure, failure);
    }
}
