#if WINDOWS
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Managers.PowerShell7Manager;

namespace UniGetUI.PackageEngine.Tests;

public sealed class PowerShell7ManagerTests
{
    [Fact]
    public void ParseInstalledPackages_BuildsPackagesFromTabDelimitedOutput()
    {
        var manager = new PowerShell7();

        var packages = PowerShell7.ParseInstalledPackages(
            [
                "##SCOPE:AllUsers##",
                "Pester\t5.7.1\tPSGallery",
                "##SCOPE:CurrentUser##",
                "PSReadLine\t2.2.5\tPSGallery",
            ],
            manager
        );

        Assert.Collection(
            packages,
            package =>
            {
                Assert.Equal("Pester", package.Id);
                Assert.Equal("5.7.1", package.VersionString);
                Assert.Equal("PSGallery", package.Source.Name);
                Assert.Equal(PackageScope.Machine, package.OverridenOptions.Scope);
            },
            package =>
            {
                Assert.Equal("PSReadLine", package.Id);
                Assert.Equal("2.2.5", package.VersionString);
                Assert.Equal("PSGallery", package.Source.Name);
                Assert.Equal(PackageScope.User, package.OverridenOptions.Scope);
            }
        );
    }

    [Fact]
    public void ParseInstalledPackages_SkipsBlankAndMalformedLines()
    {
        var manager = new PowerShell7();

        var package = Assert.Single(
            PowerShell7.ParseInstalledPackages(
                [
                    "##SCOPE:AllUsers##",
                    "",
                    "not-enough-columns",
                    "only\ttwo",
                    "\t\t",
                    "Pester\t5.7.1\tPSGallery",
                ],
                manager
            )
        );

        Assert.Equal("Pester", package.Id);
    }

    // Regression for https://github.com/Devolutions/UniGetUI/issues/4781:
    // the previous Format-Table-based pipeline truncated long names (e.g.
    // "Microsoft.Graph.Beta.DeviceManagement.Administration" → "Microsoft.Graph.Beta..")
    // which then poisoned the GetUpdates() URL sent to PSGallery, returning
    // NotFound and silently dropping every PS7 update from the list.
    [Fact]
    public void ParseInstalledPackages_PreservesLongNamesAndVersionsVerbatim()
    {
        var manager = new PowerShell7();

        var packages = PowerShell7.ParseInstalledPackages(
            [
                "##SCOPE:CurrentUser##",
                "Microsoft.Graph.Beta.DeviceManagement.Administration\t2.34.0\tPSGallery",
                "Az.RedisEnterpriseCache\t1.6.0\tPSGallery",
                "Microsoft.PowerShell.SecretManagement\t1.1.2\tPSGallery",
            ],
            manager
        );

        Assert.Collection(
            packages,
            package =>
            {
                Assert.Equal("Microsoft.Graph.Beta.DeviceManagement.Administration", package.Id);
                Assert.Equal("2.34.0", package.VersionString);
            },
            package =>
            {
                Assert.Equal("Az.RedisEnterpriseCache", package.Id);
                Assert.Equal("1.6.0", package.VersionString);
            },
            package =>
            {
                Assert.Equal("Microsoft.PowerShell.SecretManagement", package.Id);
                Assert.Equal("1.1.2", package.VersionString);
            }
        );
    }
}
#endif
