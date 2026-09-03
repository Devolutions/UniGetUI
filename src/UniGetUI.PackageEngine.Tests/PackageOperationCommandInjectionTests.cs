#if WINDOWS
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Managers.NpmManager;
using UniGetUI.PackageEngine.Managers.PowerShell7Manager;
using UniGetUI.PackageEngine.Managers.PowerShellManager;
using UniGetUI.PackageEngine.Managers.ScoopManager;
using UniGetUI.PackageEngine.Serializable;

namespace UniGetUI.PackageEngine.Tests;

public sealed class PackageOperationCommandInjectionTests
{
    private const string VersionPayload =
        @"1.2.3.4.5; Start-Process calc; New-Item -Force C:\Users\ops\f02_win_marker.txt | Out-Null; Write-Output F02_WIN_RCE_OBSERVED";

    private const string IdPayload = "powershell-yaml; Start-Process calc";

    private static IPackage WinPowerShellPackage(PowerShell manager, string id = "Devolutions.PowerShell")
        => Assert.Single(PowerShell.ParseInstalledPackages(
            [
                "Version Name Repository Description",
                "------- ---- ---------- -----------",
                $"1.0.0 {id} PSGallery x",
            ],
            manager));

    private static IPackage PowerShell7Package(PowerShell7 manager, string id = "Devolutions.PowerShell")
        => Assert.Single(PowerShell7.ParseInstalledPackages(
            ["##SCOPE:CurrentUser##", $"{id}\t1.0.0\tPSGallery"],
            manager));

    [Fact]
    public void WindowsPowerShell_RefusesAVersionCarryingAStatementSeparator()
    {
        var manager = new PowerShell();
        var package = WinPowerShellPackage(manager);
        var options = new InstallOptions { Version = VersionPayload };

        Assert.Throws<InvalidOperationException>(
            () => manager.OperationHelper.GetParameters(package, options, OperationType.Install)
        );
    }

    [Fact]
    public void PowerShell7_RefusesAVersionCarryingAStatementSeparator()
    {
        var manager = new PowerShell7();
        var package = PowerShell7Package(manager);
        var options = new InstallOptions { Version = VersionPayload };

        Assert.Throws<InvalidOperationException>(
            () => manager.OperationHelper.GetParameters(package, options, OperationType.Install)
        );
    }

    [Theory]
    [InlineData(OperationType.Install)]
    [InlineData(OperationType.Update)]
    [InlineData(OperationType.Uninstall)]
    public void WindowsPowerShell_RefusesAnIdentifierCarryingAStatementSeparator(OperationType operation)
    {
        var manager = new PowerShell();
        var package = WinPowerShellPackage(manager, IdPayload);

        Assert.Throws<InvalidOperationException>(
            () => manager.OperationHelper.GetParameters(package, new InstallOptions(), operation)
        );
    }

    [Theory]
    [InlineData(OperationType.Install)]
    [InlineData(OperationType.Update)]
    [InlineData(OperationType.Uninstall)]
    public void Scoop_RefusesAnIdentifierCarryingAStatementSeparator(OperationType operation)
    {
        var manager = new Scoop();
        var package = new PackageClasses.Package("Payload", IdPayload, "1.0.0", manager.DefaultSource, manager);

        Assert.Throws<InvalidOperationException>(
            () => manager.OperationHelper.GetParameters(package, new InstallOptions(), operation)
        );
    }

    [Fact]
    public void Npm_RefusesAVersionThatBreaksOutOfTheShellQuotes()
    {
        var manager = new Npm();
        var package = new PackageClasses.Package("Payload", "express", "1.0.0", manager.DefaultSource, manager);
        var options = new InstallOptions { Version = "1.0.0'; Start-Process calc; '" };

        Assert.Throws<InvalidOperationException>(
            () => manager.OperationHelper.GetParameters(package, options, OperationType.Install)
        );
    }

    [Fact]
    public void Npm_KeepsScopedPackageNamesInstallable()
    {
        var manager = new Npm();
        var package = new PackageClasses.Package("Babel", "@babel/core", "7.0.0", manager.DefaultSource, manager);
        var options = new InstallOptions { Version = "7.24.0" };

        var parameters = manager.OperationHelper.GetParameters(package, options, OperationType.Install);

        Assert.Contains(
            "@babel/core@7.24.0",
            parameters
        );
    }

    [Fact]
    public void WindowsPowerShell_KeepsALegitimateVersionPinned()
    {
        var manager = new PowerShell();
        var package = WinPowerShellPackage(manager);
        var options = new InstallOptions { Version = "1.2.3-preview1" };

        var parameters = manager.OperationHelper.GetParameters(package, options, OperationType.Install);

        var index = parameters.ToList().IndexOf("-RequiredVersion");
        Assert.True(index >= 0);
        Assert.Equal("1.2.3-preview1", parameters[index + 1]);
    }

    [Fact]
    public void PowerShell7_RefusesToUninstallWithAnInstalledVersionThatIsNotAVersion()
    {
        var manager = new PowerShell7();
        var package = new PackageClasses.Package(
            "Payload",
            "powershell-yaml",
            "1.0.0; Start-Process calc",
            manager.DefaultSource,
            manager
        );

        Assert.Throws<InvalidOperationException>(
            () => manager.OperationHelper.GetParameters(package, new InstallOptions(), OperationType.Uninstall)
        );
    }
}
#endif
