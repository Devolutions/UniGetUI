using Avalonia.Threading;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;
using UniGetUI.Avalonia.Views;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.PackageOperations;

namespace UniGetUI.Avalonia.Infrastructure;

internal static class QuitAfterAutomaticUpdates
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

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
            AutomaticQuitDecision decision = await AutomaticQuitMonitor.RunAsync(
                launching,
                () => Task.Delay(PollInterval),
                decide => Dispatcher.UIThread.InvokeAsync(decide).GetTask(),
                Sample,
                Quit);

            switch (decision)
            {
                case AutomaticQuitDecision.WindowVisible:
                    Logger.Info("Automatic updates have finished, but UniGetUI will not quit because its window is open");
                    break;
                case AutomaticQuitDecision.UnsavedWork:
                    Logger.Info("Automatic updates have finished, but UniGetUI will not quit because there is unsaved work");
                    break;
            }
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

    private static AutomaticQuitSnapshot? Sample()
    {
        if (MainWindow.Instance is not { } window)
            return null;

        return new AutomaticQuitSnapshot(
            IsBusy: IsBusy(),
            IsEnabled: Settings.Get(Settings.K.QuitAfterAutomaticUpdates),
            IsAlreadyQuitting: window.IsQuitting,
            IsWindowVisible: window.IsVisible,
            HasUnsavedWork: window.HasUnsavedWork);
    }

    private static bool IsBusy()
        => AvaloniaOperationRegistry.HasActiveOperations()
            || AbstractOperation.QueueLength() > 0
            || MaintenanceScheduler.IsAnyTaskRunning
            || BackupViewModel.IsBackupRunning
            || AvaloniaAutoUpdater.IsBusy;

    private static void Quit()
    {
        Logger.ImportantInfo("Automatic updates have finished, quitting UniGetUI as configured");
        MainWindow.Instance?.QuitApplication();
    }
}
