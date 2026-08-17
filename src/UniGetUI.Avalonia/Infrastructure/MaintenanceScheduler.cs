using Avalonia.Threading;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools.Scheduling;
using UniGetUI.PackageEngine.PackageLoader;

namespace UniGetUI.Avalonia.Infrastructure;

internal static class MaintenanceScheduler
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PendingInstallLifetime = TimeSpan.FromHours(2);
    private static readonly HashSet<MaintenanceTaskKind> RunningTasks = [];

    private static DispatcherTimer? _timer;
    private static bool _started;
    private static bool _updatesWereLoaded;
    private static DateTime? _pendingInstallSince;

    public static event EventHandler<MaintenanceTaskKind>? TaskFinished;

    public static void Start()
    {
        if (_started) return;
        _started = true;

        if (UpgradablePackagesLoader.Instance is { } loader)
        {
            _updatesWereLoaded = loader.IsLoaded;
            loader.FinishedLoading += (_, _) =>
            {
                _updatesWereLoaded = true;
                MaintenanceScheduleStore.SetLastRun(MaintenanceTaskKind.CheckForUpdates, DateTime.UtcNow);
            };
        }

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TickInterval };
        _timer.Tick += (_, _) => Evaluate();
        _timer.Start();
    }

    public static bool ShouldAutoInstallNow()
    {
        var schedule = MaintenanceScheduleStore.Get(MaintenanceTaskKind.InstallUpdates);
        if (!schedule.Enabled)
            return false;

        if (schedule.Frequency is ScheduleFrequency.AfterEveryUpdateCheck)
            return true;

        if (_pendingInstallSince is { } since)
        {
            _pendingInstallSince = null;
            if (DateTime.Now - since <= PendingInstallLifetime)
                return true;
        }

        return ScheduleEvaluator.IsTimeBased(schedule.Frequency)
            && ScheduleEvaluator.IsInsideWindow(schedule, DateTime.Now);
    }

    public static bool ShouldRunAtAppStart(MaintenanceTaskKind kind)
    {
        var schedule = MaintenanceScheduleStore.Get(kind);
        return schedule.Enabled && schedule.Frequency is ScheduleFrequency.AtAppStart;
    }

    public static async Task RunAsync(MaintenanceTaskKind kind)
    {
        lock (RunningTasks)
        {
            if (!RunningTasks.Add(kind))
                return;
        }

        try
        {
            MaintenanceScheduleStore.SetLastRun(kind, DateTime.UtcNow);
            Logger.ImportantInfo($"Running the maintenance task \"{MaintenanceTasks.GetId(kind)}\"");

            switch (kind)
            {
                case MaintenanceTaskKind.CheckForUpdates:
                    await ReloadUpdatesAsync();
                    break;

                case MaintenanceTaskKind.InstallUpdates:
                    _pendingInstallSince = DateTime.Now;
                    await ReloadUpdatesAsync();
                    break;

                case MaintenanceTaskKind.LocalBackup:
                    await EnsureInstalledPackagesAreLoadedAsync();
                    await BackupViewModel.DoLocalBackupStatic();
                    break;

                case MaintenanceTaskKind.CloudBackup:
                    await EnsureInstalledPackagesAreLoadedAsync();
                    await BackupViewModel.DoCloudBackupStatic();
                    break;
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"The maintenance task \"{MaintenanceTasks.GetId(kind)}\" failed");
            Logger.Error(ex);
        }
        finally
        {
            lock (RunningTasks)
                RunningTasks.Remove(kind);

            Dispatcher.UIThread.Post(() => TaskFinished?.Invoke(null, kind));
        }
    }

    private static void Evaluate()
    {
        if (!_updatesWereLoaded)
            return;

        DateTime now = DateTime.Now;

        foreach (var kind in MaintenanceTasks.All)
        {
            try
            {
                var schedule = MaintenanceScheduleStore.Get(kind);
                if (!ScheduleEvaluator.IsTimeBased(schedule.Frequency))
                    continue;

                if (!ScheduleEvaluator.IsDue(schedule, MaintenanceScheduleStore.GetLastRun(kind), now))
                    continue;

                _ = RunAsync(kind);
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
            }
        }
    }

    private static async Task ReloadUpdatesAsync()
    {
        if (UpgradablePackagesLoader.Instance is { } loader)
            await loader.ReloadPackages();
    }

    private static async Task EnsureInstalledPackagesAreLoadedAsync()
    {
        if (InstalledPackagesLoader.Instance is { IsLoaded: false } loader)
            await loader.ReloadPackages();
    }
}
