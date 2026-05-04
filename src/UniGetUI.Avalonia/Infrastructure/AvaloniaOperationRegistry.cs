using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using UniGetUI.Avalonia.ViewModels;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.Interface;
using UniGetUI.PackageEngine.Classes.Packages.Classes;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageOperations;

namespace UniGetUI.Avalonia.Infrastructure;

/// <summary>
/// Global registry of operations for the Avalonia shell.
/// The operations panel binds to <see cref="OperationViewModels"/>.
/// </summary>
public static class AvaloniaOperationRegistry
{
    /// <summary>Raw operations — kept for compatibility / queue checks.</summary>
    public static readonly ObservableCollection<AbstractOperation> Operations = new();

    /// <summary>Bindable view-models shown in the operations panel.</summary>
    public static readonly AvaloniaList<OperationViewModel> OperationViewModels = new();

    // Mirrors WinUI's MainApp.Tooltip.ErrorsOccurred / RestartRequired
    private static readonly ConcurrentDictionary<AbstractOperation, int> _errorCounts = new();
    private static int _errorsOccurred;
    public static int ErrorsOccurred => _errorsOccurred;
    public static bool RestartRequired { get; set; }

    /// <summary>
    /// Register an operation and create its UI view-model.
    /// Must be called before <c>operation.MainThread()</c>.
    /// </summary>
    public static void Add(AbstractOperation op)
    {
        IpcOperationApi.Track(op);
        var vm = new OperationViewModel(op);

        Dispatcher.UIThread.Post(() =>
        {
            if (!Operations.Contains(op))
            {
                Operations.Add(op);
                OperationViewModels.Add(vm);
            }
        });

        op.OperationStarting += (_, _) =>
        {
            Dispatcher.UIThread.Post(() => ShowOperationProgressNotification(op));
        };

        op.OperationSucceeded += (_, _) =>
        {
            if (_errorCounts.TryRemove(op, out int errCount) && errCount > 0)
                Interlocked.Add(ref _errorsOccurred, -errCount);

            if (!Settings.Get(Settings.K.MaintainSuccessfulInstalls))
                _ = RemoveAfterDelayAsync(op, milliseconds: 4000);

            _ = Task.Run(() => AppendOperationHistory(op));

            Dispatcher.UIThread.Post(() => ShowOperationSuccessNotification(op));

            _ = RunPostOperationChecksAsync();
            Dispatcher.UIThread.Post(UpdateTrayStatus);
        };

        op.OperationFailed += (_, _) =>
        {
            _errorCounts.AddOrUpdate(op, 1, (_, n) => n + 1);
            Interlocked.Increment(ref _errorsOccurred);

            _ = Task.Run(() => AppendOperationHistory(op));
            Dispatcher.UIThread.Post(() => ShowOperationFailureNotification(op));
            Dispatcher.UIThread.Post(UpdateTrayStatus);
        };

        op.StatusChanged += (_, status) =>
        {
            if (status is OperationStatus.Canceled)
            {
                WindowsAppNotificationBridge.RemoveProgress(op);
                _ = RemoveAfterDelayAsync(op, milliseconds: 2500);
            }
            Dispatcher.UIThread.Post(UpdateTrayStatus);
        };
    }

    public static void RetryFailed()
    {
        var failed = OperationViewModels
            .Where(vm => vm.Operation.Status is OperationStatus.Failed)
            .ToList();
        foreach (var vm in failed)
            vm.Operation.Retry(AbstractOperation.RetryMode.Retry);
    }

    public static void ClearSuccessful()
    {
        var succeeded = OperationViewModels
            .Where(vm => vm.Operation.Status is OperationStatus.Succeeded)
            .ToList();
        foreach (var vm in succeeded)
            Remove(vm);
    }

    public static void ClearFinished()
    {
        var finished = OperationViewModels
            .Where(vm => vm.Operation.Status
                is OperationStatus.Succeeded or OperationStatus.Failed or OperationStatus.Canceled)
            .ToList();
        foreach (var vm in finished)
            Remove(vm);
    }

