using Devolutions.Now.Policy.Api;
using UniGetUI.PackageEngine.AgentBroker;
using UniGetUI.PackageEngine.Serializable;
using UniGetUI.PackageEngine.Tests.Infrastructure.Builders;
using OperationType = UniGetUI.PackageEngine.Enums.OperationType;
using PackageScope = UniGetUI.PackageEngine.Enums.PackageScope;
using UniGetUIArchitecture = UniGetUI.PackageEngine.Enums.Architecture;

namespace UniGetUI.PackageEngine.Tests;

public class BrokerRequestBuilderTests
{
    private static UniGetUI.PackageEngine.PackageClasses.Package BuildWinGetPackage()
        => new PackageBuilder()
            .WithManager(new PackageManagerBuilder().WithName("Winget").Build())
            .WithId("Contoso.Test")
            .Build();

    private static UniGetUI.PackageEngine.PackageClasses.Package BuildPowerShellPackage()
        => new PackageBuilder()
            .WithManager(new PackageManagerBuilder().WithName("PowerShell").Build())
            .WithId("PowerShellGet")
            .Build();

    [Theory]
    [InlineData(OperationType.Install, Operation.Install)]
    [InlineData(OperationType.Update, Operation.Update)]
    [InlineData(OperationType.Uninstall, Operation.Uninstall)]
    public void Build_MapsOperationType(OperationType role, Operation expected)
    {
        var request = BrokerRequestBuilder.Build(BuildWinGetPackage(), new InstallOptions(), role);
        Assert.Equal(expected, request.Operation);
    }

    [Theory]
    [InlineData("Winget", ManagerName.Winget)]
    [InlineData("PowerShell", ManagerName.PowerShell)]
    [InlineData("PowerShell7", ManagerName.PowerShell7)]
    [InlineData("Apt", ManagerName.Apt)]
    [InlineData("Bun", ManagerName.Bun)]
    [InlineData("Cargo", ManagerName.Cargo)]
    [InlineData("Chocolatey", ManagerName.Chocolatey)]
    [InlineData("Dnf", ManagerName.Dnf)]
    [InlineData(".NET Tool", ManagerName.Dotnet)]
    [InlineData("Flatpak", ManagerName.Flatpak)]
    [InlineData("Homebrew", ManagerName.Homebrew)]
    [InlineData("Npm", ManagerName.Npm)]
    [InlineData("Pacman", ManagerName.Pacman)]
    [InlineData("Pip", ManagerName.Pip)]
    [InlineData("Scoop", ManagerName.Scoop)]
    [InlineData("Snap", ManagerName.Snap)]
    [InlineData("vcpkg", ManagerName.Vcpkg)]
    public void Build_MapsSupportedManagers(string managerName, ManagerName expected)
    {
        var package = new PackageBuilder()
            .WithManager(new PackageManagerBuilder().WithName(managerName).Build())
            .WithId("contoso-test")
            .Build();

        var request = BrokerRequestBuilder.Build(package, new InstallOptions(), OperationType.Install);
        Assert.Equal(expected, request.Manager);
    }

    [Fact]
    public void Build_ThrowsForUnsupportedManager()
    {
        var package = new PackageBuilder()
            .WithManager(new PackageManagerBuilder().WithName("NotARealManager").Build())
            .Build();

        Assert.Throws<ArgumentException>(
            () => BrokerRequestBuilder.Build(package, new InstallOptions(), OperationType.Install));
    }

    [Theory]
    [InlineData("Winget", true)]
    [InlineData("Chocolatey", true)]
    [InlineData("Scoop", true)]
    [InlineData("NotARealManager", false)]
    public void SupportsManager_MatchesMapping(string managerName, bool expected)
    {
        Assert.Equal(expected, BrokerRequestBuilder.SupportsManager(managerName));
    }

    [Fact]
    public void Build_UsesSavedInstallationScope()
    {
        var options = new InstallOptions { InstallationScope = PackageScope.Machine };
        var request = BrokerRequestBuilder.Build(BuildWinGetPackage(), options, OperationType.Install);
        Assert.Equal(Scope.Machine, request.Options.Scope);
    }

