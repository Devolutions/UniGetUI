#if WINDOWS
using UniGetUI.PackageEngine.Managers.PowerShellManager;

namespace UniGetUI.PackageEngine.Tests;

public sealed class PowerShellManagerTests
{
    [Fact]
    public void ParseInstalledPackages_BuildsPackagesFromModuleTable()
    {
        var manager = new PowerShell();
        var packages = PowerShell.ParseInstalledPackages(
            [
                "Version Name Repository Description",
                "------- ---- ---------- -----------",
                "5.5.0 Pester PSGallery Test framework",
                "2.2.5 PSReadLine PSGallery Command line editing",
            ],
            manager
        );

        Assert.Collection(
            packages,
            package =>
            {
                Assert.Equal("Pester", package.Id);
                Assert.Equal("5.5.0", package.VersionString);
                Assert.Equal("PSGallery", package.Source.Name);
            },
            package =>
            {
                Assert.Equal("PSReadLine", package.Id);
                Assert.Equal("2.2.5", package.VersionString);
                Assert.Equal("PSGallery", package.Source.Name);
            }
        );
    }

    [Fact]
    public void ParseInstalledPackages_SkipsMalformedLines()
    {
        var manager = new PowerShell();

        var package = Assert.Single(
            PowerShell.ParseInstalledPackages(
                [
                    "Version Name Repository Description",
                    "------- ---- ---------- -----------",
                    "not-enough-columns",
                    "5.5.0 Pester PSGallery Test framework",
                ],
                manager
            )
        );

        Assert.Equal("Pester", package.Id);
    }
}
#endif
