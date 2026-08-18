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
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(15);
    private const int MaxRetriesPerOccurrence = 2;

    private static readonly HashSet<MaintenanceTaskKind> RunningTasks = [];
    private static readonly Dictionary<MaintenanceTaskKind, RetryState> Retries = [];
    private static readonly object RetryLock = new();

    private static DispatcherTimer? _timer;
    private static System.Timers.Timer? _headlessTimer;
    private static bool _started;
    private static bool _isHeadless;
    private static volatile bool _updatesWereLoaded;
    private static DateTime? _pendingInstallSince;

    private sealed record RetryState(int Attempts, DateTime NextAttemptLocal);

    public static event EventHandler<MaintenanceTaskKind>? TaskFinished;

    public static void Start()
    {
        if (_started) return;
        _started = true;

        WatchUpdateLoads();

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TickInterval };
        _timer.Tick += (_, _) => Evaluate();
        _timer.Start();
    }

    public static void StartHeadless()
    {
        if (_started) return;
        _started = true;
        _isHeadless = true;
        _updatesWereLoaded = true;

        WatchUpdateLoads();

        _headlessTimer = new System.Timers.Timer(TickInterval.TotalMilliseconds) { AutoReset = true };
        _headlessTimer.Elapsed += (_, _) => Evaluate();
        _headlessTimer.Start();
        Logger.ImportantInfo("The maintenance scheduler is running headless, only update checks are scheduled");
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

        DateTime? previousRun = MaintenanceScheduleStore.GetLastRun(kind);

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

            ClearRetries(kind);
        }
        catch (Exception ex)
        {
            Logger.Error($"The maintenance task \"{MaintenanceTasks.GetId(kind)}\" failed");
            Logger.Error(ex);
            ScheduleRetry(kind, previousRun);
        }
        finally
        {
            lock (RunningTasks)
                RunningTasks.Remove(kind);

            if (_isHeadless)
                TaskFinished?.Invoke(null, kind);
            else
                Dispatcher.UIThread.Post(() => TaskFinished?.Invoke(null, kind));
        }
    }

    private static void WatchUpdateLoads()
    {
        if (UpgradablePackagesLoader.Instance is not { } loader)
            return;

        _updatesWereLoaded |= loader.IsLoaded;
        loader.FinishedLoading += (_, _) =>
        {
            _updatesWereLoaded = true;
            MaintenanceScheduleStore.SetLastRun(MaintenanceTaskKind.CheckForUpdates, DateTime.UtcNow);
        };
    }

    private static void Evaluate()
    {
        DateTime now = DateTime.Now;

        foreach (var kind in MaintenanceTasks.All)
        {
            try
            {
                if (_isHeadless && kind is not MaintenanceTaskKind.CheckForUpdates)
                    continue;

                if (!IsReadyFor(kind))
                    continue;

                var schedule = MaintenanceScheduleStore.Get(kind);
                if (!schedule.Enabled || !ScheduleEvaluator.IsTimeBased(schedule.Frequency))
                    continue;

                if (!IsRetryDue(kind, now)
                    && !ScheduleEvaluator.IsDue(schedule, MaintenanceScheduleStore.GetLastRun(kind), now))
                    continue;

                _ = RunAsync(kind);
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
            }
        }
    }

    private static bool IsReadyFor(MaintenanceTaskKind kind) => kind switch
    {
        MaintenanceTaskKind.CheckForUpdates or MaintenanceTaskKind.InstallUpdates => _updatesWereLoaded,
        _ => InstalledPackagesLoader.Instance is { IsLoaded: true },
    };

    private static bool IsRetryDue(MaintenanceTaskKind kind, DateTime now)
    {
        lock (RetryLock)
            return Retries.TryGetValue(kind, out var retry) && retry.NextAttemptLocal <= now;
    }

    private static void ClearRetries(MaintenanceTaskKind kind)
    {
        lock (RetryLock)
            Retries.Remove(kind);
    }

    private static void ScheduleRetry(MaintenanceTaskKind kind, DateTime? previousRun)
    {
        int attempts;
        lock (RetryLock)
        {
            attempts = (Retries.TryGetValue(kind, out var retry) ? retry.Attempts : 0) + 1;

            if (attempts > MaxRetriesPerOccurrence)
            {
                Retries.Remove(kind);
                Logger.Warn($"The maintenance task \"{MaintenanceTasks.GetId(kind)}\" keeps failing, no further retries until its next occurrence");
                return;
            }

            Retries[kind] = new RetryState(attempts, DateTime.Now + RetryDelay);
        }

        if (previousRun is { } stamp)
            MaintenanceScheduleStore.SetLastRun(kind, stamp);
        else
            MaintenanceScheduleStore.ClearLastRun(kind);

        Logger.Warn($"Retrying the maintenance task \"{MaintenanceTasks.GetId(kind)}\" in {RetryDelay.TotalMinutes:0} minutes (attempt {attempts} of {MaxRetriesPerOccurrence})");
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
