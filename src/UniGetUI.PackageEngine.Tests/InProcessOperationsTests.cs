using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Enums;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Operations;
using UniGetUI.PackageEngine.PackageLoader;
using UniGetUI.PackageEngine.Serializable;
using UniGetUI.PackageEngine.Tests.Infrastructure.Builders;
using UniGetUI.PackageEngine.Tests.Infrastructure.Fakes;
using UniGetUI.PackageOperations;
using LineType = UniGetUI.PackageOperations.AbstractOperation.LineType;

namespace UniGetUI.PackageEngine.Tests;

/// <summary>
/// Package and source operations of managers that run inside UniGetUI instead of launching an
/// executable.
/// </summary>
[Collection(nameof(OperationOrchestrationTestCollection))]
public sealed class InProcessOperationsTests
{
    [Fact]
    public async Task InstallRunsThroughTheInProcessHelperWithoutStartingAProcess()
    {
        var (manager, operationHelper, _) = CreateInProcessManager();
        operationHelper.PerformFactory = (_, _, operation, output, _) =>
        {
            Assert.Equal(OperationType.Install, operation);
            output.Info("Installing the skill");
            output.Error("A warning from the library");
            output.Verbose("A diagnostic line");
            return Task.FromResult(OperationVeredict.Success);
        };
        var package = new PackageBuilder().WithManager(manager).Build();
        InitializeLoaders();
        using var operation = new InstallPackageOperation(package, new InstallOptions());

        await operation.MainThread();

        Assert.Equal(OperationStatus.Succeeded, operation.Status);
        Assert.Equal(1, operationHelper.PerformCalls);
        Assert.Null(operation.LastReturnCode);
        Assert.Equal(PackageTag.AlreadyInstalled, package.Tag);
        var output = operation.GetOutput();
        Assert.Contains(("Installing the skill", LineType.Information), output);
        Assert.Contains(("A warning from the library", LineType.Error), output);
        Assert.Contains(("A diagnostic line", LineType.VerboseDetails), output);
    }

    [Fact]
    public async Task UpdateAndUninstallPassTheirRoleToTheInProcessHelper()
    {
        var (manager, operationHelper, _) = CreateInProcessManager();
        var roles = new List<OperationType>();
        operationHelper.PerformFactory = (_, _, operation, _, _) =>
        {
            roles.Add(operation);
            return Task.FromResult(OperationVeredict.Success);
        };
        var package = new PackageBuilder().WithManager(manager).WithNewVersion("2.0.0").Build();
        InitializeLoaders();

        using (var update = new UpdatePackageOperation(package, new InstallOptions()))
            await update.MainThread();
        using (var uninstall = new UninstallPackageOperation(package, new InstallOptions()))
            await uninstall.MainThread();

        Assert.Equal([OperationType.Update, OperationType.Uninstall], roles);
    }

    [Fact]
    public async Task FailureReportedByTheInProcessHelperKeepsItsMessage()
    {
        var (manager, operationHelper, _) = CreateInProcessManager();
        operationHelper.PerformFactory = (_, _, _, output, _) =>
        {
            output.SetFailureMessage("The source has no skill named example");
            return Task.FromResult(OperationVeredict.Failure);
        };
        var package = new PackageBuilder().WithManager(manager).Build();
        InitializeLoaders();
        using var operation = new InstallPackageOperation(package, new InstallOptions());

        await operation.MainThread();

        Assert.Equal(OperationStatus.Failed, operation.Status);
        Assert.Equal("The source has no skill named example", operation.Metadata.FailureMessage);
        Assert.Equal(PackageTag.Failed, package.Tag);
    }

    [Fact]
    public async Task ExceptionThrownByTheInProcessHelperFailsTheOperation()
    {
        var (manager, operationHelper, _) = CreateInProcessManager();
        operationHelper.PerformFactory = (_, _, _, _, _) =>
            throw new InvalidOperationException("Failed to clone repository");
        var package = new PackageBuilder().WithManager(manager).Build();
        InitializeLoaders();
        using var operation = new InstallPackageOperation(package, new InstallOptions());

        await operation.MainThread();

        Assert.Equal(OperationStatus.Failed, operation.Status);
        Assert.Contains(("Failed to clone repository", LineType.Error), operation.GetOutput());
    }

    [Fact]
    public async Task CancelingStopsTheInProcessHelperThroughItsToken()
    {
        var (manager, operationHelper, _) = CreateInProcessManager();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        operationHelper.PerformFactory = async (_, _, _, _, cancellationToken) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return OperationVeredict.Success;
        };
        var package = new PackageBuilder().WithManager(manager).Build();
        InitializeLoaders();
        using var operation = new InstallPackageOperation(package, new InstallOptions());

