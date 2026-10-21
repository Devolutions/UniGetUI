using System.Diagnostics;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Classes.Manager.ManagerHelpers;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Interfaces.ManagerProviders;
using UniGetUI.PackageEngine.Managers.CargoManager;
using UniGetUI.PackageEngine.Operations;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.Serializable;
using UniGetUI.PackageEngine.Tests.Infrastructure.Builders;

namespace UniGetUI.PackageEngine.Tests;

[Collection(nameof(OperationOrchestrationTestCollection))]
public sealed class CargoOperationHelperTests
{
    private static Package TestPackage(Cargo manager, string id = "ripgrep") =>
        new PackageBuilder()
            .WithManager(manager)
            .WithId(id)
            .WithVersion("14.1.0")
            .WithNewVersion("14.1.1")
            .Build();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DependenciesAndCommandsNeverUseExtensionsEvenWhenInstalled(bool helpersPresent)
    {
        using var sandbox = new CargoTestSandbox();
        using var environment = sandbox.IsolateEnvironment();
        if (helpersPresent)
        {
            foreach (string helper in new[] { "cargo-binstall", "cargo-install-update" })
            {
                sandbox.Write("home/bin/" + helper + (OperatingSystem.IsWindows() ? ".exe" : ""), []);
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(sandbox.PathFor("home/bin/" + helper),
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
        var manager = new Cargo();
        Assert.Empty(manager.Dependencies);
        Assert.False(manager.Capabilities.CanSkipIntegrityChecks);
        Assert.True(manager.Capabilities.SupportsCustomLocations);
        Assert.IsAssignableFrom<IManagedPackageOperationHelper>(manager.OperationHelper);
        foreach (string id in new[] { "ripgrep", "cargo-binstall", "cargo-update" })
        {
            var package = TestPackage(manager, id);
            Assert.Equal(
                ["install", id, "--version", CoreTools.EscapeCommandLineArgument("14.1.1"), "--locked", "--force"],
                manager.OperationHelper.GetParameters(package, new InstallOptions(), OperationType.Update));
        }
    }

    [Theory]
    [InlineData(OperationType.Install)]
    [InlineData(OperationType.Update)]
    public void SourceAndStandaloneCommandsPinTheExactSelectedVersion(OperationType operation)
    {
        var manager = new Cargo();
        var package = TestPackage(manager);
        var options = new InstallOptions();
        string version = operation == OperationType.Update ? "14.1.1" : "14.1.0";
        List<string> expected = ["install", "ripgrep", "--version", CoreTools.EscapeCommandLineArgument(version), "--locked"];
        if (operation == OperationType.Update)
            expected.Add("--force");
        Assert.Equal(expected, manager.OperationHelper.GetParameters(package, options, operation));
        Assert.Equal(expected, manager.OperationHelper.GetStandaloneParameters(package, options, operation));

        options.Version = "15.0.0-rc.2";
        expected[3] = CoreTools.EscapeCommandLineArgument("15.0.0-rc.2");
        Assert.Equal(expected, manager.OperationHelper.GetParameters(package, options, operation));

        options.Version = "15.0.0";
        expected[3] = CoreTools.EscapeCommandLineArgument("15.0.0");
        Assert.Equal(expected, manager.OperationHelper.GetParameters(package, options, operation));
        Assert.Equal(expected, manager.OperationHelper.GetStandaloneParameters(package, options, operation));
    }

    [Theory]
    [InlineData(OperationType.Install)]
    [InlineData(OperationType.Update)]
    [InlineData(OperationType.Uninstall)]
    public void CustomRootAndSourceArgumentsArePreserved(OperationType operation)
    {
        using var sandbox = new CargoTestSandbox();
        var manager = new Cargo();
        var options = new InstallOptions { CustomInstallLocation = sandbox.PathFor("root with spaces") };
        options.CustomParameters_Install.AddRange(["--features", "fast", "--no-default-features"]);
        options.CustomParameters_Update.AddRange(["--git", "https://github.com/publisher/tool", "--tag", "v2"]);
        options.CustomParameters_Uninstall.AddRange(["--bin", "rg"]);
        List<string> expected = operation == OperationType.Uninstall
            ? ["uninstall", "ripgrep"]
            : ["install", "ripgrep", "--version", CoreTools.EscapeCommandLineArgument(
                operation == OperationType.Install ? "14.1.0" : "14.1.1"), "--locked"];
        if (operation == OperationType.Update)
            expected.Add("--force");
        expected.AddRange(["--root", CoreTools.EscapeCommandLineArgument(options.CustomInstallLocation)]);
        expected.AddRange(operation switch
        {
            OperationType.Install => options.CustomParameters_Install,
            OperationType.Update => options.CustomParameters_Update,
            _ => options.CustomParameters_Uninstall,
        });
        Assert.Equal(expected, manager.OperationHelper.GetParameters(TestPackage(manager), options, operation));
        Assert.Equal(expected, manager.OperationHelper.GetStandaloneParameters(TestPackage(manager), options, operation));
    }

    [Fact]
    public void ProcessPreviewPreservesCargoOverrideAndUsesPinnedSourceCommand()
    {
        var manager = new Cargo
        {
            Status = new ManagerStatus
            {
                Found = true,
                ExecutablePath = Path.Join(Path.GetTempPath(), "custom-cargo.exe"),
                ExecutableCallArgs = "+stable",
            },
        };
        using var operation = new InspectableUpdate(TestPackage(manager), new InstallOptions());
        ProcessStartInfo info = operation.PrepareProcessStartInfoForTests();
        Assert.Equal(manager.Status.ExecutablePath, info.FileName);
        Assert.Equal("+stable install ripgrep --version " + CoreTools.EscapeCommandLineArgument("14.1.1")
            + " --locked --force", info.Arguments.Trim());
    }

    private sealed class InspectableUpdate : UpdatePackageOperation
    {
        public InspectableUpdate(IPackage package, InstallOptions options)
            : base(package, options, true) { }

        public ProcessStartInfo PrepareProcessStartInfoForTests()
        {
            process.StartInfo.FileName = "unset";
            process.StartInfo.Arguments = "unset";
            PrepareProcessStartInfo();
            return process.StartInfo;
        }
    }

    [Theory]
    [InlineData("--no-confirm")]
    [InlineData("--skip-signatures")]
    [InlineData("--install-path")]
    [InlineData("--install-path=elsewhere")]
    [InlineData("--disable-strategies")]
    [InlineData("--disable-strategies=compile")]
    [InlineData("--strategies=quick-install")]
    [InlineData("--pkg-url=https://publisher.test/tool")]
    [InlineData("--pkg-fmt")]
    [InlineData("--bin-dir")]
    [InlineData("--bin-dir=bin")]
    [InlineData("--only-signed")]
    public void LegacyExtensionArgumentsFailWithMigrationMessage(string argument)
    {
        var manager = new Cargo();
        foreach (OperationType operation in new[] { OperationType.Install, OperationType.Update, OperationType.Uninstall })
        {
            var options = new InstallOptions();
            options.CustomParameters_Install.Add(argument);
            options.CustomParameters_Update.Add(argument);
            options.CustomParameters_Uninstall.Add(argument);
            var error = Assert.Throws<InvalidOperationException>(() =>
                manager.OperationHelper.GetParameters(TestPackage(manager), options, operation));
            Assert.Contains("Cargo extension arguments are no longer supported", error.Message);
        }
    }

    [Theory]
    [InlineData(OperationType.Install)]
    [InlineData(OperationType.Update)]
    public async Task IntegritySkipIsRejectedByCommandAndManagedPaths(OperationType operation)
    {
        var manager = new Cargo();
        var options = new InstallOptions { SkipHashCheck = true };
        var package = TestPackage(manager);
        Assert.Contains("integrity verification", Assert.Throws<InvalidOperationException>(() =>
            manager.OperationHelper.GetParameters(package, options, operation)).Message);
        var helper = Assert.IsAssignableFrom<IManagedPackageOperationHelper>(manager.OperationHelper);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            helper.TryPerformAsync(package, options, operation, _ => { }, _ => { }, CancellationToken.None));
        Assert.Contains("integrity verification", error.Message);
    }

    [Fact]
    public async Task ManagedUninstallDefersToCargoWithoutNetworkOrMutation()
    {
        using var sandbox = new CargoTestSandbox();
        using var environment = sandbox.IsolateEnvironment();
        using var http = new CargoTestHttpFixture();
        var manager = new Cargo();
        var helper = Assert.IsAssignableFrom<IManagedPackageOperationHelper>(manager.OperationHelper);
        Assert.Null(await helper.TryPerformAsync(TestPackage(manager), new InstallOptions(),
            OperationType.Uninstall, _ => { }, _ => { }, CancellationToken.None));
        Assert.Empty(http.Requests);
        Assert.False(Directory.Exists(sandbox.PathFor("root")));
        Assert.Equal(["uninstall", "ripgrep"],
            manager.OperationHelper.GetParameters(TestPackage(manager), new InstallOptions { SkipHashCheck = true },
                OperationType.Uninstall));
    }

    [Theory]
    [InlineData("cargo-binstall")]
    [InlineData("cargo-update")]
    [InlineData("ripgrep")]
    public void FailedOperationsNeverRetryThroughExtensions(string id)
    {
        var manager = new Cargo();
        var package = TestPackage(manager, id);
        foreach (OperationType operation in new[] { OperationType.Install, OperationType.Update })
        {
            Assert.Equal(OperationVeredict.Failure, manager.OperationHelper.GetResult(package, operation, [], 101));
            Assert.Equal(OperationVeredict.Success, manager.OperationHelper.GetResult(package, operation, [], 0));
        }
    }

    [Theory]
    [InlineData("git")]
    [InlineData("registry")]
    [InlineData("path")]
    public void SourceUpdateCommandPreservesInstalledProvenance(string sourceKind)
    {
        using var sandbox = new CargoTestSandbox();
        var manager = new Cargo();
        string url = sourceKind switch
        {
            "git" => "https://github.com/publisher/tool?branch=main#abcdef",
            "registry" => "https://private.test/index",
            _ => new Uri(sandbox.PathFor("local source")).AbsoluteUri,
        };
        string sourceName = sourceKind == "registry" ? "registry+" + url : sourceKind + "+" + url;
        var source = new SourceBuilder().WithManager(manager).WithName(sourceName).WithUrl(url).Build();
        var package = new PackageBuilder().WithManager(manager).WithSource(source).WithId("tool")
            .WithVersion("1.0.0").WithNewVersion("1.2.3").Build();
        List<string> expected = ["install", "tool", "--version", CoreTools.EscapeCommandLineArgument("1.2.3"),
            "--locked", "--force"];
        expected.AddRange(sourceKind switch
        {
            "git" => ["--git", CoreTools.EscapeCommandLineArgument("https://github.com/publisher/tool"),
                "--rev", CoreTools.EscapeCommandLineArgument("abcdef")],
            "registry" => ["--index", CoreTools.EscapeCommandLineArgument(url)],
            _ => new[] { "--path", CoreTools.EscapeCommandLineArgument(sandbox.PathFor("local source")) },
        });
        Assert.Equal(expected, manager.OperationHelper.GetParameters(package, new InstallOptions(), OperationType.Update));
        Assert.Equal(expected, manager.OperationHelper.GetStandaloneParameters(package, new InstallOptions(), OperationType.Update));
        Assert.Equal(["uninstall", "tool"],
            manager.OperationHelper.GetParameters(package, new InstallOptions(), OperationType.Uninstall));
    }
}
