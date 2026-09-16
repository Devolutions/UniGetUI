using System.Diagnostics;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Classes.Manager.ManagerHelpers;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Managers.CargoManager;
using UniGetUI.PackageEngine.Operations;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.Serializable;
using UniGetUI.PackageEngine.Tests.Infrastructure.Builders;

namespace UniGetUI.PackageEngine.Tests;

[Collection(nameof(OperationOrchestrationTestCollection))]
public sealed class CargoOperationHelperTests
{
    private static void WithBinstallAvailable(Action<Cargo> test)
    {
        string binaryName = OperatingSystem.IsWindows() ? "cargo-binstall.exe" : "cargo-binstall";
        string cargoHome = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
        string binaryPath = Path.Join(cargoHome, "bin", binaryName);
        string? previousCargoHome = Environment.GetEnvironmentVariable("CARGO_HOME");

        try
        {
            Directory.CreateDirectory(Path.Join(cargoHome, "bin"));
            File.WriteAllText(binaryPath, "");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(
                    binaryPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                );

            Environment.SetEnvironmentVariable("CARGO_HOME", cargoHome);
            var manager = new Cargo();
            Assert.True(manager.HasBinstall);

            test(manager);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CARGO_HOME", previousCargoHome);
            Directory.Delete(cargoHome, true);
        }
    }

    private static Package BinstallPackage(Cargo manager) =>
        new PackageBuilder()
            .WithManager(manager)
            .WithId("cargo-binstall")
            .WithVersion("1.19.0")
            .WithNewVersion("1.23.0")
            .Build();

    [Fact]
    public void UpdateParametersDropTheCompileStrategyWhenBinstallTargetsItself()
    {
        WithBinstallAvailable(manager =>
        {
            var parameters = manager.OperationHelper.GetParameters(
                BinstallPackage(manager),
                new InstallOptions(),
                OperationType.Update
            );

            Assert.Equal(
                ["binstall", "cargo-binstall", "--no-confirm", "--disable-strategies", "compile"],
                parameters
            );
        });
    }

    [Fact]
    public void UpdateParametersKeepEveryStrategyForOtherPackages()
    {
        WithBinstallAvailable(manager =>
        {
            var package = new PackageBuilder()
                .WithManager(manager)
                .WithId("ripgrep")
                .WithVersion("14.1.0")
                .WithNewVersion("14.1.1")
                .Build();

            var parameters = manager.OperationHelper.GetParameters(
                package,
                new InstallOptions(),
                OperationType.Update
            );

            Assert.Equal(["binstall", "ripgrep", "--no-confirm"], parameters);
        });
    }

    [Fact]
    public void AFailedBinstallSelfUpdateRetriesThroughCargoInstall()
    {
        WithBinstallAvailable(manager =>
        {
            var package = BinstallPackage(manager);
            IReadOnlyList<string> output =
            [
                "error: failed to move `\\cargo-installAeQfV8\\cargo-binstall.exe` to `\\cargo-binstall.exe`",
                "Caused by:",
                "  Access is denied. (os error 5)",
            ];

            var veredict = manager.OperationHelper.GetResult(
                package,
                OperationType.Update,
                output,
                101
            );

            Assert.Equal(OperationVeredict.AutoRetry, veredict);
            Assert.True(package.OverridenOptions.Cargo_DoNotUseBinstall);

            var retryParameters = manager.OperationHelper.GetParameters(
                package,
                new InstallOptions(),
                OperationType.Update
            );

            Assert.Equal(["install", "cargo-binstall", "--locked", "--force"], retryParameters);
            Assert.Equal(
                OperationVeredict.Failure,
                manager.OperationHelper.GetResult(package, OperationType.Update, output, 101)
            );
        });
    }

    [Fact]
    public void AFailedInstallOfBinstallItselfRetriesThroughCargoInstall()
    {
        WithBinstallAvailable(manager =>
        {
            var package = BinstallPackage(manager);

            Assert.Equal(
                OperationVeredict.AutoRetry,
                manager.OperationHelper.GetResult(package, OperationType.Install, [], 1)
            );

            var retryParameters = manager.OperationHelper.GetParameters(
                package,
                new InstallOptions(),
                OperationType.Install
            );

            Assert.Equal(
                ["install", "cargo-binstall", "--version", "\"1.19.0\"", "--locked", "--force"],
                retryParameters
            );
        });
    }

