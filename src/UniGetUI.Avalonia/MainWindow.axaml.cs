using System.Net;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.Models;
using UniGetUI.Avalonia.Views;
using UniGetUI.Avalonia.Views.Pages;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Enums;

namespace UniGetUI.Avalonia;

public partial class MainWindow : Window
{
    /// <summary>Gets the currently active <see cref="MainWindow"/> instance.</summary>
    public static MainWindow? Instance { get; private set; }

    private bool _initialized;
    private TrayIcon? _trayIcon;
    private WindowNotificationManager? _notificationManager;
    private bool _isExplicitQuit;
    private CancellationTokenSource? _geometrySaveCts;

    public MainWindow()
    {
        Instance = this;
        ApplyTheme();
        InitializeComponent();
        InitializeNotificationManager();
        Title = BuildWindowTitle();
        Opened += OnOpened;
        SizeChanged += (_, _) => _ = SaveGeometryDebounced();
        PositionChanged += (_, _) => _ = SaveGeometryDebounced();
    }

    private void InitializeNotificationManager()
    {
        _notificationManager = new WindowNotificationManager(this)
        {
            Position = NotificationPosition.TopRight,
            MaxItems = 4,
        };

        // ── B2: hook notification activation callback ──────────────────────
        WindowsAppNotificationBridge.NotificationActivated += OnNotificationActivated;
    }

