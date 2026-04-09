using System.Diagnostics;
using System.Reflection;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Enums;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Operations;
using UniGetUI.PackageEngine.PackageLoader;
using UniGetUI.PackageEngine.Serializable;
using UniGetUI.PackageEngine.Structs;
using UniGetUI.PackageEngine.Tests.Infrastructure.Builders;
using UniGetUI.PackageOperations;

namespace UniGetUI.PackageEngine.Tests;

[CollectionDefinition(nameof(OperationOrchestrationTestCollection), DisableParallelization = true)]
public sealed class OperationOrchestrationTestCollection;

[Collection(nameof(OperationOrchestrationTestCollection))]
public sealed class PackageOperationsTests
{
    [Fact]
    public void RetryModesMutateInstallOptionsAndMetadata()
    {
        var package = CreatePackage();
        var options = new InstallOptions();
        var operation = new InspectableInstallPackageOperation(package, options);

        operation.Retry(AbstractOperation.RetryMode.Retry_AsAdmin);
        operation.Retry(AbstractOperation.RetryMode.Retry_Interactive);
        operation.Retry(AbstractOperation.RetryMode.Retry_SkipIntegrity);

        Assert.True(options.RunAsAdministrator);
        Assert.True(options.InteractiveInstallation);
        Assert.True(options.SkipHashCheck);
        Assert.Contains("Retried package operation", operation.Metadata.OperationInformation);
        Assert.Contains(package.Id, operation.Metadata.OperationInformation);
        Assert.Throws<InvalidOperationException>(() => operation.Retry("InvalidRetryMode"));
    }

    [Fact]
    public void InstallOperationBuildsPrerequisitesKillListAndPreCommand()
    {
        var package = CreatePackage();
        var options = new InstallOptions
        {
            PreInstallCommand = "echo before install",
            AbortOnPreInstallFail = false,
        };
        options.KillBeforeOperation.Add("proc-one");
        options.KillBeforeOperation.Add("proc-two");
        using var prerequisite = new StubOperation();
        using var operation = new InspectableInstallPackageOperation(package, options, req: prerequisite);

        var preOperations = GetInnerOperations(operation, "PreOperations");

        Assert.Collection(
            preOperations,
            inner =>
            {
                Assert.Same(prerequisite, inner.Operation);
                Assert.True(inner.MustSucceed);
            },
            inner =>
            {
                Assert.IsType<KillProcessOperation>(inner.Operation);
                Assert.False(inner.MustSucceed);
            },
            inner =>
            {
                Assert.IsType<KillProcessOperation>(inner.Operation);
                Assert.False(inner.MustSucceed);
            },
            inner =>
            {
                var preCommand = Assert.IsType<PrePostOperation>(inner.Operation);
                Assert.False(inner.MustSucceed);
                Assert.Contains("echo before install", preCommand.Metadata.Status);
            }
        );
    }

    [Fact]
    public async Task UpdateOperationBuildsPostOperationsForCommandAndPreviousVersions()
    {
        var manager = CreateManager();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Tool")
            .WithVersion("1.0.0")
            .WithNewVersion("3.0.0")
            .Build();
        var olderInstalledVersion = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Tool")
            .WithVersion("2.0.0")
            .Build();
        var newerInstalledVersion = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Tool")
            .WithVersion("3.1.0")
            .Build();
        InitializeLoaders();
        await InstalledPackagesLoader.Instance.AddForeign(olderInstalledVersion);
        await InstalledPackagesLoader.Instance.AddForeign(newerInstalledVersion);
        var options = new InstallOptions
        {
            PostUpdateCommand = "echo after update",
            UninstallPreviousVersionsOnUpdate = true,
        };

        using var operation = new UpdatePackageOperation(package, options);
        var postOperations = GetInnerOperations(operation, "PostOperations");

        Assert.Collection(
            postOperations,
            inner =>
            {
                var postCommand = Assert.IsType<PrePostOperation>(inner.Operation);
                Assert.False(inner.MustSucceed);
                Assert.Contains("echo after update", postCommand.Metadata.Status);
            },
            inner =>
            {
                var uninstall = Assert.IsType<UninstallPackageOperation>(inner.Operation);
                Assert.False(inner.MustSucceed);
                Assert.Equal("2.0.0", uninstall.Package.VersionString);
            }
        );
        Assert.Contains("1.0.0 -> 3.0.0", operation.Metadata.OperationInformation);
    }

    [Fact]
    public void UninstallOperationBuildsPreAndPostCommandsForUninstallPath()
    {
        var package = CreatePackage();
        var options = new InstallOptions
        {
            PreUninstallCommand = "echo before uninstall",
            AbortOnPreUninstallFail = false,
            PostUninstallCommand = "echo after uninstall",
        };
        using var operation = new UninstallPackageOperation(package, options);

        var preOperations = GetInnerOperations(operation, "PreOperations");
        var postOperations = GetInnerOperations(operation, "PostOperations");

        var preOperation = Assert.Single(preOperations);
        var preCommand = Assert.IsType<PrePostOperation>(preOperation.Operation);
        Assert.False(preOperation.MustSucceed);
        Assert.Contains("echo before uninstall", preCommand.Metadata.Status);

        var postOperation = Assert.Single(postOperations);
        var postCommand = Assert.IsType<PrePostOperation>(postOperation.Operation);
        Assert.False(postOperation.MustSucceed);
        Assert.Contains("echo after uninstall", postCommand.Metadata.Status);
    }