    [Fact]
    public void TheRetryRebuildsTheProcessCommandLineWithoutBinstall()
    {
        WithBinstallAvailable(manager =>
        {
            manager.Status = new ManagerStatus
            {
                Found = true,
                ExecutablePath = Path.Join("C:", "cargo", "bin", "cargo.exe"),
                ExecutableCallArgs = "",
            };
            var package = BinstallPackage(manager);
            using var operation = new InspectableUpdate(package, new InstallOptions());

            var firstAttempt = operation.PrepareProcessStartInfoForTests();

            Assert.Equal(
                Path.Join("C:", "cargo", "bin", "cargo.exe"),
                firstAttempt.FileName
            );
            Assert.Equal(
                "binstall cargo-binstall --no-confirm --disable-strategies compile",
                firstAttempt.Arguments.Trim()
            );

            Assert.Equal(
                OperationVeredict.AutoRetry,
                manager.OperationHelper.GetResult(package, OperationType.Update, [], 101)
            );

            var secondAttempt = operation.PrepareProcessStartInfoForTests();

            Assert.Equal("install cargo-binstall --locked --force", secondAttempt.Arguments.Trim());
        });
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

    [Fact]
    public void ABrokeredFailureIsNotRetriedBecauseTheRequestWouldBeIdentical()
    {
        WithBinstallAvailable(manager =>
        {
            bool originalSetting = Settings.Get(Settings.K.UseAgentBroker);
            Settings.Set(Settings.K.UseAgentBroker, true);
            try
            {
                var package = BinstallPackage(manager);

                Assert.Equal(
                    OperationVeredict.Failure,
                    manager.OperationHelper.GetResult(package, OperationType.Update, [], 101)
                );
                Assert.False(package.OverridenOptions.Cargo_DoNotUseBinstall);
            }
            finally
            {
                Settings.Set(Settings.K.UseAgentBroker, originalSetting);
            }
        });
    }

    [Fact]
    public void ACustomInstallPathKeepsTheUntouchedBinstallCommandLine()
    {
        WithBinstallAvailable(manager =>
        {
            var options = new InstallOptions
            {
                CustomInstallLocation = Path.Join("D:", "tools", "bin"),
            };

            var parameters = manager.OperationHelper.GetParameters(
                BinstallPackage(manager),
                options,
                OperationType.Update
            );

            Assert.Equal(
                [
                    "binstall",
                    "cargo-binstall",
                    "--no-confirm",
                    "--install-path",
                    CoreTools.EscapeCommandLineArgument(Path.Join("D:", "tools", "bin")),
                ],
                parameters
            );
        });
    }

    [Fact]
    public void ACustomInstallPathIsNeverRelocatedByTheRetry()
    {
        WithBinstallAvailable(manager =>
        {
            var package = BinstallPackage(manager);
            var options = new InstallOptions
            {
                CustomInstallLocation = Path.Join("D:", "tools", "bin"),
            };
            manager.OperationHelper.GetParameters(package, options, OperationType.Update);

            Assert.Equal(
                OperationVeredict.Failure,
                manager.OperationHelper.GetResult(package, OperationType.Update, [], 101)
            );
            Assert.False(package.OverridenOptions.Cargo_DoNotUseBinstall);
        });
    }

    [Fact]
    public void AFailedOperationOnOtherPackagesIsNotRetried()
    {
        WithBinstallAvailable(manager =>
        {
            var package = new PackageBuilder()
                .WithManager(manager)
                .WithId("ripgrep")
                .WithVersion("14.1.0")
                .Build();

            Assert.Equal(
                OperationVeredict.Failure,
                manager.OperationHelper.GetResult(package, OperationType.Update, [], 101)
            );
            Assert.False(package.OverridenOptions.Cargo_DoNotUseBinstall);
        });
    }
}