    private void OnNotificationActivated(string action)
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (action == NotificationArguments.UpdateAllPackages)
                {
                    _ = AvaloniaPackageOperationHelper.UpdateAllAsync();
                }
                else if (action == NotificationArguments.ShowOnUpdatesTab)
                {
                    ShowFromTray();
                    if (Content is Views.MainShellView shell)
                        shell.OpenPage(Models.ShellPageType.Updates);
                }
                else if (action == NotificationArguments.Show)
                {
                    ShowFromTray();
                }
                else if (action == NotificationArguments.ReleaseSelfUpdateLock)
                {
                    AvaloniaAutoUpdater.ReleaseLockForAutoupdate_Notification = true;
                }
            }
            catch (Exception ex)
            {
                Logger.Error("OnNotificationActivated error:");
                Logger.Error(ex);
            }
        });
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        RestoreGeometry();

        // Handle --daemon: launch straight to tray without showing the window
        if (Environment.GetCommandLineArgs().Contains("--daemon"))
        {
            Hide();
        }

        Content = new LoadingView();

        try
        {
            await AvaloniaBootstrapper.InitializeAsync();
            Content = new MainShellView();
            Closing += OnWindowClosing;
            Closed += OnWindowClosed;
            InitTrayIcon();

            // Background integrity check (non-blocking)
            _ = Task.Run(() => IntegrityTester.CheckIntegrity()).ContinueWith(async t =>
            {
                if (!t.Result.Passed && !Settings.Get(Settings.K.DisableIntegrityChecks))
                    await Dispatcher.UIThread.InvokeAsync(() => ShowIntegrityWarningAsync(this));
            }, TaskScheduler.Default);

            // Check for missing package manager dependencies (non-blocking — runs after shell is shown)
            _ = Task.Run(AvaloniaBootstrapper.GetMissingDependenciesAsync)
                .ContinueWith(async t =>
                {
                    if (t.IsCompletedSuccessfully && t.Result.Count > 0)
                        await Dispatcher.UIThread.InvokeAsync(() => HandleMissingDependenciesAsync(t.Result));
                    else if (t.IsFaulted)
                        Logger.Error(t.Exception!);
                }, TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            Logger.Error("UniGetUI initialization failed");
            Logger.Error(ex);
            Content = new ErrorView(
                CoreTools.Translate("An error occurred"),
                ex.Message
            );
        }
    }

    /// <summary>Sequentially shows a <see cref="MissingDependencyDialog"/> for each missing dependency.</summary>
    private async Task HandleMissingDependenciesAsync(
        IReadOnlyList<UniGetUI.PackageEngine.Classes.Manager.Classes.ManagerDependency> dependencies)
    {
        int current = 1, total = dependencies.Count;
        foreach (var dep in dependencies)
        {
            var dialog = new Views.Pages.MissingDependencyDialog(dep, current++, total);
            await dialog.ShowDialog(this);
        }
    }

    private void InitTrayIcon()
    {
        try
        {
            using var iconStream = AssetLoader.Open(new Uri("avares://UniGetUI.Avalonia/Assets/icon.ico"));
            var windowIcon = new WindowIcon(iconStream);

            _trayIcon = new TrayIcon
            {
                Icon = windowIcon,
                ToolTipText = "UniGetUI",
                IsVisible = !Settings.Get(Settings.K.DisableSystemTray),
            };

            var menu = new NativeMenu();

            var showItem = new NativeMenuItem(CoreTools.Translate("Open UniGetUI"));
            showItem.Click += (_, _) => ShowFromTray();
            menu.Items.Add(showItem);

            menu.Items.Add(new NativeMenuItemSeparator());

            var discoverItem = new NativeMenuItem(CoreTools.Translate("Discover packages"));
            discoverItem.Click += (_, _) => { ShowFromTray(); NavigateShell(ShellPageType.Discover); };
            menu.Items.Add(discoverItem);

            var updatesItem = new NativeMenuItem(CoreTools.Translate("Software Updates"));
            updatesItem.Click += (_, _) => { ShowFromTray(); NavigateShell(ShellPageType.Updates); };
            menu.Items.Add(updatesItem);

            var installedItem = new NativeMenuItem(CoreTools.Translate("Installed packages"));
            installedItem.Click += (_, _) => { ShowFromTray(); NavigateShell(ShellPageType.Installed); };
            menu.Items.Add(installedItem);

            menu.Items.Add(new NativeMenuItemSeparator());

            var releaseNotesItem = new NativeMenuItem(CoreTools.Translate("Release notes"));
            releaseNotesItem.Click += (_, _) =>
            {
                ShowFromTray();
                var win = new ReleaseNotesWindow();
                _ = win.ShowDialog(this);
            };
            menu.Items.Add(releaseNotesItem);

            var aboutItem = new NativeMenuItem(CoreTools.Translate("About WingetUI"));
            aboutItem.Click += (_, _) => new AboutPageWindow().Show();
            menu.Items.Add(aboutItem);

            menu.Items.Add(new NativeMenuItemSeparator());

            var quitItem = new NativeMenuItem(CoreTools.Translate("Quit"));
            quitItem.Click += (_, _) => QuitApplication();
            menu.Items.Add(quitItem);

            _trayIcon.Menu = menu;
            _trayIcon.Clicked += (_, _) => ShowFromTray();
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to initialize system tray icon:");
            Logger.Error(ex);
        }
    }

    internal void NavigateShell(ShellPageType pageType)
    {
        if (Content is MainShellView shell)
            shell.OpenPage(pageType);
    }

    private string _lastTrayIconVariant = "";

    public void UpdateSystemTrayStatus()
    {
        if (_trayIcon is null) return;
        try
        {
            bool anyRunning = AvaloniaOperationRegistry.Operations.Any(
                o => o.Status is UniGetUI.PackageEngine.Enums.OperationStatus.Running
                    or UniGetUI.PackageEngine.Enums.OperationStatus.InQueue);

            bool anyFailed = AvaloniaOperationRegistry.Operations.Any(
                o => o.Status == UniGetUI.PackageEngine.Enums.OperationStatus.Failed);

            int updates = UniGetUI.PackageEngine.PackageLoader.UpgradablePackagesLoader.Instance.Count();

            string modifier;
            string tooltip;

            if (anyRunning)
            {
                modifier = "blue";
                tooltip = CoreTools.Translate("Operation in progress") + " — UniGetUI";
            }
            else if (anyFailed)
            {
                modifier = "orange";
                tooltip = CoreTools.Translate("Failed") + " — UniGetUI";
            }
            else if (updates == 1)
            {
                modifier = "green";
                tooltip = CoreTools.Translate("1 update is available") + " — UniGetUI";
            }
            else if (updates > 1)
            {
                modifier = "green";
                tooltip = CoreTools.Translate("{0} updates are available", updates) + " — UniGetUI";
            }
            else
            {
                modifier = "empty";
                tooltip = CoreTools.Translate("Everything is up to date") + " — UniGetUI";
            }

            _trayIcon.ToolTipText = tooltip;

            // Determine light/dark theme from registry to pick black/white icon variant
            string themeSuffix = "white"; // default: dark taskbar → white icon
#pragma warning disable CA1416
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                if (key?.GetValue("SystemUsesLightTheme") is int val && val > 0)
                    themeSuffix = "black";
            }
            catch { /* registry unavailable; keep default */ }
#pragma warning restore CA1416

            string variant = $"tray_{modifier}_{themeSuffix}";
            if (variant == _lastTrayIconVariant) return;
            _lastTrayIconVariant = variant;

            using var stream = AssetLoader.Open(new Uri($"avares://UniGetUI.Avalonia/Assets/{variant}.ico"));
            _trayIcon.Icon = new WindowIcon(stream);
        }
        catch (Exception ex)
        {
            Logger.Warn("Failed to update system tray status");
            Logger.Warn(ex);
        }
    }

    public void ApplyTrayIconVisibility()
    {
        if (_trayIcon is null) return;
        _trayIcon.IsVisible = !Settings.Get(Settings.K.DisableSystemTray);
    }

    internal void ShowFromTray()
    {
        SetEfficiencyMode(false); // B3: leave EcoQoS when coming back to foreground
        Show();
        Activate();
        WindowState = WindowState.Normal;

        // Silently reload the Installed packages list so changes made outside
        // UniGetUI while it was minimized/hidden are reflected immediately.
        if (!UniGetUI.PackageEngine.PackageLoader.InstalledPackagesLoader.Instance.IsLoading)
            _ = UniGetUI.PackageEngine.PackageLoader.InstalledPackagesLoader.Instance.ReloadPackagesSilently();
    }

    public void ShowRuntimeNotification(string title, string message, RuntimeNotificationLevel level)
    {
        if (_notificationManager is null)
        {
            return;
        }

        NotificationType type = level switch
        {
            RuntimeNotificationLevel.Success => NotificationType.Success,
            RuntimeNotificationLevel.Error => NotificationType.Error,
            _ => NotificationType.Information,
        };

        TimeSpan expiration = level == RuntimeNotificationLevel.Error
            ? TimeSpan.FromSeconds(8)
            : TimeSpan.FromSeconds(5);

        _notificationManager.Show(new Notification(title, message, type, expiration));
    }

    public void QuitApplication()
    {
        // A3: release the auto-updater window lock so a pending installer can proceed
        AvaloniaAutoUpdater.ReleaseLockForAutoupdate_Window = true;
        _isExplicitQuit = true;
        Close();
    }

    /// <summary>
    /// Restart the application by spawning a fresh process and exiting immediately.
    /// Mirrors WinUI3's <c>MainApp.Instance.KillAndRestart()</c>.
    /// </summary>
    public static void KillAndRestart()
    {
        try
        {
            System.Diagnostics.Process.Start(CoreData.UniGetUIExecutableFile);
        }
        catch (Exception ex)
        {
            Logger.Error("KillAndRestart: failed to start new process");
            Logger.Error(ex);
        }
        finally
        {
            Instance?.QuitApplication();
        }
    }

    private static async Task ShowIntegrityWarningAsync(Window owner)
    {
        try
        {
            var dialog = new Window
            {
                Title = CoreTools.Translate("An error occurred"),
                Width = 520,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new StackPanel
                {
                    Margin = new Thickness(20),
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = CoreTools.Translate("UniGetUI or some of its components are missing or corrupt.")
                                   + " " + CoreTools.Translate("It is strongly recommended to reinstall UniGetUI to adress the situation."),
                            TextWrapping = TextWrapping.Wrap,
                            FontWeight = FontWeight.SemiBold,
                        },
                        new TextBlock
                        {
                            Text = " ● " + CoreTools.Translate("Refer to the UniGetUI Logs to get more details regarding the affected file(s)"),
                            TextWrapping = TextWrapping.Wrap,
                        },
                        new TextBlock
                        {
                            Text = " ● " + CoreTools.Translate("Integrity checks can be disabled from the Experimental Settings"),
                            TextWrapping = TextWrapping.Wrap,
                        },
                        new Button
                        {
                            Content = CoreTools.Translate("Close"),
                            HorizontalAlignment = HorizontalAlignment.Right,
                        },
                    },
                },
            };
            // Wire close button
            if (dialog.Content is StackPanel sp
                && sp.Children[^1] is Button btn)
                btn.Click += (_, _) => dialog.Close();

            await dialog.ShowDialog(owner);
        }
        catch (Exception ex)
        {
            Logger.Warn("Failed to show integrity warning dialog");
            Logger.Warn(ex);
        }
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!_isExplicitQuit && !Settings.Get(Settings.K.DisableSystemTray))
        {
            e.Cancel = true;
            Hide();
            SetEfficiencyMode(true); // B3: enter EcoQoS while hidden to tray
            return;
        }

        // When tray is disabled (or user did an explicit quit via tray menu),
        // check for active operations and ask for confirmation.
        if (!_isExplicitQuit && HasRunningOperations())
        {
            e.Cancel = true;
            Dispatcher.UIThread.Post(async () => await ConfirmQuitWithRunningOpsAsync());
        }
    }

    private static bool HasRunningOperations()
    {
        return AvaloniaOperationRegistry.Operations.Any(
            o => o.Status is UniGetUI.PackageEngine.Enums.OperationStatus.Running
                or UniGetUI.PackageEngine.Enums.OperationStatus.InQueue);
    }

    private async Task ConfirmQuitWithRunningOpsAsync()
    {
        try
        {
            var tcs = new TaskCompletionSource<bool>();
            var dialog = new Window
            {
                Title = CoreTools.Translate("Operation in progress"),
                Width = 460,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new StackPanel
                {
                    Margin = new Thickness(20),
                    Spacing = 16,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = CoreTools.Translate("There are ongoing operations. Quitting WingetUI may cause them to fail. Do you want to continue?"),
                            TextWrapping = TextWrapping.Wrap,
                        },
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Spacing = 8,
                            Children =
                            {
                                new Button { Content = CoreTools.Translate("Quit") },
                                new Button { Content = CoreTools.Translate("Cancel") },
                            },
                        },
                    },
                },
            };

            if (dialog.Content is StackPanel sp
                && sp.Children[1] is StackPanel btns)
            {
                ((Button)btns.Children[0]).Click += (_, _) => { tcs.TrySetResult(true); dialog.Close(); };
                ((Button)btns.Children[1]).Click += (_, _) => { tcs.TrySetResult(false); dialog.Close(); };
            }

            dialog.Closed += (_, _) => tcs.TrySetResult(false);

            await dialog.ShowDialog(this);
            if (await tcs.Task)
            {
                QuitApplication();
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to show quit-confirmation dialog:");
            Logger.Error(ex);
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _trayIcon?.Dispose();
        _trayIcon = null;
    }

    // ── B3: Windows EcoQoS / efficiency mode ──────────────────────────────
    // Applied when the window hides to tray to reduce CPU/battery consumption;
    // reverted when the window is brought back to the foreground.
    // P/Invoke is guarded by RuntimeInformation so it compiles on all platforms.

