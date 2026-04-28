using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using UniGetUI.Avalonia.Models;
using UniGetUI.Avalonia.Views;
using UniGetUI.Core.Data;
using UniGetUI.Core.IconEngine;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.SettingsEngine.SecureSettings;
using UniGetUI.Core.Tools;
using UniGetUI.Interface;
using UniGetUI.Interface.Telemetry;
using UniGetUI.PackageEngine;
using UniGetUI.PackageEngine.Classes.Manager.Classes;

namespace UniGetUI.Avalonia.Infrastructure;

internal static class AvaloniaBootstrapper
{
    private static bool _hasStarted;
    private static BackgroundApiRunner? _backgroundApi;

    public static async Task InitializeAsync()
    {
        if (_hasStarted)
        {
            return;
        }

        _hasStarted = true;
        Logger.Info("Starting Avalonia shell bootstrap");

        await Task.WhenAll(
            InitializeSharedServicesAsync(),
            InitializePackageEngineAsync()
        );

        await RunPostLoadChecksAsync();

        Logger.Info("Avalonia shell bootstrap completed");
    }

    private static async Task RunPostLoadChecksAsync()
    {
        if (!Settings.Get(Settings.K.DisableIntegrityChecks))
        {
            var result = await Task.Run(() => IntegrityTester.CheckIntegrity(allowRetry: true));
            if (!result.Passed)
            {
                Logger.Warn("Integrity check failed; showing integrity violation dialog.");
                await Dispatcher.UIThread.InvokeAsync(ShowIntegrityViolationDialogAsync);
            }
        }

        var missing = await GetMissingDependenciesAsync();
        if (missing.Count > 0)
        {
            Logger.Info($"Found {missing.Count} missing dependencies; showing install dialogs.");
            for (int i = 0; i < missing.Count; i++)
            {
                int idx = i;
                await Dispatcher.UIThread.InvokeAsync(
                    () => ShowMissingDependencyDialogAsync(missing[idx], idx + 1, missing.Count));
            }
        }
    }

    private static async Task ShowIntegrityViolationDialogAsync()
    {
        if (MainWindow.Instance is not { } owner) return;

        var dialog = new Window
        {
            Width = 520,
            Height = 230,
            MinWidth = 400,
            MinHeight = 180,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Title = CoreTools.Translate("Integrity violation"),
        };

        var bodyText = new TextBlock
        {
            Text = CoreTools.Translate("UniGetUI or some of its components are missing or corrupt.")
                + " " + CoreTools.Translate("It is strongly recommended to reinstall UniGetUI to adress the situation."),
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeight.SemiBold,
        };

        var hint1 = new TextBlock
        {
            Text = " • " + CoreTools.Translate("Refer to the UniGetUI Logs to get more details regarding the affected file(s)"),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.8,
        };

        var hint2 = new TextBlock
        {
            Text = " • " + CoreTools.Translate("Integrity checks can be disabled from the Experimental Settings"),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.8,
        };

        var closeButton = new Button
        {
            Content = CoreTools.Translate("Close"),
            MinWidth = 90,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        closeButton.Click += (_, _) => dialog.Close();
        closeButton.Classes.Add("accent");

        var root = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 10,
            Children = { bodyText, hint1, hint2, closeButton },
        };

        dialog.Content = root;
        await dialog.ShowDialog(owner);
    }

