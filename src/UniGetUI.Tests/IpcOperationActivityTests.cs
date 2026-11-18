using UniGetUI.Interface;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageOperations;

namespace UniGetUI.Tests;

public class IpcOperationActivityTests
{
    // Efficiency mode relies on this to notice operations started over IPC, which never reach
    // the shell's own operation list.
    [Fact]
    public void TrackedOperationsReportActivityUntilTheyFinish()
    {
        using var operation = new StubOperation { Status = OperationStatus.InQueue };
        int notifications = 0;
        void OnActivityChanged(object? sender, EventArgs e) => Interlocked.Increment(ref notifications);

        IpcOperationApi.ActivityChanged += OnActivityChanged;
        try
        {
            IpcOperationApi.Track(operation);
            Assert.True(IpcOperationApi.HasActiveOperations());
            Assert.True(notifications >= 1);

            int beforeRunning = notifications;
            operation.Status = OperationStatus.Running;
            Assert.True(IpcOperationApi.HasActiveOperations());
            Assert.True(notifications > beforeRunning);

            int beforeFinished = notifications;
            operation.Status = OperationStatus.Succeeded;
            Assert.True(notifications > beforeFinished);
        }
        finally
        {
            IpcOperationApi.ActivityChanged -= OnActivityChanged;
            operation.Status = OperationStatus.Succeeded;
            IpcOperationApi.ForgetTracking(operation.Metadata.Identifier);
        }
    }

    // Bundle installs and source changes run to completion inside one IPC call and are never
    // tracked as operations, so they report their activity through a scope instead.
    [Fact]
    public void AnActivityScopeCountsAsActivityUntilItIsDisposed()
    {
        int notifications = 0;
        void OnActivityChanged(object? sender, EventArgs e) => Interlocked.Increment(ref notifications);

        IpcOperationApi.ActivityChanged += OnActivityChanged;
        try
        {
            IDisposable outer = IpcOperationApi.BeginActivity();
            IDisposable inner = IpcOperationApi.BeginActivity();
            Assert.True(IpcOperationApi.HasActiveOperations());
            Assert.True(notifications >= 2);

            int beforeInner = notifications;
            inner.Dispose();
            inner.Dispose();
            Assert.True(IpcOperationApi.HasActiveOperations());
            Assert.True(notifications > beforeInner);

            int beforeOuter = notifications;
            outer.Dispose();
            Assert.True(notifications > beforeOuter);
        }
        finally
        {
            IpcOperationApi.ActivityChanged -= OnActivityChanged;
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

        protected override Task<OperationVeredict> PerformOperation() =>
            Task.FromResult(OperationVeredict.Success);

        public override Task<Uri> GetOperationIcon() => Task.FromResult(new Uri("about:blank"));
    }
}
