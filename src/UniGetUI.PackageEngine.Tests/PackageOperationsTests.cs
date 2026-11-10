using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Enums;
using UniGetUI.PackageEngine.AgentBroker;
using UniGetUI.PackageEngine.Classes.Manager;
using UniGetUI.PackageEngine.Classes.Manager.BaseProviders;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Interfaces.ManagerProviders;
using UniGetUI.PackageEngine.Managers.BunManager;
using UniGetUI.PackageEngine.Managers.NpmManager;
using UniGetUI.PackageEngine.Managers.PipManager;
using UniGetUI.PackageEngine.Operations;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.PackageLoader;
using UniGetUI.PackageEngine.Serializable;
using UniGetUI.PackageEngine.Structs;
using UniGetUI.PackageEngine.Tests.Infrastructure;
using UniGetUI.PackageEngine.Tests.Infrastructure.Builders;
using UniGetUI.PackageEngine.Tests.Infrastructure.Fakes;
using UniGetUI.PackageOperations;
using BrokerApiArchitecture = Devolutions.Now.Policy.Api.Architecture;
using BrokerApiCancelResponse = Devolutions.Now.Policy.Api.CancelResponse;
using BrokerApiCapabilitiesResponse = Devolutions.Now.Policy.Api.CapabilitiesResponse;
using BrokerApiConstants = Devolutions.Now.Policy.Api.BrokerApi;
using BrokerApiDecision = Devolutions.Now.Policy.Api.Decision;
using BrokerApiDecisionInfo = Devolutions.Now.Policy.Api.DecisionInfo;
using BrokerApiElevation = Devolutions.Now.Policy.Api.Elevation;
using BrokerApiErrorCode = Devolutions.Now.Policy.Api.ErrorCode;
using BrokerApiErrorResponse = Devolutions.Now.Policy.Api.ErrorResponse;
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
using BrokerSerializer = Devolutions.Now.Policy.Api.BrokerSerializer;
using BrokerTransportKind = Devolutions.Now.Policy.Api.Transport;
using BrokerTransportRequest = Devolutions.Now.Policy.Client.BrokerTransportRequest;
using BrokerTransportResponse = Devolutions.Now.Policy.Client.BrokerTransportResponse;
using IBrokerTransport = Devolutions.Now.Policy.Client.IBrokerTransport;
using LineType = UniGetUI.PackageOperations.AbstractOperation.LineType;

namespace UniGetUI.PackageEngine.Tests;

[CollectionDefinition(nameof(OperationOrchestrationTestCollection), DisableParallelization = true)]
public sealed class OperationOrchestrationTestCollection
    : ICollectionFixture<IsolatedUserConfigurationFixture>;

