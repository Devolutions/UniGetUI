using Devolutions.Now.Policy.Client;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.PackageEngine.Classes.Manager.BaseProviders;
using UniGetUI.PackageEngine.Classes.Manager.ManagerHelpers;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Interfaces.ManagerProviders;
using UniGetUI.PackageEngine.Managers.CargoManager;
using UniGetUI.PackageEngine.Operations;
using UniGetUI.PackageEngine.Serializable;
using UniGetUI.PackageEngine.Tests.Infrastructure.Builders;

namespace UniGetUI.PackageEngine.Tests;

[Collection(nameof(OperationOrchestrationTestCollection))]
public sealed class CargoManagedOperationRoutingTests
{
    [Fact]
    public async Task LocalManagedSuccessReportsProgressWithoutStartingCargo()
    {
        bool original = Settings.Get(Settings.K.UseAgentBroker);
        Settings.Set(Settings.K.UseAgentBroker, false);
        try
        {
            var manager = new ManagedCargo();
            manager.Helper.Perform = (_, progress, _) =>
            {
                progress(OperationProgress.ForStage(OperationProgressStage.Installing));
                return Task.FromResult<OperationVeredict?>(OperationVeredict.Success);
            };
            using var operation = CreateOperation(manager, "first");
            Assert.Equal(OperationVeredict.Success, await operation.Run());
            Assert.Equal(["first"], manager.Helper.Calls);
            Assert.Equal(OperationProgressStage.Installing, operation.CurrentProgress.Stage);
            Assert.False(operation.ProcessWasStarted);
        }
        finally
        {
            Settings.Set(Settings.K.UseAgentBroker, original);
        }
    }

    [Fact]
    public async Task ManagedOperationsRunInsideTheExistingCargoSerializationGate()
    {
        bool original = Settings.Get(Settings.K.UseAgentBroker);
        Settings.Set(Settings.K.UseAgentBroker, false);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var manager = new ManagedCargo();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            manager.Helper.Perform = async (package, _, _) =>
            {
                if (package.Id == "first")
                {
                    entered.TrySetResult();
                    await release.Task;
                }
                return OperationVeredict.Success;
            };
            using var first = CreateOperation(manager, "first");
            using var second = CreateOperation(manager, "second");
            Task<OperationVeredict> firstRun = first.Run();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Task<OperationVeredict> secondRun = second.Run();
            Assert.True(second.IsWaitingForManager);
            Assert.Equal(["first"], manager.Helper.Calls);
            release.TrySetResult();
            Assert.Equal([OperationVeredict.Success, OperationVeredict.Success],
                await Task.WhenAll(firstRun, secondRun).WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(["first", "second"], manager.Helper.Calls);
            Assert.False(first.ProcessWasStarted);
            Assert.False(second.ProcessWasStarted);
        }
        finally
        {
            release.TrySetResult();
            Settings.Set(Settings.K.UseAgentBroker, original);
        }
    }

    [Fact]
    public async Task BrokerUnavailableNeverCallsTheLocalManagedInstaller()
    {
        bool original = Settings.Get(Settings.K.UseAgentBroker);
        var originalFactory = PackageOperation.BrokerTransportFactory;
        var transport = new UnavailableTransport();
        Settings.Set(Settings.K.UseAgentBroker, true);
        PackageOperation.BrokerTransportFactory = () => transport;
        try
        {
            var manager = new ManagedCargo();
            using var operation = CreateOperation(manager, "fixture-tool");
            Assert.Equal(OperationVeredict.Failure, await operation.Run());
            Assert.Empty(manager.Helper.Calls);
            Assert.False(operation.ProcessWasStarted);
            Assert.Contains("/v1/health", transport.Paths);
            Assert.False(string.IsNullOrWhiteSpace(operation.Metadata.FailureMessage));
        }
        finally
        {
            PackageOperation.BrokerTransportFactory = originalFactory;
            Settings.Set(Settings.K.UseAgentBroker, original);
        }
    }

    private static ProbeOperation CreateOperation(ManagedCargo manager, string id) =>
        new(new PackageBuilder().WithManager(manager).WithId(id).WithVersion("1.2.3").Build());

    private sealed class ProbeOperation(IPackage package) : InstallPackageOperation(package, new InstallOptions(), true)
    {
        internal Task<OperationVeredict> Run() => PerformOperation();
        internal bool ProcessWasStarted => process.StartInfo.FileName != ""
            && ProcessStarted();
        private bool ProcessStarted()
        {
            try { return process.Id != 0; }
            catch (InvalidOperationException) { return false; }
        }
    }

    private sealed class ManagedCargo : Cargo
    {
        internal ProbeHelper Helper { get; }
        internal ManagedCargo()
        {
            Helper = new ProbeHelper(this);
            OperationHelper = Helper;
            Status = new ManagerStatus { Found = true, ExecutablePath = "cargo-must-not-start", ExecutableCallArgs = "" };
        }
    }

    private sealed class ProbeHelper(Cargo manager) : BasePkgOperationHelper(manager), IManagedPackageOperationHelper
    {
        internal List<string> Calls { get; } = [];
        internal Func<IPackage, Action<OperationProgress>, CancellationToken, Task<OperationVeredict?>> Perform { get; set; } =
            (_, _, _) => Task.FromResult<OperationVeredict?>(OperationVeredict.Success);
        public Task<OperationVeredict?> TryPerformAsync(IPackage package, InstallOptions options, OperationType operation,
            Action<string> log, Action<OperationProgress> progress, CancellationToken cancellationToken)
        {
            Calls.Add(package.Id);
            return Perform(package, progress, cancellationToken);
        }
        protected override IReadOnlyList<string> _getOperationParameters(IPackage package, InstallOptions options, OperationType operation) =>
            ["install", package.Id];
        protected override OperationVeredict _getOperationResult(IPackage package, OperationType operation,
            IReadOnlyList<string> processOutput, int returnCode) => OperationVeredict.Failure;
    }

    private sealed class UnavailableTransport : IBrokerTransport
    {
        internal List<string> Paths { get; } = [];
        public Devolutions.Now.Policy.Api.Transport Kind => Devolutions.Now.Policy.Api.Transport.HttpNamedPipe;
        public Task<BrokerTransportResponse> Send(BrokerTransportRequest request, CancellationToken cancellationToken = default)
        {
            Paths.Add(request.Path);
            throw new BrokerClientException(BrokerClientErrorKind.BrokerUnavailable, "Simulated outage", request.Path);
        }
        public void Dispose() { }
    }
}