        Task mainThread = operation.MainThread();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        operation.Cancel();
        await mainThread.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(OperationStatus.Canceled, operation.Status);
    }

    [Fact]
    public async Task InProcessOperationIgnoresARequestedElevation()
    {
        var (manager, _, _) = CreateInProcessManager();
        var package = new PackageBuilder().WithManager(manager).Build();
        InitializeLoaders();
        var options = new InstallOptions
        {
            RunAsAdministrator = true,
            InteractiveInstallation = true,
            SkipHashCheck = true,
        };
        using var operation = new InstallPackageOperation(package, options);
        AbstractOperation.BadgeCollection? badges = null;
        operation.BadgesChanged += (_, value) => badges = value;

        Assert.Equal(CoreTools.IsAdministrator(), operation.WillRunElevated);
        await operation.MainThread();

        Assert.Equal(OperationStatus.Succeeded, operation.Status);
        Assert.NotNull(badges);
        Assert.Equal(CoreTools.IsAdministrator(), badges!.AsAdministrator);
        Assert.False(badges.Interactive);
        Assert.False(badges.SkipHashCheck);
        Assert.Equal(CoreTools.IsAdministrator(), operation.WillRunElevated);
    }

    [Fact]
    public async Task InProcessOperationRunsLocallyAndSaysSoWhenTheAgentBrokerIsEnabled()
    {
        var (manager, operationHelper, _) = CreateInProcessManager();
        var package = new PackageBuilder().WithManager(manager).Build();
        InitializeLoaders();
        Settings.Set(Settings.K.UseAgentBroker, true);
        try
        {
            using var operation = new InstallPackageOperation(package, new InstallOptions());

            await operation.MainThread();

            Assert.Equal(OperationStatus.Succeeded, operation.Status);
            Assert.Equal(1, operationHelper.PerformCalls);
            Assert.Contains(
                operation.GetOutput(),
                line =>
                    line.Item2 is LineType.Information
                    && line.Item1.Contains("not checked against the package policy")
            );
        }
        finally
        {
            Settings.Set(Settings.K.UseAgentBroker, false);
        }
    }

    [Fact]
    public async Task SourcesAreAddedAndRemovedThroughTheInProcessSourceHelper()
    {
        var (manager, _, sourcesHelper) = CreateInProcessManager();
        var source = new SourceBuilder()
            .WithManager(manager)
            .WithName("contoso")
            .WithUrl("https://skills.contoso.test")
            .Build();

        using (var add = new AddSourceOperation(source))
        {
            await add.MainThread();
            Assert.Equal(OperationStatus.Succeeded, add.Status);
        }

        Assert.Contains(sourcesHelper.GetSources(), s => s.Name == "contoso");

        using (var remove = new RemoveSourceOperation(source))
        {
            await remove.MainThread();
            Assert.Equal(OperationStatus.Succeeded, remove.Status);
        }

        Assert.Equal(1, sourcesHelper.AddCalls);
        Assert.Equal(1, sourcesHelper.RemoveCalls);
        Assert.DoesNotContain(sourcesHelper.GetSources(), s => s.Name == "contoso");
    }

    [Fact]
    public async Task SourceFailureReportedByTheInProcessSourceHelperFailsTheOperation()
    {
        var (manager, _, sourcesHelper) = CreateInProcessManager();
        sourcesHelper.AddFactory = (_, output) =>
        {
            output.Error("No skills were found at this address");
            return OperationVeredict.Failure;
        };
        var source = new SourceBuilder().WithManager(manager).WithName("broken").Build();
        using var operation = new AddSourceOperation(source);

        await operation.MainThread();

        Assert.Equal(OperationStatus.Failed, operation.Status);
        Assert.Contains(("No skills were found at this address", LineType.Error), operation.GetOutput());
    }

    [Fact]
    public void ManagerDeclaredInProcessWithoutAnInProcessHelperIsNotReady()
    {
        var manager = new PackageManagerBuilder()
            .ConfigureCapabilities(capabilities =>
            {
                capabilities.RunsInProcess = true;
                return capabilities;
            })
            .Build();

        Assert.False(manager.IsReady());
    }

    private static (
        TestPackageManager Manager,
        TestInProcessOperationHelper OperationHelper,
        TestInProcessSourceHelper SourcesHelper
    ) CreateInProcessManager()
    {
        TestInProcessOperationHelper? operationHelper = null;
        TestInProcessSourceHelper? sourcesHelper = null;
        var manager = new PackageManagerBuilder()
            .WithName("InProcessManager")
            .ConfigureManager(manager =>
            {
                operationHelper = new TestInProcessOperationHelper(manager);
                sourcesHelper = new TestInProcessSourceHelper(manager);
                manager.UseInProcessOperations(operationHelper, sourcesHelper);
            })
            .Build();

        Assert.True(manager.IsReady());
        return (manager, operationHelper!, sourcesHelper!);
    }

    private static void InitializeLoaders()
    {
        _ = new DiscoverablePackagesLoader([]);
        _ = new UpgradablePackagesLoader([]);
        _ = new InstalledPackagesLoader([]);
    }
}
