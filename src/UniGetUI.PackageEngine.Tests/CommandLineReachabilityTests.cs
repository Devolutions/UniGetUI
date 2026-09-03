using System.Diagnostics;
using UniGetUI.Core.Data;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Operations;
using UniGetUI.PackageEngine.Serializable;
using UniGetUI.PackageEngine.Tests.Infrastructure.Builders;
using UniGetUI.PackageEngine.Tests.Infrastructure.Fakes;

namespace UniGetUI.PackageEngine.Tests;

[Collection(nameof(OperationOrchestrationTestCollection))]
public sealed class CommandLineReachabilityTests
{
    private const string Payload = "Start-Process calc";

    private static TestPackageManager ShellManager() =>
        new PackageManagerBuilder()
            .WithName("ShellManager")
            .ConfigureManager(manager =>
            {
                manager.SetCommandLineIsShellInterpreted(true);
                manager.ExecutablePath = "C:\\Windows\\System32\\powershell.exe";
                manager.ExecutableArguments = "-NoProfile -Command";
            })
            .ConfigureOperation(helper =>
                helper.ParametersFactory = (package, options, _) =>
                    options.Version.Length > 0
                        ? ["Install-Module", "-Name", package.Id, "-RequiredVersion", options.Version]
                        : ["Install-Module", "-Name", package.Id]
            )
            .Build();

    [Fact]
    public void InstallOperation_ProducesTheExpectedCommandLineForALegitimateVersion()
    {
        var manager = ShellManager();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("powershell-yaml")
            .WithVersion("1.0.0")
            .Build();
        var options = new InstallOptions { Version = "0.4.7" };
        using var operation = new InspectableInstall(package, options);

        var startInfo = operation.PrepareProcessStartInfoForTests();

        Assert.Equal(
            "-NoProfile -Command Install-Module -Name powershell-yaml -RequiredVersion 0.4.7",
            startInfo.Arguments.Trim()
        );
    }

    [Fact]
    public void InstallOperation_PayloadInTheVersionNeverReachesTheCommandLine()
    {
        var manager = ShellManager();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("powershell-yaml")
            .WithVersion("1.0.0")
            .Build();
        var options = new InstallOptions { Version = $"1.2.3.4.5; {Payload}" };
        using var operation = new InspectableInstall(package, options);

        var thrown = Assert.Throws<InvalidOperationException>(
            operation.PrepareProcessStartInfoForTests
        );

        Assert.Contains("not a valid package version", thrown.Message);
        Assert.DoesNotContain(Payload, operation.ArgumentsAfterAttempt());
    }

    [Fact]
    public void InstallOperation_PayloadInTheIdentifierNeverReachesTheCommandLine()
    {
        var manager = ShellManager();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId($"powershell-yaml; {Payload}")
            .WithVersion("1.0.0")
            .Build();
        using var operation = new InspectableInstall(package, new InstallOptions());

        Assert.Throws<InvalidOperationException>(
            operation.PrepareProcessStartInfoForTests
        );

        Assert.DoesNotContain(Payload, operation.ArgumentsAfterAttempt());
    }

    [Fact]
    public void AddSourceOperation_ProducesTheExpectedCommandLineForALegitimateSource()
    {
        var manager = SourceManager();
        var source = new SourceBuilder().WithManager(manager).WithName("PSGallery").Build();
        using var operation = new InspectableAddSource(source);

        var startInfo = operation.PrepareProcessStartInfoForTests();

        Assert.Contains("PSGallery", startInfo.Arguments);
    }

    [Theory]
    [InlineData("PSGallery; Start-Process calc")]
    [InlineData("PSGallery$(calc)")]
    [InlineData("PSGallery`ncalc")]
    [InlineData("PSGallery with spaces")]
    public void AddSourceOperation_PayloadInTheSourceNameNeverReachesTheCommandLine(string name)
    {
        var manager = SourceManager();
        var source = new SourceBuilder().WithManager(manager).WithName(name).Build();
        using var operation = new InspectableAddSource(source);

        Assert.Throws<InvalidOperationException>(
            operation.PrepareProcessStartInfoForTests
        );

        Assert.DoesNotContain("calc", operation.ArgumentsAfterAttempt());
    }

    [Fact]
    public void RemoveSourceOperation_PayloadInTheSourceNameNeverReachesTheCommandLine()
    {
        var manager = SourceManager();
        var source = new SourceBuilder()
            .WithManager(manager)
            .WithName("PSGallery; Start-Process calc")
            .Build();
        using var operation = new InspectableRemoveSource(source);

        Assert.Throws<InvalidOperationException>(
            operation.PrepareProcessStartInfoForTests
        );

        Assert.DoesNotContain("calc", operation.ArgumentsAfterAttempt());
    }