/// <summary>
/// Redirects the settings store to a throwaway directory for every test in the collection.
/// These tests toggle real setting keys (UseAgentBroker, ProhibitElevation) and restore them in a
/// finally block, which never runs when a test host is killed — without this the developer's own
/// configuration keeps the toggled value.
/// </summary>
public sealed class IsolatedUserConfigurationFixture : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        nameof(IsolatedUserConfigurationFixture),
        Guid.NewGuid().ToString("N")
    );

    public IsolatedUserConfigurationFixture()
    {
        Directory.CreateDirectory(_root);
        CoreData.TEST_DataDirectoryOverride = Path.Combine(_root, "Data");
        Directory.CreateDirectory(CoreData.UniGetUIUserConfigurationDirectory);
        Settings.ResetSettings();
    }

    public void Dispose()
    {
        Settings.ResetSettings();
        CoreData.TEST_DataDirectoryOverride = null;
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}

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
                Assert.True(inner.MustSucceed);
            },
            inner =>
            {
                Assert.IsType<KillProcessOperation>(inner.Operation);
                Assert.True(inner.MustSucceed);
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

    [Theory]
    [InlineData(true, true, "install Contoso.Tool 3.0.0")]
    [InlineData(false, true, "update Contoso.Tool")]
    [InlineData(true, false, "update Contoso.Tool")]
    public void UpdateOperationUninstallsThenInstallsTheNewVersionWhenAsked(
        bool uninstallBeforeUpdate,
        bool canUninstall,
        string expectedArguments
    )
    {
        var manager = new PackageManagerBuilder()
            .ConfigureCapabilities(capabilities =>
            {
                capabilities.CanUninstall = canUninstall;
                return capabilities;
            })
            .ConfigureManager(manager => manager.ExecutablePath = "C:\\tools\\pkgmgr.exe")
            .ConfigureOperation(helper =>
                helper.ParametersFactory = (package, options, operation) =>
                    new[] { operation.ToString().ToLowerInvariant(), package.Id, options.Version }
                        .Where(part => part.Length > 0)
                        .ToList())
            .Build();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Tool")
            .WithVersion("1.0.0")
            .WithNewVersion("3.0.0")
            .Build();
        var options = new InstallOptions { UninstallBeforeUpdate = uninstallBeforeUpdate };

        using var operation = new InspectableUpdatePackageOperation(package, options);
        var startInfo = operation.PrepareProcessStartInfoForTests();
        var uninstalls = GetInnerOperations(operation, "PreOperations")
            .Concat(operation.AttemptPreOperationsForTests())
            .Where(inner => inner.Operation is UninstallPackageOperation)
            .ToList();

        Assert.Equal(expectedArguments, startInfo.Arguments.Trim());
        Assert.Equal("", options.Version);
        if (uninstallBeforeUpdate && canUninstall)
        {
            var inner = Assert.Single(uninstalls);
            Assert.True(inner.MustSucceed);
            var uninstall = Assert.IsType<UninstallPackageOperation>(inner.Operation);
            Assert.Equal("1.0.0", uninstall.Package.VersionString);
        }
        else
        {
            Assert.Empty(uninstalls);
        }
    }

    [Fact]
    public void UpdateOperationDoesNotUninstallAgainWhenRetriedAfterTheUninstallSucceeded()
    {
        var manager = CreateManager();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Tool")
            .WithVersion("1.0.0")
            .WithNewVersion("3.0.0")
            .Build();

        using var operation = new InspectableUpdatePackageOperation(
            package,
            new InstallOptions { UninstallBeforeUpdate = true }
        );
        var uninstall = Assert.Single(operation.AttemptPreOperationsForTests()).Operation;

        uninstall.Status = OperationStatus.Failed;
        Assert.Single(operation.AttemptPreOperationsForTests());

        uninstall.Status = OperationStatus.Succeeded;
        Assert.Empty(operation.AttemptPreOperationsForTests());
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    public void UpdateOperationReinstallsWhenTheUpdateRequiresItOrTheOptionIsOn(
        bool requiresReinstall,
        bool uninstallBeforeUpdate,
        bool expectsUninstallFirst
    )
    {
        var builder = new PackageBuilder()
            .WithManager(CreateManager())
            .WithId("Contoso.Tool")
            .WithVersion("1.0.0")
            .WithNewVersion("2.0.0");
        if (requiresReinstall)
            builder.RequiringReinstallToUpdate();

        using var operation = new InspectableUpdatePackageOperation(
            builder.Build(),
            new InstallOptions { UninstallBeforeUpdate = uninstallBeforeUpdate }
        );

        var preOperations = operation.AttemptPreOperationsForTests();
        if (expectsUninstallFirst)
            Assert.IsType<UninstallPackageOperation>(Assert.Single(preOperations).Operation);
        else
            Assert.Empty(preOperations);
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    public void BulkUpdatesLeaveOutUpdatesThatNeedAReinstallUnlessTheOptionIsOn(
        bool requiresReinstall,
        bool uninstallBeforeUpdate,
        bool expected
    )
    {
        var builder = new PackageBuilder()
            .WithManager(CreateManager())
            .WithVersion("1.0.0")
            .WithNewVersion("2.0.0");
        if (requiresReinstall)
            builder.RequiringReinstallToUpdate();

        Assert.Equal(
            expected,
            PackageOperation.IsLeftOutOfBulkUpdates(
                builder.Build(),
                new InstallOptions { UninstallBeforeUpdate = uninstallBeforeUpdate }
            )
        );
    }

    [Fact]
    public async Task BulkUpdateNotificationsOnlyNameTheUpdatesThatRun()
    {
        var manager = CreateManager();
        var regular = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Regular")
            .WithVersion("1.0.0")
            .WithNewVersion("2.0.0")
            .Build();
        var reinstall = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Reinstall")
            .WithVersion("1.0.0")
            .WithNewVersion("2.0.0")
            .RequiringReinstallToUpdate()
            .Build();

        var kept = await PackageOperation.WithoutUpdatesLeftOutOfBulkAsync([regular, reinstall]);

        Assert.Same(regular, Assert.Single(kept));
    }

    [Fact]
    public async Task UpdatesThatNeedAReinstallAreNotCheckedByDefault()
    {
        InitializeLoaders();
        var manager = CreateManager();
        var regular = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Regular")
            .WithVersion("1.0.0")
            .WithNewVersion("2.0.0")
            .Build();
        var reinstall = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Reinstall")
            .WithVersion("1.0.0")
            .WithNewVersion("2.0.0")
            .RequiringReinstallToUpdate()
            .Build();

        await UpgradablePackagesLoader.Instance.AddForeign(regular);
        await UpgradablePackagesLoader.Instance.AddForeign(reinstall);

        Assert.True(regular.IsChecked);
        Assert.False(reinstall.IsChecked);
    }

    [Theory]
    [InlineData(false, false, "install Contoso.Tool 3.0.0")]
    [InlineData(true, false, "install Contoso.Tool 3.0.0")]
    [InlineData(false, true, "install Contoso.Tool 3.0.0")]
    [InlineData(true, true, "install Contoso.Tool 2.0.0")]
    public void ReinstallKeepsOnlyAnExplicitVersionAndOnlyWhenTheManagerHonoursItOnUpdates(
        bool supportsCustomVersionsOnUpdate,
        bool versionIsExplicit,
        string expectedArguments
    )
    {
        var manager = new PackageManagerBuilder()
            .ConfigureCapabilities(capabilities =>
            {
                capabilities.SupportsCustomVersionsOnUpdate = supportsCustomVersionsOnUpdate;
                return capabilities;
            })
            .ConfigureManager(manager => manager.ExecutablePath = "C:\\tools\\pkgmgr.exe")
            .ConfigureOperation(helper =>
                helper.ParametersFactory = (package, options, operation) =>
                    new[] { operation.ToString().ToLowerInvariant(), package.Id, options.Version }
                        .Where(part => part.Length > 0)
                        .ToList())
            .Build();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Tool")
            .WithVersion("1.0.0")
            .WithNewVersion("3.0.0")
            .Build();

        using var operation = new InspectableUpdatePackageOperation(
            package,
            new InstallOptions
            {
                UninstallBeforeUpdate = true,
                Version = "2.0.0",
                VersionIsExplicit = versionIsExplicit,
            }
        );

        Assert.Equal(expectedArguments, operation.PrepareProcessStartInfoForTests().Arguments.Trim());
    }

    [Theory]
    [InlineData(OperationType.Update, false, "update Contoso.Tool")]
    [InlineData(OperationType.Update, true, "update Contoso.Tool 2.0.0")]
    [InlineData(OperationType.Install, false, "install Contoso.Tool 2.0.0")]
    public void ASavedVersionIsUsedForInstallsButNotForUpdates(
        OperationType role,
        bool versionIsExplicit,
        string expectedArguments
    )
    {
        var manager = new PackageManagerBuilder()
            .ConfigureManager(manager => manager.ExecutablePath = "C:\\tools\\pkgmgr.exe")
            .ConfigureOperation(helper =>
                helper.ParametersFactory = (package, options, operation) =>
                    new[] { operation.ToString().ToLowerInvariant(), package.Id, options.Version }
                        .Where(part => part.Length > 0)
                        .ToList())
            .Build();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Tool")
            .WithVersion("1.0.0")
            .WithNewVersion("3.0.0")
            .Build();
        var options = new InstallOptions { Version = "2.0.0", VersionIsExplicit = versionIsExplicit };

        string arguments;
        if (role is OperationType.Install)
        {
            using var install = new InspectableInstallPackageOperation(package, options);
            arguments = install.PrepareProcessStartInfoForTests().Arguments.Trim();
        }
        else
        {
            using var update = new InspectableUpdatePackageOperation(package, options);
            arguments = update.PrepareProcessStartInfoForTests().Arguments.Trim();
        }

        Assert.Equal(expectedArguments, arguments);
        Assert.Equal("2.0.0", options.Version);
    }

    [Fact]
    public void AFailureExplanationDoesNotOutliveTheAttemptThatProducedIt()
    {
        var manager = new PackageManagerBuilder()
            .ConfigureOperation(helper =>
                helper.FailureExplanationFactory = (_, _, output, _) =>
                    output.Contains("sources offer nothing newer") ? "Nothing newer is available" : null)
            .Build();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Tool")
            .WithVersion("1.0.0")
            .WithNewVersion("3.0.0")
            .Build();
        using var operation = new InspectableUpdatePackageOperation(package, new InstallOptions());
        string genericMessage = operation.Metadata.FailureMessage;

        operation.GetProcessVeredictForTests(1, ["sources offer nothing newer"]);
        Assert.Equal("Nothing newer is available", operation.Metadata.FailureMessage);

        operation.GetProcessVeredictForTests(1, ["network error"]);
        Assert.Equal(genericMessage, operation.Metadata.FailureMessage);
    }

    [Fact]
    public async Task AnAutomaticRetryIsJudgedOnlyOnItsOwnOutput()
    {
        if (!OperatingSystem.IsWindows())
            return;

        int attempts = 0;
        var outputsSeen = new List<IReadOnlyList<string>>();
        var manager = new PackageManagerBuilder()
            .WithName($"RetryOutputManager{Guid.NewGuid():N}")
            .ConfigureManager(manager =>
            {
                manager.ExecutablePath = Path.Join(Environment.SystemDirectory, "cmd.exe");
                manager.ExecutableArguments = "/c";
            })
            .ConfigureOperation(helper =>
            {
                helper.ParametersFactory = (_, _, _) => ["echo", $"attempt-{++attempts}"];
                helper.ResultFactory = (_, _, output, _) =>
                {
                    outputsSeen.Add(output.ToList());
                    return outputsSeen.Count == 1 ? OperationVeredict.AutoRetry : OperationVeredict.Success;
                };
            })
            .Build();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Tool")
            .WithVersion("1.0.0")
            .WithNewVersion("3.0.0")
            .Build();
        InitializeLoaders();
        using var operation = new UpdatePackageOperation(package, new InstallOptions(), IgnoreParallelInstalls: true);

        await operation.MainThread().WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(OperationStatus.Succeeded, operation.Status);
        Assert.Equal(2, outputsSeen.Count);
        Assert.Contains(outputsSeen[1], line => line.Contains("attempt-2"));
        Assert.DoesNotContain(outputsSeen[1], line => line.Contains("attempt-1"));
    }

    [Fact]
    public void ReinstallUninstallKeepsDataAndTheUpdateListing()
    {
        var manager = CreateManager();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Tool")
            .WithVersion("1.0.0")
            .WithNewVersion("3.0.0")
            .Build();
        var options = new InstallOptions
        {
            UninstallBeforeUpdate = true,
            RemoveDataOnUninstall = true,
            KillBeforeOperation = ["contoso.exe"],
        };

        using var operation = new InspectableUpdatePackageOperation(package, options);
        var uninstall = Assert.IsType<UninstallPackageOperation>(
            Assert.Single(operation.AttemptPreOperationsForTests()).Operation
        );

        Assert.False(uninstall.Options.RemoveDataOnUninstall);
        Assert.Empty(uninstall.Options.KillBeforeOperation);
        Assert.True(uninstall.IsReinstallStep);
        Assert.True(options.RemoveDataOnUninstall);
        Assert.Equal(["contoso.exe"], options.KillBeforeOperation);
    }

    [Fact]
    public void ReinstallDoesNotUninstallLocallyWhenTheBrokerIsEnabledAfterQueueing()
    {
        bool originalSetting = Settings.Get(Settings.K.UseAgentBroker);
        var manager = new PackageManagerBuilder().WithName("Chocolatey").Build();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Tool")
            .WithVersion("1.0.0")
            .WithNewVersion("3.0.0")
            .Build();
        try
        {
            Settings.Set(Settings.K.UseAgentBroker, false);
            using var operation = new InspectableUpdatePackageOperation(
                package,
                new InstallOptions { UninstallBeforeUpdate = true }
            );
            Assert.Single(operation.AttemptPreOperationsForTests());

            Settings.Set(Settings.K.UseAgentBroker, true);

            Assert.Empty(operation.AttemptPreOperationsForTests());
            Assert.False(PackageOperation.SupportsUninstallBeforeUpdate(package));
        }
        finally
        {
            Settings.Set(Settings.K.UseAgentBroker, originalSetting);
        }
    }

    [Fact]
    public async Task ReinstallDoesNotQueueTheAlreadyUninstalledVersionAsAPreviousVersion()
    {
        var manager = CreateManager();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Tool")
            .WithVersion("1.0.0")
            .WithNewVersion("3.0.0")
            .Build();
        var otherInstalledVersion = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Tool")
            .WithVersion("2.0.0")
            .Build();
        InitializeLoaders();
        await InstalledPackagesLoader.Instance.AddForeign(
            new PackageBuilder()
                .WithManager(manager)
                .WithId("Contoso.Tool")
                .WithVersion("1.0.0")
                .Build()
        );
        await InstalledPackagesLoader.Instance.AddForeign(otherInstalledVersion);

        using var operation = new UpdatePackageOperation(
            package,
            new InstallOptions
            {
                UninstallBeforeUpdate = true,
                UninstallPreviousVersionsOnUpdate = true,
            }
        );

        var inner = Assert.Single(GetInnerOperations(operation, "PostOperations"));
        var uninstall = Assert.IsType<UninstallPackageOperation>(inner.Operation);
        Assert.Equal("2.0.0", uninstall.Package.VersionString);
    }

    private static TestPackageManager CreateRecordingProcessManager(
        List<string> executed,
        Func<OperationType, bool> succeeds
    ) =>
        new PackageManagerBuilder()
            .WithName($"RecordingManager{Guid.NewGuid():N}")
            .ConfigureManager(manager =>
            {
                manager.ExecutablePath = Path.Join(Environment.SystemDirectory, "cmd.exe");
                manager.ExecutableArguments = "/c";
            })
            .ConfigureOperation(helper =>
            {
                helper.ParametersFactory = (package, options, operation) =>
                    ["echo", $"RAN-{operation}-{package.Id}-{options.Version}"];
                helper.ResultFactory = (_, operation, output, _) =>
                {
                    lock (executed)
                        executed.Add(
                            $"{operation}|"
                                + (
                                    output.LastOrDefault(line => line.StartsWith("RAN-", StringComparison.Ordinal))?.Trim()
                                    ?? "NO-OUTPUT"
                                )
                        );
                    return succeeds(operation) ? OperationVeredict.Success : OperationVeredict.Failure;
                };
            })
            .Build();

    [Fact]
    public async Task ReinstallingUpdateRunsTheUninstallThenTheInstallOfTheNewVersion()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var executed = new List<string>();
        var manager = CreateRecordingProcessManager(executed, _ => true);
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Tool")
            .WithVersion("1.0.0")
            .WithNewVersion("3.0.0")
            .Build();
        InitializeLoaders();
        await UpgradablePackagesLoader.Instance.AddForeign(package);
        using var operation = new UpdatePackageOperation(
            package,
            new InstallOptions { UninstallBeforeUpdate = true },
            IgnoreParallelInstalls: true
        );

        await operation.MainThread().WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(OperationStatus.Succeeded, operation.Status);
        Assert.Equal(["Uninstall|RAN-Uninstall-Contoso.Tool-", "Install|RAN-Install-Contoso.Tool-3.0.0"], executed);
        Assert.False(UpgradablePackagesLoader.Instance.Contains(package));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AMajorUpdateIsRefusedBeforeAnythingRunsWhenMajorUpdatesAreSkipped(bool reinstalls)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var executed = new List<string>();
        var manager = CreateRecordingProcessManager(executed, _ => true);
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Licensed")
            .WithVersion("1.0.0")
            .WithNewVersion("3.0.0")
            .Build();
        InitializeLoaders();
        await UpgradablePackagesLoader.Instance.AddForeign(package);
        using var operation = new UpdatePackageOperation(
            package,
            new InstallOptions
            {
                SkipMajorUpdates = true,
                UninstallBeforeUpdate = reinstalls,
                PreUpdateCommand = "echo RAN-PreUpdate",
            },
            IgnoreParallelInstalls: true
        );

        await operation.MainThread().WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(OperationStatus.Failed, operation.Status);
        Assert.Empty(executed);
        Assert.DoesNotContain(operation.GetOutput(), line => line.Item1.Trim() == "RAN-PreUpdate");
        Assert.DoesNotContain(operation.GetOutput(), line => line.Item1.StartsWith("Running PreOperation", StringComparison.Ordinal));
        Assert.True(UpgradablePackagesLoader.Instance.Contains(package));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("1.5.0", true)]
    public async Task AnUpdateWithNoKnownTargetIsRefusedWhileMajorUpdatesAreSkipped(string requestedVersion, bool runs)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var executed = new List<string>();
        var manager = CreateRecordingProcessManager(executed, _ => true);
        var installedOnly = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Licensed")
            .WithVersion("1.0.0")
            .Build();
        InitializeLoaders();
        var options = new InstallOptions { SkipMajorUpdates = true };
        if (requestedVersion.Length > 0)
        {
            options.Version = requestedVersion;
            options.VersionIsExplicit = true;
        }
        using var operation = new UpdatePackageOperation(installedOnly, options, IgnoreParallelInstalls: true);

        await operation.MainThread().WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(runs ? OperationStatus.Succeeded : OperationStatus.Failed, operation.Status);
        Assert.Equal(runs, executed.Count > 0);
    }

    [Fact]
    public async Task ARequestedVersionTheManagerCannotTargetDoesNotLetAMajorUpdateThrough()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var executed = new List<string>();
        var manager = new PackageManagerBuilder()
            .WithName($"RecordingManager{Guid.NewGuid():N}")
            .ConfigureCapabilities(capabilities =>
            {
                capabilities.SupportsCustomVersionsOnUpdate = false;
                return capabilities;
            })
            .ConfigureManager(fake =>
            {
                fake.ExecutablePath = Path.Join(Environment.SystemDirectory, "cmd.exe");
                fake.ExecutableArguments = "/c";
            })
            .ConfigureOperation(helper =>
            {
                helper.ParametersFactory = (package, options, operation) => ["echo", $"RAN-{operation}"];
                helper.ResultFactory = (_, operation, _, _) =>
                {
                    lock (executed)
                        executed.Add(operation.ToString());
                    return OperationVeredict.Success;
                };
            })
            .Build();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Licensed")
            .WithVersion("1.0.0")
            .WithNewVersion("3.0.0")
            .Build();
        InitializeLoaders();
        using var operation = new UpdatePackageOperation(
            package,
            new InstallOptions { SkipMajorUpdates = true, Version = "1.5.0", VersionIsExplicit = true },
            IgnoreParallelInstalls: true
        );

        await operation.MainThread().WaitAsync(TimeSpan.FromSeconds(60));

        Assert.False(manager.CanUpdateToExactVersion);
        Assert.Equal(OperationStatus.Failed, operation.Status);
        Assert.Empty(executed);
    }

    [Fact]
    public async Task APinnedUpdateIsRefusedWhenANewerVersionIsAlreadyInstalled()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var executed = new List<string>();
        var manager = CreateRecordingProcessManager(executed, _ => true);
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Licensed")
            .WithVersion("1.0.0")
            .WithNewVersion("1.5.0")
            .Build();
        package.OverridenOptions.PinUpdateToNewVersion = true;
        InitializeLoaders();
        await InstalledPackagesLoader.Instance.AddForeign(
            new PackageBuilder().WithManager(manager).WithId("Contoso.Licensed").WithVersion("2.0.0").Build()
        );
        using var operation = new UpdatePackageOperation(
            package,
            new InstallOptions { SkipMajorUpdates = true },
            IgnoreParallelInstalls: true
        );

        await operation.MainThread().WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(OperationStatus.Failed, operation.Status);
        Assert.Empty(executed);
    }

    [Theory]
    [InlineData("1.5.0", true, false, "1.5.0")]
    [InlineData("3.0.0", false, true, "3.0.0")]
    [InlineData("3.0.0", false, false, "")]
    public async Task UpdatesThatSkipMajorUpdatesAllowStillRun(
        string newVersion,
        bool pinned,
        bool explicitVersion,
        string expectedVersion
    )
    {
        if (!OperatingSystem.IsWindows())
            return;

        var executed = new List<string>();
        var manager = CreateRecordingProcessManager(executed, _ => true);
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Licensed")
            .WithVersion("1.0.0")
            .WithNewVersion(newVersion)
            .Build();
        package.OverridenOptions.PinUpdateToNewVersion = pinned;
        InitializeLoaders();
        await UpgradablePackagesLoader.Instance.AddForeign(package);
        var options = explicitVersion
            ? new InstallOptions { SkipMajorUpdates = true, Version = "3.0.0", VersionIsExplicit = true }
            : new InstallOptions { SkipMajorUpdates = pinned };
        using var operation = new UpdatePackageOperation(package, options, IgnoreParallelInstalls: true);

        await operation.MainThread().WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(OperationStatus.Succeeded, operation.Status);
        Assert.Equal([$"Update|RAN-Update-Contoso.Licensed-{expectedVersion}"], executed);
    }

    [Fact]
    public async Task RetryingAReinstallAfterAFailedInstallOnlyRunsTheInstall()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var executed = new List<string>();
        bool installSucceeds = false;
        var manager = CreateRecordingProcessManager(
            executed,
            operation => operation is not OperationType.Install || installSucceeds
        );
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Tool")
            .WithVersion("1.0.0")
            .WithNewVersion("3.0.0")
            .Build();
        InitializeLoaders();
        await UpgradablePackagesLoader.Instance.AddForeign(package);
        using var operation = new UpdatePackageOperation(
            package,
            new InstallOptions { UninstallBeforeUpdate = true, RemoveDataOnUninstall = true },
            IgnoreParallelInstalls: true
        );
        await InstalledPackagesLoader.Instance.AddForeign(
            new PackageBuilder()
                .WithManager(manager)
                .WithId("Contoso.Tool")
                .WithVersion("1.0.0")
                .Build()
        );
        Assert.True(InstalledPackagesLoader.Instance.Contains(package));

        await operation.MainThread().WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(OperationStatus.Failed, operation.Status);
        Assert.Equal(["Uninstall|RAN-Uninstall-Contoso.Tool-", "Install|RAN-Install-Contoso.Tool-3.0.0"], executed);
        Assert.True(UpgradablePackagesLoader.Instance.Contains(package));
        Assert.False(InstalledPackagesLoader.Instance.Contains(package));

        installSucceeds = true;
        operation.Retry(AbstractOperation.RetryMode.Retry);
        await WaitForConditionAsync(() => operation.Status is OperationStatus.Succeeded);

        Assert.Equal(
            [
                "Uninstall|RAN-Uninstall-Contoso.Tool-",
                "Install|RAN-Install-Contoso.Tool-3.0.0",
                "Install|RAN-Install-Contoso.Tool-3.0.0",
            ],
            executed
        );
    }

    [Fact]
    public async Task ReinstallDoesNotCarryTheUninstallRetryStateIntoTheInstall()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var installSawLocalIdentifier = new List<bool>();
        var manager = new PackageManagerBuilder()
            .WithName($"LocalIdManager{Guid.NewGuid():N}")
            .ConfigureManager(manager =>
            {
                manager.ExecutablePath = Path.Join(Environment.SystemDirectory, "cmd.exe");
                manager.ExecutableArguments = "/c";
            })
            .ConfigureOperation(helper =>
            {
                helper.ParametersFactory = (package, _, operation) =>
                {
                    if (operation is OperationType.Install)
                        installSawLocalIdentifier.Add(package.OverridenOptions.WinGet_UseLocalIdentifier);
                    return ["echo", operation.ToString()];
                };
                helper.ResultFactory = (package, operation, _, _) =>
                {
                    if (operation is not OperationType.Uninstall || package.OverridenOptions.WinGet_UseLocalIdentifier)
                        return OperationVeredict.Success;
                    package.OverridenOptions.WinGet_UseLocalIdentifier = true;
                    return OperationVeredict.AutoRetry;
                };
            })
            .Build();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Tool")
            .WithVersion("1.0.0")
            .WithNewVersion("3.0.0")
            .Build();
        InitializeLoaders();
        using var operation = new UpdatePackageOperation(
            package,
            new InstallOptions { UninstallBeforeUpdate = true },
            IgnoreParallelInstalls: true
        );

        await operation.MainThread().WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(OperationStatus.Succeeded, operation.Status);
        Assert.Equal([false], installSawLocalIdentifier);
    }

    [Fact]
    public async Task ReinstallUninstallRefusesToRunThroughTheBrokerEnabledAfterQueueing()
    {
        bool originalSetting = Settings.Get(Settings.K.UseAgentBroker);
        var originalFactory = PackageOperation.BrokerTransportFactory;
        bool brokerContacted = false;
        var manager = new PackageManagerBuilder().WithName("Chocolatey").Build();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Tool")
            .WithVersion("1.0.0")
            .WithNewVersion("3.0.0")
            .Build();
        try
        {
            Settings.Set(Settings.K.UseAgentBroker, false);
            using var operation = new InspectableUpdatePackageOperation(
                package,
                new InstallOptions { UninstallBeforeUpdate = true }
            );
            var uninstall = Assert.Single(operation.AttemptPreOperationsForTests()).Operation;
            PackageOperation.BrokerTransportFactory = () =>
            {
                brokerContacted = true;
                throw new InvalidOperationException("The broker must not be contacted");
            };
            Settings.Set(Settings.K.UseAgentBroker, true);

            await uninstall.MainThread().WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(OperationStatus.Failed, uninstall.Status);
            Assert.False(brokerContacted);
        }
        finally
        {
            PackageOperation.BrokerTransportFactory = originalFactory;
            Settings.Set(Settings.K.UseAgentBroker, originalSetting);
        }
    }

    [Fact]
    public void ReinstallSendsAnInstallOnceTheUninstallHasRunEvenIfTheBrokerIsEnabledLater()
    {
        bool originalSetting = Settings.Get(Settings.K.UseAgentBroker);
        var manager = new PackageManagerBuilder()
            .WithName("Chocolatey")
            .ConfigureManager(manager => manager.ExecutablePath = "C:\\tools\\choco.exe")
            .ConfigureOperation(helper =>
                helper.ParametersFactory = (_, _, operation) => [operation.ToString().ToLowerInvariant()])
            .Build();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Tool")
            .WithVersion("1.0.0")
            .WithNewVersion("3.0.0")
            .Build();
        try
        {
            Settings.Set(Settings.K.UseAgentBroker, false);
            using var operation = new InspectableUpdatePackageOperation(
                package,
                new InstallOptions { UninstallBeforeUpdate = true }
            );
            var uninstall = Assert.Single(operation.AttemptPreOperationsForTests()).Operation;
            uninstall.Status = OperationStatus.Succeeded;
            Settings.Set(Settings.K.UseAgentBroker, true);

            Assert.Empty(operation.AttemptPreOperationsForTests());
            Assert.Equal("install", operation.PrepareProcessStartInfoForTests().Arguments.Trim());
        }
        finally
        {
            Settings.Set(Settings.K.UseAgentBroker, originalSetting);
        }
    }

    [Fact]
    public void ReinstallIsUnavailableWhenTheManagerDoesNotOfferIt()
    {
        var manager = new PackageManagerBuilder()
            .ConfigureCapabilities(capabilities =>
            {
                capabilities.CanReinstallOnUpdate = false;
                return capabilities;
            })
            .Build();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Tool")
            .WithVersion("1.0.0")
            .WithNewVersion("3.0.0")
            .Build();

        using var operation = new InspectableUpdatePackageOperation(
            package,
            new InstallOptions { UninstallBeforeUpdate = true }
        );

        Assert.False(PackageOperation.SupportsUninstallBeforeUpdate(package));
        Assert.Empty(operation.AttemptPreOperationsForTests());
    }

    [Fact]
    public void ReinstallOnNpmAndBunInstallsTheUpdateTargetRatherThanASavedVersion()
    {
        foreach (IPackageManager manager in new IPackageManager[] { new Npm(), new Bun() })
        {
            Assert.False(manager.Capabilities.SupportsCustomVersionsOnUpdate);
            var package = new PackageBuilder()
                .WithManager(manager)
                .WithId("contoso-tool")
                .WithVersion("1.0.0")
                .WithNewVersion("3.0.0")
                .WithOptions(new OverridenInstallationOptions(PackageScope.Global))
                .Build();

            using var operation = new UpdatePackageOperation(
                package,
                new InstallOptions { UninstallBeforeUpdate = true, Version = "1.0.0" }
            );
            var parameters = string.Join(
                " ",
                manager.OperationHelper.GetParameters(package, operation.Options, OperationType.Install)
            );

            Assert.Contains("contoso-tool@3.0.0", parameters);
            Assert.DoesNotContain("@1.0.0", parameters);
        }
    }

    [Fact]
    public void AFailedReinstallUninstallBecauseTheAppIsRunningOffersTheCloseAppRetryOnTheUpdate()
    {
        var manager = CreateManager();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Tool")
            .WithVersion("1.0.0")
            .WithNewVersion("3.0.0")
            .Build();
        using var operation = new InspectableUpdatePackageOperation(
            package,
            new InstallOptions { UninstallBeforeUpdate = true }
        );
        var uninstall = Assert.IsType<UninstallPackageOperation>(
            Assert.Single(operation.AttemptPreOperationsForTests()).Operation
        );
        typeof(PackageOperation)
            .GetProperty(nameof(PackageOperation.FailedBecauseApplicationRunning))!
            .SetValue(uninstall, true);
        uninstall.Metadata.FailureMessage = "Contoso Tool is currently running";

        Assert.False(operation.FailedBecauseApplicationRunning);
        operation.AdoptReinstallStepFailure();

        Assert.True(operation.FailedBecauseApplicationRunning);
        Assert.Equal("Contoso Tool is currently running", operation.Metadata.FailureMessage);
    }

    [Fact]
    public void AnUpdateWithoutANewVersionRunsAsANormalUpdate()
    {
        var manager = new PackageManagerBuilder()
            .ConfigureManager(manager => manager.ExecutablePath = "C:\\tools\\pkgmgr.exe")
            .ConfigureOperation(helper =>
                helper.ParametersFactory = (_, _, operation) => [operation.ToString().ToLowerInvariant()])
            .Build();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Tool")
            .WithVersion("1.0.0")
            .Build();

        using var operation = new InspectableUpdatePackageOperation(
            package,
            new InstallOptions { UninstallBeforeUpdate = true }
        );

        Assert.Empty(operation.AttemptPreOperationsForTests());
        Assert.Equal("update", operation.PrepareProcessStartInfoForTests().Arguments.Trim());
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public async Task ARetriedReinstallSkipsTheUninstallWhenTheLoadedInstalledListNoLongerHasThePackage(
        bool stillInstalled,
        int expectedUninstalls
    )
    {
        var manager = CreateManager();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Tool")
            .WithVersion("1.0.0")
            .WithNewVersion("3.0.0")
            .Build();
        InitializeLoaders();
        if (stillInstalled)
            await InstalledPackagesLoader.Instance.AddForeign(
                new PackageBuilder()
                    .WithManager(manager)
                    .WithId("Contoso.Tool")
                    .WithVersion("1.0.0")
                    .Build()
            );
        var isLoaded = typeof(AbstractPackageLoader).GetProperty(nameof(AbstractPackageLoader.IsLoaded))!;
        isLoaded.SetValue(InstalledPackagesLoader.Instance, true);
        try
        {
            using var operation = new InspectableUpdatePackageOperation(
                package,
                new InstallOptions { UninstallBeforeUpdate = true }
            );

            Assert.Equal(expectedUninstalls, operation.AttemptPreOperationsForTests().Count);
        }
        finally
        {
            isLoaded.SetValue(InstalledPackagesLoader.Instance, false);
        }
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public async Task ARetriedReinstallMatchesTheInstalledPackageAcrossFeedsBeforeSkippingTheUninstall(
        bool installedPackagesKeepTheirSource,
        int expectedUninstalls
    )
    {
        var manager = new PackageManagerBuilder()
            .ConfigureCapabilities(capabilities =>
            {
                capabilities.InstalledPackagesKeepTheirSource = installedPackagesKeepTheirSource;
                return capabilities;
            })
            .Build();
        var otherFeed = new ManagerSource(manager, "other", new Uri("https://example.test/other"));
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Tool")
            .WithVersion("1.0.0")
            .WithNewVersion("3.0.0")
            .Build();
        InitializeLoaders();
        await InstalledPackagesLoader.Instance.AddForeign(
            new PackageBuilder()
                .WithManager(manager)
                .WithSource(otherFeed)
                .WithId("Contoso.Tool")
                .WithVersion("1.0.0")
                .Build()
        );
        var isLoaded = typeof(AbstractPackageLoader).GetProperty(nameof(AbstractPackageLoader.IsLoaded))!;
        isLoaded.SetValue(InstalledPackagesLoader.Instance, true);
        try
        {
            using var operation = new InspectableUpdatePackageOperation(
                package,
                new InstallOptions { UninstallBeforeUpdate = true }
            );

            Assert.Equal(expectedUninstalls, operation.AttemptPreOperationsForTests().Count);
        }
        finally
        {
            isLoaded.SetValue(InstalledPackagesLoader.Instance, false);
        }
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("2.0.0")]
    public async Task ARetriedReinstallKeepsTheUninstallWhenAnyVersionOfThePackageIsStillInstalled(
        string installedVersion
    )
    {
        var manager = CreateManager();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Tool")
            .WithVersion("1.0.0")
            .WithNewVersion("3.0.0")
            .Build();
        InitializeLoaders();
        await InstalledPackagesLoader.Instance.AddForeign(
            new PackageBuilder()
                .WithManager(manager)
                .WithId("Contoso.Tool")
                .WithVersion(installedVersion)
                .Build()
        );
        var isLoaded = typeof(AbstractPackageLoader).GetProperty(nameof(AbstractPackageLoader.IsLoaded))!;
        isLoaded.SetValue(InstalledPackagesLoader.Instance, true);
        try
        {
            using var operation = new InspectableUpdatePackageOperation(
                package,
                new InstallOptions { UninstallBeforeUpdate = true }
            );

            Assert.Single(operation.AttemptPreOperationsForTests());
        }
        finally
        {
            isLoaded.SetValue(InstalledPackagesLoader.Instance, false);
        }
    }

    [Fact]
    public void AFailedInstalledListingIsNotTakenAsProofThePackageWasUninstalled()
    {
        var manager = new PackageManagerBuilder().Build();
        manager.SetInstalledPackages(() => throw new InvalidOperationException("listing failed"));
        manager.GetInstalledPackages();
        Assert.True(manager.LastInstalledListingFailed);
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Tool")
            .WithVersion("1.0.0")
            .WithNewVersion("3.0.0")
            .Build();
        InitializeLoaders();
        var isLoaded = typeof(AbstractPackageLoader).GetProperty(nameof(AbstractPackageLoader.IsLoaded))!;
        isLoaded.SetValue(InstalledPackagesLoader.Instance, true);
        try
        {
            using var operation = new InspectableUpdatePackageOperation(
                package,
                new InstallOptions { UninstallBeforeUpdate = true }
            );

            Assert.Single(operation.AttemptPreOperationsForTests());
        }
        finally
        {
            isLoaded.SetValue(InstalledPackagesLoader.Instance, false);
        }
    }

    [Fact]
    public async Task TheReinstallUninstallRemovesTheInstalledEntryListedUnderAnotherFeed()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var manager = new PackageManagerBuilder()
            .WithName($"CrossFeedManager{Guid.NewGuid():N}")
            .ConfigureCapabilities(capabilities =>
            {
                capabilities.InstalledPackagesKeepTheirSource = false;
                return capabilities;
            })
            .ConfigureManager(manager =>
            {
                manager.ExecutablePath = Path.Join(Environment.SystemDirectory, "cmd.exe");
                manager.ExecutableArguments = "/c";
            })
            .ConfigureOperation(helper =>
            {
                helper.ParametersFactory = (_, _, operation) => ["echo", operation.ToString()];
                helper.ResultFactory = (_, operation, _, _) =>
                    operation is OperationType.Install ? OperationVeredict.Failure : OperationVeredict.Success;
            })
            .Build();
        var otherFeed = new ManagerSource(manager, "other", new Uri("https://example.test/other"));
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Tool")
            .WithVersion("1.0.0")
            .WithNewVersion("3.0.0")
            .Build();
        var listed = new PackageBuilder()
            .WithManager(manager)
            .WithSource(otherFeed)
            .WithId("Contoso.Tool")
            .WithVersion("1.0.0")
            .Build();
        InitializeLoaders();
        await InstalledPackagesLoader.Instance.AddForeign(listed);
        Assert.NotNull(InstalledPackagesLoader.Instance.GetEquivalentPackage(package));
        using var operation = new UpdatePackageOperation(
            package,
            new InstallOptions { UninstallBeforeUpdate = true },
            IgnoreParallelInstalls: true
        );

        await operation.MainThread().WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(OperationStatus.Failed, operation.Status);
        Assert.Null(InstalledPackagesLoader.Instance.GetEquivalentPackage(package));
    }

    [Theory]
    [InlineData(AbstractOperation.RetryMode.Retry_AsAdmin, true, false)]
    [InlineData(AbstractOperation.RetryMode.Retry_Interactive, false, true)]
    public void RetryingAFailedReinstallUninstallAppliesTheRetryChoiceToTheUninstall(
        string retryMode,
        bool expectAdmin,
        bool expectInteractive
    )
    {
        var manager = CreateManager();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Tool")
            .WithVersion("1.0.0")
            .WithNewVersion("3.0.0")
            .Build();
        using var operation = new InspectableUpdatePackageOperation(
            package,
            new InstallOptions { UninstallBeforeUpdate = true }
        );
        var uninstall = Assert.IsType<UninstallPackageOperation>(
            Assert.Single(operation.AttemptPreOperationsForTests()).Operation
        );
        uninstall.Status = OperationStatus.Failed;

        operation.ApplyRetryActionForTests(retryMode);

        Assert.Equal(expectAdmin, uninstall.Options.RunAsAdministrator);
        Assert.Equal(expectInteractive, uninstall.Options.InteractiveInstallation);
    }

    // UninstallPreviousVersionsOnUpdate is the one destructive consumer of the per-manager
    // version comparison: it queues every installed copy it considers superseded for removal.
    // On a SemVer registry the pre-release IS superseded by its stable release and must be
    // cleaned up, which the shared numeric comparison could never see.
    [Fact]
    public async Task UpdateOperationQueuesASupersededPreReleaseForUninstallOnSemVerManagers()
    {
        var manager = new Npm();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("contoso-tool")
            .WithVersion("2.0.0-rc1")
            .WithNewVersion("2.0.0")
            .Build();
        InitializeLoaders();
        await InstalledPackagesLoader.Instance.AddForeign(
            new PackageBuilder()
                .WithManager(manager)
                .WithId("contoso-tool")
                .WithVersion("2.0.0-rc1")
                .Build()
        );

        using var operation = new UpdatePackageOperation(
            package,
            new InstallOptions { UninstallPreviousVersionsOnUpdate = true }
        );

        var inner = Assert.Single(GetInnerOperations(operation, "PostOperations"));
        var uninstall = Assert.IsType<UninstallPackageOperation>(inner.Operation);
        Assert.Equal("2.0.0-rc1", uninstall.Package.VersionString);
    }

    // The mirror image, and the reason this had to be per-manager: on PyPI a bare trailing
    // dash-number is an implicit POST-release, so "1.0.0-1" is NEWER than "1.0.0" and must
    // never be queued for removal when updating onto it.
    [Fact]
    public async Task UpdateOperationLeavesANewerPostReleaseInstalledOnPip()
    {
        var manager = new Pip();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("contoso-tool")
            .WithVersion("0.9.0")
            .WithNewVersion("1.0.0")
            .Build();
        InitializeLoaders();
        await InstalledPackagesLoader.Instance.AddForeign(
            new PackageBuilder()
                .WithManager(manager)
                .WithId("contoso-tool")
                .WithVersion("1.0.0-1")
                .Build()
        );

        using var operation = new UpdatePackageOperation(
            package,
            new InstallOptions { UninstallPreviousVersionsOnUpdate = true }
        );

        Assert.Empty(GetInnerOperations(operation, "PostOperations"));
    }

    // Pins pre-existing behaviour rather than endorsing it: when an installed version cannot be
    // parsed at all, it is still treated as superseded and queued for removal. Left as-is
    // deliberately - changing what a destructive operation does to unparseable input belongs in
    // its own change, not in a version-comparison fix.
    [Fact]
    public async Task UpdateOperationStillQueuesAnUnparseableInstalledVersionForUninstall()
    {
        var manager = CreateManager();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Tool")
            .WithVersion("1.0.0")
            .WithNewVersion("3.0.0")
            .Build();
        InitializeLoaders();
        await InstalledPackagesLoader.Instance.AddForeign(
            new PackageBuilder()
                .WithManager(manager)
                .WithId("Contoso.Tool")
                .WithVersion("10c8e557")
                .Build()
        );

        using var operation = new UpdatePackageOperation(
            package,
            new InstallOptions { UninstallPreviousVersionsOnUpdate = true }
        );

        var inner = Assert.Single(GetInnerOperations(operation, "PostOperations"));
        var uninstall = Assert.IsType<UninstallPackageOperation>(inner.Operation);
        Assert.Equal("10c8e557", uninstall.Package.VersionString);
    }

    [Fact]
    public async Task UpdateOperationOnlyQueuesPreviousVersionsFromTheSameScope()
    {
        var manager = CreateManager();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Tool")
            .WithVersion("2.0.0")
            .WithNewVersion("3.0.0")
            .WithOptions(new OverridenInstallationOptions(PackageScope.User))
            .Build();
        InitializeLoaders();
        await InstalledPackagesLoader.Instance.AddForeign(
            new PackageBuilder()
                .WithManager(manager)
                .WithId("Contoso.Tool")
                .WithVersion("1.0.0")
                .WithOptions(new OverridenInstallationOptions(PackageScope.Machine))
                .Build()
        );
        await InstalledPackagesLoader.Instance.AddForeign(
            new PackageBuilder()
                .WithManager(manager)
                .WithId("Contoso.Tool")
                .WithVersion("1.5.0")
                .WithOptions(new OverridenInstallationOptions(PackageScope.User))
                .Build()
        );
        await InstalledPackagesLoader.Instance.AddForeign(
            new PackageBuilder()
                .WithManager(manager)
                .WithId("Contoso.Tool")
                .WithVersion("1.7.0")
                .Build()
        );

        using var operation = new UpdatePackageOperation(
            package,
            new InstallOptions { UninstallPreviousVersionsOnUpdate = true }
        );

        var uninstalled = GetInnerOperations(operation, "PostOperations")
            .Select(inner => Assert.IsType<UninstallPackageOperation>(inner.Operation).Package.VersionString)
            .Order()
            .ToArray();
        Assert.Equal(["1.5.0", "1.7.0"], uninstalled);
    }

    [Fact]
    public async Task UpdateOperationQueuesPreviousVersionsFromTheScopeChosenForTheUpdate()
    {
        var manager = CreateManager();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Tool")
            .WithVersion("2.0.0")
            .WithNewVersion("3.0.0")
            .WithOptions(new OverridenInstallationOptions(PackageScope.User))
            .Build();
        InitializeLoaders();
        await InstalledPackagesLoader.Instance.AddForeign(
            new PackageBuilder()
                .WithManager(manager)
                .WithId("Contoso.Tool")
                .WithVersion("1.0.0")
                .WithOptions(new OverridenInstallationOptions(PackageScope.Machine))
                .Build()
        );
        await InstalledPackagesLoader.Instance.AddForeign(
            new PackageBuilder()
                .WithManager(manager)
                .WithId("Contoso.Tool")
                .WithVersion("1.5.0")
                .WithOptions(new OverridenInstallationOptions(PackageScope.User))
                .Build()
        );

        using var operation = new UpdatePackageOperation(
            package,
            new InstallOptions
            {
                UninstallPreviousVersionsOnUpdate = true,
                InstallationScope = PackageScope.Machine,
            }
        );

        var inner = Assert.Single(GetInnerOperations(operation, "PostOperations"));
        var uninstall = Assert.IsType<UninstallPackageOperation>(inner.Operation);
        Assert.Equal("1.0.0", uninstall.Package.VersionString);
    }

    [Fact]
    public async Task UpdateOperationWithoutAScopeQueuesPreviousVersionsFromEveryScope()
    {
        var manager = CreateManager();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Tool")
            .WithVersion("2.0.0")
            .WithNewVersion("3.0.0")
            .Build();
        InitializeLoaders();
        await InstalledPackagesLoader.Instance.AddForeign(
            new PackageBuilder()
                .WithManager(manager)
                .WithId("Contoso.Tool")
                .WithVersion("1.0.0")
                .WithOptions(new OverridenInstallationOptions(PackageScope.Machine))
                .Build()
        );
        await InstalledPackagesLoader.Instance.AddForeign(
            new PackageBuilder()
                .WithManager(manager)
                .WithId("Contoso.Tool")
                .WithVersion("1.5.0")
                .WithOptions(new OverridenInstallationOptions(PackageScope.User))
                .Build()
        );

        using var operation = new UpdatePackageOperation(
            package,
            new InstallOptions { UninstallPreviousVersionsOnUpdate = true }
        );

        var uninstalled = GetInnerOperations(operation, "PostOperations")
            .Select(inner => Assert.IsType<UninstallPackageOperation>(inner.Operation).Package.VersionString)
            .Order()
            .ToArray();
        Assert.Equal(["1.0.0", "1.5.0"], uninstalled);
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

    private static TestPackageManager CreateSlowProcessManager(bool serializesOperations)
    {
        return new PackageManagerBuilder()
            .WithName($"SlowManager{Guid.NewGuid():N}")
            .ConfigureCapabilities(capabilities =>
            {
                capabilities.SerializesOperations = serializesOperations;
                return capabilities;
            })
            .ConfigureManager(manager =>
            {
                manager.ExecutablePath = Path.Join(Environment.SystemDirectory, "cmd.exe");
                manager.ExecutableArguments = "/c";
            })
            .ConfigureOperation(helper =>
            {
                helper.ParametersFactory = (_, _, _) => ["ping", "-n", "3", "127.0.0.1"];
                helper.ResultFactory = (_, _, _, _) => OperationVeredict.Success;
            })
            .Build();
    }

    private static TestPackageManager CreateControlledManagedManager(
        bool serializesOperations,
        Func<IPackage, CancellationToken, Task> perform
    )
    {
        return new PackageManagerBuilder()
            .WithName($"ControlledManager{Guid.NewGuid():N}")
            .ConfigureCapabilities(capabilities =>
            {
                capabilities.SerializesOperations = serializesOperations;
                return capabilities;
            })
            .ConfigureManager(manager =>
                manager.SetOperationHelper(new ControlledManagedOperationHelper(manager, perform))
            )
            .Build();
    }

    private sealed class ControlledManagedOperationHelper(
        TestPackageManager manager,
        Func<IPackage, CancellationToken, Task> perform
    ) : BasePkgOperationHelper(manager), IManagedPackageOperationHelper
    {
        public async Task<OperationVeredict?> TryPerformAsync(
            IPackage package,
            InstallOptions options,
            OperationType operation,
            Action<string> log,
            Action<OperationProgress> progress,
            CancellationToken cancellationToken
        )
        {
            await perform(package, cancellationToken);
            return OperationVeredict.Success;
        }

        protected override IReadOnlyList<string> _getOperationParameters(
            IPackage package,
            InstallOptions options,
            OperationType operation
        ) => [];

        protected override OperationVeredict _getOperationResult(
            IPackage package,
            OperationType operation,
            IReadOnlyList<string> processOutput,
            int returnCode
        ) => OperationVeredict.Failure;
    }

    private static bool HasLine(AbstractOperation operation, string prefix) =>
        operation.GetOutput().Any(line => line.Item1.StartsWith(prefix, StringComparison.Ordinal));

    private static async Task WaitForConditionAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!condition())
            await Task.Delay(25, timeout.Token);
    }

    private static Func<bool> ReportsWaitingOnTheGate(AbstractOperation operation)
    {
        bool reported = false;
        operation.LogLineAdded += (_, line) =>
        {
            if (line.Item1.StartsWith("Waiting for another", StringComparison.Ordinal))
                reported = true;
        };
        return () => reported;
    }

    [Fact]
    public async Task ASuccessfulOperationInvalidatesTheInstalledPackagesOfItsManager()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var manager = CreateSlowProcessManager(serializesOperations: false);
        InitializeLoaders();
        long generationBefore = manager.InstalledPackagesGeneration;
        using var operation = new UpdatePackageOperation(
            new PackageBuilder().WithManager(manager).WithId("updated").Build(),
            new InstallOptions(),
            IgnoreParallelInstalls: true
        );

        await operation.MainThread().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(OperationStatus.Succeeded, operation.Status);
        Assert.NotEqual(generationBefore, manager.InstalledPackagesGeneration);
    }

    [Fact]
    public async Task OperationsOfASerializingManagerNeverRunTheirProcessesConcurrently()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var manager = CreateSlowProcessManager(serializesOperations: true);
        InitializeLoaders();
        using var first = new InstallPackageOperation(
            new PackageBuilder().WithManager(manager).WithId("first").Build(),
            new InstallOptions(),
            IgnoreParallelInstalls: true
        );
        using var second = new InstallPackageOperation(
            new PackageBuilder().WithManager(manager).WithId("second").Build(),
            new InstallOptions(),
            IgnoreParallelInstalls: true
        );
        var secondWaited = ReportsWaitingOnTheGate(second);
        bool? firstFinishedWhenSecondStarted = null;
        second.LogLineAdded += (_, line) =>
        {
            if (line.Item1.StartsWith("Executing process", StringComparison.Ordinal))
                firstFinishedWhenSecondStarted = HasLine(first, "Process return value");
        };

        Task firstRun = first.MainThread();
        await WaitForConditionAsync(() => HasLine(first, "Start Time"));
        Task secondRun = second.MainThread();
        await Task.WhenAll(firstRun, secondRun).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(OperationStatus.Succeeded, first.Status);
        Assert.Equal(OperationStatus.Succeeded, second.Status);
        Assert.True(firstFinishedWhenSecondStarted);
        Assert.True(secondWaited());
    }

    [WindowsFact]
    public async Task AnOperationWaitingOnTheManagerGateSeesVariablesPersistedWhileItWaited()
    {
        string name = "UNIGETUI_GATE_ENVIRONMENT_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(name, null, EnvironmentVariableTarget.User);
        Environment.SetEnvironmentVariable(name, null, EnvironmentVariableTarget.Process);
        try
        {
            var manager = new PackageManagerBuilder()
                .WithName($"GateManager{Guid.NewGuid():N}")
                .ConfigureCapabilities(capabilities =>
                {
                    capabilities.SerializesOperations = true;
                    return capabilities;
                })
                .ConfigureManager(fake =>
                {
                    fake.ExecutablePath = Path.Join(Environment.SystemDirectory, "cmd.exe");
                    fake.ExecutableArguments = "/c";
                })
                .ConfigureOperation(helper =>
                {
                    helper.ParametersFactory = (package, _, _) =>
                        package.Id == "first"
                            ? ["ping", "-n", "3", "127.0.0.1", ">nul", "&", "setx", name, "fromfirst"]
                            : ["echo", $"value=%{name}%"];
                    helper.ResultFactory = (_, _, _, _) => OperationVeredict.Success;
                })
                .Build();
            InitializeLoaders();
            PersistedEnvironment.TEST_ResetBaseline();
            using var first = new InstallPackageOperation(
                new PackageBuilder().WithManager(manager).WithId("first").Build(),
                new InstallOptions(),
                IgnoreParallelInstalls: true
            );
            using var second = new InstallPackageOperation(
                new PackageBuilder().WithManager(manager).WithId("second").Build(),
                new InstallOptions(),
                IgnoreParallelInstalls: true
            );
            var secondWaited = ReportsWaitingOnTheGate(second);

            Task firstRun = first.MainThread();
            await WaitForConditionAsync(() => HasLine(first, "Start Time"));
            Task secondRun = second.MainThread();
            await Task.WhenAll(firstRun, secondRun).WaitAsync(TimeSpan.FromSeconds(30));

            Assert.True(secondWaited());
            Assert.Equal(OperationStatus.Succeeded, second.Status);
            Assert.True(HasLine(second, "value=fromfirst"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null, EnvironmentVariableTarget.User);
            Environment.SetEnvironmentVariable(name, null, EnvironmentVariableTarget.Process);
            PersistedEnvironment.TEST_ResetBaseline();
        }
    }

    [Fact]
    public async Task OperationsOfANonSerializingManagerRunTheirProcessesConcurrently()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var manager = CreateSlowProcessManager(serializesOperations: false);
        InitializeLoaders();
        using var first = new InstallPackageOperation(
            new PackageBuilder().WithManager(manager).WithId("first").Build(),
            new InstallOptions(),
            IgnoreParallelInstalls: true
        );
        using var second = new InstallPackageOperation(
            new PackageBuilder().WithManager(manager).WithId("second").Build(),
            new InstallOptions(),
            IgnoreParallelInstalls: true
        );
        var secondWaited = ReportsWaitingOnTheGate(second);
        bool? firstFinishedWhenSecondStarted = null;
        second.LogLineAdded += (_, line) =>
        {
            if (line.Item1.StartsWith("Executing process", StringComparison.Ordinal))
                firstFinishedWhenSecondStarted = HasLine(first, "Process return value");
        };

        Task firstRun = first.MainThread();
        await WaitForConditionAsync(() => HasLine(first, "Start Time"));
        Task secondRun = second.MainThread();
        await Task.WhenAll(firstRun, secondRun).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(firstFinishedWhenSecondStarted);
        Assert.False(secondWaited());
    }

    [Fact]
    public async Task CancelingAnOperationWaitingOnASerializingManagerLeavesTheGateUsable()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var manager = CreateSlowProcessManager(serializesOperations: true);
        InitializeLoaders();
        using var first = new InstallPackageOperation(
            new PackageBuilder().WithManager(manager).WithId("first").Build(),
            new InstallOptions(),
            IgnoreParallelInstalls: true
        );
        using var waiting = new InstallPackageOperation(
            new PackageBuilder().WithManager(manager).WithId("waiting").Build(),
            new InstallOptions(),
            IgnoreParallelInstalls: true
        );
        using var third = new InstallPackageOperation(
            new PackageBuilder().WithManager(manager).WithId("third").Build(),
            new InstallOptions(),
            IgnoreParallelInstalls: true
        );
        var waitingReported = ReportsWaitingOnTheGate(waiting);
        var thirdWaited = ReportsWaitingOnTheGate(third);

        Task firstRun = first.MainThread();
        await WaitForConditionAsync(() => HasLine(first, "Start Time"));
        Task waitingRun = waiting.MainThread();
        await WaitForConditionAsync(waitingReported);
        waiting.Cancel();
        await waitingRun.WaitAsync(TimeSpan.FromSeconds(10));
        await firstRun.WaitAsync(TimeSpan.FromSeconds(30));
        await third.MainThread().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(OperationStatus.Canceled, waiting.Status);
        Assert.False(HasLine(waiting, "Executing process"));
        Assert.DoesNotContain(
            waiting.GetOutput(),
            line => line.Item1.Contains("CanceledException", StringComparison.Ordinal)
        );
        Assert.Equal(OperationStatus.Succeeded, first.Status);
        Assert.Equal(OperationStatus.Succeeded, third.Status);
        Assert.False(thirdWaited());
    }

    [Fact]
    public async Task AnOperationWaitingOnASerializingManagerDoesNotHoldAParallelSlot()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waitingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unrelatedStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serialized = CreateControlledManagedManager(
            serializesOperations: true,
            async (package, cancellationToken) =>
            {
                if (package.Id == "first")
                {
                    firstStarted.TrySetResult();
                    await releaseFirst.Task.WaitAsync(cancellationToken);
                }
                else
                {
                    waitingStarted.TrySetResult();
                }
            }
        );
        var other = CreateControlledManagedManager(
            serializesOperations: false,
            (_, _) =>
            {
                unrelatedStarted.TrySetResult();
                return Task.CompletedTask;
            }
        );
        InitializeLoaders();
        using var first = new InstallPackageOperation(
            new PackageBuilder().WithManager(serialized).WithId("first").Build(),
            new InstallOptions()
        );
        using var waiting = new InstallPackageOperation(
            new PackageBuilder().WithManager(serialized).WithId("waiting").Build(),
            new InstallOptions()
        );
        using var unrelated = new InstallPackageOperation(
            new PackageBuilder().WithManager(other).WithId("unrelated").Build(),
            new InstallOptions()
        );

        AbstractOperation.OperationQueue.Clear();
        int previousMax = AbstractOperation.MAX_OPERATIONS;
        AbstractOperation.MAX_OPERATIONS = 2;
        Task? firstRun = null;
        Task? waitingRun = null;
        Task? unrelatedRun = null;
        try
        {
            firstRun = first.MainThread();
            await Task.WhenAny(firstStarted.Task, firstRun, Task.Delay(TimeSpan.FromSeconds(15)));
            Assert.True(
                firstStarted.Task.IsCompletedSuccessfully,
                $"The first operation did not enter its managed helper; status: {first.Status}."
            );

            waitingRun = waiting.MainThread();
            await WaitForConditionAsync(() => waiting.IsWaitingForManager || waitingRun.IsCompleted);
            Assert.True(waiting.IsWaitingForManager, "The second operation did not wait for its manager.");
            Assert.False(waitingStarted.Task.IsCompleted);

            unrelatedRun = unrelated.MainThread();
            await Task.WhenAny(unrelatedStarted.Task, unrelatedRun, Task.Delay(TimeSpan.FromSeconds(15)));
            Assert.True(
                unrelatedStarted.Task.IsCompletedSuccessfully,
                $"The unrelated operation did not start while the first held its manager gate; status: {unrelated.Status}."
            );
            Assert.False(firstRun.IsCompleted);
            Assert.True(waiting.IsWaitingForManager);
            Assert.False(waitingStarted.Task.IsCompleted);

            releaseFirst.TrySetResult();
            await Task.WhenAll(firstRun, waitingRun, unrelatedRun).WaitAsync(TimeSpan.FromSeconds(30));

            Assert.True(waitingStarted.Task.IsCompletedSuccessfully);
            Assert.Equal(OperationStatus.Succeeded, first.Status);
            Assert.Equal(OperationStatus.Succeeded, waiting.Status);
            Assert.Equal(OperationStatus.Succeeded, unrelated.Status);
            Assert.Equal(0, AbstractOperation.QueueLength());
        }
        finally
        {
            if (
                firstRun?.IsCompleted is false
                || waitingRun?.IsCompleted is false
                || unrelatedRun?.IsCompleted is false
            )
            {
                first.Cancel();
                waiting.Cancel();
                unrelated.Cancel();
            }
            releaseFirst.TrySetResult();
            try
            {
                await Task.WhenAll(
                    new[] { firstRun, waitingRun, unrelatedRun }.OfType<Task>()
                ).WaitAsync(TimeSpan.FromSeconds(30));
            }
            finally
            {
                AbstractOperation.MAX_OPERATIONS = previousMax;
                AbstractOperation.OperationQueue.Clear();
            }
        }
    }

    private static TestPackageManager CreateFailingEchoManager(params string[] echoed)
    {
        return new PackageManagerBuilder()
            .WithName($"FailingManager{Guid.NewGuid():N}")
            .ConfigureManager(manager =>
            {
                manager.ExecutablePath = Path.Join(Environment.SystemDirectory, "cmd.exe");
                manager.ExecutableArguments = "/c";
            })
            .ConfigureOperation(helper =>
            {
                helper.ParametersFactory = (_, _, _) => ["echo", .. echoed];
                helper.ResultFactory = (_, _, _, _) => OperationVeredict.Failure;
            })
            .Build();
    }

    [Fact]
    public async Task AFailureCausedByAFileInUseTellsTheUserToCloseTheProgramUsingIt()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var manager = CreateFailingEchoManager("ERROR:", "[WinError", "32]", "file", "busy");
        InitializeLoaders();
        using var operation = new UpdatePackageOperation(
            new PackageBuilder().WithManager(manager).WithId("botocore").WithName("botocore").Build(),
            new InstallOptions(),
            IgnoreParallelInstalls: true
        );

        await operation.MainThread().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(OperationStatus.Failed, operation.Status);
        Assert.Equal(
            "A file of botocore is in use by another program. Close any program that may be using it, then try again",
            operation.Metadata.FailureMessage
        );
    }

    [Fact]
    public async Task AFailureWithoutAFileInUseKeepsTheGenericFailureMessage()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var manager = CreateFailingEchoManager("ERROR:", "[WinError", "5]", "denied");
        InitializeLoaders();
        using var operation = new UpdatePackageOperation(
            new PackageBuilder().WithManager(manager).WithId("botocore").WithName("botocore").Build(),
            new InstallOptions(),
            IgnoreParallelInstalls: true
        );

        await operation.MainThread().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(OperationStatus.Failed, operation.Status);
        Assert.DoesNotContain("in use by another program", operation.Metadata.FailureMessage);
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
            new InstallOptions { Version = "3.0.3", VersionIsExplicit = true },
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
    public async Task UpdateOperationSuccessfulRunIgnoresAnInstallOnlyPinnedVersion()
    {
        TestPackageManager? manager = null;
        Package? installedPackage = null;
        manager = new PackageManagerBuilder()
            .ConfigureCapabilities(capabilities =>
            {
                capabilities.SupportsCustomVersionsOnUpdate = false;
                return capabilities;
            })
            .WithInstalledPackages(_ => [Assert.IsType<Package>(installedPackage)])
            .Build();
        var installedBeforeUpdate = new PackageBuilder()
            .WithManager(manager)
            .WithId("jq")
            .WithVersion("1.7.1")
            .WithNewVersion("1.8.2")
            .Build();
        installedPackage = new PackageBuilder()
            .WithManager(manager)
            .WithId("jq")
            .WithVersion("1.8.2")
            .Build();
        InitializeLoaders();
        await InstalledPackagesLoader.Instance.AddForeign(installedBeforeUpdate);
        using var operation = new SimulatedUpdatePackageOperation(
            installedBeforeUpdate,
            new InstallOptions { Version = "1.7.1" },
            OperationVeredict.Success
        );

        await operation.MainThread();
        await WaitForAsync(() =>
            InstalledPackagesLoader.Instance.GetEquivalentPackages(installedBeforeUpdate)
                .Any(package => package.VersionString == "1.8.2")
        );

        Assert.DoesNotContain(
            InstalledPackagesLoader.Instance.GetEquivalentPackages(installedBeforeUpdate),
            package => package.VersionString == "1.7.1"
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HeldOperationsDoNotStartUntilTheHoldIsReleased(bool queueEnabled)
    {
        using var operation = new CancellationAwareStubOperation(queueEnabled);
        Task mainThread;
        AbstractOperation.HoldNewOperations = true;
        try
        {
            mainThread = operation.MainThread();
            await Task.Delay(300);

            Assert.False(operation.PerformStarted.Task.IsCompleted);
            Assert.Equal(OperationStatus.InQueue, operation.Status);
        }
        finally
        {
            AbstractOperation.HoldNewOperations = false;
        }

        await operation.PerformStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        operation.Cancel();
        await operation.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
        operation.AllowCleanupToComplete.TrySetResult(true);
        await mainThread;
    }

    [Fact]
    public async Task TheHoldIsRefusedWhileAnOperationIsExecuting()
    {
        using var operation = new CancellationAwareStubOperation();
        Task mainThread = operation.MainThread();
        await operation.PerformStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.False(AbstractOperation.TryHoldNewOperations());

        operation.Cancel();
        await operation.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
        operation.AllowCleanupToComplete.TrySetResult(true);
        await mainThread;

        Assert.True(AbstractOperation.TryHoldNewOperations());
        AbstractOperation.HoldNewOperations = false;
    }

    [Fact]
    public void AdmittedWorkAndTheHoldExcludeEachOther()
    {
        Assert.True(AbstractOperation.TryBeginWork());
        try
        {
            Assert.False(AbstractOperation.TryHoldNewOperations());
        }
        finally
        {
            AbstractOperation.EndWork();
        }

        Assert.True(AbstractOperation.TryHoldNewOperations());
        try
        {
            Assert.False(AbstractOperation.TryBeginWork());
        }
        finally
        {
            AbstractOperation.HoldNewOperations = false;
        }
    }

    [Fact]
    public async Task ARetryWaitsWhileNewOperationsAreHeld()
    {
        using var operation = new RetryAwareStubOperation();
        Task firstRun = operation.MainThread();
        await operation.FirstRunStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        operation.Cancel();
        await operation.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
        operation.Retry(AbstractOperation.RetryMode.Retry);

        AbstractOperation.HoldNewOperations = true;
        try
        {
            operation.AllowCleanupToComplete.TrySetResult(true);
            await firstRun;
            await Task.Delay(300);

            Assert.False(operation.SecondRunStarted.Task.IsCompleted);
        }
        finally
        {
            AbstractOperation.HoldNewOperations = false;
        }

        await operation.SecondRunStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitForAsync(() => operation.Status is OperationStatus.Succeeded);
    }

    [Fact]
    public async Task NoWorkIsAdmittedOnceTheHoldIsTaken()
    {
        int overlaps = 0;
        using var stop = new CancellationTokenSource();
        Task[] workers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                if (!AbstractOperation.TryBeginWork())
                    continue;
                Thread.SpinWait(200);
                if (AbstractOperation.HoldNewOperations)
                    Interlocked.Increment(ref overlaps);
                AbstractOperation.EndWork();
            }
        })).ToArray();

        try
        {
            for (int i = 0; i < 2000; i++)
            {
                if (!AbstractOperation.TryHoldNewOperations())
                    continue;
                Thread.SpinWait(200);
                AbstractOperation.HoldNewOperations = false;
            }
        }
        finally
        {
            stop.Cancel();
            await Task.WhenAll(workers);
            AbstractOperation.HoldNewOperations = false;
        }

        Assert.Equal(0, overlaps);
    }

    [Fact]
    public async Task CancelingAHeldOperationEndsItWithoutRunning()
    {
        using var operation = new CancellationAwareStubOperation(queueEnabled: true);
        AbstractOperation.HoldNewOperations = true;
        try
        {
            Task mainThread = operation.MainThread();
            await Task.Delay(300);

            operation.Cancel();
            await mainThread.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.False(operation.PerformStarted.Task.IsCompleted);
            Assert.Equal(OperationStatus.Canceled, operation.Status);
            Assert.Equal(0, AbstractOperation.QueueLength());
        }
        finally
        {
            AbstractOperation.HoldNewOperations = false;
        }
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

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task UserRequestedOperationsQueueAheadOfBackgroundOnes(int parallelOperations)
    {
        var running = new List<CancellationAwareStubOperation>();
        var runningTasks = new List<Task>();
        for (int i = 0; i < parallelOperations; i++)
            running.Add(new CancellationAwareStubOperation(queueEnabled: true) { IsBackgroundOperation = true });

        using var firstBackground = new CancellationAwareStubOperation(queueEnabled: true)
        {
            IsBackgroundOperation = true,
        };
        using var secondBackground = new CancellationAwareStubOperation(queueEnabled: true)
        {
            IsBackgroundOperation = true,
        };
        using var userRequested = new CancellationAwareStubOperation(queueEnabled: true);

        AbstractOperation.OperationQueue.Clear();
        int previousMax = AbstractOperation.MAX_OPERATIONS;
        AbstractOperation.MAX_OPERATIONS = parallelOperations;

        try
        {
            foreach (var blocker in running)
            {
                runningTasks.Add(blocker.MainThread());
                await blocker.PerformStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            }

            _ = firstBackground.MainThread();
            await WaitUntilQueued(firstBackground);
            _ = secondBackground.MainThread();
            await WaitUntilQueued(secondBackground);
            _ = userRequested.MainThread();
            await WaitUntilQueued(userRequested);

            var expected = new List<AbstractOperation>(running)
            {
                userRequested,
                firstBackground,
                secondBackground,
            };
            Assert.Equal(expected, AbstractOperation.OperationQueue);

            firstBackground.Cancel();
            secondBackground.Cancel();
            userRequested.Cancel();

            foreach (var blocker in running)
            {
                blocker.Cancel();
                await blocker.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
                blocker.AllowCleanupToComplete.TrySetResult(true);
            }

            await Task.WhenAll(runningTasks);
        }
        finally
        {
            foreach (var blocker in running)
                blocker.Dispose();
            AbstractOperation.MAX_OPERATIONS = previousMax;
            AbstractOperation.OperationQueue.Clear();
        }
    }

    [Fact]
    public async Task AUserRequestedOperationDoesNotOvertakeEarlierUserRequestedOnes()
    {
        using var running = new CancellationAwareStubOperation(queueEnabled: true)
        {
            IsBackgroundOperation = true,
        };
        using var firstUser = new CancellationAwareStubOperation(queueEnabled: true);
        using var secondUser = new CancellationAwareStubOperation(queueEnabled: true);
        using var promotedBackground = new CancellationAwareStubOperation(queueEnabled: true)
        {
            IsBackgroundOperation = true,
        };
        using var lateUser = new CancellationAwareStubOperation(queueEnabled: true);

        AbstractOperation.OperationQueue.Clear();
        int previousMax = AbstractOperation.MAX_OPERATIONS;
        AbstractOperation.MAX_OPERATIONS = 1;

        try
        {
            Task runningTask = running.MainThread();
            await running.PerformStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

            _ = firstUser.MainThread();
            await WaitUntilQueued(firstUser);
            _ = secondUser.MainThread();
            await WaitUntilQueued(secondUser);
            _ = promotedBackground.MainThread();
            await WaitUntilQueued(promotedBackground);

            promotedBackground.RunNext();

            _ = lateUser.MainThread();
            await WaitUntilQueued(lateUser);

            Assert.Equal(
                new AbstractOperation[]
                {
                    running,
                    promotedBackground,
                    firstUser,
                    secondUser,
                    lateUser,
                },
                AbstractOperation.OperationQueue
            );

            firstUser.Cancel();
            secondUser.Cancel();
            promotedBackground.Cancel();
            lateUser.Cancel();

            running.Cancel();
            await running.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
            running.AllowCleanupToComplete.TrySetResult(true);
            await runningTask;
        }
        finally
        {
            AbstractOperation.MAX_OPERATIONS = previousMax;
            AbstractOperation.OperationQueue.Clear();
        }
    }

    [Fact]
    public void ReorderingAnOperationThatLeftTheQueueDoesNotPutItBack()
    {
        using var runNext = new CancellationAwareStubOperation(queueEnabled: true);
        using var backOfQueue = new CancellationAwareStubOperation(queueEnabled: true);

        AbstractOperation.OperationQueue.Clear();

        try
        {
            Assert.Equal(OperationStatus.InQueue, runNext.Status);
            Assert.Equal(OperationStatus.InQueue, backOfQueue.Status);

            runNext.RunNext();
            backOfQueue.BackOfTheQueue();

            Assert.Empty(AbstractOperation.OperationQueue);
        }
        finally
        {
            AbstractOperation.OperationQueue.Clear();
        }
    }

    [Fact]
    public void RetryingABackgroundOperationMakesItUserRequested()
    {
        using var operation = new CancellationAwareStubOperation(queueEnabled: true)
        {
            IsBackgroundOperation = true,
        };

        operation.Retry(AbstractOperation.RetryMode.Retry);

        Assert.False(operation.IsBackgroundOperation);
    }

    [Fact]
    public async Task APromotedBackgroundOperationKeepsItsSlotWhenNoUserOperationIsQueued()
    {
        using var running = new CancellationAwareStubOperation(queueEnabled: true)
        {
            IsBackgroundOperation = true,
        };
        using var untouchedBackground = new CancellationAwareStubOperation(queueEnabled: true)
        {
            IsBackgroundOperation = true,
        };
        using var promotedBackground = new CancellationAwareStubOperation(queueEnabled: true)
        {
            IsBackgroundOperation = true,
        };
        using var lateUser = new CancellationAwareStubOperation(queueEnabled: true);

        AbstractOperation.OperationQueue.Clear();
        int previousMax = AbstractOperation.MAX_OPERATIONS;
        AbstractOperation.MAX_OPERATIONS = 1;

        try
        {
            Task runningTask = running.MainThread();
            await running.PerformStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

            _ = untouchedBackground.MainThread();
            await WaitUntilQueued(untouchedBackground);
            _ = promotedBackground.MainThread();
            await WaitUntilQueued(promotedBackground);

            promotedBackground.RunNext();

            _ = lateUser.MainThread();
            await WaitUntilQueued(lateUser);

            Assert.Equal(
                new AbstractOperation[]
                {
                    running,
                    promotedBackground,
                    lateUser,
                    untouchedBackground,
                },
                AbstractOperation.OperationQueue
            );

            untouchedBackground.Cancel();
            promotedBackground.Cancel();
            lateUser.Cancel();

            running.Cancel();
            await running.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
            running.AllowCleanupToComplete.TrySetResult(true);
            await runningTask;
        }
        finally
        {
            AbstractOperation.MAX_OPERATIONS = previousMax;
            AbstractOperation.OperationQueue.Clear();
        }
    }

    private static async Task WaitUntilQueued(AbstractOperation operation)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(1);
        while (!AbstractOperation.OperationQueue.Contains(operation))
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The operation never reached the queue");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task BrokerOperationFailsWithoutLocalFallbackWhenProbeFails()
    {
        var transport = new FakeBrokerTransport(healthy: false);

        await AssertBrokerUnavailableFailure(transport);
    }

    [Theory]
    [InlineData(BrokerUnreachableCause.NotListening, "The Devolutions Agent package broker is not running")]
    [InlineData(BrokerUnreachableCause.ConnectionClosed, "Devolutions Agent refused this copy of UniGetUI")]
    [InlineData(BrokerUnreachableCause.Unknown, "Agent broker unavailable")]
    public async Task BrokerOperationExplainsWhyTheBrokerIsUnreachable(BrokerUnreachableCause cause, string expectedTitle)
    {
        var transport = new FakeBrokerTransport(healthy: false);
        PackageOperation.BrokerUnreachableDiagnosis = _ => Task.FromResult(cause);
        try
        {
            await AssertBrokerUnavailableFailure(transport, expectedTitle);
        }
        finally
        {
            PackageOperation.BrokerUnreachableDiagnosis = null;
        }
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

    private static async Task<(OperationVeredict Veredict, string? Title, string? Message)> RunBrokeredOperationCapturingFailure(
        ScriptedBrokerTransport transport,
        string? packageId = null)
    {
        AbstractOperation? created = null;
        var result = await RunBrokeredOperationWithOutput(
            transport,
            onOperationCreated: operation => created = operation,
            packageId: packageId);
        return (result.Veredict, created?.Metadata.FailureTitle, created?.Metadata.FailureMessage);
    }

    [Fact]
    public async Task BrokerPolicyDenialExplainsTheReasonAndRule()
    {
        var transport = new ScriptedBrokerTransport
        {
            Denial = new BrokerApiDecisionInfo
            {
                Decision = BrokerApiDecision.Deny,
                RuleId = "deny-old-versions",
                Reason = "Versions before 2.0 are not allowed",
            },
        };

        var (veredict, title, message) = await RunBrokeredOperationCapturingFailure(transport);

        Assert.Equal(OperationVeredict.Failure, veredict);
        Assert.Equal(CoreTools.Translate("Operation denied by policy"), title);
        Assert.Contains("deny-old-versions", message);
        Assert.Contains("Versions before 2.0 are not allowed", message);
    }

    [Theory]
    [InlineData(401, BrokerApiErrorCode.Unauthorized)]
    [InlineData(403, BrokerApiErrorCode.Forbidden)]
    public async Task BrokerRefusingTheCapabilitiesRequestIsReportedAsAnAuthorizationFailure(
        int statusCode,
        BrokerApiErrorCode code)
    {
        bool originalSetting = Settings.Get(Settings.K.UseAgentBroker);
        BrokerFailureDescription? notified = null;
        EventHandler<BrokerFailureDescription> onBrokerUnavailable = (_, failure) => notified = failure;
        PackageOperation.BrokerUnavailable += onBrokerUnavailable;
        try
        {
            var transport = new ScriptedBrokerTransport
            {
                CapabilitiesError = (statusCode, code),
            };

            var (veredict, title, message) = await RunBrokeredOperationCapturingFailure(transport);

            Assert.Equal(OperationVeredict.Failure, veredict);
            Assert.Equal(CoreTools.Translate("UniGetUI is not authorized to use the Devolutions Agent"), title);
            Assert.Contains("signed", message);
            Assert.Equal(title, notified?.Title);
            Assert.Equal(message, notified?.Message);
            Assert.DoesNotContain("/v1/package-operations/execute", transport.RequestedPaths);
        }
        finally
        {
            PackageOperation.BrokerUnavailable -= onBrokerUnavailable;
            Settings.Set(Settings.K.UseAgentBroker, originalSetting);
        }
    }

    [Fact]
    public async Task BrokerPausedWithoutPolicyIsExplained()
    {
        var transport = new ScriptedBrokerTransport
        {
            CapabilitiesError = (503, BrokerApiErrorCode.BrokerPaused),
        };

        var (veredict, title, _) = await RunBrokeredOperationCapturingFailure(transport);

        Assert.Equal(OperationVeredict.Failure, veredict);
        Assert.Equal(CoreTools.Translate("Package operations are paused"), title);
    }

    [Fact]
    public async Task BrokerRequestThatTheBrokerWouldRejectIsNotSent()
    {
        var transport = new ScriptedBrokerTransport();

        var (veredict, title, message) = await RunBrokeredOperationCapturingFailure(transport, packageId: "git;7zip");

        Assert.Equal(OperationVeredict.Failure, veredict);
        Assert.Equal(CoreTools.Translate("The package broker cannot accept this request"), title);
        Assert.Contains("git;7zip", message);
        Assert.DoesNotContain("/v1/package-operations/execute", transport.RequestedPaths);
    }

    [Fact]
    public async Task BrokeredInstallSendsTheResolvedPackageVersion()
    {
        var transport = new ScriptedBrokerTransport();

        await RunBrokeredOperation(transport);

        Assert.NotNull(transport.LastExecuteRequestBody);
        var request = BrokerSerializer.Deserialize<BrokerApiPackageRequest>(transport.LastExecuteRequestBody!);
        Assert.Equal("1.0.0", request!.Package.Version);
    }

    /// <summary>
    /// Runs an install operation against a broker whose transport simulates an outage and
    /// asserts the policy-enforcement contract: the operation fails with the
    /// broker-unavailable metadata, raises <see cref="PackageOperation.BrokerUnavailable"/>,
    /// and never falls back to local process execution.
    /// </summary>
    private static async Task AssertBrokerUnavailableFailure(
        FakeBrokerTransport transport,
        string expectedTitle = "Agent broker unavailable")
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
        string? notifiedTitle = null;
        EventHandler<BrokerFailureDescription> onBrokerUnavailable = (_, failure) =>
        {
            notifiedTitle = failure.Title;
            notifiedMessage = failure.Message;
        };
        PackageOperation.BrokerUnavailable += onBrokerUnavailable;
        PackageOperation.BrokerTransportFactory = () => transport;
        Settings.Set(Settings.K.UseAgentBroker, true);
        try
        {
            using var operation = new BrokerProbingInstallPackageOperation(package, new InstallOptions());
            var veredict = await operation.InvokePerformOperationForTests();

            Assert.Equal(OperationVeredict.Failure, veredict);
            Assert.Equal(
                CoreTools.Translate(expectedTitle),
                operation.Metadata.FailureTitle);
            Assert.False(string.IsNullOrWhiteSpace(operation.Metadata.FailureMessage));
            Assert.Equal(operation.Metadata.FailureTitle, notifiedTitle);
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
        TimeSpan? operationTimeout = null,
        string? packageId = null)
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
        var package = new PackageBuilder().WithManager(manager).WithId(packageId ?? "Contoso.Test").Build();
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
    public async Task BrokeredOperationAppliesTheManagerOperationOverrides()
    {
        var transport = new ScriptedBrokerTransport
        {
            StatusAfterCancel = BrokerApiOperationStatus.Completed,
            CompletedExitCode = 0,
        };

        var result = await RunBrokeredOperationWithOutput(
            transport,
            configureOperationHelper: helper => helper.OperationOverridesAction =
                (_, options, _) => options.Architecture = Architecture.x64);

        Assert.Equal(OperationVeredict.Success, result.Veredict);
        Assert.Equal(
            BrokerApiArchitecture.X64,
            DeserializeExecuteRequest(transport).Package.Architecture);
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
        var request = BrokerSerializer.Deserialize<BrokerApiPackageRequest>(transport.LastExecuteRequestBody);
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
    /// real Api types via <see cref="BrokerSerializer"/>. Status responses are drained from
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

        /// <summary>When set, the capabilities endpoint answers with this structured error.</summary>
        public (int StatusCode, BrokerApiErrorCode Code)? CapabilitiesError { get; set; }

        /// <summary>When set, the execute endpoint reports this policy decision without an operation.</summary>
        public BrokerApiDecisionInfo? Denial { get; set; }

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
                "/v1/health" => Json(BrokerSerializer.Serialize(BuildHealthResponse())),
                "/v1/capabilities" when CapabilitiesError is { } error => Task.FromResult(
                    new BrokerTransportResponse
                    {
                        StatusCode = error.StatusCode,
                        Body = BrokerSerializer.Serialize(new BrokerApiErrorResponse
                        {
                            Code = error.Code,
                            Message = "capabilities refused",
                        }),
                    }),
                "/v1/capabilities" => Json(BrokerSerializer.Serialize(BuildCapabilities())),
                "/v1/package-operations/execute" => Json(BrokerSerializer.Serialize(BuildExecutionResponse())),
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

            return Json(BrokerSerializer.Serialize(new BrokerApiStatusResponse
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
            return Json(BrokerSerializer.Serialize(new BrokerApiCancelResponse
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
                    Architectures = [BrokerApiArchitecture.X86, BrokerApiArchitecture.X64],
                    SupportsCustomParameters = true,
                    SupportsCustomInstallLocation = true,
                    SupportsCaptureOutput = true,
                },
            ],
        };

        private BrokerApiExecutionResponse BuildExecutionResponse()
        {
            if (Denial is { } denial)
            {
                return new()
                {
                    ResponseKind = BrokerApiConstants.ExecutionResponseKind,
                    ResponseVersion = BrokerApiConstants.Version,
                    Decision = denial,
                };
            }

            return new()
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

    private sealed class InspectableUpdatePackageOperation : UpdatePackageOperation
    {
        public InspectableUpdatePackageOperation(IPackage package, InstallOptions options)
            : base(package, options, IgnoreParallelInstalls: true) { }

        public ProcessStartInfo PrepareProcessStartInfoForTests()
        {
            process.StartInfo.UseShellExecute = false;
            PrepareProcessStartInfo();
            return process.StartInfo;
        }

        public IReadOnlyList<InnerOperation> AttemptPreOperationsForTests() =>
            GetAttemptPreOperations();

        public void ApplyRetryActionForTests(string retryMode) => ApplyRetryAction(retryMode);

        public OperationVeredict GetProcessVeredictForTests(int returnCode, List<string> output) =>
            GetProcessVeredict(returnCode, output).GetAwaiter().GetResult();
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

    [Fact]
    public async Task UnmetPreconditionIsReportedWithoutTheInternalErrorStackTrace()
    {
        using var operation = new ThrowingStubOperation(
            new OperationPreconditionException("Run UniGetUI as a regular user and try again.")
        );

        await operation.MainThread();

        Assert.Equal(OperationStatus.Failed, operation.Status);
        var lines = operation.GetOutput().Select(line => line.Item1).ToList();
        Assert.Contains("Run UniGetUI as a regular user and try again.", lines);
        Assert.DoesNotContain("An internal error occurred:", lines);
        Assert.DoesNotContain(lines, line => line.Contains("OperationPreconditionException"));
    }

    [Fact]
    public async Task AnUnexpectedExceptionStillReportsItsStackTrace()
    {
        using var operation = new ThrowingStubOperation(
            new InvalidOperationException("some unexpected failure")
        );

        await operation.MainThread();

        Assert.Equal(OperationStatus.Failed, operation.Status);
        var lines = operation.GetOutput().Select(line => line.Item1).ToList();
        Assert.Contains(lines, line => line.Contains("InvalidOperationException"));
        Assert.Contains(lines, line => line.TrimStart().StartsWith("at ", StringComparison.Ordinal));
    }

    private sealed class ThrowingStubOperation : AbstractOperation
    {
        private readonly Exception _exception;

        public ThrowingStubOperation(Exception exception)
            : base(queue_enabled: false)
        {
            _exception = exception;
            Metadata.Status = "Throwing stub status";
            Metadata.Title = "Throwing stub title";
            Metadata.OperationInformation = "Throwing stub info";
            Metadata.SuccessTitle = "Throwing stub success";
            Metadata.SuccessMessage = "Throwing stub success";
            Metadata.FailureTitle = "Throwing stub failure";
            Metadata.FailureMessage = "Throwing stub failure";
        }

        protected override void ApplyRetryAction(string retryMode) { }

        protected override Task<OperationVeredict> PerformOperation() => throw _exception;

        public override Task<Uri> GetOperationIcon() => Task.FromResult(new Uri("about:blank"));
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
