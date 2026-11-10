using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.Views.DialogPages;

namespace UniGetUI.Tests;

public sealed class ShutdownCountdownTests
{
    private readonly HeadlessUnitTestSession _session =
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(ShutdownCountdownTests).Assembly);

    private Task DispatchAsync(Func<Task> action) => _session.Dispatch<bool>(async () =>
    {
        await action();
        return true;
    }, CancellationToken.None);

    private sealed class FakeRunner
    {
        public readonly List<string> Calls = [];
        public bool Result = true;

        public Task<bool> Run(string arguments, string _)
        {
            Calls.Add(arguments);
            return Task.FromResult(Result);
        }
    }

    private sealed class FakePresenter
    {
        public ImmersiveConfirmationDialog? Shown;
        public TimeSpan HoldAfterClose = TimeSpan.Zero;

        public async Task Present(Window owner, ImmersiveDialog dialog)
        {
            Shown = Assert.IsType<ImmersiveConfirmationDialog>(dialog);
            var closed = new TaskCompletionSource();
            dialog.CloseRequested += (_, _) => closed.TrySetResult();
            owner.Content = dialog;
            dialog.NotifyOpened();
            await closed.Task;
            if (HoldAfterClose > TimeSpan.Zero)
                await Task.Delay(HoldAfterClose);
            if (ReferenceEquals(owner.Content, dialog))
                owner.Content = null;
        }
    }

    private static async Task<T> WithFakeShutdown<T>(
        FakeRunner runner,
        FakePresenter presenter,
        int seconds,
        Func<Task<T>> body)
    {
        int oldSeconds = SystemRestartService.CountdownSeconds;
        TimeSpan oldInterval = SystemRestartService.CountdownTickInterval;
        var oldRunner = SystemRestartService.ShutdownCommandRunner;
        var oldPresenter = SystemRestartService.DialogPresenter;
        SystemRestartService.CountdownSeconds = seconds;
        SystemRestartService.CountdownTickInterval = TimeSpan.FromMilliseconds(10);
        SystemRestartService.ShutdownCommandRunner = runner.Run;
        SystemRestartService.DialogPresenter = presenter.Present;
        try
        {
            return await body();
        }
        finally
        {
            SystemRestartService.CountdownSeconds = oldSeconds;
            SystemRestartService.CountdownTickInterval = oldInterval;
            SystemRestartService.ShutdownCommandRunner = oldRunner;
            SystemRestartService.DialogPresenter = oldPresenter;
        }
    }

    private static async Task<ImmersiveConfirmationDialog> WaitForDialogAsync(FakePresenter presenter)
    {
        for (int i = 0; i < 200 && presenter.Shown is null; i++)
            await Task.Delay(10);

        return presenter.Shown ?? throw new TimeoutException("The countdown dialog never opened");
    }

    [Fact]
    public Task Countdown_ShowsTheRemainingSecondsAndAcceptsAtZero() => DispatchAsync(() =>
    {
        var countdown = new ShutdownCountdown(restart: false, seconds: 3);

        Assert.Contains("shut down in 3 seconds", countdown.MessageText);
        Assert.False(countdown.Tick());
        Assert.Contains("shut down in 2 seconds", countdown.MessageText);
        Assert.False(countdown.Tick());
        Assert.Null(countdown.Dialog.Result);
        Assert.True(countdown.Tick());
        Assert.Equal(0, countdown.Remaining);
        Assert.True(countdown.Dialog.Result);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Countdown_FocusesCancelSoAStrayKeyDoesNotShutDown() => DispatchAsync(() =>
    {
        var shutdown = new ShutdownCountdown(restart: false, seconds: 60);
        var restart = new ShutdownCountdown(restart: true, seconds: 60);

        Assert.False(shutdown.Dialog.FocusPrimaryButton);
        Assert.Equal("Shut down now", shutdown.Dialog.FindControl<Button>("PrimaryButton")?.Content);
        Assert.Equal("Cancel", shutdown.Dialog.FindControl<Button>("SecondaryButton")?.Content);
        Assert.Equal("Restart now", restart.Dialog.FindControl<Button>("PrimaryButton")?.Content);
        Assert.Contains("restart in 60 seconds", restart.MessageText);
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(false, "/s /t 0")]
    [InlineData(true, "/r /t 0")]
    public Task CountdownReachingZero_RunsTheShutdownCommand(bool restart, string expectedArguments) =>
        DispatchAsync(async () =>
        {
            var runner = new FakeRunner();
            var presenter = new FakePresenter();
            var owner = new Window();
            owner.Show();
            try
            {
                var outcome = await WithFakeShutdown(runner, presenter, seconds: 2, () =>
                    SystemRestartService.CountdownAndShutDownAsync(owner, restart, () => false));

                Assert.Equal(ShutdownCountdownOutcome.Requested, outcome);
                Assert.Equal([expectedArguments], runner.Calls);
            }
            finally
            {
                owner.Close();
            }
        });

    [Fact]
    public Task PressingCancel_NeverRunsTheShutdownCommand() => DispatchAsync(async () =>
    {
        var runner = new FakeRunner();
        var presenter = new FakePresenter();
        var owner = new Window();
        owner.Show();
        try
        {
            var outcome = await WithFakeShutdown(runner, presenter, seconds: 60, async () =>
            {
                var pending = SystemRestartService.CountdownAndShutDownAsync(owner, false, () => false);
                var dialog = await WaitForDialogAsync(presenter);
                dialog.FindControl<Button>("SecondaryButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                return await pending;
            });

            Assert.Equal(ShutdownCountdownOutcome.Canceled, outcome);
            Assert.Empty(runner.Calls);
        }
        finally
        {
            owner.Close();
        }
    });

    [Fact]
    public Task PressingShutDownNow_SkipsTheRestOfTheCountdown() => DispatchAsync(async () =>
    {
        var runner = new FakeRunner();
        var presenter = new FakePresenter();
        var owner = new Window();
        owner.Show();
        try
        {
            var outcome = await WithFakeShutdown(runner, presenter, seconds: 60, async () =>
            {
                var pending = SystemRestartService.CountdownAndShutDownAsync(owner, false, () => false);
                var dialog = await WaitForDialogAsync(presenter);
                dialog.FindControl<Button>("PrimaryButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                return await pending;
            });

            Assert.Equal(ShutdownCountdownOutcome.Requested, outcome);
            Assert.Equal(["/s /t 0"], runner.Calls);
        }
        finally
        {
            owner.Close();
        }
    });

    [Fact]
    public Task OperationsStartedDuringTheCountdown_DeferTheShutdown() => DispatchAsync(async () =>
    {
        var runner = new FakeRunner();
        var presenter = new FakePresenter();
        var owner = new Window();
        owner.Show();
        try
        {
            var outcome = await WithFakeShutdown(runner, presenter, seconds: 1, () =>
                SystemRestartService.CountdownAndShutDownAsync(owner, false, () => true));

            Assert.Equal(ShutdownCountdownOutcome.Deferred, outcome);
            Assert.Empty(runner.Calls);
        }
        finally
        {
            owner.Close();
        }
    });

    [Fact]
    public Task ARefusedShutdownCommand_IsReportedAsFailed() => DispatchAsync(async () =>
    {
        var runner = new FakeRunner { Result = false };
        var presenter = new FakePresenter();
        var owner = new Window();
        owner.Show();
        try
        {
            var outcome = await WithFakeShutdown(runner, presenter, seconds: 1, () =>
                SystemRestartService.CountdownAndShutDownAsync(owner, false, () => false));

            Assert.Equal(ShutdownCountdownOutcome.Failed, outcome);
            Assert.Equal(["/s /t 0"], runner.Calls);
        }
        finally
        {
            owner.Close();
        }
    });

    [Fact]
    public Task ATickAfterCancel_CannotTurnItIntoAShutdown() => DispatchAsync(() =>
    {
        var countdown = new ShutdownCountdown(restart: false, seconds: 1);

        countdown.Dialog.FindControl<Button>("SecondaryButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(countdown.Tick());

        Assert.False(countdown.Dialog.Result);
        return Task.CompletedTask;
    });

    [Fact]
    public Task CancelOrEsc_StopsTheTimerBeforeTheCloseAnimationEnds() => DispatchAsync(async () =>
    {
        var runner = new FakeRunner();
        var presenter = new FakePresenter { HoldAfterClose = TimeSpan.FromMilliseconds(1500) };
        var owner = new Window();
        owner.Show();
        try
        {
            var outcome = await WithFakeShutdown(runner, presenter, seconds: 3, async () =>
            {
                var pending = SystemRestartService.CountdownAndShutDownAsync(owner, false, () => false);
                var dialog = await WaitForDialogAsync(presenter);
                dialog.FindControl<Button>("SecondaryButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                return await pending;
            });

            Assert.Equal(ShutdownCountdownOutcome.Canceled, outcome);
            Assert.Empty(runner.Calls);
        }
        finally
        {
            owner.Close();
        }
    });

    [Fact]
    public Task NewWorkDuringTheCountdown_ClosesItWithoutShuttingDown() => DispatchAsync(async () =>
    {
        var runner = new FakeRunner();
        var presenter = new FakePresenter();
        using var newWork = new CancellationTokenSource();
        var owner = new Window();
        owner.Show();
        try
        {
            var outcome = await WithFakeShutdown(runner, presenter, seconds: 60, async () =>
            {
                var pending = SystemRestartService.CountdownAndShutDownAsync(owner, false, () => false, newWork.Token);
                await WaitForDialogAsync(presenter);
                newWork.Cancel();
                return await pending;
            });

            Assert.Equal(ShutdownCountdownOutcome.Deferred, outcome);
            Assert.Empty(runner.Calls);
        }
        finally
        {
            owner.Close();
        }
    });

    private static string MessageOf(ImmersiveConfirmationDialog dialog)
    {
        var body = Assert.IsType<StackPanel>(dialog.FindControl<ContentControl>("BodyPresenter")!.Content);
        return Assert.IsType<TextBlock>(body.Children[0]).Text ?? "";
    }

    [Fact]
    public Task CancelPressedBeforeNewWorkStarts_StaysCanceled() => DispatchAsync(async () =>
    {
        var runner = new FakeRunner();
        var presenter = new FakePresenter { HoldAfterClose = TimeSpan.FromMilliseconds(500) };
        using var newWork = new CancellationTokenSource();
        var owner = new Window();
        owner.Show();
        try
        {
            var outcome = await WithFakeShutdown(runner, presenter, seconds: 60, async () =>
            {
                var pending = SystemRestartService.CountdownAndShutDownAsync(owner, false, () => false, newWork.Token);
                var dialog = await WaitForDialogAsync(presenter);
                dialog.FindControl<Button>("SecondaryButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                newWork.Cancel();
                return await pending;
            });

            Assert.Equal(ShutdownCountdownOutcome.Canceled, outcome);
            Assert.Empty(runner.Calls);
        }
        finally
        {
            owner.Close();
        }
    });

    [Fact]
    public Task ACoveredCountdown_PausesAndStartsOverWhenItIsShownAgain() => DispatchAsync(async () =>
    {
        var runner = new FakeRunner();
        var presenter = new FakePresenter();
        var owner = new Window();
        owner.Show();
        try
        {
            var outcome = await WithFakeShutdown(runner, presenter, seconds: 3, async () =>
            {
                var pending = SystemRestartService.CountdownAndShutDownAsync(owner, false, () => false);
                var dialog = await WaitForDialogAsync(presenter);

                owner.Content = new TextBlock { Text = "Manage shortcuts" };
                await Task.Delay(1500);
                Assert.False(pending.IsCompleted);
                Assert.Empty(runner.Calls);

                owner.Content = dialog;
                Assert.Contains("shut down in 3 seconds", MessageOf(dialog));
                dialog.FindControl<Button>("SecondaryButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                return await pending;
            });

            Assert.Equal(ShutdownCountdownOutcome.Canceled, outcome);
            Assert.Empty(runner.Calls);
        }
        finally
        {
            owner.Close();
        }
    });

    private sealed class Operations
    {
        public readonly List<UniGetUI.PackageEngine.Enums.OperationStatus> Batch = [];
        public int Countdowns;
        public readonly ShutdownWhenFinishedController Controller;

        public Operations()
        {
            Controller = new ShutdownWhenFinishedController(
                true,
                () => Batch.Select(status => new BatchOperationState(status)).ToList(),
                () => false,
                (_, _, _) =>
                {
                    Countdowns++;
                    return new TaskCompletionSource<ShutdownCountdownOutcome>().Task;
                },
                () => { });
        }
    }

    private static (Window Window, MenuItem Item) ShowBoundMenuItem(ShutdownWhenFinishedController controller)
    {
        var item = new MenuItem
        {
            Header = "Shut down when finished",
            ToggleType = MenuItemToggleType.CheckBox,
            DataContext = controller,
        };
        item.Bind(MenuItem.IsCheckedProperty, new Binding(nameof(ShutdownWhenFinishedController.IsArmed)) { Mode = BindingMode.TwoWay });
        var window = new Window { Width = 400, Height = 200, Content = new Menu { Items = { item } } };
        window.Show();
        return (window, item);
    }

    private static void Click(Window window, MenuItem item)
    {
        Dispatcher.UIThread.RunJobs();
        Point center = item.TranslatePoint(new Point(item.Bounds.Width / 2, item.Bounds.Height / 2), window)
            ?? throw new InvalidOperationException("The menu item is not laid out");
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    [Fact]
    public Task ClickingTheMenuItemWhileOperationsRun_ArmsAndDisarmsTheShutdown() => DispatchAsync(() =>
    {
        var ops = new Operations();
        ops.Batch.Add(UniGetUI.PackageEngine.Enums.OperationStatus.Running);
        var (window, item) = ShowBoundMenuItem(ops.Controller);
        try
        {
            Click(window, item);
            Assert.True(ops.Controller.IsArmed);
            Assert.True(item.IsChecked);

            Click(window, item);
            Assert.False(ops.Controller.IsArmed);
            Assert.False(item.IsChecked);
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task FinishingTheBatch_UnticksTheMenuItem() => DispatchAsync(() =>
    {
        var ops = new Operations();
        ops.Batch.Add(UniGetUI.PackageEngine.Enums.OperationStatus.Running);
        var (window, item) = ShowBoundMenuItem(ops.Controller);
        try
        {
            Click(window, item);
            Assert.True(item.IsChecked);

            ops.Batch[0] = UniGetUI.PackageEngine.Enums.OperationStatus.Succeeded;
            ops.Controller.OnOperationsChanged();
            Dispatcher.UIThread.RunJobs();

            Assert.False(ops.Controller.IsArmed);
            Assert.False(item.IsChecked);
            Assert.Equal(1, ops.Countdowns);
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task ClickingTheMenuItemAfterEverythingFinished_StartsTheCountdownAndUnticks() => DispatchAsync(() =>
    {
        var ops = new Operations();
        ops.Batch.Add(UniGetUI.PackageEngine.Enums.OperationStatus.Succeeded);
        var (window, item) = ShowBoundMenuItem(ops.Controller);
        try
        {
            Click(window, item);

            Assert.Equal(1, ops.Countdowns);
            Assert.False(ops.Controller.IsArmed);
            Assert.False(item.IsChecked);
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });
}
