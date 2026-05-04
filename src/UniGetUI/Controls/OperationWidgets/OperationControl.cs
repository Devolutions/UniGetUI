using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.Interface;
using UniGetUI.Interface.Enums;
using UniGetUI.Interface.Telemetry;
using UniGetUI.Interface.Widgets;
using UniGetUI.PackageEngine;
using UniGetUI.PackageEngine.Classes.Packages.Classes;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Operations;
using UniGetUI.PackageEngine.PackageLoader;
using UniGetUI.PackageOperations;
using UniGetUI.Pages.DialogPages;
using Windows.UI;

namespace UniGetUI.Controls.OperationWidgets;

public partial class OperationControl : INotifyPropertyChanged
{
    public AbstractOperation Operation;
    public BetterMenu OpMenu;
    public OperationStatus? MenuStateOnLoaded;
    public ObservableCollection<OperationBadge> Badges = [];
    private int _errorCount;

    public OperationControl(AbstractOperation operation)
    {
        OpMenu = new BetterMenu();
        Operation = operation;
        Operation.LogLineAdded += (_, values) => LiveLine = values.Item1;
        Operation.StatusChanged += OnOperationStatusChanged;
        Operation.OperationStarting += OnOperationStarting;
        Operation.OperationFinished += OnOperationFinished;
        Operation.OperationFailed += OnOperationFailed;
        Operation.OperationSucceeded += OnOperationSucceeded;

        Operation.BadgesChanged += (_, badges) =>
        {
            Badges.Clear();
            if (badges.AsAdministrator)
                Badges.Add(
                    new(
                        CoreTools.Translate("Administrator privileges"),
                        IconType.UAC,
                        CoreTools.Translate(
                            "This operation is running with administrator privileges."
                        ),
                        ""
                    )
                );

            if (badges.Interactive)
                Badges.Add(
                    new(
                        CoreTools.Translate("Interactive operation"),
                        IconType.Interactive,
                        CoreTools.Translate("This operation is running interactively."),
                        CoreTools.Translate("You will likely need to interact with the installer.")
                    )
                );

            if (badges.SkipHashCheck)
                Badges.Add(
                    new(
                        CoreTools.Translate("Integrity checks skipped"),
                        IconType.Checksum,
                        CoreTools.Translate(
                            "Integrity checks will not be performed during this operation"
                        ),
                        CoreTools.Translate("This is not recommended.")
                            + " "
                            + CoreTools.Translate("Proceed at your own risk.")
                    )
                );

            /*if (badges.Scope is not null)
            {
                if (badges.Scope is PackageScope.Local)
                    Badges.Add(new(
                        CoreTools.Translate ("Local operation"),
                        IconType.Home,
                        CoreTools.Translate ("The changes performed by this operation will affect only the current user."),
                        ""
                    ));
                else
                    Badges.Add(new(
                        CoreTools.Translate ("Global operation"),
                        IconType.LocalPc,
                        CoreTools.Translate ("The changes performed by this operation may affect other users on this machine."),
                        ""
                ));
            }*/
        };

        _title = Operation.Metadata.Title;
        _liveLine = operation.GetOutput().Any()
            ? operation.GetOutput()[operation.GetOutput().Count - 1].Item1
            : CoreTools.Translate("Please wait...");
        _buttonText = "";
        OnOperationStatusChanged(this, operation.Status);
        _ = LoadIcon();
        if (!operation.Started)
            _ = operation.MainThread();
    }

    private void OnOperationStarting(object? sender, EventArgs e)
    {
        ShowProgressToast();
        if (MainApp.Instance.MainWindow.NavigationPage.OperationList.Items.Contains(this))
        {
            MainApp.Instance.MainWindow.NavigationPage.OperationList.SmoothScrollIntoViewWithItemAsync(
                this
            );
        }
    }

    private void OnOperationSucceeded(object? sender, EventArgs e) => _ = _onOperationSucceeded();