    [Fact]
    public void Build_PackageScopeOverride_TakesPrecedenceOverSavedScope()
    {
        var package = BuildWinGetPackage();
        package.OverridenOptions.Scope = PackageScope.User;
        var options = new InstallOptions { InstallationScope = PackageScope.Machine };

        var request = BrokerRequestBuilder.Build(package, options, OperationType.Install);
        Assert.Equal(Scope.User, request.Options.Scope);
    }

    [Fact]
    public void Build_DropArchAndScopeRetry_OmitsScopeAndArchitecture()
    {
        var package = BuildWinGetPackage();
        package.OverridenOptions.WinGet_DropArchAndScope = true;
        var options = new InstallOptions
        {
            InstallationScope = PackageScope.Machine,
            Architecture = UniGetUIArchitecture.x64,
        };

        var request = BrokerRequestBuilder.Build(package, options, OperationType.Update);

        Assert.Null(request.Options.Scope);
        Assert.Null(request.Package.Architecture);
    }

    [Fact]
    public void Build_AllowClobberRetry_AddsTheParameterForPowerShell5Installs()
    {
        var package = BuildPowerShellPackage();
        package.OverridenOptions.PowerShell_AllowClobber = true;

        var request = BrokerRequestBuilder.Build(
            package,
            new InstallOptions { CustomParameters_Install = ["-Proxy", "http://proxy"] },
            OperationType.Install
        );

        Assert.Equal(["-Proxy", "http://proxy", "-AllowClobber"], request.Options.CustomParameters);
    }

    [Fact]
    public void Build_AllowClobberRetry_LeavesTheSavedCustomParametersUntouched()
    {
        var package = BuildPowerShellPackage();
        package.OverridenOptions.PowerShell_AllowClobber = true;
        var options = new InstallOptions { CustomParameters_Install = ["-Proxy"] };

        BrokerRequestBuilder.Build(package, options, OperationType.Install);

        Assert.Equal(["-Proxy"], options.CustomParameters_Install);
    }

    [Theory]
    [InlineData(OperationType.Update)]
    [InlineData(OperationType.Uninstall)]
    public void Build_AllowClobberRetry_IsInstallOnly(OperationType role)
    {
        var package = BuildPowerShellPackage();
        package.OverridenOptions.PowerShell_AllowClobber = true;

        var request = BrokerRequestBuilder.Build(package, new InstallOptions(), role);

        Assert.DoesNotContain("-AllowClobber", request.Options.CustomParameters);
    }

    [Theory]
    [InlineData("x86", Architecture.X86)]
    [InlineData("x64", Architecture.X64)]
    [InlineData("arm64", Architecture.Arm64)]
    public void Build_MapsArchitecture(string architecture, Architecture expected)
    {
        var options = new InstallOptions { Architecture = architecture };
        var request = BrokerRequestBuilder.Build(BuildWinGetPackage(), options, OperationType.Install);
        Assert.Equal(expected, request.Package.Architecture);
    }

    [Theory]
    [InlineData(OperationType.Install, "--install-param")]
    [InlineData(OperationType.Update, "--update-param")]
    [InlineData(OperationType.Uninstall, "--uninstall-param")]
    public void Build_SelectsCustomParametersForRole(OperationType role, string expected)
    {
        var options = new InstallOptions
        {
            CustomParameters_Install = ["--install-param"],
            CustomParameters_Update = ["--update-param"],
            CustomParameters_Uninstall = ["--uninstall-param"],
        };

        var request = BrokerRequestBuilder.Build(BuildWinGetPackage(), options, role);
        Assert.Equal([expected], request.Options.CustomParameters);
    }

