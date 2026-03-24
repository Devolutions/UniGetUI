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

        Logger.Info("Avalonia shell bootstrap completed");
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
        await Task.Run(PEInterface.LoadLoaders);
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
                    MainWindow.Instance?.NavigateShell(ShellPageType.Updates);
                    MainWindow.Instance?.ShowFromTray();
                });

            _backgroundApi.OnShowSharedPackage += (_, pkg) =>
                Dispatcher.UIThread.Post(() =>
                {
                    Logger.Info($"BackgroundApi: ShowSharedPackage {pkg.Key}/{pkg.Value}");
                    MainWindow.Instance?.ShowFromTray();
                    if (MainWindow.Instance?.Content is Views.MainShellView shell)
                    {
                        shell.OpenSharedPackage(pkg.Key, pkg.Value);
                    }
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
