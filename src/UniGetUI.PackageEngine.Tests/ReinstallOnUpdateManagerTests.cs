using UniGetUI.Core.SettingsEngine;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Managers.AptManager;
using UniGetUI.PackageEngine.Managers.BunManager;
using UniGetUI.PackageEngine.Managers.CargoManager;
using UniGetUI.PackageEngine.Managers.DnfManager;
using UniGetUI.PackageEngine.Managers.DotNetManager;
using UniGetUI.PackageEngine.Managers.FlatpakManager;
using UniGetUI.PackageEngine.Managers.HomebrewManager;
using UniGetUI.PackageEngine.Managers.MacPkgManager;
using UniGetUI.PackageEngine.Managers.NpmManager;
using UniGetUI.PackageEngine.Managers.PacmanManager;
using UniGetUI.PackageEngine.Managers.PipManager;
using UniGetUI.PackageEngine.Managers.SnapManager;
using UniGetUI.PackageEngine.Managers.VcpkgManager;
using UniGetUI.PackageEngine.Operations;
using UniGetUI.PackageEngine.Serializable;
using UniGetUI.PackageEngine.Structs;
using UniGetUI.PackageEngine.Tests.Infrastructure.Builders;
#if WINDOWS
using UniGetUI.PackageEngine.Managers.ChocolateyManager;
using UniGetUI.PackageEngine.Managers.PowerShell7Manager;
using UniGetUI.PackageEngine.Managers.PowerShellManager;
using UniGetUI.PackageEngine.Managers.ScoopManager;
using UniGetUI.PackageEngine.Managers.WingetManager;
using UniGetUI.PackageEngine.Managers.WinInstallerManager;
#endif

namespace UniGetUI.PackageEngine.Tests;

public class ReinstallOnUpdateManagerTests
{
    public static TheoryData<string, bool> ReinstallCapabilities()
    {
        var data = new TheoryData<string, bool>
        {
            { nameof(Npm), true },
            { nameof(Bun), true },
            { nameof(Pip), true },
            { nameof(DotNet), true },
            { nameof(Homebrew), true },
            { nameof(Pacman), true },
            { nameof(Flatpak), false },
            { nameof(Cargo), false },
            { nameof(Vcpkg), false },
            { nameof(Apt), false },
            { nameof(Dnf), false },
            { nameof(Snap), false },
            { nameof(MacPkg), false },
        };
#if WINDOWS
        data.Add(nameof(WinGet), true);
        data.Add(nameof(Scoop), true);
        data.Add(nameof(Chocolatey), true);
        data.Add(nameof(WinInstaller), true);
        data.Add(nameof(PowerShell), false);
        data.Add(nameof(PowerShell7), false);
#endif
        return data;
    }

    private static IPackageManager Create(string name) =>
        name switch
        {
            nameof(Npm) => new Npm(),
            nameof(Bun) => new Bun(),
            nameof(Pip) => new Pip(),
            nameof(DotNet) => new DotNet(),
            nameof(Homebrew) => new Homebrew(),
            nameof(Pacman) => new Pacman(),
            nameof(Flatpak) => new Flatpak(),
            nameof(Cargo) => new Cargo(),
            nameof(Vcpkg) => new Vcpkg(),
            nameof(Apt) => new Apt(),
            nameof(Dnf) => new Dnf(),
            nameof(Snap) => new Snap(),
            nameof(MacPkg) => new MacPkg(),
#if WINDOWS
            nameof(WinGet) => new WinGet(),
            nameof(Scoop) => new Scoop(),
            nameof(Chocolatey) => new Chocolatey(),
            nameof(WinInstaller) => new WinInstaller(),
            nameof(PowerShell) => new PowerShell(),
            nameof(PowerShell7) => new PowerShell7(),
#endif
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
        };

    [Theory]
    [MemberData(nameof(ReinstallCapabilities))]
    public void OnlyManagersWhoseUninstallIsSafeToUndoOfferReinstallOnUpdate(
        string managerName,
        bool expected
    )
    {
        var manager = Create(managerName);

        Assert.Equal(expected, manager.Capabilities.CanReinstallOnUpdate);
    }

    [Theory]
    [InlineData(nameof(Npm), PackageScope.Global, true)]
    [InlineData(nameof(Npm), PackageScope.Local, false)]
    [InlineData(nameof(Bun), PackageScope.Global, true)]
    [InlineData(nameof(Bun), PackageScope.Local, false)]
    public void NpmAndBunOfferReinstallOnlyForGlobalPackages(
        string managerName,
        string scope,
        bool expected
    )
    {
        var package = new PackageBuilder()
            .WithManager(Create(managerName))
            .WithId("contoso-tool")
            .WithVersion("1.0.0")
            .WithNewVersion("3.0.0")
            .WithOptions(new OverridenInstallationOptions(scope))
            .Build();

        Assert.Equal(expected, PackageOperation.SupportsUninstallBeforeUpdate(package));
    }

#if WINDOWS
    [Fact]
    public void WinGetReinstallKeepsTheArchitectureANormalUpdateWouldUse()
    {
        var package = new PackageBuilder()
            .WithManager(new WinGet())
            .WithId("Contoso.Tool")
            .WithName("Contoso Tool 32-bit")
            .WithVersion("1.0.0")
            .WithNewVersion("3.0.0")
            .Build();

        using var operation = new UpdatePackageOperation(
            package,
            new InstallOptions { UninstallBeforeUpdate = true, Architecture = Architecture.x64 }
        );

        Assert.Equal(Architecture.x86, operation.Options.Architecture);
    }

    [Fact]
    public void WinInstallerReinstallInstallsTheUpdateTargetRatherThanASavedVersion()
    {
        var manager = new WinInstaller();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Tool")
            .WithVersion("1.0.0")
            .WithNewVersion("3.0.0")
            .Build();

        using var operation = new UpdatePackageOperation(
            package,
            new InstallOptions { UninstallBeforeUpdate = true, Version = "1.0.0" }
        );

        Assert.False(manager.Capabilities.SupportsCustomVersionsOnUpdate);
        Assert.Equal("3.0.0", operation.Options.Version);
    }
#endif
}

#if WINDOWS
[Collection(WinGetManagerTestCollection.Name)]
public class WinGetReinstallLocationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WinGetReinstallUsesTheLocationANormalUpdateWouldUse(bool locationIsExplicit)
    {
        string expectedLocation =
            locationIsExplicit || Settings.Get(Settings.K.WinGetForceLocationOnUpdate)
                ? "D:\\Apps\\Contoso"
                : "";
        var manager = new WinGet();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId($"Contoso.NotInstalled{Guid.NewGuid():N}")
            .WithVersion("1.0.0")
            .WithNewVersion("3.0.0")
            .Build();
        var options = new InstallOptions
        {
            UninstallBeforeUpdate = true,
            CustomInstallLocation = "D:\\Apps\\Contoso",
            CustomInstallLocationIsExplicit = locationIsExplicit,
        };

        using var operation = new UpdatePackageOperation(package, options);

        Assert.Equal(expectedLocation, operation.Options.CustomInstallLocation);
        Assert.Equal("D:\\Apps\\Contoso", options.CustomInstallLocation);
    }
}
#endif