#pragma warning disable CA1416
    private const int ProcessPowerThrottling = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(
        IntPtr hProcess,
        int ProcessInformationClass,
        ref PROCESS_POWER_THROTTLING_STATE ProcessInformation,
        uint ProcessInformationSize);
#pragma warning restore CA1416

    private static void SetEfficiencyMode(bool enable)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;
        try
        {
            var state = new PROCESS_POWER_THROTTLING_STATE
            {
                Version = 1,
                ControlMask = 1, // PROCESS_POWER_THROTTLING_EXECUTION_SPEED
                StateMask = enable ? 1u : 0u,
            };
            SetProcessInformation(
                System.Diagnostics.Process.GetCurrentProcess().Handle,
                ProcessPowerThrottling,
                ref state,
                (uint)Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
        }
        catch (Exception ex)
        {
            Logger.Warn("EcoQoS efficiency mode change failed:");
            Logger.Warn(ex);
        }
    }

    private static string BuildWindowTitle()
    {
        var details = new List<string>();

        if (
            Settings.Get(Settings.K.ShowVersionNumberOnTitlebar)
            && !string.IsNullOrWhiteSpace(CoreData.VersionName)
        )
        {
            details.Add(CoreTools.Translate("version {0}", CoreData.VersionName));
        }

        if (CoreTools.IsAdministrator())
        {
            details.Add(CoreTools.Translate("[RAN AS ADMINISTRATOR]"));
        }

        if (CoreData.IsPortable)
        {
            details.Add(CoreTools.Translate("Portable mode"));
        }

#if DEBUG
        details.Add(CoreTools.Translate("DEBUG BUILD"));
#endif

        return details.Count == 0 ? "UniGetUI" : $"UniGetUI - {string.Join(" - ", details)}";
    }

    public void RefreshWindowTitle()
    {
        Title = BuildWindowTitle();
    }

    public static void ApplyProxyVariableToProcess()
    {
        try
        {
            var proxyUri = Settings.GetProxyUrl();
            if (proxyUri is null || !Settings.Get(Settings.K.EnableProxy))
            {
                Environment.SetEnvironmentVariable(
                    "HTTP_PROXY",
                    string.Empty,
                    EnvironmentVariableTarget.Process
                );
                return;
            }

            string content;
            if (!Settings.Get(Settings.K.EnableProxyAuth))
            {
                content = proxyUri.ToString();
            }
            else
            {
                var credentials = Settings.GetProxyCredentials();
                if (credentials is null)
                {
                    content = proxyUri.ToString();
                }
                else
                {
                    content =
                        $"{proxyUri.Scheme}://{Uri.EscapeDataString(credentials.UserName)}"
                        + $":{Uri.EscapeDataString(credentials.Password)}"
                        + $"@{proxyUri.AbsoluteUri.Replace($"{proxyUri.Scheme}://", string.Empty)}";
                }
            }

            Environment.SetEnvironmentVariable(
                "HTTP_PROXY",
                content,
                EnvironmentVariableTarget.Process
            );
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to apply proxy settings:");
            Logger.Error(ex);
        }
    }

    private void ApplyTheme()
    {
        RequestedThemeVariant = Settings.GetValue(Settings.K.PreferredTheme) switch
        {
            "dark" => ThemeVariant.Dark,
            "light" => ThemeVariant.Light,
            _ => ThemeVariant.Default,
        };
    }

    private async Task SaveGeometryDebounced()
    {
        _geometrySaveCts?.Cancel();
        _geometrySaveCts = new CancellationTokenSource();
        var token = _geometrySaveCts.Token;
        try
        {
            await Task.Delay(300, token);
            if (token.IsCancellationRequested) return;
            SaveGeometry();
        }
        catch (TaskCanceledException) { }
    }

    private void SaveGeometry()
    {
        try
        {
            if (WindowState == WindowState.Minimized) return;
            int state = WindowState == WindowState.Maximized ? 1 : 0;
            string geometry = $"v2,{Position.X},{Position.Y},{(int)Width},{(int)Height},{state}";
            Logger.Debug($"Saving window geometry: {geometry}");
            Settings.SetValue(Settings.K.WindowGeometry, geometry);
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to save window geometry:");
            Logger.Error(ex);
        }
    }

    private void RestoreGeometry()
    {
        try
        {
            var geometry = Settings.GetValue(Settings.K.WindowGeometry);
            var parts = geometry.Split(',');
            if (parts.Length == 6 && parts[0] == "v2")
            {
                int x = int.Parse(parts[1]);
                int y = int.Parse(parts[2]);
                int w = int.Parse(parts[3]);
                int h = int.Parse(parts[4]);
                int state = int.Parse(parts[5]);

                if (w > 200 && h > 100)
                {
                    Width = w;
                    Height = h;
                    Position = new PixelPoint(x, y);
                }
                if (state == 1)
                    WindowState = WindowState.Maximized;

                Logger.Debug($"Restored window geometry: {geometry}");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Failed to restore window geometry:");
            Logger.Warn(ex);
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public enum RuntimeNotificationLevel
    {
        Progress,
        Success,
        Error,
    }
}
