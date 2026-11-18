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

    private static readonly AutomaticQuitRequest Request = new();

    private static bool _loggedDialogWait;

    public static void QuitWhenOperationsFinish(Task? launching = null, bool nothingToInstall = false)
    {
        if (!IsEnabled(nothingToInstall))
        {
            if (!nothingToInstall)
                Request.NoteUpdatePass();
            return;
        }

        if (!Request.TryBegin(nothingToInstall))
            return;

        if (nothingToInstall)
            Logger.Info("Automatic updates found nothing to install, UniGetUI will quit once its other tasks have finished");
        _ = WaitAndQuitAsync(launching);
    }

    public static void NoteUpdatesLaunched() => Request.NoteUpdatePass();

    private static bool IsEnabled(bool nothingToInstall)
        => AutomaticQuitMonitor.IsQuitEnabled(
            Settings.Get(Settings.K.QuitAfterAutomaticUpdates),
            Settings.Get(Settings.K.QuitWhenNothingToInstall),
            nothingToInstall);

    private static async Task WaitAndQuitAsync(Task? launching)
    {
        AutomaticQuitDecision decision = AutomaticQuitDecision.Cancelled;
        _loggedDialogWait = false;
        try
        {
            decision = await AutomaticQuitMonitor.RunAsync(
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
            if (Request.End() is { } lateNothingToInstall && decision is not AutomaticQuitDecision.Quit)
                QuitWhenOperationsFinish(nothingToInstall: lateNothingToInstall);
        }
    }

    private static AutomaticQuitSnapshot? Sample()
    {
        Request.MarkSampled();
        if (MainWindow.Instance is not { } window)
            return null;

        AutomaticQuitSnapshot snapshot = AutomaticQuitMonitor.Capture(
            isBusy: IsBusy(),
            isEnabled: IsEnabled(Request.NothingToInstall),
            isAlreadyQuitting: window.IsQuitting,
            isWindowVisible: window.IsVisible,
            hasUnsavedWork: window.HasUnsavedWork,
            hasOpenDialog: window.HasOpenDialog,
            quitEvenWhenWindowIsOpen: Settings.Get(Settings.K.QuitEvenWhenWindowIsOpen));

        if (snapshot.IsDialogOpen && !snapshot.IsBusy && !_loggedDialogWait)
        {
            _loggedDialogWait = true;
            Logger.Info("Automatic updates have finished, UniGetUI will quit once its open dialog is closed, or after five minutes");
        }

        return snapshot;
    }

    internal static bool IsBusy()
        => AvaloniaOperationRegistry.HasActiveOperations()
            || AbstractOperation.QueueLength() > 0
            || MaintenanceScheduler.IsAnyTaskRunning
            || BackupViewModel.IsBackupRunning
            || AvaloniaAutoUpdater.IsBusy;

    private static void Quit()
    {
        if (MainWindow.Instance is { IsVisible: true, HasOpenDialog: true })
            Logger.Warn("A dialog has stayed open for five minutes after automatic updates finished, quitting UniGetUI without waiting for it");
        Logger.ImportantInfo("Automatic updates have finished, quitting UniGetUI as configured");
        MainWindow.Instance?.QuitApplication();
    }
}
