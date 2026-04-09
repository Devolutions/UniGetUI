using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Managers.DotNetManager;
using UniGetUI.PackageEngine.Structs;

namespace UniGetUI.PackageEngine.Tests;

public sealed class DotNetManagerTests
{
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
