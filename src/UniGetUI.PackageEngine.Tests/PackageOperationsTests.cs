using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
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
using BrokerApiCancelResponse = Devolutions.Now.Policy.Api.CancelResponse;
using BrokerApiCapabilitiesResponse = Devolutions.Now.Policy.Api.CapabilitiesResponse;
using BrokerApiConstants = Devolutions.Now.Policy.Api.BrokerApi;
using BrokerApiDecision = Devolutions.Now.Policy.Api.Decision;
using BrokerApiDecisionInfo = Devolutions.Now.Policy.Api.DecisionInfo;
using BrokerApiElevation = Devolutions.Now.Policy.Api.Elevation;
using BrokerApiEventChannel = Devolutions.Now.Policy.Api.EventChannel;
using BrokerApiEventChannelKind = Devolutions.Now.Policy.Api.EventChannelKind;
using BrokerApiExecutionResponse = Devolutions.Now.Policy.Api.ExecutionResponse;
using BrokerApiHealthResponse = Devolutions.Now.Policy.Api.HealthResponse;
using BrokerApiHealthStatus = Devolutions.Now.Policy.Api.HealthStatus;
using BrokerApiManagerCapability = Devolutions.Now.Policy.Api.ManagerCapability;
using BrokerApiManagerName = Devolutions.Now.Policy.Api.ManagerName;
using BrokerApiOperation = Devolutions.Now.Policy.Api.Operation;
using BrokerApiOperationStatus = Devolutions.Now.Policy.Api.OperationStatus;
using BrokerApiOperationSubmission = Devolutions.Now.Policy.Api.OperationSubmission;
using BrokerApiPackageRequest = Devolutions.Now.Policy.Api.PackageRequest;
using BrokerApiServerContext = Devolutions.Now.Policy.Api.ServerContext;
using BrokerApiStatusResponse = Devolutions.Now.Policy.Api.StatusResponse;
using BrokerClientErrorKind = Devolutions.Now.Policy.Client.BrokerClientErrorKind;
using BrokerClientException = Devolutions.Now.Policy.Client.BrokerClientException;
using BrokerJson = Devolutions.Now.Policy.Api.BrokerJson;
using BrokerTransportKind = Devolutions.Now.Policy.Api.Transport;
using BrokerTransportRequest = Devolutions.Now.Policy.Client.BrokerTransportRequest;
using BrokerTransportResponse = Devolutions.Now.Policy.Client.BrokerTransportResponse;
using IBrokerTransport = Devolutions.Now.Policy.Client.IBrokerTransport;
using LineType = UniGetUI.PackageOperations.AbstractOperation.LineType;

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
    public async Task InstallOperationCanceledByManagerClearsPackageTag()
    {
        var package = CreatePackage();
        InitializeLoaders();
        using var operation = new SimulatedInstallPackageOperation(
            package,
            new InstallOptions(),
            OperationVeredict.Canceled
        );

        await operation.MainThread();

        Assert.Equal(OperationStatus.Canceled, operation.Status);
        Assert.Equal(PackageTag.Default, package.Tag);
    }

    [Fact]
    public async Task InstallOperationCanceledWhileQueuedClearsPackageTag()
    {
        var package = CreatePackage();
        InitializeLoaders();
        using var operation = new SimulatedInstallPackageOperation(
            package,
            new InstallOptions(),
            OperationVeredict.Success
        );
        operation.Enqueued += (_, _) => operation.Cancel();

        await operation.MainThread();

        Assert.Equal(OperationStatus.Canceled, operation.Status);
        Assert.Equal(PackageTag.Default, package.Tag);
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
    public async Task OutputSnapshotsDoNotThrowWhileLinesAreAppended()
    {
        using var operation = new LoggingStubOperation();
        const int lines = 20_000;

        var writer = Task.Run(() =>
        {
            for (int i = 0; i < lines; i++)
                operation.EmitLine($"line {i}", AbstractOperation.LineType.Information);
        });

        var reader = Task.Run(() =>
        {
            while (!writer.IsCompleted)
            {
                foreach (var _ in operation.GetOutput()) { }
                foreach (var _ in operation.RawOutputForTests()) { }
            }
        });

        await Task.WhenAll(writer, reader);

        Assert.Equal(lines, operation.GetOutput().Count);
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

    [Fact]
    public async Task BrokerOperationFailsWithoutLocalFallbackWhenProbeFails()
    {
        var transport = new FakeBrokerTransport(healthy: false);

        await AssertBrokerUnavailableFailure(transport);
    }

    [Fact]
    public async Task BrokerOperationFailsWithoutLocalFallbackWhenBrokerDropsAfterProbe()
    {
        var transport = new FakeBrokerTransport(healthy: true);

        await AssertBrokerUnavailableFailure(transport);

        // The availability probe succeeded; the outage happened on the actual request.
        Assert.Contains("/v1/health", transport.RequestedPaths);
        Assert.Contains(transport.RequestedPaths, path => path != "/v1/health");
    }

    /// <summary>
    /// Runs an install operation against a broker whose transport simulates an outage and
    /// asserts the policy-enforcement contract: the operation fails with the
    /// broker-unavailable metadata, raises <see cref="PackageOperation.BrokerUnavailable"/>,
    /// and never falls back to local process execution.
    /// </summary>
    private static async Task AssertBrokerUnavailableFailure(FakeBrokerTransport transport)
    {
        bool originalSetting = Settings.Get(Settings.K.UseAgentBroker);
        bool localExecutionPrepared = false;
        var manager = new PackageManagerBuilder()
            .WithName("Chocolatey")
            .ConfigureManager(m =>
            {
                m.ExecutablePath = "C:\\test-tools\\choco.exe";
                m.ExecutableArguments = "--test";
            })
            .ConfigureOperation(helper =>
                helper.ParametersFactory = (package, _, operation) =>
                {
                    localExecutionPrepared = true;
                    return [operation.ToString().ToLowerInvariant(), package.Id];
                })
            .Build();
        var package = new PackageBuilder().WithManager(manager).Build();
        string? notifiedMessage = null;
        EventHandler<string> onBrokerUnavailable = (_, message) => notifiedMessage = message;
        PackageOperation.BrokerUnavailable += onBrokerUnavailable;
        PackageOperation.BrokerTransportFactory = () => transport;
        Settings.Set(Settings.K.UseAgentBroker, true);
        try
        {
            using var operation = new BrokerProbingInstallPackageOperation(package, new InstallOptions());
            var veredict = await operation.InvokePerformOperationForTests();

            Assert.Equal(OperationVeredict.Failure, veredict);
            Assert.Equal(
                CoreTools.Translate("Agent broker unavailable"),
                operation.Metadata.FailureTitle);
            Assert.False(string.IsNullOrWhiteSpace(operation.Metadata.FailureMessage));
            Assert.Equal(operation.Metadata.FailureMessage, notifiedMessage);
            Assert.False(localExecutionPrepared);
        }
        finally
        {
            Settings.Set(Settings.K.UseAgentBroker, originalSetting);
            PackageOperation.BrokerTransportFactory = null;
            PackageOperation.BrokerUnavailable -= onBrokerUnavailable;
        }
    }

    private sealed record BrokeredRunResult(
        OperationVeredict Veredict,
        IReadOnlyList<(string, LineType)> Output);

    /// <summary>
    /// Runs an install operation against a scripted broker transport with the UseAgentBroker
    /// setting enabled, fast status polling, and short cancel timeouts. The caller scripts the
    /// transport behavior and can trigger operation cancellation from transport callbacks.
    /// </summary>
    private static async Task<OperationVeredict> RunBrokeredOperation(
        ScriptedBrokerTransport transport,
        Action<CancellationTokenSource>? configureCancellation = null,
        TimeSpan? operationTimeout = null)
    {
        var result = await RunBrokeredOperationWithOutput(
            transport, configureCancellation, operationTimeout: operationTimeout);
        return result.Veredict;
    }

    /// <summary>
    /// Like <see cref="RunBrokeredOperation"/>, but also returns the operation's output
    /// log and allows customizing the manager's operation helper (e.g. to inspect the
    /// process output passed to the result parser) and hooking the created operation.
    /// </summary>
    private static async Task<BrokeredRunResult> RunBrokeredOperationWithOutput(
        ScriptedBrokerTransport transport,
        Action<CancellationTokenSource>? configureCancellation = null,
        Action<TestPackageOperationHelper>? configureOperationHelper = null,
        Action<AbstractOperation>? onOperationCreated = null,
        TimeSpan? operationTimeout = null)
    {
        bool originalSetting = Settings.Get(Settings.K.UseAgentBroker);
        int originalPollInterval = PackageOperation.BrokerStatusPollIntervalMs;
        TimeSpan originalCancelRequestTimeout = PackageOperation.BrokerCancelRequestTimeout;
        TimeSpan originalCancelConfirmTimeout = PackageOperation.BrokerCancelConfirmTimeout;
        TimeSpan originalOperationTimeout = PackageOperation.BrokerOperationTimeout;
        var manager = new PackageManagerBuilder()
            .WithName("Chocolatey")
            .ConfigureManager(m =>
            {
                m.ExecutablePath = "C:\\test-tools\\choco.exe";
                m.ExecutableArguments = "--test";
            })
            .ConfigureOperation(helper => configureOperationHelper?.Invoke(helper))
            .Build();
        var package = new PackageBuilder().WithManager(manager).Build();
        PackageOperation.BrokerTransportFactory = () => transport;
        PackageOperation.BrokerStatusPollIntervalMs = 5;
        PackageOperation.BrokerCancelRequestTimeout = TimeSpan.FromSeconds(2);
        PackageOperation.BrokerCancelConfirmTimeout = TimeSpan.FromSeconds(2);
        if (operationTimeout is not null)
            PackageOperation.BrokerOperationTimeout = operationTimeout.Value;
        Settings.Set(Settings.K.UseAgentBroker, true);
        try
        {
            using var operation = new BrokerProbingInstallPackageOperation(package, new InstallOptions());
            if (configureCancellation is not null)
            {
                // Attach a cancellation source the same way MainThread() would, so the
                // operation's CancellationToken plumbing is exercised end-to-end.
                var cancellationSource = new CancellationTokenSource();
                operation.SetRunCancellationSourceForTests(cancellationSource);
                configureCancellation(cancellationSource);
            }

            onOperationCreated?.Invoke(operation);

            var veredict = await operation.InvokePerformOperationForTests().WaitAsync(TimeSpan.FromSeconds(10));
            return new BrokeredRunResult(veredict, operation.GetOutput());
        }
        finally
        {
            Settings.Set(Settings.K.UseAgentBroker, originalSetting);
            PackageOperation.BrokerTransportFactory = null;
            PackageOperation.BrokerStatusPollIntervalMs = originalPollInterval;
            PackageOperation.BrokerCancelRequestTimeout = originalCancelRequestTimeout;
            PackageOperation.BrokerCancelConfirmTimeout = originalCancelConfirmTimeout;
            PackageOperation.BrokerOperationTimeout = originalOperationTimeout;
        }
    }

    [Fact]
    public async Task CancelingBrokeredOperationRequestsRemoteCancelAndYieldsCanceledVeredict()
    {
        var transport = new ScriptedBrokerTransport();
        transport.StatusScript.Enqueue(BrokerApiOperationStatus.Running);
        transport.StatusScript.Enqueue(BrokerApiOperationStatus.Running);
        transport.StatusAfterCancel = BrokerApiOperationStatus.Canceled;

        var veredict = await RunBrokeredOperation(
            transport,
            cancellation => transport.OnStatusQueried = () =>
            {
                if (transport.StatusQueryCount >= 2)
                    cancellation.Cancel();
            });

        Assert.Equal(OperationVeredict.Canceled, veredict);
        Assert.Equal(1, transport.CancelRequestCount);
        Assert.Contains("/v1/package-operations/cancel", transport.RequestedPaths);
    }

    [Fact]
    public async Task CanceledBrokeredOperationHonorsCompletedTerminalStatusWhenProcessWinsTheRace()
    {
        var transport = new ScriptedBrokerTransport();
        transport.StatusScript.Enqueue(BrokerApiOperationStatus.Running);
        // The remote process finishes before the broker-side cancel takes effect.
        transport.StatusAfterCancel = BrokerApiOperationStatus.Completed;
        transport.CompletedExitCode = 0;

        var veredict = await RunBrokeredOperation(
            transport,
            cancellation => transport.OnStatusQueried = () => cancellation.Cancel());

        Assert.Equal(OperationVeredict.Success, veredict);
        Assert.Equal(1, transport.CancelRequestCount);
    }

    [Fact]
    public async Task FailedBrokerCancelRequestStillYieldsCanceledVeredict()
    {
        var transport = new ScriptedBrokerTransport
        {
            FailCancelRequests = true,
        };
        transport.StatusScript.Enqueue(BrokerApiOperationStatus.Running);
        // The broker keeps reporting a non-terminal status, so the bounded
        // confirmation wait times out and the cancellation is honored anyway.
        transport.StatusAfterCancel = BrokerApiOperationStatus.Canceling;

        var veredict = await RunBrokeredOperation(
            transport,
            cancellation => transport.OnStatusQueried = () => cancellation.Cancel());

        Assert.Equal(OperationVeredict.Canceled, veredict);
        Assert.Equal(1, transport.CancelRequestCount);
    }

    [Fact]
    public async Task BrokeredOperationThatNeverReachesTerminalStatusFailsAfterTimeout()
    {
        var transport = new ScriptedBrokerTransport
        {
            // The broker keeps reporting Running forever.
            StatusAfterCancel = BrokerApiOperationStatus.Running,
        };

        var veredict = await RunBrokeredOperation(
            transport,
            operationTimeout: TimeSpan.FromMilliseconds(200));

        Assert.Equal(OperationVeredict.Failure, veredict);
        Assert.Equal(0, transport.CancelRequestCount);
    }

    [Fact]
    public async Task BrokeredOperationConsultsElevationRequirementsAndRequestsElevated()
    {
        var transport = new ScriptedBrokerTransport
        {
            StatusAfterCancel = BrokerApiOperationStatus.Completed,
            CompletedExitCode = 0,
        };

        var result = await RunBrokeredOperationWithOutput(
            transport,
            configureOperationHelper: helper => helper.ElevationRequirementsAction =
                (package, _, _) => package.OverridenOptions.RunAsAdministrator = true);

        Assert.Equal(OperationVeredict.Success, result.Veredict);
        Assert.Equal(
            BrokerApiElevation.Elevated,
            DeserializeExecuteRequest(transport).Client?.RequestedElevation);
        Assert.Contains(result.Output, line => line.Item1.Contains("Elevation: Elevated"));
    }

    [Fact]
    public async Task BrokeredOperationRequestsStandardElevationByDefault()
    {
        var transport = new ScriptedBrokerTransport
        {
            StatusAfterCancel = BrokerApiOperationStatus.Completed,
            CompletedExitCode = 0,
        };

        var result = await RunBrokeredOperationWithOutput(transport);

        Assert.Equal(OperationVeredict.Success, result.Veredict);
        Assert.Equal(
            BrokerApiElevation.Standard,
            DeserializeExecuteRequest(transport).Client?.RequestedElevation);
        Assert.Contains(result.Output, line => line.Item1.Contains("Elevation: Standard"));
    }

    [Fact]
    public async Task ProhibitElevationSettingForcesStandardBrokerElevation()
    {
        bool originalProhibitElevation = Settings.Get(Settings.K.ProhibitElevation);
        Settings.Set(Settings.K.ProhibitElevation, true);
        try
        {
            var transport = new ScriptedBrokerTransport
            {
                StatusAfterCancel = BrokerApiOperationStatus.Completed,
                CompletedExitCode = 0,
            };

            var result = await RunBrokeredOperationWithOutput(
                transport,
                configureOperationHelper: helper => helper.ElevationRequirementsAction =
                    (package, _, _) => package.OverridenOptions.RunAsAdministrator = true);

            Assert.Equal(OperationVeredict.Success, result.Veredict);
            Assert.Equal(
                BrokerApiElevation.Standard,
                DeserializeExecuteRequest(transport).Client?.RequestedElevation);
        }
        finally
        {
            Settings.Set(Settings.K.ProhibitElevation, originalProhibitElevation);
        }
    }

    private static BrokerApiPackageRequest DeserializeExecuteRequest(ScriptedBrokerTransport transport)
    {
        Assert.NotNull(transport.LastExecuteRequestBody);
        var request = BrokerJson.Deserialize<BrokerApiPackageRequest>(transport.LastExecuteRequestBody);
        Assert.NotNull(request);
        return request;
    }

    [Fact]
    public async Task StreamedEventChannelOutputReachesOperationOutputAndResultParser()
    {
        var transport = new ScriptedBrokerTransport
        {
            StatusAfterCancel = BrokerApiOperationStatus.Completed,
            CompletedExitCode = 0,
        };
        await using var server = new EventChannelPipeServer(async pipe =>
        {
            await pipe.WriteHello();
            await pipe.WriteStdout("hello from broker\n");
            await pipe.WriteStderr("an error line\n");
            // Lines split across frames must be reassembled before being emitted.
            await pipe.WriteStdout("par");
            await pipe.WriteStdout("tial\n");
            // A CR progress line superseded by new text must not be re-emitted when a
            // later bare LF arrives.
            await pipe.WriteStdout("42%\rdone\n\n");
            await pipe.WriteStdout("trailing");
            await pipe.WriteStdoutOverflow(42);
            await pipe.WriteFinish();
        });
        transport.EventChannelPipeName = server.PipeName;

        IReadOnlyList<string>? parsedOutput = null;
        var result = await RunBrokeredOperationWithOutput(
            transport,
            configureOperationHelper: helper =>
            {
                var defaultFactory = helper.ResultFactory;
                helper.ResultFactory = (package, operation, processOutput, returnCode) =>
                {
                    parsedOutput = processOutput;
                    return defaultFactory(package, operation, processOutput, returnCode);
                };
            });

        Assert.Equal(OperationVeredict.Success, result.Veredict);
        Assert.Contains(("hello from broker", LineType.Information), result.Output);
        Assert.Contains(("an error line", LineType.Error), result.Output);
        Assert.Contains(("partial", LineType.Information), result.Output);
        Assert.Contains(("done", LineType.Information), result.Output);
        // The superseded "42%" progress text was never promoted to a regular line.
        Assert.DoesNotContain(("42%", LineType.Information), result.Output);
        // The trailing partial line is flushed when the stream finishes.
        Assert.Contains(("trailing", LineType.Information), result.Output);
        Assert.Contains(result.Output, line =>
            line.Item1.Contains("42 bytes") && line.Item2 is LineType.Information);
        // FINISH triggered exactly one final status query; no polling loop ran.
        Assert.Equal(1, transport.StatusQueryCount);
        // The streamed output was fed back to the manager's result parser...
        Assert.NotNull(parsedOutput);
        Assert.Contains("hello from broker", parsedOutput);
        Assert.Contains("an error line", parsedOutput);
        Assert.Contains("partial", parsedOutput);
        // ...but internal informational lines were not: the parser sees only raw
        // process output.
        Assert.DoesNotContain(parsedOutput, line => line.Contains("42 bytes"));
        Assert.DoesNotContain(parsedOutput, line => line.Contains("Devolutions Agent broker"));
    }

    [Fact]
    public async Task StatusUpdatedFrameTriggersStatusQueryAndFinishTriggersFinalStatus()
    {
        var transport = new ScriptedBrokerTransport
        {
            StatusAfterCancel = BrokerApiOperationStatus.Completed,
            CompletedExitCode = 0,
        };
        transport.StatusScript.Enqueue(BrokerApiOperationStatus.Running);
        await using var server = new EventChannelPipeServer(async pipe =>
        {
            await pipe.WriteHello();
            await pipe.WriteStatusUpdated();
            await pipe.WriteFinish();
        });
        transport.EventChannelPipeName = server.PipeName;

        var result = await RunBrokeredOperationWithOutput(transport);

        Assert.Equal(OperationVeredict.Success, result.Veredict);
        // One query for the STATUS_UPDATED hint, one final query after FINISH.
        Assert.Equal(2, transport.StatusQueryCount);
    }

    [Fact]
    public async Task MissingEventChannelFallsBackToStatusPolling()
    {
        var transport = new ScriptedBrokerTransport
        {
            StatusAfterCancel = BrokerApiOperationStatus.Completed,
            CompletedExitCode = 0,
        };
        transport.StatusScript.Enqueue(BrokerApiOperationStatus.Running);
        transport.StatusScript.Enqueue(BrokerApiOperationStatus.Running);

        var veredict = await RunBrokeredOperation(transport);

        Assert.Equal(OperationVeredict.Success, veredict);
        // The polling loop drained the Running statuses before the terminal one.
        Assert.Equal(3, transport.StatusQueryCount);
    }

    [Fact]
    public async Task EventChannelDecodeErrorFallsBackToStatusPolling()
    {
        var transport = new ScriptedBrokerTransport
        {
            StatusAfterCancel = BrokerApiOperationStatus.Completed,
            CompletedExitCode = 0,
        };
        transport.StatusScript.Enqueue(BrokerApiOperationStatus.Running);
        await using var server = new EventChannelPipeServer(async pipe =>
        {
            // Advertise an unsupported protocol major version: a fatal decode error.
            await pipe.WriteHello(major: 2);
        });
        transport.EventChannelPipeName = server.PipeName;

        var result = await RunBrokeredOperationWithOutput(transport);

        Assert.Equal(OperationVeredict.Success, result.Veredict);
        Assert.True(transport.StatusQueryCount >= 2);
        Assert.Contains(result.Output, line => line.Item1.Contains("streaming was interrupted"));
    }

    [Fact]
    public async Task CancelDuringEventChannelStreamingYieldsCanceledVeredict()
    {
        var transport = new ScriptedBrokerTransport();
        var cancelRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.OnCancelRequested = () => cancelRequested.TrySetResult();
        await using var server = new EventChannelPipeServer(async pipe =>
        {
            await pipe.WriteHello();
            await pipe.WriteStdout("streamed-line\n");
            // Hold the channel open until the broker-side cancel request arrives, then
            // emit the output tail and finish, as a real broker would.
            await cancelRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await pipe.WriteStderr("shutting down\n");
            await pipe.WriteStatusUpdated();
            await pipe.WriteFinish();
        });
        transport.EventChannelPipeName = server.PipeName;

        CancellationTokenSource? cancellationSource = null;
        var result = await RunBrokeredOperationWithOutput(
            transport,
            configureCancellation: cancellation => cancellationSource = cancellation,
            onOperationCreated: operation => operation.LogLineAdded += (_, line) =>
            {
                if (line.Item1 == "streamed-line")
                    cancellationSource!.Cancel();
            });

        Assert.Equal(OperationVeredict.Canceled, result.Veredict);
        Assert.Equal(1, transport.CancelRequestCount);
        Assert.Contains("/v1/package-operations/cancel", transport.RequestedPaths);
        Assert.Contains(("streamed-line", LineType.Information), result.Output);
        // Output that arrived while the cancellation was being confirmed is preserved.
        Assert.Contains(("shutting down", LineType.Error), result.Output);
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

    private sealed class BrokerProbingInstallPackageOperation : InstallPackageOperation
    {
        public BrokerProbingInstallPackageOperation(IPackage package, InstallOptions options)
            : base(package, options, IgnoreParallelInstalls: true) { }

        public Task<OperationVeredict> InvokePerformOperationForTests() => PerformOperation();
    }

    private sealed class FakeBrokerTransport(bool healthy) : IBrokerTransport
    {
        public List<string> RequestedPaths { get; } = [];

        public BrokerTransportKind Kind => BrokerTransportKind.HttpNamedPipe;

        public Task<BrokerTransportResponse> Send(
            BrokerTransportRequest request,
            CancellationToken cancellationToken = default
        )
        {
            RequestedPaths.Add(request.Path);
            if (healthy && request.Path == "/v1/health")
            {
                return Task.FromResult(new BrokerTransportResponse { StatusCode = 200, Body = "{}" });
            }

            throw new BrokerClientException(
                BrokerClientErrorKind.BrokerUnavailable,
                "Simulated broker outage",
                request.Path);
        }

        public void Dispose() { }
    }

    /// <summary>
    /// A scriptable broker transport implementing the full happy-path endpoint surface
    /// (health, capabilities, execute, get-status, cancel) with responses built from the
    /// real Api types via <see cref="BrokerJson"/>. Status responses are drained from
    /// <see cref="StatusScript"/>; once a cancel request has been received (or the script
    /// is empty), <see cref="StatusAfterCancel"/> is reported instead.
    /// </summary>
    private sealed class ScriptedBrokerTransport : IBrokerTransport
    {
        private const string OperationId = "test-operation-1";

        public List<string> RequestedPaths { get; } = [];
        public Queue<BrokerApiOperationStatus> StatusScript { get; } = new();
        public BrokerApiOperationStatus StatusAfterCancel { get; set; } = BrokerApiOperationStatus.Canceled;
        public int CompletedExitCode { get; set; }
        public bool FailCancelRequests { get; set; }
        public int StatusQueryCount { get; private set; }
        public int CancelRequestCount { get; private set; }
        public Action? OnStatusQueried { get; set; }
        public Action? OnCancelRequested { get; set; }

        /// <summary>
        /// When set, the execution response advertises a LocalPipe event channel with
        /// this pipe name, and the client is expected to stream operation events from it.
        /// </summary>
        public string? EventChannelPipeName { get; set; }

        /// <summary>
        /// Body of the last /v1/package-operations/execute request, for asserting on
        /// the wire-level request (e.g. the requested elevation).
        /// </summary>
        public string? LastExecuteRequestBody { get; private set; }

        private bool cancelReceived;

        public BrokerTransportKind Kind => BrokerTransportKind.HttpNamedPipe;

        public Task<BrokerTransportResponse> Send(
            BrokerTransportRequest request,
            CancellationToken cancellationToken = default
        )
        {
            RequestedPaths.Add(request.Path);
            if (request.Path == "/v1/package-operations/execute")
            {
                LastExecuteRequestBody = request.Body;
            }

            return request.Path switch
            {
                "/v1/health" => Json(BrokerJson.Serialize(BuildHealthResponse())),
                "/v1/capabilities" => Json(BrokerJson.Serialize(BuildCapabilities())),
                "/v1/package-operations/execute" => Json(BrokerJson.Serialize(BuildExecutionResponse())),
                "/v1/package-operations/get-status" => HandleStatusQuery(),
                "/v1/package-operations/cancel" => HandleCancelRequest(),
                _ => throw new BrokerClientException(
                    BrokerClientErrorKind.InvalidRequest,
                    $"Unexpected request path: {request.Path}",
                    request.Path),
            };
        }

        public void Dispose() { }

        private static Task<BrokerTransportResponse> Json(string body) =>
            Task.FromResult(new BrokerTransportResponse { StatusCode = 200, Body = body });

        private Task<BrokerTransportResponse> HandleStatusQuery()
        {
            StatusQueryCount++;
            OnStatusQueried?.Invoke();
            BrokerApiOperationStatus status =
                cancelReceived || StatusScript.Count == 0
                    ? StatusAfterCancel
                    : StatusScript.Dequeue();

            return Json(BrokerJson.Serialize(new BrokerApiStatusResponse
            {
                ResponseKind = BrokerApiConstants.StatusResponseKind,
                ResponseVersion = BrokerApiConstants.Version,
                OperationId = OperationId,
                Status = status,
                ExitCode = status is BrokerApiOperationStatus.Completed ? CompletedExitCode : null,
            }));
        }

        private Task<BrokerTransportResponse> HandleCancelRequest()
        {
            CancelRequestCount++;
            if (FailCancelRequests)
            {
                throw new BrokerClientException(
                    BrokerClientErrorKind.BrokerError,
                    "Simulated cancel failure",
                    "/v1/package-operations/cancel");
            }

            cancelReceived = true;
            OnCancelRequested?.Invoke();
            return Json(BrokerJson.Serialize(new BrokerApiCancelResponse
            {
                ResponseKind = BrokerApiConstants.CancelResponseKind,
                ResponseVersion = BrokerApiConstants.Version,
                OperationId = OperationId,
                Status = BrokerApiOperationStatus.Canceling,
            }));
        }

        private static BrokerApiHealthResponse BuildHealthResponse() => new()
        {
            ResponseKind = BrokerApiConstants.HealthResponseKind,
            ResponseVersion = BrokerApiConstants.Version,
            Server = new BrokerApiServerContext
            {
                ServerVersion = "0.0.0-tests",
                Transport = BrokerTransportKind.HttpNamedPipe,
            },
            Status = BrokerApiHealthStatus.Ready,
        };

        private static BrokerApiCapabilitiesResponse BuildCapabilities() => new()
        {
            ResponseKind = BrokerApiConstants.CapabilitiesResponseKind,
            ResponseVersion = BrokerApiConstants.Version,
            MaxRequestBodyBytes = 1_000_000,
            Transports = [BrokerTransportKind.HttpNamedPipe],
            Managers =
            [
                new BrokerApiManagerCapability
                {
                    Manager = BrokerApiManagerName.Chocolatey,
                    Operations = [BrokerApiOperation.Install, BrokerApiOperation.Update, BrokerApiOperation.Uninstall],
                    SupportsCustomParameters = true,
                    SupportsCustomInstallLocation = true,
                    SupportsCaptureOutput = true,
                },
            ],
        };

        private BrokerApiExecutionResponse BuildExecutionResponse() => new()
        {
            ResponseKind = BrokerApiConstants.ExecutionResponseKind,
            ResponseVersion = BrokerApiConstants.Version,
            Decision = new BrokerApiDecisionInfo { Decision = BrokerApiDecision.Allow },
            Operation = new BrokerApiOperationSubmission
            {
                OperationId = OperationId,
                Status = BrokerApiOperationStatus.Starting,
                SubmittedAt = DateTimeOffset.UtcNow,
                EventChannel = EventChannelPipeName is null
                    ? null
                    : new BrokerApiEventChannel
                    {
                        Kind = BrokerApiEventChannelKind.LocalPipe,
                        Path = EventChannelPipeName,
                    },
            },
        };
    }

    /// <summary>
    /// Test-side server for the NOW_BROKER per-operation event channel: hosts a named
    /// pipe that <c>BrokerClient.OpenEventChannel</c> connects to, and writes protocol
    /// frames scripted by the test. Frame layout (little-endian):
    /// <c>u32 body_size (excludes 6-byte header) | u16 kind | body</c>.
    /// </summary>
    private sealed class EventChannelPipeServer : IAsyncDisposable
    {
        private const ushort HelloKind = 0x0000;
        private const ushort StatusUpdatedKind = 0x0001;
        private const ushort FinishKind = 0x0002;
        private const ushort StdoutKind = 0x0003;
        private const ushort StderrKind = 0x0004;
        private const ushort StdoutOverflowKind = 0x0005;

        public string PipeName { get; } = $"unigetui-test-events-{Guid.NewGuid():N}";

        private readonly NamedPipeServerStream _pipe;
        private readonly Task _serverTask;

        public EventChannelPipeServer(Func<EventChannelPipeServer, Task> script)
        {
            _pipe = new NamedPipeServerStream(
                PipeName,
                PipeDirection.Out,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
            _serverTask = Run(script);
        }

        private async Task Run(Func<EventChannelPipeServer, Task> script)
        {
            await _pipe.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(10));
            try
            {
                await script(this);
            }
            catch (IOException)
            {
                // The client may dispose the channel early (e.g. after a decode error).
            }
        }

        public Task WriteHello(ushort major = 1, ushort minor = 0)
        {
            byte[] body = new byte[4];
            BinaryPrimitives.WriteUInt16LittleEndian(body, major);
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(2), minor);
            return WriteFrame(HelloKind, body);
        }

        public Task WriteStatusUpdated() => WriteFrame(StatusUpdatedKind, []);

        public Task WriteFinish() => WriteFrame(FinishKind, []);

        public Task WriteStdout(string text) => WriteFrame(StdoutKind, Encoding.UTF8.GetBytes(text));

        public Task WriteStderr(string text) => WriteFrame(StderrKind, Encoding.UTF8.GetBytes(text));

        public Task WriteStdoutOverflow(uint bytesSkipped)
        {
            byte[] body = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(body, bytesSkipped);
            return WriteFrame(StdoutOverflowKind, body);
        }

        private async Task WriteFrame(ushort kind, byte[] body)
        {
            byte[] header = new byte[6];
            BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)body.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), kind);
            await _pipe.WriteAsync(header);
            await _pipe.WriteAsync(body);
            await _pipe.FlushAsync();
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _serverTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (TimeoutException)
            {
                // Dispose below unblocks a stuck script.
            }

            await _pipe.DisposeAsync();
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