    [Theory]
    [InlineData(OperationType.Install, "pre-install.cmd", "post-install.cmd")]
    [InlineData(OperationType.Update, "pre-update.cmd", "post-update.cmd")]
    [InlineData(OperationType.Uninstall, "pre-uninstall.cmd", "post-uninstall.cmd")]
    public void Build_SelectsPrePostCommandsForRole(OperationType role, string expectedPre, string expectedPost)
    {
        var options = new InstallOptions
        {
            PreInstallCommand = "pre-install.cmd",
            PostInstallCommand = "post-install.cmd",
            PreUpdateCommand = "pre-update.cmd",
            PostUpdateCommand = "post-update.cmd",
            PreUninstallCommand = "pre-uninstall.cmd",
            PostUninstallCommand = "post-uninstall.cmd",
        };

        var request = BrokerRequestBuilder.Build(BuildWinGetPackage(), options, role);
        Assert.Equal(expectedPre, request.Options.PreOperationCommand);
        Assert.Equal(expectedPost, request.Options.PostOperationCommand);
    }

    [Fact]
    public void Build_UsesEffectiveInstallLocation_NotSavedOptions()
    {
        var options = new InstallOptions { CustomInstallLocation = @"C:\stale\location" };

        var request = BrokerRequestBuilder.Build(
            BuildWinGetPackage(), options, OperationType.Update, @"C:\actual\portable\location");

        Assert.Equal(@"C:\actual\portable\location", request.Options.CustomInstallLocation);
    }

    [Fact]
    public void Build_OmitsInstallLocation_WhenNoneResolved()
    {
        var options = new InstallOptions { CustomInstallLocation = @"C:\stale\location" };
        var request = BrokerRequestBuilder.Build(BuildWinGetPackage(), options, OperationType.Update);
        Assert.Null(request.Options.CustomInstallLocation);
    }

    [Fact]
    public void Build_DoesNotMapSkipMinorUpdatesToNoUpgrade()
    {
        var options = new InstallOptions { SkipMinorUpdates = true };
        var request = BrokerRequestBuilder.Build(BuildWinGetPackage(), options, OperationType.Update);
        Assert.False(request.Options.NoUpgrade);
    }

    [Theory]
    [InlineData(OperationType.Install, false)]
    [InlineData(OperationType.Update, true)]
    [InlineData(OperationType.Uninstall, false)]
    public void Build_SetsUninstallPreviousOnlyForUpdates(OperationType role, bool expected)
    {
        var options = new InstallOptions { UninstallPreviousVersionsOnUpdate = true };
        var request = BrokerRequestBuilder.Build(BuildWinGetPackage(), options, role);
        Assert.Equal(expected, request.Options.UninstallPrevious);
    }

    [Fact]
    public void Build_CarriesKillBeforeOperationProcesses()
    {
        var options = new InstallOptions { KillBeforeOperation = ["app.exe", "helper.exe"] };
        var request = BrokerRequestBuilder.Build(BuildWinGetPackage(), options, OperationType.Install);
        Assert.Equal(["app.exe", "helper.exe"], request.Options.KillBeforeOperation);
    }

    [Fact]
    public void Build_MapsSourceAndPackageIdentity()
    {
        var package = BuildWinGetPackage();
        var options = new InstallOptions { Version = "1.2.3" };

        var request = BrokerRequestBuilder.Build(package, options, OperationType.Install);

        Assert.Equal("Contoso.Test", request.Package.Id);
        Assert.Equal("1.2.3", request.Package.Version);
        Assert.Equal(package.Source.Name, request.Source.Name);
    }

    [Theory]
    [InlineData("PowerShell")]
    [InlineData("PowerShell7")]
    [InlineData("Scoop")]
    [InlineData("Npm")]
    public void Build_RefusesAnInjectedVersionForShellInterpretedManagers(string managerName)
    {
        var package = new PackageBuilder()
            .WithManager(new PackageManagerBuilder().WithName(managerName).Build())
            .WithId("powershell-yaml")
            .Build();
        var options = new InstallOptions { Version = "1.2.3; Start-Process calc" };

        Assert.Throws<InvalidOperationException>(
            () => BrokerRequestBuilder.Build(package, options, OperationType.Install)
        );
    }

    [Fact]
    public void Build_RefusesAnInjectedIdentifierForShellInterpretedManagers()
    {
        var package = new PackageBuilder()
            .WithManager(new PackageManagerBuilder().WithName("PowerShell").Build())
            .WithId("powershell-yaml; Start-Process calc")
            .Build();

        Assert.Throws<InvalidOperationException>(
            () => BrokerRequestBuilder.Build(package, new InstallOptions(), OperationType.Install)
        );
    }

