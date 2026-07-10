using System.Diagnostics;
using System.Reflection;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Enums;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Operations;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.PackageLoader;
using UniGetUI.PackageEngine.Serializable;
using UniGetUI.PackageEngine.Structs;
using UniGetUI.PackageEngine.Tests.Infrastructure.Builders;
using UniGetUI.PackageEngine.Tests.Infrastructure.Fakes;
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

    [Fact]
    public async Task InstallOperationSuccessfulRunPrefersAuthoritativeInstalledVersion()
    {
        TestPackageManager? manager = null;
        Package? installedPackage = null;
        manager = new PackageManagerBuilder()
            .WithInstalledPackages(_ => [Assert.IsType<Package>(installedPackage)])
            .Build();
        var searchResult = new PackageBuilder()
            .WithManager(manager)
            .WithId("dotnetsay")
            .WithVersion("3.0.3")
            .Build();
        installedPackage = new PackageBuilder()
            .WithManager(manager)
            .WithId("dotnetsay")
            .WithVersion("2.1.4")
            .Build();
        InitializeLoaders();
        using var operation = new SimulatedInstallPackageOperation(
            searchResult,
            new InstallOptions { Version = "2.1.4" },
            OperationVeredict.Success
        );

        await operation.MainThread();
        await WaitForAsync(() =>
            InstalledPackagesLoader.Instance.GetEquivalentPackages(searchResult)
                .Any(package => package.VersionString == "2.1.4")
        );

        Assert.DoesNotContain(
            InstalledPackagesLoader.Instance.GetEquivalentPackages(searchResult),
            package => package.VersionString == "3.0.3"
        );
    }

    [Fact]
    public async Task UpdateOperationSuccessfulRunPrefersAuthoritativeInstalledVersion()
    {
        TestPackageManager? manager = null;
        Package? installedPackage = null;
        manager = new PackageManagerBuilder()
            .WithInstalledPackages(_ => [Assert.IsType<Package>(installedPackage)])
            .Build();
        var upgradablePackage = new PackageBuilder()
            .WithManager(manager)
            .WithId("dotnetsay")
            .WithVersion("2.1.4")
            .WithNewVersion("3.0.0")
            .Build();
        installedPackage = new PackageBuilder()
            .WithManager(manager)
            .WithId("dotnetsay")
            .WithVersion("3.0.3")
            .Build();
        InitializeLoaders();
        await InstalledPackagesLoader.Instance.AddForeign(upgradablePackage);
        using var operation = new SimulatedUpdatePackageOperation(
            upgradablePackage,
            new InstallOptions(),
            OperationVeredict.Success
        );

        await operation.MainThread();
        await WaitForAsync(() =>
            InstalledPackagesLoader.Instance.GetEquivalentPackages(upgradablePackage)
                .Any(package => package.VersionString == "3.0.3")
        );

        Assert.DoesNotContain(
            InstalledPackagesLoader.Instance.GetEquivalentPackages(upgradablePackage),
            package => package.VersionString == "3.0.0"
        );
    }

    [Fact]
    public async Task UpdateOperationSuccessfulRunPrefersRequestedVersionWhenSnapshotLags()
    {
        TestPackageManager? manager = null;
        Package? installedPackage = null;
        manager = new PackageManagerBuilder()
            .WithInstalledPackages(_ => [Assert.IsType<Package>(installedPackage)])
            .Build();
        var installedBeforeUpdate = new PackageBuilder()
            .WithManager(manager)
            .WithId("dotnetsay")
            .WithVersion("2.1.4")
            .Build();
        installedPackage = new PackageBuilder()
            .WithManager(manager)
            .WithId("dotnetsay")
            .WithVersion("2.1.4")
            .Build();
        InitializeLoaders();
        await InstalledPackagesLoader.Instance.AddForeign(installedBeforeUpdate);
        using var operation = new SimulatedUpdatePackageOperation(
            installedBeforeUpdate,
            new InstallOptions { Version = "3.0.3" },
            OperationVeredict.Success
        );

        await operation.MainThread();
        await WaitForAsync(() =>
            InstalledPackagesLoader.Instance.GetEquivalentPackages(installedBeforeUpdate)
                .Any(package => package.VersionString == "3.0.3")
        );

        Assert.DoesNotContain(
            InstalledPackagesLoader.Instance.GetEquivalentPackages(installedBeforeUpdate),
            package => package.VersionString == "2.1.4"
        );
    }

    [Fact]
    public void UsernameRedactionAppliesToDisplayOutputButNeverToResultParsingOutput()
    {
        // Regression: result parsing must see raw output; only display (GetOutput) may redact.
        var username = Environment.UserName;
        if (string.IsNullOrEmpty(username))
            return;

        using var operation = new LoggingStubOperation();
        var rawLine = $"Error: the operation was canceled by C:\\Users\\{username}\\app";
        operation.EmitLine(rawLine, AbstractOperation.LineType.Information);

        bool previous = Logger.RedactUsername;
        Logger.RedactUsername = true;
        try
        {
            var parsingOutput = operation.RawOutputForTests();
            var displayOutput = operation.GetOutput();

            Assert.Contains(parsingOutput, l => l.Item1 == rawLine);
            Assert.DoesNotContain(displayOutput, l => l.Item1.Contains(username));
            Assert.Contains(displayOutput, l => l.Item1.Contains("****"));
        }
        finally
        {
            Logger.RedactUsername = previous;
        }
    }

    [Fact]
    public async Task CancelWaitsForTheActiveOperationToCompleteCleanup()
    {
        using var operation = new CancellationAwareStubOperation();
        Task mainThread = operation.MainThread();
        await operation.PerformStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        operation.Cancel();
        await operation.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.False(mainThread.IsCompleted);
        operation.AllowCleanupToComplete.TrySetResult(true);
        await mainThread;

        Assert.Equal(OperationStatus.Canceled, operation.Status);
    }

    [Fact]
    public async Task RetryWaitsForCanceledRunCleanupBeforeStartingAgain()
    {
        using var operation = new RetryAwareStubOperation();
        Task firstRun = operation.MainThread();
        await operation.FirstRunStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        operation.Cancel();
        await operation.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
        operation.Retry(AbstractOperation.RetryMode.Retry);

        await Task.Delay(50);
        Assert.Equal(1, operation.RunCount);
        Assert.False(firstRun.IsCompleted);

        operation.AllowCleanupToComplete.TrySetResult(true);
        await firstRun;
        await operation.SecondRunStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await WaitForAsync(() => operation.Status is OperationStatus.Succeeded);

        Assert.Equal(2, operation.RunCount);
        Assert.Equal(1, operation.MaxConcurrentRuns);
    }

    [Fact]
    public async Task ConcurrentMainThreadCallsShareTheActiveRun()
    {
        using var operation = new CancellationAwareStubOperation();

        Task firstCall = operation.MainThread();
        Task secondCall = operation.MainThread();

        Assert.Same(firstCall, secondCall);
        await operation.PerformStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        operation.Cancel();
        await operation.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
        operation.AllowCleanupToComplete.TrySetResult(true);
        await firstCall;
    }

    [Fact]
    public async Task RetryRequestedFromRetriedRunCompletionSchedulesAnotherRun()
    {
        using var operation = new RepeatedRetryStubOperation();
        await operation.MainThread();
        operation.OperationFailed += (_, _) =>
        {
            if (operation.RunCount == 2)
                operation.Retry(AbstractOperation.RetryMode.Retry);
        };

        operation.Retry(AbstractOperation.RetryMode.Retry);

        await operation.ThirdRunStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await WaitForAsync(() => operation.Status is OperationStatus.Failed);
        Assert.Equal(3, operation.RunCount);
    }

    [Fact]
    public async Task DisposePreventsADeferredRetryFromStarting()
    {
        var operation = new RetryAwareStubOperation();
        Task firstRun = operation.MainThread();
        await operation.FirstRunStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        operation.Cancel();
        await operation.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
        operation.Retry(AbstractOperation.RetryMode.Retry);

        operation.Dispose();
        operation.AllowCleanupToComplete.TrySetResult(true);
        await firstRun;
        await Task.Delay(50);

        Assert.Equal(1, operation.RunCount);
        Assert.Throws<ObjectDisposedException>(() =>
        {
            _ = operation.MainThread();
        });
    }

    [Fact]
    public async Task DisposeFromTerminalEventPreservesCompletedStatus()
    {
        var operation = new RepeatedRetryStubOperation();
        operation.OperationFailed += (_, _) => operation.Dispose();

        await operation.MainThread();

        Assert.Equal(OperationStatus.Failed, operation.Status);
    }

    [Fact]
    public async Task DisposeFromStartupFailurePreservesFailedStatus()
    {
        var operation = new InvalidMetadataStubOperation();
        operation.OperationFailed += (_, _) => operation.Dispose();

        await operation.MainThread();

        Assert.Equal(OperationStatus.Failed, operation.Status);
    }

    [Fact]
    public async Task RetryOptionFailureDoesNotPreventALaterRetry()
    {
        using var operation = new ThrowingRetryStubOperation();
        await operation.MainThread();

        operation.Retry("InvalidRetryMode");
        operation.Retry(AbstractOperation.RetryMode.Retry);

        await operation.SecondRunStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(2, operation.RunCount);
    }

    [Fact]
    public async Task CancellationFromEnqueuedDoesNotRemainOnAFullQueue()
    {
        using var firstOperation = new CancellationAwareStubOperation(queueEnabled: true);
        using var canceledOperation = new CancellationAwareStubOperation(queueEnabled: true);
        AbstractOperation.OperationQueue.Clear();
        AbstractOperation.MAX_OPERATIONS = 1;

        Task firstRun = firstOperation.MainThread();
        await firstOperation.PerformStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        canceledOperation.Enqueued += (_, _) => canceledOperation.Cancel();

        await canceledOperation.MainThread().WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(OperationStatus.Canceled, canceledOperation.Status);
        Assert.DoesNotContain(canceledOperation, AbstractOperation.OperationQueue);

        firstOperation.Cancel();
        await firstOperation.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
        firstOperation.AllowCleanupToComplete.TrySetResult(true);
        await firstRun;
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

    private sealed class SimulatedUpdatePackageOperation : UpdatePackageOperation
    {
        private readonly OperationVeredict _veredict;

        public SimulatedUpdatePackageOperation(
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

    private sealed class CancellationAwareStubOperation : AbstractOperation
    {
        public TaskCompletionSource<bool> PerformStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        public TaskCompletionSource<bool> CancellationObserved { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        public TaskCompletionSource<bool> AllowCleanupToComplete { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public CancellationAwareStubOperation(bool queueEnabled = false)
            : base(queue_enabled: queueEnabled)
        {
            Metadata.Status = "Cancelable stub status";
            Metadata.Title = "Cancelable stub title";
            Metadata.OperationInformation = "Cancelable stub info";
            Metadata.SuccessTitle = "Cancelable stub success";
            Metadata.SuccessMessage = "Cancelable stub success";
            Metadata.FailureTitle = "Cancelable stub failure";
            Metadata.FailureMessage = "Cancelable stub failure";
        }

        protected override void ApplyRetryAction(string retryMode) { }

        protected override async Task<OperationVeredict> PerformOperation()
        {
            PerformStarted.TrySetResult(true);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken);
            }
            catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested)
            {
                CancellationObserved.TrySetResult(true);
                await AllowCleanupToComplete.Task;
                return OperationVeredict.Canceled;
            }

            return OperationVeredict.Success;
        }

        public override Task<Uri> GetOperationIcon() => Task.FromResult(new Uri("about:blank"));
    }

    private sealed class RetryAwareStubOperation : AbstractOperation
    {
        private int _concurrentRuns;
        private int _maxConcurrentRuns;
        private int _runCount;

        public TaskCompletionSource<bool> FirstRunStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        public TaskCompletionSource<bool> CancellationObserved { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        public TaskCompletionSource<bool> AllowCleanupToComplete { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        public TaskCompletionSource<bool> SecondRunStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public int RunCount => Volatile.Read(ref _runCount);
        public int MaxConcurrentRuns => Volatile.Read(ref _maxConcurrentRuns);

        public RetryAwareStubOperation()
            : base(queue_enabled: false)
        {
            Metadata.Status = "Retryable stub status";
            Metadata.Title = "Retryable stub title";
            Metadata.OperationInformation = "Retryable stub info";
            Metadata.SuccessTitle = "Retryable stub success";
            Metadata.SuccessMessage = "Retryable stub success";
            Metadata.FailureTitle = "Retryable stub failure";
            Metadata.FailureMessage = "Retryable stub failure";
        }

        protected override void ApplyRetryAction(string retryMode) { }

        protected override async Task<OperationVeredict> PerformOperation()
        {
            int concurrentRuns = Interlocked.Increment(ref _concurrentRuns);
            UpdateMaximum(ref _maxConcurrentRuns, concurrentRuns);
            int run = Interlocked.Increment(ref _runCount);
            try
            {
                if (run == 1)
                {
                    FirstRunStarted.TrySetResult(true);
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken);
                    }
                    catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested)
                    {
                        CancellationObserved.TrySetResult(true);
                        await AllowCleanupToComplete.Task;
                        return OperationVeredict.Canceled;
                    }
                }

                SecondRunStarted.TrySetResult(true);
                return OperationVeredict.Success;
            }
            finally
            {
                Interlocked.Decrement(ref _concurrentRuns);
            }
        }

        private static void UpdateMaximum(ref int target, int value)
        {
            int current;
            do
            {
                current = Volatile.Read(ref target);
                if (current >= value)
                    return;
            } while (Interlocked.CompareExchange(ref target, value, current) != current);
        }

        public override Task<Uri> GetOperationIcon() => Task.FromResult(new Uri("about:blank"));
    }

    private sealed class RepeatedRetryStubOperation : AbstractOperation
    {
        private int _runCount;

        public int RunCount => Volatile.Read(ref _runCount);
        public TaskCompletionSource<bool> ThirdRunStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public RepeatedRetryStubOperation()
            : base(queue_enabled: false)
        {
            Metadata.Status = "Repeated retry stub status";
            Metadata.Title = "Repeated retry stub title";
            Metadata.OperationInformation = "Repeated retry stub info";
            Metadata.SuccessTitle = "Repeated retry stub success";
            Metadata.SuccessMessage = "Repeated retry stub success";
            Metadata.FailureTitle = "Repeated retry stub failure";
            Metadata.FailureMessage = "Repeated retry stub failure";
        }

        protected override void ApplyRetryAction(string retryMode) { }

        protected override Task<OperationVeredict> PerformOperation()
        {
            if (Interlocked.Increment(ref _runCount) == 3)
                ThirdRunStarted.TrySetResult(true);
            return Task.FromResult(OperationVeredict.Failure);
        }

        public override Task<Uri> GetOperationIcon() => Task.FromResult(new Uri("about:blank"));
    }

    private sealed class InvalidMetadataStubOperation : AbstractOperation
    {
        public InvalidMetadataStubOperation()
            : base(queue_enabled: false) { }

        protected override void ApplyRetryAction(string retryMode) { }

        protected override Task<OperationVeredict> PerformOperation()
            => Task.FromResult(OperationVeredict.Success);

        public override Task<Uri> GetOperationIcon() => Task.FromResult(new Uri("about:blank"));
    }

    private sealed class ThrowingRetryStubOperation : AbstractOperation
    {
        private int _runCount;

        public int RunCount => Volatile.Read(ref _runCount);
        public TaskCompletionSource<bool> SecondRunStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public ThrowingRetryStubOperation()
            : base(queue_enabled: false)
        {
            Metadata.Status = "Throwing retry stub status";
            Metadata.Title = "Throwing retry stub title";
            Metadata.OperationInformation = "Throwing retry stub info";
            Metadata.SuccessTitle = "Throwing retry stub success";
            Metadata.SuccessMessage = "Throwing retry stub success";
            Metadata.FailureTitle = "Throwing retry stub failure";
            Metadata.FailureMessage = "Throwing retry stub failure";
        }

        protected override void ApplyRetryAction(string retryMode)
        {
            if (retryMode == "InvalidRetryMode")
                throw new InvalidOperationException("Invalid retry mode");
        }

        protected override Task<OperationVeredict> PerformOperation()
        {
            if (Interlocked.Increment(ref _runCount) == 2)
                SecondRunStarted.TrySetResult(true);
            return Task.FromResult(OperationVeredict.Failure);
        }

        public override Task<Uri> GetOperationIcon() => Task.FromResult(new Uri("about:blank"));
    }

    private sealed class LoggingStubOperation : AbstractOperation
    {
        public LoggingStubOperation()
            : base(queue_enabled: false) { }

        public void EmitLine(string line, LineType type) => Line(line, type);

        public IReadOnlyList<(string, LineType)> RawOutputForTests() => GetRawOutput();

        protected override void ApplyRetryAction(string retryMode) { }

        protected override Task<OperationVeredict> PerformOperation()
            => Task.FromResult(OperationVeredict.Success);

        public override Task<Uri> GetOperationIcon() => Task.FromResult(new Uri("about:blank"));
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