    private static async Task ShowMissingDependencyDialogAsync(
        ManagerDependency dep, int current, int total)
    {
        if (MainWindow.Instance is not { } owner) return;

        bool notFirstTime =
            Settings.GetDictionaryItem<string, string>(Settings.K.DependencyManagement, dep.Name)
            == "attempted";
        Settings.SetDictionaryItem(Settings.K.DependencyManagement, dep.Name, "attempted");

        bool hasInstalled = false;
        bool blockClose = false;

        string title = CoreTools.Translate("Missing dependency")
            + (total > 1 ? $" ({current}/{total})" : "");

        var dialog = new Window
        {
            Width = 560,
            Height = 310,
            MinHeight = 230,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Title = title,
        };

        var titleBlock = new TextBlock
        {
            Text = title,
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
        };

        var descBlock = new TextBlock
        {
            Text = CoreTools.Translate(
                "UniGetUI requires {0} to operate, but it was not found on your system.", dep.Name),
            TextWrapping = TextWrapping.Wrap,
        };

        var infoBlock = new TextBlock
        {
            Text = CoreTools.Translate(
                "Click on Install to begin the installation process. If you skip the installation, UniGetUI may not work as expected."),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
        };

        var cmdBlock = new TextBlock
        {
            Text = dep.FancyInstallCommand,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Monospace"),
            FontSize = 12,
            Opacity = 0.75,
        };

        var skipCheck = notFirstTime
            ? new CheckBox { Content = CoreTools.Translate("Do not show this dialog again for {0}", dep.Name) }
            : null;

        if (skipCheck is not null)
        {
            skipCheck.IsCheckedChanged += (_, _) =>
            {
                var val = skipCheck.IsChecked == true ? "skipped" : "attempted";
                Settings.SetDictionaryItem(Settings.K.DependencyManagement, dep.Name, val);
            };
        }

        var progressBar = new ProgressBar
        {
            IsIndeterminate = false,
            IsVisible = false,
        };

        var installBtn = new Button
        {
            Content = CoreTools.Translate("Install {0}", dep.Name),
            MinWidth = 140,
        };
        installBtn.Classes.Add("accent");

        var skipBtn = new Button
        {
            Content = CoreTools.Translate("Not right now"),
            MinWidth = 100,
        };
        skipBtn.Click += (_, _) => { if (!blockClose) dialog.Close(); };

        installBtn.Click += async (_, _) =>
        {
            if (!hasInstalled)
            {
                try
                {
                    installBtn.IsEnabled = false;
                    skipBtn.IsEnabled = false;
                    if (skipCheck is not null) skipCheck.IsEnabled = false;
                    progressBar.IsIndeterminate = true;
                    progressBar.IsVisible = true;
                    blockClose = true;
                    infoBlock.Text = CoreTools.Translate(
                        "Please wait while {0} is being installed. A black window may show up. Please wait until it closes.",
                        dep.Name);

                    using var p = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = dep.InstallFileName,
                            Arguments = dep.InstallArguments,
                        },
                    };
                    p.Start();
                    await p.WaitForExitAsync();

                    hasInstalled = true;
                    progressBar.IsIndeterminate = false;
                    progressBar.IsVisible = false;
                    installBtn.IsEnabled = true;
                    skipBtn.IsEnabled = true;
                    blockClose = false;

                    if (current < total)
                    {
                        infoBlock.Text =
                            CoreTools.Translate("{0} has been installed successfully.", dep.Name)
                            + " "
                            + CoreTools.Translate("Please click on \"Continue\" to continue", dep.Name);
                        installBtn.Content = CoreTools.Translate("Continue");
                        skipBtn.Content = "";
                        skipBtn.IsVisible = false;
                    }
                    else
                    {
                        infoBlock.Text = CoreTools.Translate(
                            "{0} has been installed successfully. It is recommended to restart UniGetUI to finish the installation",
                            dep.Name);
                        installBtn.Content = CoreTools.Translate("Restart UniGetUI");
                        skipBtn.Content = CoreTools.Translate("Restart later");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error(ex);
                    hasInstalled = true;
                    progressBar.IsIndeterminate = false;
                    progressBar.IsVisible = false;
                    installBtn.IsEnabled = true;
                    skipBtn.IsEnabled = true;
                    blockClose = false;
                    infoBlock.Text =
                        CoreTools.Translate("An error occurred:") + " " + ex.Message + "\n"
                        + CoreTools.Translate("Please click on \"Continue\" to continue");
                    installBtn.Content = current < total
                        ? CoreTools.Translate("Continue")
                        : CoreTools.Translate("Close");
                    skipBtn.IsVisible = false;
                }
            }
            else if (current == total)
            {
                var exe = Environment.ProcessPath;
                if (exe is not null)
                    Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
                (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
                    ?.Shutdown();
            }
            else
            {
                dialog.Close();
            }
        };

        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        btnRow.Children.Add(skipBtn);
        btnRow.Children.Add(installBtn);

        var contentStack = new StackPanel { Spacing = 8 };
        contentStack.Children.Add(descBlock);
        contentStack.Children.Add(infoBlock);
        contentStack.Children.Add(cmdBlock);
        if (skipCheck is not null) contentStack.Children.Add(skipCheck);
        contentStack.Children.Add(progressBar);

        var root = new Grid
        {
            Margin = new Thickness(20),
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            RowSpacing = 12,
        };
        Grid.SetRow(titleBlock, 0);
        Grid.SetRow(contentStack, 1);
        Grid.SetRow(btnRow, 2);
        root.Children.Add(titleBlock);
        root.Children.Add(contentStack);
        root.Children.Add(btnRow);

        dialog.Content = root;
        dialog.Closing += (_, e) => e.Cancel = blockClose;
        await dialog.ShowDialog(owner);
    }

