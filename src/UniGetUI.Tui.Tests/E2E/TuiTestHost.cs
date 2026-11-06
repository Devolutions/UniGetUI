using System.Collections.Concurrent;
using System.Reflection;
using Avalonia;
using Avalonia.Platform;
using Avalonia.Threading;
using Consolonia;
using Consolonia.Core.Drawing.PixelBufferImplementation;
using Consolonia.Core.Infrastructure;
using Consolonia.Fonts;
using Consolonia.NUnit;

namespace UniGetUI.Tui.Tests;

internal sealed class TuiTestHost(UnitTestConsole console, Func<AppBuilder> createAppBuilder, Action? afterDispatcherReset = null)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    private static readonly BlockingCollection<Action> UiWork = new();
    private static readonly object DispatcherLock =
        typeof(Dispatcher).GetField("s_globalLock", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null)
        ?? throw new InvalidOperationException("Could not find Avalonia's dispatcher lock.");
    private static readonly MethodInfo ResetDispatcher =
        typeof(Dispatcher).GetMethod("ResetForUnitTests", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Could not find Avalonia's test dispatcher reset.");
    private static readonly MethodInfo EnterLocatorScope =
        typeof(AvaloniaLocator).GetMethod("EnterScope", BindingFlags.Static | BindingFlags.Public)
        ?? throw new InvalidOperationException("Could not find Avalonia's test service scope.");
    private static readonly FieldInfo AppBuilderSetup =
        typeof(AppBuilder).GetField("s_setupWasAlreadyCalled", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Could not find Avalonia's application setup flag.");

    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Dispatcher? _dispatcher;
    private ConsoloniaLifetime? _lifetime;

    static TuiTestHost()
    {
        var thread = new Thread(() =>
        {
            foreach (Action work in UiWork.GetConsumingEnumerable()) work();
        })
        {
            IsBackground = true,
            Name = "UniGetUI TUI test UI thread",
        };
        thread.Start();
    }

    public static AppBuilder CreateAppBuilder(UnitTestConsole console)
        => AppBuilder.Configure<App>()
            .UseConsole(console)
            .UseConsolonia()
            .UseConsoleColorMode(new RgbConsoleColorMode())
            .With<IPlatformSettings>(new ConsoloniaPlatformSettings { UnsafeInput = false, UnsafeRendering = false })
            .LogToException()
            .WithConsoleFonts();

    public async Task StartAsync()
    {
        UiWork.Add(Run);
        await _started.Task.WaitAsync(Timeout);

        Task rendered = console.WaitRendered();
        Task first = await Task.WhenAny(rendered, _stopped.Task).WaitAsync(Timeout);
        if (first == _stopped.Task)
        {
            await _stopped.Task;
            throw new InvalidOperationException("The TUI test host stopped before its first render.");
        }

        await rendered;
        if (_stopped.Task.IsCompleted)
        {
            await _stopped.Task;
            throw new InvalidOperationException("The TUI test host stopped during startup.");
        }
    }

    public async Task StopAsync()
    {
        if (_started.Task.IsCompletedSuccessfully && !_stopped.Task.IsCompleted)
        {
            Task shutdown = _dispatcher!.InvokeAsync(() => _lifetime!.Shutdown()).GetTask();
            Task first = await Task.WhenAny(shutdown, _stopped.Task).WaitAsync(Timeout);
            if (first == shutdown) await shutdown;
        }

        await _stopped.Task.WaitAsync(Timeout);
    }

    private void Run()
    {
        IDisposable? scope = null;
        Application? previousApplication = null;
        Exception? failure = null;
        try
        {
            // Avalonia uses this same lock when resetting or creating a dispatcher. Keep the
            // reset and UI-thread claim atomic so a background AvaloniaObject cannot win the race.
            lock (DispatcherLock)
            {
                ResetDispatcher.Invoke(null, null);
                afterDispatcherReset?.Invoke();
                _dispatcher = Dispatcher.UIThread;
                _dispatcher.VerifyAccess();
            }

            scope = (IDisposable)(EnterLocatorScope.Invoke(null, null)
                ?? throw new InvalidOperationException("Avalonia did not create a test service scope."));
            previousApplication = Application.Current;
            _lifetime = ApplicationStartup.CreateLifetime(createAppBuilder(), []);
            console.SetupLifetime(_lifetime);
            _started.TrySetResult();
            _lifetime.Start([]);
        }
        catch (Exception ex)
        {
            if (_lifetime is null && scope is not null && Application.Current is { } current
                && !ReferenceEquals(current, previousApplication) && current.ApplicationLifetime is ConsoloniaLifetime lifetime)
                _lifetime = lifetime;
            if (!_started.TrySetException(ex)) failure = ex;
        }
        finally
        {
            failure = CaptureCleanupFailure(failure, () => _lifetime?.Dispose());
            failure = CaptureCleanupFailure(failure, () => AppBuilderSetup.SetValue(null, false));
            failure = CaptureCleanupFailure(failure, () => scope?.Dispose());
            failure = CaptureCleanupFailure(failure, console.Dispose);

            if (failure is null) _stopped.TrySetResult();
            else _stopped.TrySetException(failure);
        }
    }

    private static Exception? CaptureCleanupFailure(Exception? failure, Action cleanup)
    {
        try
        {
            cleanup();
            return failure;
        }
        catch (Exception ex)
        {
            return failure is null ? ex : new AggregateException(failure, ex);
        }
    }
}
