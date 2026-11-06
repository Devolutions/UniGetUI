using System.Reflection;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Consolonia.Core.Drawing.PixelBufferImplementation;
using Consolonia.NUnit;
using NUnit.Framework;
using NAssert = NUnit.Framework.Assert;

namespace UniGetUI.Tui.Tests;

[TestFixture, NonParallelizable]
internal sealed class TuiDispatcherLifecycleTests : TuiE2ETestBase
{
    private Dispatcher? _workerDispatcher;
    private Dispatcher? _workerObservedUiDispatcher;

    protected override AppBuilder CreateAppBuilder()
    {
        (_workerDispatcher, _workerObservedUiDispatcher) = Task.Run(() =>
        {
            var workerObject = new AvaloniaObject();
            return (workerObject.Dispatcher, Dispatcher.UIThread);
        }).GetAwaiter().GetResult();

        return base.CreateAppBuilder();
    }

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    public async Task Startup_BindsUiDispatcherBeforeBackgroundAvaloniaObjects(int iteration)
    {
        await OnUi(() =>
        {
            NAssert.That(Window.CheckAccess(), Is.True);
            NAssert.That(Window.Dispatcher, Is.SameAs(Dispatcher.UIThread));
            NAssert.That(_workerObservedUiDispatcher, Is.SameAs(Window.Dispatcher));
            NAssert.That(_workerDispatcher, Is.Not.SameAs(Window.Dispatcher));
            NAssert.That(Window.IsVisible, Is.True, $"Startup {iteration} should show the window");
        });
    }
}

[TestFixture, NonParallelizable]
internal sealed class TuiAtomicDispatcherTests
{
    [Test]
    public async Task ResetAndBind_BlocksBackgroundDispatcherUntilUiThreadClaimsOwnership()
    {
        object dispatcherLock = typeof(Dispatcher).GetField("s_globalLock", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null)
            ?? throw new InvalidOperationException("Could not find Avalonia's dispatcher lock.");
        var attempted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<Dispatcher>? backgroundDispatcher = null;
        var console = new UnitTestConsole(new PixelBufferSize(160, 50));

        void AfterReset()
        {
            backgroundDispatcher = Task.Run(() =>
            {
                bool lockAvailable = Monitor.TryEnter(dispatcherLock);
                if (lockAvailable) Monitor.Exit(dispatcherLock);
                attempted.SetResult(lockAvailable);
                return new AvaloniaObject().Dispatcher;
            });
            attempted.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        }

        var host = new TuiTestHost(console, () => TuiTestHost.CreateAppBuilder(console), AfterReset);
        try
        {
            await host.StartAsync();
            Dispatcher windowDispatcher = await Dispatcher.UIThread.InvokeAsync(() =>
                ((IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!).MainWindow!.Dispatcher);
            NAssert.That(await attempted.Task, Is.False);
            NAssert.That(windowDispatcher, Is.SameAs(Dispatcher.UIThread));
            NAssert.That(await backgroundDispatcher!.WaitAsync(TimeSpan.FromSeconds(10)), Is.Not.SameAs(windowDispatcher));
        }
        finally
        {
            await host.StopAsync();
        }
    }
}

[TestFixture, NonParallelizable]
internal sealed class TuiTestHostFailureTests
{
    [TestCase(0, TestName = "StartupFailure_BuilderCreation_ReportsErrorAndAllowsRestart")]
    [TestCase(1, TestName = "StartupFailure_AppSetup_ReportsErrorAndAllowsRestart")]
    [TestCase(2, TestName = "StartupFailure_AfterWindowCreation_ReportsErrorAndAllowsRestart")]
    public async Task StartupFailure_IsReportedAndAllowsRestart(int failurePoint)
    {
        const string message = "Simulated TUI startup failure";
        var console = new UnitTestConsole(new PixelBufferSize(160, 50));
        bool windowCreated = false;
        AppBuilder CreateFailingBuilder()
        {
            if (failurePoint == 0) throw new InvalidOperationException(message);
            if (failurePoint == 1)
                return TuiTestHost.CreateAppBuilder(console).AfterSetup(_ => throw new InvalidOperationException(message));
            return TuiTestHost.CreateAppBuilder(console).AfterSetup(_ =>
            {
                Application.Current!.DataTemplates.CollectionChanged += (_, _) =>
                {
                    windowCreated = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow is not null;
                    throw new InvalidOperationException(message);
                };
            });
        }

        var failedHost = new TuiTestHost(console, CreateFailingBuilder);
        try
        {
            InvalidOperationException? error = NAssert.ThrowsAsync<InvalidOperationException>(failedHost.StartAsync);
            NAssert.That(error!.Message, Is.EqualTo(message));
            if (failurePoint == 2) NAssert.That(windowCreated, Is.True);
        }
        finally
        {
            await failedHost.StopAsync();
        }

        var nextConsole = new UnitTestConsole(new PixelBufferSize(160, 50));
        var nextHost = new TuiTestHost(nextConsole, () => TuiTestHost.CreateAppBuilder(nextConsole));
        try
        {
            await nextHost.StartAsync();
            bool visible = await Dispatcher.UIThread.InvokeAsync(() =>
                ((IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!).MainWindow!.IsVisible);
            NAssert.That(visible, Is.True);
        }
        finally
        {
            await nextHost.StopAsync();
        }
    }
}