    private async Task _onOperationSucceeded()
    {
        // Success notification
        ShowSuccessToast();

        // Clean succesful operation from list
        if (
            !Settings.Get(Settings.K.MaintainSuccessfulInstalls)
            && Operation is not DownloadOperation
        )
        {
            await TimeoutAndClose();
        }
    }

    private void OnOperationFailed(object? sender, EventArgs e)
    {
        ShowErrorToast();
    }

    private void OnOperationFinished(object? sender, EventArgs e) => _ = _onOperationFinished();

    private async Task _onOperationFinished()
    {
        // Remove progress notification (if any)
        _ = AppNotificationManager.Default.RemoveByTagAsync(
            Operation.Metadata.Identifier + "progress"
        );

        if (Operation.Status is OperationStatus.Failed)
        {
            _errorCount++;
            MainApp.Tooltip.ErrorsOccurred++;
        }
        else
        {
            MainApp.Tooltip.ErrorsOccurred -= _errorCount;
            _errorCount = 0;
        }
        MainApp.Instance.MainWindow.UpdateSystemTrayStatus();

        // Generate process output
        List<string> rawOutput =
        [
            "                           ",
            "▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄",
        ];
        foreach (var line in Operation.GetOutput())
        {
            rawOutput.Add(line.Item1);
        }

        string[] oldHistory = Settings.GetValue(Settings.K.OperationHistory).Split("\n");
        if (oldHistory.Length > 300)
            oldHistory = oldHistory.Take(300).ToArray();

        List<string> newHistory = [.. rawOutput, .. oldHistory];
        Settings.SetValue(Settings.K.OperationHistory, string.Join('\n', newHistory));
        rawOutput.Add("");
        rawOutput.Add("");
        rawOutput.Add("");

        // Handle UAC for batches
        if (Settings.Get(Settings.K.DoCacheAdminRightsForBatches))
        {
            if (!MainApp.Operations.AreThereRunningOperations())
            {
                Logger.Info("Clearing UAC prompt since there are no remaining operations");
                await CoreTools.ResetUACForCurrentProcess();
            }
        }

        // Handle newly created shortcuts
        if (
            Settings.Get(Settings.K.AskToDeleteNewDesktopShortcuts)
            && !MainApp.Operations.AreThereRunningOperations()
            && DesktopShortcutsDatabase.GetUnknownShortcuts().Any()
        )
        {
            _ = DialogHelper.HandleNewDesktopShortcuts();
        }
    }

    private async Task LoadIcon()
    {
        Icon = await Operation.GetOperationIcon();
    }

