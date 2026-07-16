#if WINDOWS
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Managers.PowerShellManager;
using UniGetUI.PackageEngine.Serializable;

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

    private static UniGetUI.PackageEngine.Interfaces.IPackage BuildInstalledPackage(PowerShell manager)
        => Assert.Single(PowerShell.ParseInstalledPackages(
            [
                "Version Name Repository Description",
                "------- ---- ---------- -----------",
                "1.0.0 Devolutions.PowerShell PSGallery x",
            ],
            manager));

    [Fact]
    public void GetParameters_InstallRespectsExplicitScope()
    {
        var manager = new PowerShell();
        var package = BuildInstalledPackage(manager);

        var options = new InstallOptions { InstallationScope = PackageScope.Machine };
        var parameters = manager.OperationHelper.GetParameters(package, options, OperationType.Install);

        Assert.Contains("-Scope", parameters);
        Assert.Contains("AllUsers", parameters);
    }

    // Regression for https://github.com/Devolutions/UniGetUI/issues/5110:
    // Update-Module (Windows PowerShell 5.x / PowerShellGet 1.0.0.1) has no -Scope parameter,
    // so no scope must be emitted for an update regardless of the selected scope.
    [Fact]
    public void GetParameters_UpdateOmitsScope()
    {
        var manager = new PowerShell();
        var package = BuildInstalledPackage(manager);

        var options = new InstallOptions { InstallationScope = PackageScope.Machine };
        var parameters = manager.OperationHelper.GetParameters(package, options, OperationType.Update);

        Assert.DoesNotContain("-Scope", parameters);
    }

    [Fact]
    public void Capabilities_ScopeAppliesToInstallOnly()
    {
        var manager = new PowerShell();
        Assert.True(manager.Capabilities.SupportsCustomScopes);
        Assert.False(manager.Capabilities.SupportsCustomScopesOnUpdate);
        Assert.False(manager.Capabilities.SupportsCustomScopesOnUninstall);
    }
}
#endif