    public static void CancelAll()
    {
        var active = OperationViewModels
            .Where(vm => vm.Operation.Status is OperationStatus.Running or OperationStatus.InQueue)
            .ToList();
        foreach (var vm in active)
            vm.Operation.Cancel();
    }

    /// <summary>Remove a view-model (and its backing operation) from the panel. Called by the Close button.</summary>
    public static void Remove(OperationViewModel vm)
    {
        if (_errorCounts.TryRemove(vm.Operation, out int errCount) && errCount > 0)
            Interlocked.Add(ref _errorsOccurred, -errCount);

        Dispatcher.UIThread.Post(() =>
        {
            OperationViewModels.Remove(vm);
            Operations.Remove(vm.Operation);
            UpdateTrayStatus();
        });
        while (AbstractOperation.OperationQueue.Remove(vm.Operation)) ;
        if (vm.Operation.Status is not (OperationStatus.InQueue or OperationStatus.Running))
        {
            IpcOperationApi.ForgetTracking(vm.Operation.Metadata.Identifier);
        }
    }

    private static async Task RemoveAfterDelayAsync(AbstractOperation op, int milliseconds)
    {
        await Task.Delay(milliseconds);

        if (_errorCounts.TryRemove(op, out int errCount) && errCount > 0)
            Interlocked.Add(ref _errorsOccurred, -errCount);

        Dispatcher.UIThread.Post(() =>
        {
            var vm = OperationViewModels.FirstOrDefault(v => v.Operation == op);
            if (vm is not null) OperationViewModels.Remove(vm);
            Operations.Remove(op);
            UpdateTrayStatus();
            if (op.Status is not (OperationStatus.InQueue or OperationStatus.Running))
            {
                IpcOperationApi.ForgetTracking(op.Metadata.Identifier);
            }
            UpdateTrayStatus();
        });
    }

    private static void UpdateTrayStatus()
    {
        if (Application.Current?.ApplicationLifetime
                is IClassicDesktopStyleApplicationLifetime { MainWindow: UniGetUI.Avalonia.Views.MainWindow mw })
            mw.UpdateSystemTrayStatus();
    }

    private static void ShowOperationProgressNotification(AbstractOperation op)
    {
        if (Settings.AreProgressNotificationsDisabled())
            return;

        string title = op.Metadata.Title.Length > 0
            ? op.Metadata.Title
            : CoreTools.Translate("Operation in progress");

        string message = op.Metadata.Status.Length > 0
            ? op.Metadata.Status
            : CoreTools.Translate("Please wait...");

        AccessibilityAnnouncementService.Announce(
            $"{title}. {message}",
            AutomationLiveSetting.Polite);

        if (OperatingSystem.IsWindows() && WindowsAppNotificationBridge.ShowProgress(op))
            return;

        if (OperatingSystem.IsMacOS() && MacOsNotificationBridge.ShowProgress(op))
            return;

        if (TryGetMainWindow() is not { } mainWindow)
            return;

        mainWindow.ShowRuntimeNotification(
            title,
            message,
            UniGetUI.Avalonia.Views.MainWindow.RuntimeNotificationLevel.Progress);
    }

    private static void ShowOperationSuccessNotification(AbstractOperation op)
    {
        if (Settings.AreSuccessNotificationsDisabled())
            return;

        string title = op.Metadata.SuccessTitle.Length > 0
            ? op.Metadata.SuccessTitle
            : CoreTools.Translate("Success!");

        string message = op.Metadata.SuccessMessage.Length > 0
            ? op.Metadata.SuccessMessage
            : CoreTools.Translate("Success!");

        AccessibilityAnnouncementService.Announce(
            $"{title}. {message}",
            AutomationLiveSetting.Polite);

        WindowsAppNotificationBridge.RemoveProgress(op);

        if (OperatingSystem.IsWindows() && WindowsAppNotificationBridge.ShowSuccess(op))
            return;

        if (OperatingSystem.IsMacOS() && MacOsNotificationBridge.ShowSuccess(op))
            return;

        if (TryGetMainWindow() is not { } mainWindow)
            return;

        mainWindow.ShowRuntimeNotification(
            title,
            message,
            UniGetUI.Avalonia.Views.MainWindow.RuntimeNotificationLevel.Success);
    }