    private void OnOperationStatusChanged(object? sender, OperationStatus newStatus)
    {
        switch (newStatus)
        {
            case OperationStatus.InQueue:
                ProgressIndeterminate = false;
                ProgressValue = 0;
                ProgressForeground = (SolidColorBrush)
                    Application.Current.Resources["SystemFillColorNeutralBrush"];
                Background = (SolidColorBrush)
                    Application.Current.Resources["SystemFillColorNeutralBackgroundBrush"];
                ButtonText = CoreTools.Translate("Cancel");
                break;
            case OperationStatus.Running:
                ProgressIndeterminate = true;
                ButtonText = CoreTools.Translate("Cancel");
                ProgressForeground = (SolidColorBrush)
                    Application.Current.Resources["SystemFillColorAttentionBrush"];
                Background = (SolidColorBrush)
                    Application.Current.Resources["SystemFillColorAttentionBackgroundBrush"];
                break;
            case OperationStatus.Succeeded:
                ProgressIndeterminate = false;
                ProgressValue = 100;
                ProgressForeground = (SolidColorBrush)
                    Application.Current.Resources["SystemFillColorSuccessBrush"];
                Background = (SolidColorBrush)
                    Application.Current.Resources["SystemFillColorNeutralBackgroundBrush"];
                ButtonText = CoreTools.Translate("Close");
                break;
            case OperationStatus.Failed:
                ProgressIndeterminate = false;
                ProgressValue = 100;
                ProgressForeground = (SolidColorBrush)
                    Application.Current.Resources["SystemFillColorCriticalBrush"];
                Background = (SolidColorBrush)
                    Application.Current.Resources["SystemFillColorCriticalBackgroundBrush"];
                ButtonText = CoreTools.Translate("Close");
                break;
            case OperationStatus.Canceled:
                ProgressIndeterminate = false;
                ProgressValue = 100;
                ProgressForeground = (SolidColorBrush)
                    Application.Current.Resources["SystemFillColorCautionBrush"];
                Background = (SolidColorBrush)
                    Application.Current.Resources["SystemFillColorNeutralBackgroundBrush"];
                ButtonText = CoreTools.Translate("Close");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(newStatus), newStatus, null);
        }
    }

    public void LiveLineClick() => _ = LiveLineClickAsync();

    private async Task LiveLineClickAsync()
    {
        if (Operation.Status is OperationStatus.Failed or OperationStatus.Canceled)
        {
            await DialogHelper.ShowOperationFailedDialog(Operation, this);
        }
        else
        {
            await DialogHelper.ShowLiveLogDialog(Operation);
        }
    }

    public void ButtonClick()
    {
        if (Operation.Status is OperationStatus.Running or OperationStatus.InQueue)
        {
            Operation.Cancel();
        }
        else
        {
            Close();
        }
    }

    public void LoadMenu()
    {
        if (MenuStateOnLoaded == Operation.Status)
            return;
        MenuStateOnLoaded = Operation.Status;

        // Reset menu
        OpMenu.Items.Clear();

        // Load operation-specific entries
        var normalOptions = GetOperationOptions();
        if (normalOptions.Count != 0)
        {
            foreach (var item in normalOptions)
            {
                OpMenu.Items.Add(item);
            }

            OpMenu.Items.Add(new MenuFlyoutSeparator());
        }

        if (Operation.Status is OperationStatus.InQueue)
        {
            var skipQueue = new BetterMenuItem
            {
                Text = CoreTools.Translate("Run now"),
                Icon = new FontIcon { Glyph = "\uE768" },
            };
            skipQueue.Click += (_, _) => Operation.SkipQueue();
            OpMenu.Items.Add(skipQueue);

            var putNext = new BetterMenuItem
            {
                Text = CoreTools.Translate("Run next"),
                Icon = new FontIcon { Glyph = "\uEB9D" },
            };
            putNext.Click += (_, _) => Operation.RunNext();
            OpMenu.Items.Add(putNext);

            var putLast = new BetterMenuItem
            {
                Text = CoreTools.Translate("Run last"),
                Icon = new FontIcon { Glyph = "\uEB9E" },
            };
            putLast.Click += (_, _) => Operation.BackOfTheQueue();
            OpMenu.Items.Add(putLast);

            OpMenu.Items.Add(new MenuFlyoutSeparator());
        }

        // Create Cancel/Retry buttons
        if (Operation.Status is OperationStatus.InQueue or OperationStatus.Running)
        {
            var cancel = new BetterMenuItem
            {
                Text = CoreTools.Translate("Cancel"),
                IconName = IconType.Cross,
            };
            cancel.Click += (_, _) => Operation.Cancel();
            OpMenu.Items.Add(cancel);
        }
        else
        {
            var retry = new BetterMenuItem
            {
                Text = CoreTools.Translate("Retry"),
                IconName = IconType.Reload,
            };
            retry.Click += (_, _) => Operation.Retry(AbstractOperation.RetryMode.Retry);
            OpMenu.Items.Add(retry);

            // Add extra retry options, if applicable
            var extraRetry = GetRetryOptions(() => { });
            if (extraRetry.Count != 0)
            {
                OpMenu.Items.Add(new MenuFlyoutSeparator());

                foreach (var item in extraRetry)
                {
                    OpMenu.Items.Add(item);
                }
            }
        }
    }

    private async Task TimeoutAndClose()
    {
        var oldStatus = Operation.Status;
        await Task.Delay(5000);

        if (Operation.Status == oldStatus)
            Close();
    }

    public void Close()
    {
        MainApp.Tooltip.ErrorsOccurred -= _errorCount;
        _errorCount = 0;
        MainApp.Instance.MainWindow.UpdateSystemTrayStatus();

        MainApp.Operations._operationList.Remove(this);
        while (AbstractOperation.OperationQueue.Remove(Operation))
            ;
        if (Operation.Status is not (OperationStatus.InQueue or OperationStatus.Running))
        {
            IpcOperationApi.ForgetTracking(Operation.Metadata.Identifier);
        }
    }

    private string _buttonText;
    public string ButtonText
    {
        get => _buttonText;
        set
        {
            _buttonText = value;
            OnPropertyChanged();
        }
    }

    private string _liveLine;
    public string LiveLine
    {
        get => _liveLine;
        set
        {
            _liveLine = value;
            OnPropertyChanged();
        }
    }

    private string _title;
    public string Title
    {
        get => _title;
        set
        {
            _title = value;
            OnPropertyChanged();
        }
    }

    private bool _progressIndeterminate;
    public bool ProgressIndeterminate
    {
        get => _progressIndeterminate;
        set
        {
            _progressIndeterminate = value;
            OnPropertyChanged();
        }
    }

    private int _progressValue;
    public int ProgressValue
    {
        get => _progressValue;
        set
        {
            _progressValue = value;
            OnPropertyChanged();
        }
    }

    private Uri _icon = new("ms-appx:///Assets/images/package_color.png");
    public Uri Icon
    {
        get => _icon;
        set
        {
            _icon = value;
            OnPropertyChanged();
        }
    }

    private SolidColorBrush _background = new(Color.FromArgb(0, 0, 0, 0));
    public SolidColorBrush Background
    {
        get => _background;
        set
        {
            _background = value;
            OnPropertyChanged();
        }
    }

    private SolidColorBrush _progressForeground = new(Color.FromArgb(0, 0, 0, 0));
    public SolidColorBrush ProgressForeground
    {
        get => _progressForeground;
        set
        {
            _progressForeground = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        MainApp.Dispatcher.TryEnqueue(() =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName))
        );
    }

    private void ShowProgressToast()
    {
        if (Settings.AreProgressNotificationsDisabled())
            return;

        try
        {
            _ = AppNotificationManager.Default.RemoveByTagAsync(
                Operation.Metadata.Identifier + "progress"
            );
            AppNotificationBuilder builder = new AppNotificationBuilder()
                .SetScenario(AppNotificationScenario.Default)
                .SetTag(Operation.Metadata.Identifier + "progress")
                .AddProgressBar(
                    new AppNotificationProgressBar()
                        .SetStatus(CoreTools.Translate("Please wait..."))
                        .SetValueStringOverride("\u2003")
                        .SetTitle(Operation.Metadata.Status)
                        .SetValue(1.0)
                )
                .AddArgument("action", NotificationArguments.Show);
            AppNotification notification = builder.BuildNotification();
            notification.ExpiresOnReboot = true;
            notification.SuppressDisplay = true;
            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to show toast notification");
            Logger.Error(ex);
        }
    }

    private void ShowSuccessToast()
    {
        if (Settings.AreSuccessNotificationsDisabled())
            return;

        try
        {
            _ = AppNotificationManager.Default.RemoveByTagAsync(Operation.Metadata.Identifier);
            AppNotificationBuilder builder = new AppNotificationBuilder()
                .SetScenario(AppNotificationScenario.Default)
                .SetTag(Operation.Metadata.Identifier)
                .AddText(Operation.Metadata.SuccessTitle)
                .AddText(Operation.Metadata.SuccessMessage)
                .AddArgument("action", NotificationArguments.Show);
            AppNotification notification = builder.BuildNotification();
            notification.ExpiresOnReboot = true;
            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to show toast notification");
            Logger.Error(ex);
        }
    }

    private void ShowErrorToast()
    {
        if (Settings.AreErrorNotificationsDisabled())
            return;

        try
        {
            _ = AppNotificationManager.Default.RemoveByTagAsync(Operation.Metadata.Identifier);
            AppNotificationBuilder builder = new AppNotificationBuilder()
                .SetScenario(AppNotificationScenario.Urgent)
                .SetTag(Operation.Metadata.Identifier)
                .AddText(Operation.Metadata.FailureTitle)
                .AddText(Operation.Metadata.FailureMessage)
                .AddArgument("action", NotificationArguments.Show);
            AppNotification notification = builder.BuildNotification();
            notification.ExpiresOnReboot = true;
            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to show toast notification");
            Logger.Error(ex);
        }
    }

    public List<MenuFlyoutItemBase> GetRetryOptions(Action callback)
    {
        var retryOptionsMenu = new List<MenuFlyoutItemBase>();

        if (Operation is SourceOperation sourceOp && !sourceOp.ForceAsAdministrator)
        {
            var adminButton = new BetterMenuItem
            {
                Text = CoreTools.Translate("Retry as administrator"),
                IconName = IconType.UAC,
            };
            adminButton.Click += (_, _) =>
            {
                callback();
                Operation.Retry(AbstractOperation.RetryMode.Retry_AsAdmin);
            };
            retryOptionsMenu.Add(adminButton);
        }
        else if (Operation is PackageOperation packageOp)
        {
            if (
                !packageOp.Options.RunAsAdministrator
                && packageOp.Package.Manager.Capabilities.CanRunAsAdmin
            )
            {
                var adminButton = new BetterMenuItem
                {
                    Text = CoreTools.Translate("Retry as administrator"),
                    IconName = IconType.UAC,
                };
                adminButton.Click += (_, _) =>
                {
                    callback();
                    Operation.Retry(AbstractOperation.RetryMode.Retry_AsAdmin);
                };
                retryOptionsMenu.Add(adminButton);
            }

            if (
                !packageOp.Options.InteractiveInstallation
                && packageOp.Package.Manager.Capabilities.CanRunInteractively
            )
            {
                var interactiveButton = new BetterMenuItem
                {
                    Text = CoreTools.Translate("Retry interactively"),
                    IconName = IconType.Interactive,
                };
                interactiveButton.Click += (_, _) =>
                {
                    callback();
                    Operation.Retry(AbstractOperation.RetryMode.Retry_Interactive);
                };
                retryOptionsMenu.Add(interactiveButton);
            }

            if (
                !packageOp.Options.SkipHashCheck
                && packageOp.Package.Manager.Capabilities.CanSkipIntegrityChecks
            )
            {
                var skiphashButton = new BetterMenuItem
                {
                    Text = CoreTools.Translate("Retry skipping integrity checks"),
                    IconName = IconType.Checksum,
                };
                skiphashButton.Click += (_, _) =>
                {
                    callback();
                    Operation.Retry(AbstractOperation.RetryMode.Retry_SkipIntegrity);
                };
                retryOptionsMenu.Add(skiphashButton);
            }

            if (
                packageOp is UpdatePackageOperation
                && packageOp.Status is OperationStatus.Failed or OperationStatus.Canceled
            )
            {
                retryOptionsMenu.Add(new MenuFlyoutSeparator());

                var reinstall = new BetterMenuItem()
                {
                    Text = CoreTools.Translate("Reinstall package"),
                };
                reinstall.IconName = IconType.Download;
                reinstall.Click += async (_, _) =>
                {
                    callback();
                    this.Close();
                    _ = MainApp.Operations.Install(
                        packageOp.Package,
                        TEL_InstallReferral.ALREADY_INSTALLED,
                        ignoreParallel: true
                    );
                };
                retryOptionsMenu.Add(reinstall);

                var uninstallReinstall = new BetterMenuItem()
                {
                    Text = CoreTools.Translate("Uninstall package, then reinstall it"),
                };
                uninstallReinstall.IconName = IconType.Undelete;
                uninstallReinstall.Click += async (_, _) =>
                {
                    callback();
                    this.Close();
                    var op = await MainApp.Operations.Uninstall(
                        packageOp.Package,
                        ignoreParallel: true
                    );
                    _ = MainApp.Operations.Install(
                        packageOp.Package,
                        TEL_InstallReferral.ALREADY_INSTALLED,
                        ignoreParallel: true,
                        req: op
                    );
                };
                retryOptionsMenu.Add(uninstallReinstall);

                retryOptionsMenu.Add(new MenuFlyoutSeparator());

                var skipThisVersion = new BetterMenuItem()
                {
                    Text = CoreTools.Translate("Skip this version"),
                };
                skipThisVersion.IconName = IconType.Skip;
                skipThisVersion.Click += async (_, _) =>
                {
                    callback();
                    await packageOp.Package.AddToIgnoredUpdatesAsync(
                        packageOp.Package.NewVersionString
                    );
                    UpgradablePackagesLoader.Instance.Remove(packageOp.Package);
                    Close();
                };
                retryOptionsMenu.Add(skipThisVersion);

                var ignoreUpdates = new BetterMenuItem()
                {
                    Text = CoreTools.Translate("Ignore updates for this package"),
                };
                ignoreUpdates.IconName = IconType.Pin;
                ignoreUpdates.Click += async (_, _) =>
                {
                    callback();
                    await packageOp.Package.AddToIgnoredUpdatesAsync();
                    UpgradablePackagesLoader.Instance.Remove(packageOp.Package);
                    Close();
                };
                retryOptionsMenu.Add(ignoreUpdates);
            }
        }

        return retryOptionsMenu;
    }

    public List<MenuFlyoutItemBase> GetOperationOptions()
    {
        var optionsMenu = new List<MenuFlyoutItemBase>();
        if (Operation is PackageOperation packageOp)
        {
            var details = new BetterMenuItem
            {
                Text = CoreTools.Translate("Package details"),
                IconName = IconType.Info_Round,
                IsEnabled = !packageOp.Package.Source.IsVirtualManager,
            };
            details.Click += (_, _) =>
            {
                _ = DialogHelper.ShowPackageDetails(
                    packageOp.Package,
                    OperationType.None,
                    TEL_InstallReferral.DIRECT_SEARCH
                );
            };
            optionsMenu.Add(details);

            var installationSettings = new BetterMenuItem
            {
                Text = CoreTools.Translate("Installation options"),
                IconName = IconType.Options,
                IsEnabled = !packageOp.Package.Source.IsVirtualManager,
            };
            installationSettings.Click += (_, _) =>
            {
                _ = DialogHelper.ShowInstallatOptions_Continue(
                    packageOp.Package,
                    OperationType.None
                );
            };
            optionsMenu.Add(installationSettings);

            string? location = packageOp.Package.Manager.DetailsHelper.GetInstallLocation(
                packageOp.Package
            );
            var openLocation = new BetterMenuItem
            {
                Text = CoreTools.Translate("Open install location"),
                IconName = IconType.OpenFolder,
            };
            openLocation.Click += (_, _) => CoreTools.Launch(location);
            openLocation.IsEnabled = location is not null && Directory.Exists(location);
            optionsMenu.Add(openLocation);
        }
        else if (Operation is DownloadOperation downloadOp)
        {
            var launchInstaller = new BetterMenuItem
            {
                Text = CoreTools.Translate("Open"),
                IconName = IconType.Launch,
            };
            launchInstaller.Click += (_, _) => CoreTools.Launch(downloadOp.DownloadLocation);
            launchInstaller.IsEnabled = downloadOp.Status is OperationStatus.Succeeded;
            optionsMenu.Add(launchInstaller);

            var showFileInExplorer = new BetterMenuItem
            {
                Text = CoreTools.Translate("Show in explorer"),
                IconName = IconType.OpenFolder,
            };
            showFileInExplorer.Click += (_, _) =>
                _ = CoreTools.ShowFileOnExplorer(downloadOp.DownloadLocation);
            showFileInExplorer.IsEnabled = downloadOp.Status is OperationStatus.Succeeded;
            optionsMenu.Add(showFileInExplorer);
        }

        return optionsMenu;
    }
}
