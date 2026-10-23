using Avalonia.Threading;
using UniGetUI.Avalonia.Models;
using UniGetUI.Avalonia.Views;
using UniGetUI.Avalonia.Views.DialogPages;
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
using UniGetUI.PackageEngine.Classes.Packages.Classes;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Operations;
using UniGetUI.PackageEngine.Operations.Reboot;
using UniGetUI.PackageOperations;

namespace UniGetUI.Avalonia.Infrastructure;

internal static class AvaloniaBootstrapper
{
    private static bool _hasStarted;
    private static readonly TaskCompletionSource<bool> _initialized =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static Task<bool> Initialized => _initialized.Task;

    // Coalesces broker-unavailable notifications: during bulk operations every failed
    // package raises the event, but only one dialog should be visible at a time.
    private static bool _brokerUnavailableDialogActive;
    private static IpcServer? _ipcApi;

    public static async Task InitializeAsync()
    {
        if (_hasStarted)
        {
            return;
        }

        _hasStarted = true;
        Logger.Info("Starting Avalonia shell bootstrap");

        try
        {
            await Task.WhenAll(
                InitializeSharedServicesAsync(),
                InitializePackageEngineAsync()
            );

            await RunPostLoadChecksAsync();
        }
        catch (Exception)
        {
            _initialized.TrySetResult(false);
            throw;
        }

        _initialized.TrySetResult(true);

        Logger.Info("Avalonia shell bootstrap completed");
    }

