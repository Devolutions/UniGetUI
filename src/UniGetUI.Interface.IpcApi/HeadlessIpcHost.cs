using UniGetUI.Core.Logging;

namespace UniGetUI.Interface;

public static class HeadlessIpcHost
{
    public static async Task<int> RunAsync(Func<Task> initializeAsync, string hostName = "UniGetUI")
    {
        ArgumentNullException.ThrowIfNull(initializeAsync);

        IpcServer? backgroundApi = null;
        using var shutdown = new CancellationTokenSource();

        void RequestShutdown()
        {
            if (!shutdown.IsCancellationRequested)
            {
                shutdown.Cancel();
            }
        }

        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            RequestShutdown();
        };
        Console.CancelKeyPress += cancelHandler;

        EventHandler processExitHandler = (_, _) => RequestShutdown();
        AppDomain.CurrentDomain.ProcessExit += processExitHandler;

        try
        {
            Logger.Info($"Starting {hostName} headless daemon");

            await initializeAsync();

            backgroundApi = CreateIpcServer(RequestShutdown);
            await backgroundApi.Start();

            Logger.Info($"{hostName} headless daemon is ready");
            await WaitForShutdownAsync(shutdown.Token);
            return 0;
        }
        catch (Exception ex)
        {
            Logger.Error($"{hostName} headless daemon failed to start");
            Logger.Error(ex);
            return ex.HResult != 0 ? ex.HResult : 1;
        }
        finally
        {
            AppDomain.CurrentDomain.ProcessExit -= processExitHandler;
            Console.CancelKeyPress -= cancelHandler;

            if (backgroundApi is not null)
            {
                await backgroundApi.Stop();
            }
        }
    }

    private static IpcServer CreateIpcServer(Action requestShutdown)
    {
        var backgroundApi = new IpcServer
        {
            SessionKind = IpcTransportOptions.HeadlessSessionKind,
        };
        backgroundApi.AppInfoProvider = () =>
            new IpcAppInfo
            {
                Headless = true,
                WindowAvailable = false,
                WindowVisible = false,
                CanShowWindow = false,
                CanNavigate = false,
                CanQuit = true,
                SupportedPages = IpcAppPages.SupportedPages,
            };
        backgroundApi.ShowAppHandler = () =>
            throw new InvalidOperationException(
                "The current UniGetUI session is running headless and has no window to show."
            );
        backgroundApi.NavigateAppHandler = _ =>
            throw new InvalidOperationException(
                "The current UniGetUI session is running headless and cannot navigate UI pages."
            );
        backgroundApi.QuitAppHandler = () =>
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(150);
                requestShutdown();
            });
            return IpcCommandResult.Success("quit-app");
        };

        return backgroundApi;
    }

    private static Task WaitForShutdownAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cancellationToken.Register(() => completion.TrySetResult());
        return completion.Task;
    }
}