    [Fact]
    public void InstallOperationPrepareProcessStartInfoUsesManagerCommandLineAndSetsBadges()
    {
        var manager = new PackageManagerBuilder()
            .ConfigureManager(manager =>
            {
                manager.ExecutablePath = "C:\\tools\\pkgmgr.exe";
                manager.ExecutableArguments = "--cli";
            })
            .ConfigureOperation(helper =>
                helper.ParametersFactory = (package, _, operation) =>
                [
                    operation.ToString().ToLowerInvariant(),
                    package.Id,
                ])
            .Build();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithOptions(new OverridenInstallationOptions(scope: PackageScope.Machine))
            .Build();
        var options = new InstallOptions
        {
            InstallationScope = PackageScope.User,
            InteractiveInstallation = true,
            SkipHashCheck = true,
        };
        using var operation = new InspectableInstallPackageOperation(package, options);
        AbstractOperation.BadgeCollection? badges = null;
        operation.BadgesChanged += (_, updatedBadges) => badges = updatedBadges;

        var startInfo = operation.PrepareProcessStartInfoForTests();

        Assert.Equal("C:\\tools\\pkgmgr.exe", startInfo.FileName);
        Assert.Equal("--cli install Contoso.Test", startInfo.Arguments.Trim());
        Assert.Equal(PackageTag.OnQueue, package.Tag);
        Assert.NotNull(badges);
        Assert.Equal(CoreTools.IsAdministrator(), badges!.AsAdministrator);
        Assert.True(badges.Interactive);
        Assert.True(badges.SkipHashCheck);
        Assert.Equal(PackageScope.Machine, badges.Scope);
    }

    [Fact]
    public async Task InstallOperationSuccessfulRunSetsPackageTagAndAddsInstalledCopy()
    {
        var package = CreatePackage();
        InitializeLoaders();
        using var operation = new SimulatedInstallPackageOperation(
            package,
            new InstallOptions(),
            OperationVeredict.Success
        );

        await operation.MainThread();
        await WaitForAsync(() => InstalledPackagesLoader.Instance.GetEquivalentPackage(package) is not null);

        Assert.Equal(PackageTag.AlreadyInstalled, package.Tag);
        Assert.NotNull(InstalledPackagesLoader.Instance.GetEquivalentPackage(package));
    }

    private static IReadOnlyList<AbstractOperation.InnerOperation> GetInnerOperations(
        AbstractOperation operation,
        string fieldName
    )
    {
        var field = typeof(AbstractOperation).GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic
        );

        return Assert.IsAssignableFrom<IReadOnlyList<AbstractOperation.InnerOperation>>(
            field?.GetValue(operation)
        );
    }

    private static IPackage CreatePackage()
    {
        var manager = CreateManager();
        return new PackageBuilder().WithManager(manager).Build();
    }

    private static IPackageManager CreateManager()
    {
        return new PackageManagerBuilder()
            .ConfigureManager(manager =>
            {
                manager.ExecutablePath = "C:\\test-tools\\manager.exe";
                manager.ExecutableArguments = "--test";
            })
            .ConfigureOperation(helper =>
                helper.ParametersFactory = (package, _, operation) =>
                [
                    operation.ToString().ToLowerInvariant(),
                    package.Id,
                ])
            .Build();
    }

    private static void InitializeLoaders()
    {
        _ = new DiscoverablePackagesLoader([]);
        _ = new UpgradablePackagesLoader([]);
        _ = new InstalledPackagesLoader([]);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (condition())
                return;

            await Task.Delay(25);
        }
    }

    private class InspectableInstallPackageOperation : InstallPackageOperation
    {
        public InspectableInstallPackageOperation(
            IPackage package,
            InstallOptions options,
            bool ignoreParallelInstalls = true,
            AbstractOperation? req = null
        )
            : base(package, options, ignoreParallelInstalls, req) { }

        public ProcessStartInfo PrepareProcessStartInfoForTests()
        {
            InitializeProcessStartInfoDefaults();
            PrepareProcessStartInfo();
            return process.StartInfo;
        }

        private void InitializeProcessStartInfoDefaults()
        {
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardInput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.StandardOutputEncoding = System.Text.Encoding.UTF8;
            process.StartInfo.StandardErrorEncoding = System.Text.Encoding.UTF8;
            process.StartInfo.StandardInputEncoding = System.Text.Encoding.UTF8;
            process.StartInfo.WorkingDirectory = Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile
            );
            process.StartInfo.FileName = "lol";
            process.StartInfo.Arguments = "lol";
        }
    }

    private sealed class SimulatedInstallPackageOperation : InspectableInstallPackageOperation
    {
        private readonly OperationVeredict _veredict;

        public SimulatedInstallPackageOperation(
            IPackage package,
            InstallOptions options,
            OperationVeredict veredict
        )
            : base(package, options)
        {
            _veredict = veredict;
        }

        protected override Task<OperationVeredict> PerformOperation()
        {
            return Task.FromResult(_veredict);
        }
    }

    private sealed class StubOperation : AbstractOperation
    {
        public StubOperation()
            : base(queue_enabled: false)
        {
            Metadata.Status = "Stub status";
            Metadata.Title = "Stub title";
            Metadata.OperationInformation = "Stub info";
            Metadata.SuccessTitle = "Stub success";
            Metadata.SuccessMessage = "Stub success";
            Metadata.FailureTitle = "Stub failure";
            Metadata.FailureMessage = "Stub failure";
        }

        protected override void ApplyRetryAction(string retryMode) { }

        protected override Task<OperationVeredict> PerformOperation()
        {
            return Task.FromResult(OperationVeredict.Success);
        }

        public override Task<Uri> GetOperationIcon()
        {
            return Task.FromResult(new Uri("about:blank"));
        }
    }
}
