using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Managers.DotNetManager;
using UniGetUI.PackageEngine.Managers.Generic.NuGet.Internal;
using UniGetUI.PackageEngine.Serializable;
using UniGetUI.PackageEngine.Structs;
using UniGetUI.PackageEngine.Tests.Infrastructure.Builders;

namespace UniGetUI.PackageEngine.Tests;

public sealed class DotNetManagerTests
{
    // A `dotnet tool install` with neither --global nor --tool-path is a manifest-scoped LOCAL
    // install: it writes into a dotnet-tools.json tied to the working directory. UniGetUI runs
    // its own `dotnet tool list` from its install directory, so it can never enumerate a
    // manifest created anywhere else - the tool appears installed once and then vanishes, and
    // is never considered for updates. Global is therefore the default, and local must be asked
    // for explicitly.
    [Fact]
    public void InstallDefaultsToGlobalWhenNoScopeIsRequested()
    {
        var manager = new DotNet();
        var package = new PackageBuilder().WithManager(manager).WithId("dotnetsay").Build();

        var parameters = manager.OperationHelper.GetParameters(
            package,
            new InstallOptions(),
            OperationType.Install
        );

        Assert.Contains("--global", parameters);
    }

    [Theory]
    [InlineData(PackageScope.User, false)]
    [InlineData(PackageScope.Machine, true)]
    public void InstallHonoursAnExplicitlyRequestedScope(string scope, bool expectGlobal)
    {
        var manager = new DotNet();
        var package = new PackageBuilder().WithManager(manager).WithId("dotnetsay").Build();

        var parameters = manager.OperationHelper.GetParameters(
            package,
            new InstallOptions { InstallationScope = scope },
            OperationType.Install
        );

        Assert.Equal(expectGlobal, parameters.Contains("--global"));
    }

    [Theory]
    [InlineData(PackageScope.Local, false)]
    [InlineData(PackageScope.Global, true)]
    public void OperationsPreserveTheScopeAnInstalledToolWasFoundIn(
        string scope,
        bool expectGlobal
    )
    {
        var manager = new DotNet();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("dotnetsay")
            .WithOptions(new OverridenInstallationOptions(scope))
            .Build();

        foreach (var operation in new[] { OperationType.Update, OperationType.Uninstall })
        {
            var parameters = manager.OperationHelper.GetParameters(
                package,
                new InstallOptions(),
                operation
            );

            Assert.Equal(expectGlobal, parameters.Contains("--global"));
        }
    }

    // dotnet rejects --global and --tool-path together, so a custom location must suppress the
    // global default rather than produce a command line that cannot run.
    [Fact]
    public void ACustomInstallLocationSuppressesTheGlobalFlag()
    {
        var manager = new DotNet();
        var package = new PackageBuilder().WithManager(manager).WithId("dotnetsay").Build();

        var parameters = manager.OperationHelper.GetParameters(
            package,
            new InstallOptions { CustomInstallLocation = "C:\\tools" },
            OperationType.Install
        );

        Assert.Contains("--tool-path", parameters);
        Assert.DoesNotContain("--global", parameters);
    }

    [Fact]
    public void ACustomInstallLocationSuppressesTheGlobalFlagEvenWhenGlobalIsRequested()
    {
        var manager = new DotNet();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("dotnetsay")
            .WithOptions(new OverridenInstallationOptions(PackageScope.Global))
            .Build();

        var parameters = manager.OperationHelper.GetParameters(
            package,
            new InstallOptions { CustomInstallLocation = "C:\\tools" },
            OperationType.Install
        );

        Assert.Contains("--tool-path", parameters);
        Assert.DoesNotContain("--global", parameters);
    }

    [Fact]
    public void DefaultAndKnownSourcesPointAtTheNuGetV3ServiceIndex()
    {
        var manager = new DotNet();

        Assert.Equal(
            "https://api.nuget.org/v3/index.json",
            manager.Properties.DefaultSource.Url.AbsoluteUri
        );
        Assert.All(
            manager.Properties.KnownSources,
            source =>
                Assert.Equal("https://api.nuget.org/v3/index.json", source.Url.AbsoluteUri)
        );
        Assert.True(NuGetV3ServiceIndex.IsV3Source(manager.Properties.DefaultSource));
    }

    [Fact]
    public void ParseInstalledPackages_SkipsHeadersAndFalseRows()
    {
        var manager = new DotNet();
        var packages = DotNet.ParseInstalledPackages(
            [
                "Package Id      Version      Commands",
                "--------------------------------------",
                "dotnetsay       2.1.7        dotnetsay",
                "               1.0.0",
                "try-convert     0.9.232202",
            ],
            manager.DefaultSource,
            manager,
            new OverridenInstallationOptions(PackageScope.Local)
        );

        Assert.Collection(
            packages,
            package =>
            {
                Assert.Equal("dotnetsay", package.Id);
                Assert.Equal("2.1.7", package.VersionString);
                Assert.Equal(PackageScope.Local, package.OverridenOptions.Scope);
            },
            package =>
            {
                Assert.Equal("try-convert", package.Id);
                Assert.Equal("0.9.232202", package.VersionString);
            }
        );
    }

    [Fact]
    public void ParseInstalledPackages_PreservesRequestedScope()
    {
        var manager = new DotNet();
        var package = Assert.Single(
            DotNet.ParseInstalledPackages(
                [
                    "Package Id      Version      Commands",
                    "--------------------------------------",
                    "dotnetsay       2.1.7        dotnetsay",
                ],
                manager.DefaultSource,
                manager,
                new OverridenInstallationOptions(PackageScope.Global)
            )
        );

        Assert.Equal(PackageScope.Global, package.OverridenOptions.Scope);
    }
}