    [Fact]
    public void Build_RefusesWinGetVersionsThatTheBrokerRejects()
    {
        var package = new PackageBuilder()
            .WithManager(new PackageManagerBuilder().WithName("Winget").Build())
            .WithId("Contoso.Test")
            .Build();
        var options = new InstallOptions { Version = "2021 Update" };

        var exception = Assert.Throws<BrokerRequestValidationException>(
            () => BrokerRequestBuilder.Build(package, options, OperationType.Install));

        Assert.Contains(exception.Issues, issue => issue.Contains("2021 Update"));
    }

    private static UniGetUI.PackageEngine.PackageClasses.Package BuildPackage(
        string managerName,
        string id = "contoso-tool",
        string version = "1.2.3",
        string? newVersion = null)
    {
        var builder = new PackageBuilder()
            .WithManager(new PackageManagerBuilder().WithName(managerName).Build())
            .WithId(id)
            .WithVersion(version);
        if (newVersion is not null)
            builder = builder.WithNewVersion(newVersion);
        return builder.Build();
    }

    [Theory]
    [InlineData("Winget")]
    [InlineData("Chocolatey")]
    [InlineData("Npm")]
    [InlineData("Cargo")]
    [InlineData(".NET Tool")]
    [InlineData("PowerShell")]
    [InlineData("PowerShell7")]
    [InlineData("Pip")]
    [InlineData("Bun")]
    public void Build_SendsTheListedVersionForInstallsWithoutAnExplicitVersion(string managerName)
    {
        var request = BrokerRequestBuilder.Build(
            BuildPackage(managerName), new InstallOptions(), OperationType.Install);

        Assert.Equal("1.2.3", request.Package.Version);
    }

    [Fact]
    public void Build_SendsTheTargetVersionForUpdates()
    {
        var package = BuildPackage("Winget", "Contoso.Test", "1.0.0", "2.0.0");

        var request = BrokerRequestBuilder.Build(package, new InstallOptions(), OperationType.Update);

        Assert.Equal("2.0.0", request.Package.Version);
    }

    [Fact]
    public void Build_PrefersTheExplicitlySelectedVersion()
    {
        var request = BrokerRequestBuilder.Build(
            BuildPackage("Npm"), new InstallOptions { Version = "1.0.0" }, OperationType.Install);

        Assert.Equal("1.0.0", request.Package.Version);
    }

    [Theory]
    [InlineData("Winget")]
    [InlineData("Npm")]
    [InlineData("Pip")]
    public void Build_NeverSendsAVersionForUninstalls(string managerName)
    {
        var request = BrokerRequestBuilder.Build(
            BuildPackage(managerName), new InstallOptions(), OperationType.Uninstall);

        Assert.Null(request.Package.Version);
    }

    [Theory]
    [InlineData("Scoop")]
    [InlineData("vcpkg")]
    public void Build_NeverSendsAVersionForManagersThatPinTheirOwn(string managerName)
    {
        var request = BrokerRequestBuilder.Build(
            BuildPackage(managerName), new InstallOptions(), OperationType.Install);

        Assert.Null(request.Package.Version);
    }

    [Fact]
    public void Build_LeavesTheVersionUnsetForPreReleaseInstalls()
    {
        var request = BrokerRequestBuilder.Build(
            BuildPackage("Winget", "Contoso.Test"), new InstallOptions { PreRelease = true }, OperationType.Install);

        Assert.Null(request.Package.Version);
    }

    [Theory]
    [InlineData("Winget", "Unknown")]
    [InlineData("Winget", "< 1.2")]
    [InlineData("Winget", "2021 Update")]
    [InlineData("Bun", "1.2")]
    [InlineData("Npm", "^1.2.0")]
    public void Build_OmitsAListedVersionTheBrokerWouldNotAccept(string managerName, string listedVersion)
    {
        var request = BrokerRequestBuilder.Build(
            BuildPackage(managerName, version: listedVersion), new InstallOptions(), OperationType.Install);

        Assert.Null(request.Package.Version);
    }

