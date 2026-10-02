using Devolutions.Now.Policy.Api;
using Devolutions.Now.Policy.Client;
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

    private static UniGetUI.PackageEngine.PackageClasses.Package BuildPipPackage()
        => new PackageBuilder()
            .WithManager(new PackageManagerBuilder().WithName("Pip").Build())
            .WithId("requests")
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
    public void Build_OmitsMachineScopeForPip()
    {
        var package = BuildPipPackage();
        package.OverridenOptions.Scope = PackageScope.Global;
        var options = new InstallOptions { InstallationScope = PackageScope.Machine };

        var request = BrokerRequestBuilder.Build(package, options, OperationType.Update);

        Assert.Null(request.Options.Scope);
    }

    [Fact]
    public void Build_KeepsUserScopeForPip()
    {
        var package = BuildPipPackage();
        package.OverridenOptions.Scope = PackageScope.User;

        var request = BrokerRequestBuilder.Build(package, new InstallOptions(), OperationType.Update);

        Assert.Equal(Scope.User, request.Options.Scope);
    }

    [Fact]
    public void Build_OmitsAConfiguredMachineScopeForPip()
    {
        var package = BuildPipPackage();
        var options = new InstallOptions { InstallationScope = PackageScope.Machine };

        var request = BrokerRequestBuilder.Build(package, options, OperationType.Install);

        Assert.Null(request.Options.Scope);
    }

    [Fact]
    public void Build_KeepsAConfiguredMachineScopeForManagersThatResolveIt()
    {
        var package = BuildWinGetPackage();
        var options = new InstallOptions { InstallationScope = PackageScope.Machine };

        var request = BrokerRequestBuilder.Build(package, options, OperationType.Install);

        Assert.Equal(Scope.Machine, request.Options.Scope);
    }

    [Fact]
    public void Build_OmitsTheSourceUrlForPipButKeepsTheSourceName()
    {
        var package = BuildPipPackage();

        var request = BrokerRequestBuilder.Build(package, new InstallOptions(), OperationType.Update);

        Assert.Null(request.Source.Url);
        Assert.Equal(package.Source.Name, request.Source.Name);
    }

    [Fact]
    public void Build_KeepsTheSourceUrlForOtherManagers()
    {
        var package = BuildWinGetPackage();

        var request = BrokerRequestBuilder.Build(package, new InstallOptions(), OperationType.Update);

        Assert.Equal(package.Source.Url?.ToString(), request.Source.Url);
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
    public void Build_KeepsWinGetVersionsThatAreNotPlainVersions()
    {
        var package = new PackageBuilder()
            .WithManager(new PackageManagerBuilder().WithName("Winget").Build())
            .WithId("Contoso.Test")
            .Build();
        var options = new InstallOptions { Version = "2021 Update" };

        var request = BrokerRequestBuilder.Build(package, options, OperationType.Install);

        Assert.Equal("2021 Update", request.Package.Version);
    }

    [Fact]
    public async Task Build_PipUpdateRequest_ClearsTheBrokerCapabilityCheck()
    {
        var package = BuildPipPackage();
        package.OverridenOptions.Scope = PackageScope.Global;

        var request = BrokerRequestBuilder.Build(package, new InstallOptions(), OperationType.Update);

        using var client = CreatePipBrokerClient();

        var execution = await client.Execute(request, CancellationToken.None);

        Assert.Equal(Decision.Allow, execution.Decision.Decision);
    }

    [Fact]
    public async Task Build_PipRequestWithAConfiguredMachineScope_ClearsTheBrokerCapabilityCheck()
    {
        var package = BuildPipPackage();
        var options = new InstallOptions { InstallationScope = PackageScope.Machine };

        var request = BrokerRequestBuilder.Build(package, options, OperationType.Update);

        using var client = CreatePipBrokerClient();

        var execution = await client.Execute(request, CancellationToken.None);

        Assert.Equal(Decision.Allow, execution.Decision.Decision);
    }

    private const string TestEffectiveUser = "TESTDOMAIN\\tester";
    private const string TestClientExecutablePath = "C:\\test\\unigetui.exe";

    private static BrokerClient CreatePipBrokerClient()
        => new(new BrokerClientOptions
        {
            Transport = new PipCapabilityTransport(),
            RequestedElevation = Elevation.Standard,
            EffectiveUser = TestEffectiveUser,
            ClientExecutablePath = TestClientExecutablePath,
            ClientVersion = "0.0.0-tests",
        });

    private sealed class PipCapabilityTransport : IBrokerTransport
    {
        public Transport Kind => Transport.HttpNamedPipe;

        public Task<BrokerTransportResponse> Send(
            BrokerTransportRequest request,
            CancellationToken cancellationToken = default
        ) => request.Path switch
        {
            "/v1/capabilities" => Json(BrokerSerializer.Serialize(Capabilities())),
            "/v1/package-operations/execute" => Json(BrokerSerializer.Serialize(Execution())),
            _ => throw new BrokerClientException(
                BrokerClientErrorKind.InvalidRequest,
                $"Unexpected request path: {request.Path}",
                request.Path
            ),
        };

        public void Dispose() { }

        private static Task<BrokerTransportResponse> Json(string body) =>
            Task.FromResult(new BrokerTransportResponse { StatusCode = 200, Body = body });

        private static CapabilitiesResponse Capabilities() => new()
        {
            ResponseKind = BrokerApi.CapabilitiesResponseKind,
            ResponseVersion = BrokerApi.Version,
            MaxRequestBodyBytes = 1_000_000,
            Transports = [Transport.HttpNamedPipe],
            Managers =
            [
                new ManagerCapability
                {
                    Manager = ManagerName.Pip,
                    Operations = [Operation.Install, Operation.Update, Operation.Uninstall],
                    Scopes = [Scope.User],
                    Architectures = [Architecture.Neutral],
                    SupportsCustomParameters = false,
                    SupportsCustomInstallLocation = false,
                    SupportsCaptureOutput = true,
                },
            ],
        };

        private static ExecutionResponse Execution() => new()
        {
            ResponseKind = BrokerApi.ExecutionResponseKind,
            ResponseVersion = BrokerApi.Version,
            Decision = new DecisionInfo { Decision = Decision.Allow },
            Operation = new OperationSubmission
            {
                OperationId = "test-pip-operation",
                Status = OperationStatus.Starting,
                SubmittedAt = DateTimeOffset.UtcNow,
            },
        };
    }
}