    private static void ShowOperationFailureNotification(AbstractOperation op)
    {
        if (Settings.AreErrorNotificationsDisabled())
            return;

        string title = op.Metadata.FailureTitle.Length > 0
            ? op.Metadata.FailureTitle
            : CoreTools.Translate("Failed");

        string message = op.Metadata.FailureMessage.Length > 0
            ? op.Metadata.FailureMessage
            : CoreTools.Translate("An error occurred while processing this package");

        AccessibilityAnnouncementService.Announce(
            $"{title}. {message}",
            AutomationLiveSetting.Assertive);

        WindowsAppNotificationBridge.RemoveProgress(op);

        if (OperatingSystem.IsWindows() && WindowsAppNotificationBridge.ShowError(op))
            return;

        if (OperatingSystem.IsMacOS() && MacOsNotificationBridge.ShowError(op))
            return;

        if (TryGetMainWindow() is not { } mainWindow)
            return;

        mainWindow.ShowRuntimeNotification(
            title,
            message,
            UniGetUI.Avalonia.Views.MainWindow.RuntimeNotificationLevel.Error);
    }

    private static UniGetUI.Avalonia.Views.MainWindow? TryGetMainWindow()
    {
        return Application.Current?.ApplicationLifetime
            is IClassicDesktopStyleApplicationLifetime { MainWindow: UniGetUI.Avalonia.Views.MainWindow mw }
            ? mw
            : null;
    }

    private static void AppendOperationHistory(AbstractOperation op)
    {
        try
        {
            var rawOutput = new List<string>
            {
                "                           ",
                "▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄",
            };
            foreach (var (text, _) in op.GetOutput())
                rawOutput.Add(text);

            var oldLines = Settings.GetValue(Settings.K.OperationHistory).Split('\n');
            if (oldLines.Length > 300)
                oldLines = oldLines.Take(300).ToArray();

            Settings.SetValue(
                Settings.K.OperationHistory,
                string.Join('\n', rawOutput.Concat(oldLines)));
        }
        catch (Exception ex)
        {
            Logger.Warn("Failed to write operation history");
            Logger.Warn(ex);
        }
    }

    private static async Task RunPostOperationChecksAsync()
    {
        // Let all remaining operations settle before making decisions
        await Task.Delay(500);

        bool anyStillRunning = Operations.Any(
            o => o.Status is OperationStatus.Running or OperationStatus.InQueue);

        // Clear UAC cache after the last operation in a batch finishes
        if (!anyStillRunning && Settings.Get(Settings.K.DoCacheAdminRightsForBatches))
        {
            Logger.Info("Clearing UAC prompt since there are no remaining operations");
            await CoreTools.ResetUACForCurrentProcess();
        }

        if (OperatingSystem.IsWindows())
        {
            var unknownShortcuts = UniGetUI.PackageEngine.Classes.Packages.Classes.DesktopShortcutsDatabase.GetUnknownShortcuts();
            if (unknownShortcuts.Count > 0)
                WindowsAppNotificationBridge.ShowNewShortcutsNotification(unknownShortcuts);
        }
        else if (OperatingSystem.IsMacOS())
        {
            var unknownShortcuts = UniGetUI.PackageEngine.Classes.Packages.Classes.DesktopShortcutsDatabase.GetUnknownShortcuts();
            if (unknownShortcuts.Count > 0)
                MacOsNotificationBridge.ShowNewShortcutsNotification(unknownShortcuts);
        }
    }
}