    [Theory]
    [InlineData("Npm", "^1.2.0")]
    [InlineData("Npm", ">=1.0.0")]
    [InlineData("Cargo", "<2")]
    [InlineData("Bun", "1.2")]
    [InlineData("PowerShell", "[1.0,2.0)")]
    [InlineData("Scoop", "1.2.3")]
    public void Build_RefusesAnExplicitVersionTheBrokerWouldReject(string managerName, string version)
    {
        Assert.ThrowsAny<InvalidOperationException>(() => BrokerRequestBuilder.Build(
            BuildPackage(managerName), new InstallOptions { Version = version }, OperationType.Install));
    }

    [Theory]
    [InlineData("Npm", "1.2.x")]
    [InlineData("Npm", "1.x")]
    [InlineData("Cargo", "=1.2.3")]
    [InlineData(".NET Tool", "[1.0,2.0)")]
    [InlineData("Pip", "1!2.0")]
    public void Build_KeepsVersionSyntaxTheBrokerAccepts(string managerName, string version)
    {
        var request = BrokerRequestBuilder.Build(
            BuildPackage(managerName), new InstallOptions { Version = version }, OperationType.Install);

        Assert.Equal(version, request.Package.Version);
    }

    [Theory]
    [InlineData("C:\\Tools\\..\\Windows")]
    [InlineData("C:\\Tools\\.")]
    [InlineData("C:\\Tools\\App.")]
    [InlineData("C:\\Tools\\App ")]
    [InlineData("C:\\Tools\\file.txt:stream")]
    [InlineData("Tools\\App")]
    [InlineData("\\\\server\\share\\App")]
    [InlineData("\\\\?\\C:\\Tools")]
    [InlineData("C:Tools")]
    [InlineData("C:\\Tools\\%APPDATA%")]
    public void Build_RefusesInstallLocationsTheBrokerRejects(string location)
    {
        Assert.Throws<BrokerRequestValidationException>(() => BrokerRequestBuilder.Build(
            BuildWinGetPackage(), new InstallOptions(), OperationType.Install, location));
    }