    private static TestPackageManager LauncherManager() =>
        new PackageManagerBuilder()
            .WithName("ShellManager")
            .ConfigureManager(manager =>
            {
                manager.SetCommandLineIsShellInterpreted(true);
                manager.ExecutablePath = @"C:\Windows\System32\powershell.exe";
                manager.ExecutableArguments = "-NoProfile -Command";
                manager.SetOperationCallArgs(
                    "-NoProfile",
                    "-ExecutionPolicy",
                    "Bypass",
                    "-File",
                    @"C:\App\Assets\Utilities\unigetui_ps_operation.ps1",
                    "tls12"
                );
            })
            .ConfigureOperation(helper =>
                helper.ParametersFactory = (package, options, _) =>
                    ["Install-Module", "-Name", package.Id, "-Confirm:$false"]
            )
            .Build();

    [Fact]
    public void InstallOperation_UsesFileAndAnArgumentVectorInsteadOfACommandString()
    {
        var manager = LauncherManager();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("powershell-yaml")
            .WithVersion("1.0.0")
            .Build();
        using var operation = new InspectableInstall(package, new InstallOptions());

        var startInfo = operation.PrepareProcessStartInfoForTests();

        Assert.Equal(string.Empty, startInfo.Arguments);
        Assert.Equal(
            [
                "-NoProfile",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                @"C:\App\Assets\Utilities\unigetui_ps_operation.ps1",
                "tls12",
                "Install-Module",
                "-Name",
                "powershell-yaml",
                "-Confirm:$false",
            ],
            startInfo.ArgumentList
        );
        Assert.DoesNotContain("-Command", startInfo.ArgumentList);
    }

    [Fact]
    public void InstallOperation_KeepsEachArgumentSeparateSoNothingIsReassembledIntoAScript()
    {
        var manager = LauncherManager();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("powershell-yaml")
            .WithVersion("1.0.0")
            .Build();
        using var operation = new InspectableInstall(package, new InstallOptions());

        var startInfo = operation.PrepareProcessStartInfoForTests();

        Assert.DoesNotContain(startInfo.ArgumentList, argument => argument.Contains(';'));
        Assert.DoesNotContain(startInfo.ArgumentList, argument => argument.Contains("::"));
    }

    [Fact]
    public void AddSourceOperation_UsesTheArgumentVectorWhenTheManagerHasOne()
    {
        var manager = new PackageManagerBuilder()
            .WithName("ShellManager")
            .ConfigureManager(manager =>
            {
                manager.SetCommandLineIsShellInterpreted(true);
                manager.ExecutablePath = @"C:\Windows\System32\powershell.exe";
                manager.SetOperationCallArgs("-NoProfile", "-File", @"C:\App\launcher.ps1", "plain");
            })
            .ConfigureSources(helper =>
                helper.AddParametersFactory = source =>
                    ["Register-PSRepository", "-Name", source.Name]
            )
            .Build();
        var source = new SourceBuilder().WithManager(manager).WithName("PSGallery").Build();
        using var operation = new InspectableAddSource(source);

        var startInfo = operation.PrepareProcessStartInfoForTests();

        Assert.Equal(string.Empty, startInfo.Arguments);
        Assert.Equal(
            ["-NoProfile", "-File", @"C:\App\launcher.ps1", "plain", "Register-PSRepository", "-Name", "PSGallery"],
            startInfo.ArgumentList
        );
    }

    [Fact]
    public void DirectExecManagers_KeepTheConcatenatedCommandLine()
    {
        var manager = new PackageManagerBuilder()
            .WithName("DirectManager")
            .ConfigureManager(manager =>
            {
                manager.ExecutablePath = @"C:\tools\pkg.exe";
                manager.ExecutableArguments = "--cli";
            })
            .ConfigureOperation(helper =>
                helper.ParametersFactory = (package, _, _) => ["install", package.Id]
            )
            .Build();
        var package = new PackageBuilder().WithManager(manager).WithId("Contoso.Test").Build();
        using var operation = new InspectableInstall(package, new InstallOptions());

        var startInfo = operation.PrepareProcessStartInfoForTests();

        Assert.Empty(startInfo.ArgumentList);
        Assert.Equal("--cli install Contoso.Test", startInfo.Arguments.Trim());
    }