    private static Task InitializeSharedServicesAsync()
    {
        CoreTools.ReloadLanguageEngineInstance();
        MainWindow.ApplyProxyVariableToProcess();
        _ = Task.Run(AvaloniaAutoUpdater.UpdateCheckLoopAsync)
            .ContinueWith(
                t => Logger.Error(t.Exception!),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        _ = Task.Run(InitializeBackgroundApiAsync)
            .ContinueWith(
                t => Logger.Error(t.Exception!),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        TelemetryHandler.Configure(
            Secrets.GetOpenSearchUsername(),
            Secrets.GetOpenSearchPassword());
        _ = TelemetryHandler.InitializeAsync()
            .ContinueWith(
                t => Logger.Error(t.Exception!),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        _ = Task.Run(LoadElevatorAsync)
            .ContinueWith(
                t => Logger.Error(t.Exception!),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        _ = Task.Run(IconDatabase.Instance.LoadFromCacheAsync)
            .ContinueWith(
                t => Logger.Error(t.Exception!),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        _ = Task.Run(IconDatabase.Instance.LoadIconAndScreenshotsDatabaseAsync)
            .ContinueWith(
                t => Logger.Error(t.Exception!),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        return Task.CompletedTask;
    }

    private static async Task InitializePackageEngineAsync()
    {
        // LoadLoaders is called synchronously in App.axaml.cs before MainWindow creation
        await Task.Run(PEInterface.LoadManagers);
    }

    private static async Task InitializeBackgroundApiAsync()
    {
        try
        {
            if (Settings.Get(Settings.K.DisableApi))
                return;

            _backgroundApi = new BackgroundApiRunner();

            _backgroundApi.OnOpenWindow += (_, _) =>
                Dispatcher.UIThread.Post(() => MainWindow.Instance?.ShowFromTray());

            _backgroundApi.OnOpenUpdatesPage += (_, _) =>
                Dispatcher.UIThread.Post(() =>
                {
                    MainWindow.Instance?.Navigate(PageType.Updates);
                    MainWindow.Instance?.ShowFromTray();
                });

            _backgroundApi.OnUpgradeAll += (_, _) =>
                Dispatcher.UIThread.Post(() => _ = AvaloniaPackageOperationHelper.UpdateAllAsync());

            _backgroundApi.OnUpgradeAllForManager += (_, managerName) =>
                Dispatcher.UIThread.Post(() =>
                    _ = AvaloniaPackageOperationHelper.UpdateAllForManagerAsync(managerName));

            _backgroundApi.OnUpgradePackage += (_, packageId) =>
                Dispatcher.UIThread.Post(() =>
                    _ = AvaloniaPackageOperationHelper.UpdateForIdAsync(packageId));

            await _backgroundApi.Start();
        }
        catch (Exception ex)
        {
            Logger.Error("Could not initialize Background API:");
            Logger.Error(ex);
        }
    }

    public static void StopBackgroundApi() => _backgroundApi?.Stop();

    private static async Task LoadElevatorAsync()
    {
        try
        {
            if (Settings.Get(Settings.K.ProhibitElevation))
            {
                Logger.Warn("UniGetUI Elevator has been disabled since elevation is prohibited!");
                return;
            }

            if (OperatingSystem.IsLinux())
            {
                await LoadLinuxElevatorAsync();
                return;
            }

            if (SecureSettings.Get(SecureSettings.K.ForceUserGSudo))
            {
                var res = await CoreTools.WhichAsync("gsudo.exe");
                if (res.Item1)
                {
                    CoreData.ElevatorPath = res.Item2;
                    Logger.Warn($"Using user GSudo (forced by user) at {CoreData.ElevatorPath}");
                    return;
                }
            }

#if DEBUG
            Logger.Warn($"Using system GSudo since UniGetUI Elevator is not available in DEBUG builds");
            CoreData.ElevatorPath = (await CoreTools.WhichAsync("gsudo.exe")).Item2;
#else
            CoreData.ElevatorPath = Path.Join(
                CoreData.UniGetUIExecutableDirectory,
                "Assets",
                "Utilities",
                "UniGetUI Elevator.exe"
            );
            Logger.Debug($"Using built-in UniGetUI Elevator at {CoreData.ElevatorPath}");
#endif
        }
        catch (Exception ex)
        {
            Logger.Error("Elevator/GSudo failed to be loaded!");
            Logger.Error(ex);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static async Task LoadLinuxElevatorAsync()
    {
        // Prefer sudo over pkexec: sudo caches credentials on disk (per user, not per
        // process), so the user is only prompted once per ~15-minute window regardless
        // of how many packages are installed. pkexec prompts on every single invocation
        // because polkit ties its authorization cache to the calling process PID.
        var results = await Task.WhenAll(
            CoreTools.WhichAsync("sudo"),
            CoreTools.WhichAsync("pkexec"),
            CoreTools.WhichAsync("zenity"));
        var (sudoFound, sudoPath) = results[0];
        var (pkexecFound, pkexecPath) = results[1];
        var (zenityFound, zenityPath) = results[2];

        if (sudoFound)
        {
            // Find a graphical askpass helper so sudo can prompt without a terminal.
            // Most DEs (KDE, XFCE, ...) pre-set SSH_ASKPASS to their native tool;
            // GNOME doesn't, so we fall back to zenity with a small wrapper script
            // (zenity --password ignores positional args, so it needs the wrapper
            // to forward the prompt text via --text="$1").
            string? askpass = null;
            var envAskpass = Environment.GetEnvironmentVariable("SSH_ASKPASS");
            if (!string.IsNullOrEmpty(envAskpass) && File.Exists(envAskpass))
                askpass = envAskpass;
            else if (zenityFound)
            {
                askpass = Path.Join(CoreData.UniGetUIDataDirectory, "linux-askpass.sh");
                await File.WriteAllTextAsync(askpass,
                    $"#!/bin/sh\n\"{zenityPath}\" --password --title=\"UniGetUI\" --text=\"$1\"\n");
                File.SetUnixFileMode(askpass,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            if (askpass != null)
            {
                Environment.SetEnvironmentVariable("SUDO_ASKPASS", askpass);
                CoreData.ElevatorPath = sudoPath;
                CoreData.ElevatorArgs = "-A";
                Logger.Debug($"Using sudo -A with askpass '{askpass}'");
                return;
            }
        }

        // Fall back to pkexec when no usable sudo+askpass combination is found.
        // pkexec handles its own graphical prompt via polkit but prompts every invocation.
        if (pkexecFound)
        {
            CoreData.ElevatorPath = pkexecPath;
            Logger.Warn($"Using pkexec at {pkexecPath} (prompts on every operation)");
            return;
        }

        if (sudoFound)
        {
            CoreData.ElevatorPath = sudoPath;
            Logger.Warn($"Falling back to sudo without graphical askpass at {sudoPath}");
            return;
        }

        Logger.Warn("No elevation tool found (pkexec/sudo). Admin operations will fail.");
    }

    /// <summary>
    /// Checks all ready package managers for missing dependencies.
    /// Returns the list of dependencies whose installation was not skipped by the user.
    /// </summary>
    public static async Task<IReadOnlyList<ManagerDependency>> GetMissingDependenciesAsync()
    {
        var missing = new List<ManagerDependency>();

        foreach (var manager in PEInterface.Managers)
        {
            if (!manager.IsReady()) continue;

            foreach (var dep in manager.Dependencies)
            {
                bool isInstalled = true;
                try
                {
                    isInstalled = await dep.IsInstalled();
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error checking dependency {dep.Name}: {ex.Message}");
                }

                if (!isInstalled)
                {
                    if (Settings.GetDictionaryItem<string, string>(
                            Settings.K.DependencyManagement, dep.Name) == "skipped")
                    {
                        Logger.Info($"Dependency {dep.Name} skipped by user preference.");
                    }
                    else
                    {
                        Logger.Warn(
                            $"Dependency {dep.Name} not found for manager {manager.Name}.");
                        missing.Add(dep);
                    }
                }
                else
                {
                    Logger.Info($"Dependency {dep.Name} for {manager.Name} is present.");
                }
            }
        }

        return missing;
    }
}