    [Theory]
    [InlineData("C:\\Program Files\\App v1.2")]
    [InlineData("d:/Tools//App/")]
    [InlineData("D:\\")]
    public void Build_KeepsPlainLocalInstallLocations(string location)
    {
        var request = BrokerRequestBuilder.Build(
            BuildWinGetPackage(), new InstallOptions(), OperationType.Install, location);

        Assert.Equal(location, request.Options.CustomInstallLocation);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Build_SendsNoInstallLocationForBlankValues(string location)
    {
        var request = BrokerRequestBuilder.Build(
            BuildWinGetPackage(), new InstallOptions(), OperationType.Install, location);

        Assert.Null(request.Options.CustomInstallLocation);
    }

    [Theory]
    [InlineData("Chocolatey", "git;7zip")]
    [InlineData("Chocolatey", "all")]
    [InlineData("Chocolatey", "packages.config")]
    [InlineData("Chocolatey", "git.nupkg")]
    [InlineData("Chocolatey", "C:\\pkgs\\git")]
    [InlineData("Scoop", "*")]
    [InlineData("Scoop", "7z*")]
    [InlineData("Scoop", "--all")]
    [InlineData("PowerShell", "Pester*")]
    [InlineData("PowerShell7", "[Pp]ester")]
    [InlineData("Npm", "user/repo")]
    [InlineData("Npm", "github:user/repo")]
    [InlineData("Npm", "git+https://example.test/repo.git")]
    [InlineData("Npm", "file:../pkg")]
    [InlineData("Npm", "pkg.tgz")]
    [InlineData("Npm", "contoso%PATH%")]
    [InlineData("Bun", "github:user/repo")]
    [InlineData("Cargo", "my crate")]
    [InlineData("Pip", "requests[security]")]
    [InlineData("Winget", "Contoso.App&Other")]
    public void Build_RefusesPackageIdentifiersTheBrokerRejects(string managerName, string id)
    {
        Assert.ThrowsAny<InvalidOperationException>(() => BrokerRequestBuilder.Build(
            BuildPackage(managerName, id), new InstallOptions(), OperationType.Install));
    }

    [Theory]
    [InlineData("Chocolatey", "notepadplusplus.install")]
    [InlineData("Chocolatey", "allure")]
    [InlineData("Npm", "@contoso/tool")]
    [InlineData("Npm", "eslint-v9:eslint@^9.x")]
    [InlineData("Bun", "@contoso/tool")]
    [InlineData("PowerShell", "Az.Accounts")]
    [InlineData("Scoop", "7zip")]
    public void Build_KeepsPackageIdentifiersTheBrokerAccepts(string managerName, string id)
    {
        var request = BrokerRequestBuilder.Build(
            BuildPackage(managerName, id), new InstallOptions(), OperationType.Install);

        Assert.Equal(id, request.Package.Id);
    }

    [Theory]
    [InlineData("--all")]
    [InlineData("--arch=64bit")]
    [InlineData("-a")]
    [InlineData("-qa")]
    [InlineData("--global")]
    [InlineData("-g")]
    [InlineData("extras/app")]
    [InlineData("--")]
    public void Build_RefusesScoopCustomParametersTheBrokerRejects(string parameter)
    {
        var options = new InstallOptions { CustomParameters_Update = [parameter] };

        Assert.Throws<BrokerRequestValidationException>(() => BrokerRequestBuilder.Build(
            BuildPackage("Scoop", "7zip"), options, OperationType.Update));
    }

    [Theory]
    [InlineData("--no-cache")]
    [InlineData("--quiet")]
    [InlineData("-fq")]
    public void Build_KeepsScoopCustomParametersTheBrokerAccepts(string parameter)
    {
        var options = new InstallOptions { CustomParameters_Update = [parameter] };

        var request = BrokerRequestBuilder.Build(BuildPackage("Scoop", "7zip"), options, OperationType.Update);

        Assert.Equal([parameter], request.Options.CustomParameters);
    }

    [Theory]
    [InlineData("\"/S")]
    [InlineData("%TEMP%")]
    [InlineData("/D=a!b")]
    [InlineData("a^b")]
    public void Build_RefusesBatchMetacharactersInWinGetCustomParameters(string parameter)
    {
        var options = new InstallOptions { CustomParameters_Install = ["--override", parameter] };

        Assert.Throws<BrokerRequestValidationException>(() => BrokerRequestBuilder.Build(
            BuildWinGetPackage(), options, OperationType.Install));
    }

    [Fact]
    public void Build_RefusesTheArm32Architecture()
    {
        var options = new InstallOptions { Architecture = UniGetUIArchitecture.arm32 };

        Assert.Throws<BrokerRequestValidationException>(() => BrokerRequestBuilder.Build(
            BuildWinGetPackage(), options, OperationType.Install));
    }

    [Theory]
    [InlineData("--override", true)]
    [InlineData("--OVERRIDE=/S", true)]
    [InlineData("--custom", true)]
    [InlineData("--custom=/quiet", true)]
    [InlineData("--silent", false)]
    [InlineData("override", false)]
    public void HasWinGetInstallerArguments_MatchesOverrideAndCustom(string parameter, bool expected)
    {
        Assert.Equal(expected, BrokerRequestValidator.HasWinGetInstallerArguments([parameter]));
    }

    [Fact]
    public void UsesWinGetInstallerArguments_IsWinGetOnly()
    {
        var options = new InstallOptions { CustomParameters_Install = ["--override"] };

        Assert.True(BrokerRequestValidator.UsesWinGetInstallerArguments(BuildWinGetPackage(), options, OperationType.Install));
        Assert.False(BrokerRequestValidator.UsesWinGetInstallerArguments(BuildWinGetPackage(), options, OperationType.Update));
        Assert.False(BrokerRequestValidator.UsesWinGetInstallerArguments(BuildPackage("Scoop", "7zip"), options, OperationType.Install));
    }
}