    [Fact]
    public void ElevatedInstall_PutsTheElevatorPrefixAndTheVectorOnTheArgumentList()
    {
        string previousElevator = CoreData.ElevatorPath;
        string previousElevatorArgs = CoreData.ElevatorArgs;
        CoreData.ElevatorPath = @"C:\App\Assets\Utilities\UniGetUI Elevator.exe";
        CoreData.ElevatorArgs = "";

        try
        {
            var manager = LauncherManager();
            var package = new PackageBuilder()
                .WithManager(manager)
                .WithId("powershell-yaml")
                .WithVersion("1.0.0")
                .Build();
            var options = new InstallOptions { RunAsAdministrator = true };
            using var operation = new InspectableInstall(package, options);

            var startInfo = operation.PrepareProcessStartInfoForTests();

            if (CoreTools.IsAdministrator())
                return;

            Assert.Equal(CoreData.ElevatorPath, startInfo.FileName);
            Assert.Equal(string.Empty, startInfo.Arguments);
            Assert.Equal(
                [
                    @"C:\Windows\System32\powershell.exe",
                    "-NoProfile",
                    "-ExecutionPolicy",
                    "Bypass",
                    "-File",
                    @"C:\App\Assets\Utilities\unigetui_ps_operation.ps1",
                    "tls12",
                    "Install-Module",
                    "-Name",
                    "powershell-yaml",
                    "-Confirm:$false",
                ],
                startInfo.ArgumentList
            );
        }
        finally
        {
            CoreData.ElevatorPath = previousElevator;
            CoreData.ElevatorArgs = previousElevatorArgs;
        }
    }

    [Fact]
    public void ElevatedInstall_KeepsTheElevatorsOwnFlagsAsSeparateArguments()
    {
        string previousElevator = CoreData.ElevatorPath;
        string previousElevatorArgs = CoreData.ElevatorArgs;
        CoreData.ElevatorPath = "/usr/bin/sudo";
        CoreData.ElevatorArgs = "-A";

        try
        {
            var manager = LauncherManager();
            var package = new PackageBuilder().WithManager(manager).WithId("pkg").Build();
            var options = new InstallOptions { RunAsAdministrator = true };
            using var operation = new InspectableInstall(package, options);

            var startInfo = operation.PrepareProcessStartInfoForTests();

            if (CoreTools.IsAdministrator())
                return;

            Assert.Equal("-A", startInfo.ArgumentList[0]);
            Assert.Equal(@"C:\Windows\System32\powershell.exe", startInfo.ArgumentList[1]);
        }
        finally
        {
            CoreData.ElevatorPath = previousElevator;
            CoreData.ElevatorArgs = previousElevatorArgs;
        }
    }

    private static TestPackageManager SourceManager() =>
        new PackageManagerBuilder()
            .WithName("ShellManager")
            .ConfigureManager(manager =>
            {
                manager.SetCommandLineIsShellInterpreted(true);
                manager.ExecutablePath = "C:\\Windows\\System32\\powershell.exe";
                manager.ExecutableArguments = "-NoProfile -Command";
            })
            .ConfigureSources(helper =>
            {
                helper.AddParametersFactory = source =>
                    ["Register-PSRepository", "-Name", source.Name];
                helper.RemoveParametersFactory = source =>
                    ["Unregister-PSRepository", "-Name", source.Name];
            })
            .Build();

    private sealed class InspectableInstall : InstallPackageOperation
    {
        public InspectableInstall(IPackage package, InstallOptions options)
            : base(package, options, true) { }

        public ProcessStartInfo PrepareProcessStartInfoForTests()
        {
            Defaults();
            PrepareProcessStartInfo();
            return process.StartInfo;
        }

        public string ArgumentsAfterAttempt() => process.StartInfo.Arguments;

        private void Defaults()
        {
            process.StartInfo.FileName = "unset";
            process.StartInfo.Arguments = "unset";
        }
    }

    private sealed class InspectableAddSource : AddSourceOperation
    {
        public InspectableAddSource(IManagerSource source)
            : base(source) { }

        public ProcessStartInfo PrepareProcessStartInfoForTests()
        {
            process.StartInfo.FileName = "unset";
            process.StartInfo.Arguments = "unset";
            PrepareProcessStartInfo();
            return process.StartInfo;
        }

        public string ArgumentsAfterAttempt() => process.StartInfo.Arguments;
    }

    private sealed class InspectableRemoveSource : RemoveSourceOperation
    {
        public InspectableRemoveSource(IManagerSource source)
            : base(source) { }

        public ProcessStartInfo PrepareProcessStartInfoForTests()
        {
            process.StartInfo.FileName = "unset";
            process.StartInfo.Arguments = "unset";
            PrepareProcessStartInfo();
            return process.StartInfo;
        }

        public string ArgumentsAfterAttempt() => process.StartInfo.Arguments;
    }
}
