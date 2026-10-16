using Avalonia.Threading;
using UniGetUI.Avalonia.Views;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;

namespace UniGetUI.Avalonia.Infrastructure;

internal static class QuitAfterAutomaticUpdates
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private const int RequiredIdleChecks = 2;

    private static int _waiting;

    public static void QuitWhenOperationsFinish(Task? launching = null)
    {
        if (!Settings.Get(Settings.K.QuitAfterAutomaticUpdates))
            return;

        if (Interlocked.Exchange(ref _waiting, 1) == 1)
            return;

        _ = WaitAndQuitAsync(launching);
    }

    private static async Task WaitAndQuitAsync(Task? launching)
    {
        try
        {
            if (launching is not null)
                await launching;

            int idleChecks = 0;
            while (idleChecks < RequiredIdleChecks)
            {
                await Task.Delay(PollInterval);
                bool busy = await Dispatcher.UIThread.InvokeAsync(IsBusy);
                idleChecks = busy ? 0 : idleChecks + 1;
            }

            await Dispatcher.UIThread.InvokeAsync(QuitIfStillInBackground);
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
        }
        finally
        {
            Interlocked.Exchange(ref _waiting, 0);
        }
    }

    private static bool IsBusy()
        => AvaloniaOperationRegistry.HasActiveOperations()
            || MaintenanceScheduler.IsAnyTaskRunning
            || AvaloniaAutoUpdater.IsBusy;

    private static void QuitIfStillInBackground()
    {
        if (!Settings.Get(Settings.K.QuitAfterAutomaticUpdates))
            return;

        if (MainWindow.Instance is not { } window || window.IsQuitting)
            return;

        if (window.IsVisible)
        {
            Logger.Info("Automatic updates have finished, but UniGetUI will not quit because its window is open");
            return;
        }

        if (window.HasUnsavedWork)
        {
            Logger.Info("Automatic updates have finished, but UniGetUI will not quit because there is unsaved work");
            return;
        }

        Logger.ImportantInfo("Automatic updates have finished, quitting UniGetUI as configured");
        window.QuitApplication();
    }
}
