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
    public void Build_MapsSupportedManagers(string managerName, ManagerName expected)
    {
        var package = new PackageBuilder()
            .WithManager(new PackageManagerBuilder().WithName(managerName).Build())
            .Build();

        var request = BrokerRequestBuilder.Build(package, new InstallOptions(), OperationType.Install);
        Assert.Equal(expected, request.Manager);
    }

    [Fact]
    public void Build_ThrowsForUnsupportedManager()
    {
        var package = new PackageBuilder()
            .WithManager(new PackageManagerBuilder().WithName("Scoop").Build())
            .Build();

        Assert.Throws<ArgumentException>(
            () => BrokerRequestBuilder.Build(package, new InstallOptions(), OperationType.Install));
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
}