    private static async Task RunPostLoadChecksAsync()
    {
        if (!Settings.Get(Settings.K.DisableIntegrityChecks))
        {
            var result = await Task.Run(() => IntegrityTester.CheckIntegrity(allowRetry: true));
            if (!result.Passed)
            {
                // When IntegrityTree.json is absent (debug / CI builds, tree is only generated
                // during `dotnet publish`), the tester returns Passed=false with a single
                // missing-file entry for the tree itself.  That is not a real integrity failure —
                // skip the dialog so it does not fire on every dev launch.
                bool onlyTreeMissing = result.MissingFiles.Count == 1
                    && result.MissingFiles[0] == "/IntegrityTree.json"
                    && result.CorruptedFiles.Count == 0;

                if (!onlyTreeMissing)
                {
                    Logger.Warn("Integrity check failed; showing integrity violation dialog.");
                    await Dispatcher.UIThread.InvokeAsync(ShowIntegrityViolationDialogAsync);
                }
                else
                {
                    Logger.Info("IntegrityTree.json not found (dev/CI build) — skipping integrity dialog.");
                }
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
        await owner.ShowDialogAndRestoreVisibilityAsync(new IntegrityViolationDialog());
    }

    private static async Task ShowMissingDependencyDialogAsync(
        ManagerDependency dep, int current, int total)
    {
        if (MainWindow.Instance is not { } owner) return;
        await owner.ShowDialogAndRestoreVisibilityAsync(new MissingDependencyDialog(dep, current, total));
    }

    private static Task InitializeSharedServicesAsync()
    {
        CoreTools.ReloadLanguageEngineInstance();
        ProcessEnvironmentConfigurator.ApplyProxySettingsToProcess();
        _ = Task.Run(AvaloniaAutoUpdater.UpdateCheckLoopAsync)
            .ContinueWith(
                t => Logger.Error(t.Exception!),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        _ = Task.Run(InitializeIpcApiAsync)
            .ContinueWith(
                t => Logger.Error(t.Exception!),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        TelemetryHandler.Configure(
            Secrets.GetOpenSearchUsername(),
            Secrets.GetOpenSearchPassword());
        AbstractOperation.QueueDrained += (_, _) => _ = TelemetryHandler.FlushPackageEventsAsync();
        PackageOperation.BrokerUnavailable += (_, failure) =>
            Dispatcher.UIThread.Post(async void () =>
            {
                // Runs on the UI thread, so the flag needs no synchronization.
                if (_brokerUnavailableDialogActive || MainWindow.Instance is not { } owner) return;
                _brokerUnavailableDialogActive = true;
                try
                {
                    await new SimpleErrorDialog(
                        failure.Title,
                        failure.Message).ShowDialog(owner);
                }
                catch (Exception ex)
                {
                    Logger.Error(ex);
                }
                finally
                {
                    _brokerUnavailableDialogActive = false;
                }
            });
        _ = TelemetryHandler.InitializeAsync()
            .ContinueWith(
                t => Logger.Error(t.Exception!),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        _ = Task.Run(ElevatorLoader.LoadAsync)
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

        await Task.Run(() => AutoUpdatesMigration.RunOnce(PEInterface.Managers));
    }

    private static async Task InitializeIpcApiAsync()
    {
        try
        {
            if (Settings.Get(Settings.K.DisableApi))
                return;

            _ipcApi = new IpcServer
            {
                SessionKind = IpcTransportOptions.GuiSessionKind,
                // On Linux, App quits on SIGTERM and SIGINT itself.
                HandlesTerminationSignals = !OperatingSystem.IsLinux(),
            };
            _ipcApi.AppInfoProvider = () =>
                Dispatcher.UIThread.InvokeAsync(GetAppInfo).GetAwaiter().GetResult();
            _ipcApi.ShowAppHandler = () =>
                Dispatcher.UIThread.InvokeAsync(ShowApp).GetAwaiter().GetResult();
            _ipcApi.NavigateAppHandler = request =>
                Dispatcher.UIThread.InvokeAsync(() => NavigateApp(request)).GetAwaiter().GetResult();
            _ipcApi.QuitAppHandler = () =>
                Dispatcher.UIThread.InvokeAsync(QuitApp).GetAwaiter().GetResult();
            _ipcApi.ShowPackageHandler = request =>
                Dispatcher.UIThread.InvokeAsync(() => ShowPackage(request)).GetAwaiter().GetResult();

            _ipcApi.OnUpgradeAll += (_, _) =>
                Dispatcher.UIThread.Post(() => _ = AvaloniaPackageOperationHelper.UpdateAllAsync());

            _ipcApi.OnUpgradeAllForManager += (_, managerName) =>
                Dispatcher.UIThread.Post(() =>
                    _ = AvaloniaPackageOperationHelper.UpdateAllForManagerAsync(managerName));

            await _ipcApi.Start();
        }
        catch (Exception ex)
        {
            Logger.Error("Could not initialize IPC API:");
            Logger.Error(ex);
        }
    }

    public static async Task StopIpcApiAsync()
    {
        if (_ipcApi is null)
        {
            return;
        }

        IpcServer ipcApi = _ipcApi;
        _ipcApi = null;
        await ipcApi.Stop().ConfigureAwait(false);
    }

    private static IpcAppInfo GetAppInfo()
    {
        MainWindow? window = MainWindow.Instance;
        return new IpcAppInfo
        {
            Headless = false,
            WindowAvailable = window is not null,
            WindowVisible = window?.IsVisible ?? false,
            CanShowWindow = window is not null,
            CanNavigate = window is not null,
            CanQuit = true,
            CurrentPage = window is null ? "" : IpcAppPages.ToPageName(window.CurrentPage.ToString()),
            SupportedPages = IpcAppPages.SupportedPages
                .Where(page => page switch
                {
                    "catalog" => window is { SoftwareCatalogEnabled: true },
                    "catalog-editor" => window is { CatalogEditorEnabled: true },
                    _ => true,
                })
                .ToArray(),
            SystemRestartPending = PendingRebootStore.HasPending,
            SystemRestartPendingPackages = PendingRebootStore.PendingCount,
        };
    }

    private static IpcCommandResult ShowApp()
    {
        MainWindow window = MainWindow.Instance
            ?? throw new InvalidOperationException("The application window is not available.");
        window.ShowFromTray();
        return IpcCommandResult.Success("show-app");
    }

    private static IpcCommandResult NavigateApp(IpcAppNavigateRequest request)
    {
        MainWindow window = MainWindow.Instance
            ?? throw new InvalidOperationException("The application window is not available.");
        string page = IpcAppPages.NormalizePageName(request.Page);
        var manager = ResolveManager(request.ManagerName);

        switch (page)
        {
            case "discover":
                window.Navigate(PageType.Discover);
                break;
            case "updates":
                window.Navigate(PageType.Updates);
                break;
            case "installed":
                window.Navigate(PageType.Installed);
                break;
            case "bundles":
                window.Navigate(PageType.Bundles);
                break;
            case "catalog":
            case "catalog-editor":
                if (!window.SoftwareCatalogEnabled)
                    throw new InvalidOperationException("Software Catalog is disabled. Enable it in Experimental settings and developer options, then restart UniGetUI.");
                if (page == "catalog-editor" && !window.CatalogEditorEnabled)
                    throw new InvalidOperationException("Catalog Editor is disabled. Enable it in Experimental settings and developer options, then restart UniGetUI.");
                window.Navigate(page == "catalog-editor" ? PageType.CatalogEditor : PageType.Catalog);
                break;
            case "settings":
                window.Navigate(PageType.Settings);
                break;
            case "managers":
                window.OpenManagerSettings(manager);
                break;
            case "own-log":
                window.Navigate(PageType.OwnLog);
                break;
            case "manager-log":
                window.OpenManagerLogs(manager);
                break;
            case "operation-history":
                window.Navigate(PageType.OperationHistory);
                break;
            case "help":
                window.ShowHelp(request.HelpAttachment ?? "");
                break;
            case "release-notes":
                window.Navigate(PageType.ReleaseNotes);
                break;
            case "about":
                window.Navigate(PageType.About);
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported app page \"{request.Page}\"."
                );
        }

        window.ShowFromTray();
        return IpcCommandResult.Success("navigate-app");
    }

    private static IpcCommandResult ShowPackage(IpcPackageActionRequest request)
    {
        MainWindow window = MainWindow.Instance
            ?? throw new InvalidOperationException("The application window is not available.");
        IPackage package = IpcPackageApi.ResolvePackage(request);
        window.ShowFromTray();
        _ = new PackageDetailsWindow(
            package, OperationType.Install, TEL_InstallReferral.DIRECT_SEARCH).ShowDialog(window);
        return IpcCommandResult.Success("show-package");
    }

    private static IpcCommandResult QuitApp()
    {
        MainWindow window = MainWindow.Instance
            ?? throw new InvalidOperationException("The application window is not available.");
        _ = Task.Run(async () =>
        {
            await Task.Delay(150);
            await Dispatcher.UIThread.InvokeAsync(window.QuitApplication);
        });
        return IpcCommandResult.Success("quit-app");
    }

    private static IPackageManager? ResolveManager(string? managerName)
    {
        if (string.IsNullOrWhiteSpace(managerName))
        {
            return null;
        }

        return PEInterface.Managers.FirstOrDefault(manager =>
            manager.Id.Equals(managerName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"Unknown manager \"{managerName}\"."
            );
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
