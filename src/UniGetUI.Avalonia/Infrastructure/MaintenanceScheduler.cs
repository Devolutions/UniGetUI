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
    private static readonly TimeSpan TaskTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan InstalledListMaxAge = TimeSpan.FromMinutes(15);
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
        MaintenanceScheduleStore.Changed += (_, kind) => ClearRetries(kind);

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
        MaintenanceScheduleStore.Changed += (_, kind) => ClearRetries(kind);

        _headlessTimer = new System.Timers.Timer(TickInterval.TotalMilliseconds) { AutoReset = true };
        _headlessTimer.Elapsed += (_, _) => Evaluate();
        _headlessTimer.Start();
        Logger.ImportantInfo("The maintenance scheduler is running headless, only update checks are scheduled");
    }

    public static bool IsAutoInstallDue()
    {
        if (_pendingInstallSince is { } since && DateTime.Now - since > PendingInstallLifetime)
            _pendingInstallSince = null;

        var schedule = MaintenanceScheduleStore.Get(MaintenanceTaskKind.InstallUpdates);
        if (!schedule.Enabled)
        {
            _pendingInstallSince = null;
            return false;
        }

        if (schedule.Frequency is ScheduleFrequency.AfterEveryUpdateCheck)
        {
            _pendingInstallSince = null;
            return true;
        }

        return _pendingInstallSince is not null;
    }

    public static void MarkAutoInstallHandled() => _pendingInstallSince = null;

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
        bool abandoned = false;

        try
        {
            MaintenanceScheduleStore.SetLastRun(kind, DateTime.UtcNow);
            Logger.ImportantInfo($"Running the maintenance task \"{MaintenanceTasks.GetId(kind)}\"");

            Task work = ExecuteAsync(kind);
            if (!await TaskCompletion.CompletesWithin(work, TaskTimeout))
            {
                abandoned = true;
                ObserveAbandoned(work, kind);
                throw new TimeoutException(
                    $"The maintenance task \"{MaintenanceTasks.GetId(kind)}\" did not finish within {TaskTimeout.TotalMinutes:0} minutes"
                );
            }

            ClearRetries(kind);
            MaintenanceScheduleStore.ClearLastFailure(kind);
        }
        catch (Exception ex)
        {
            Logger.Error($"The maintenance task \"{MaintenanceTasks.GetId(kind)}\" failed");
            Logger.Error(ex);
            MaintenanceScheduleStore.SetLastFailure(kind, DateTime.UtcNow);
            ScheduleRetry(kind, previousRun);
        }
        finally
        {
            if (!abandoned)
            {
                lock (RunningTasks)
                    RunningTasks.Remove(kind);
            }

            if (_isHeadless)
                TaskFinished?.Invoke(null, kind);
            else
                Dispatcher.UIThread.Post(() => TaskFinished?.Invoke(null, kind));
        }
    }

    private static async Task ExecuteAsync(MaintenanceTaskKind kind)
    {
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
                await PrepareInstalledPackagesForBackupAsync();
                if (!await BackupViewModel.DoLocalBackupStatic())
                    throw new InvalidOperationException("The local backup did not complete, see the log for details");
                break;

            case MaintenanceTaskKind.CloudBackup:
                await PrepareInstalledPackagesForBackupAsync();
                if (!await BackupViewModel.DoCloudBackupStatic())
                    throw new InvalidOperationException("The cloud backup did not complete, see the log for details");
                break;
        }
    }

    private static void ObserveAbandoned(Task work, MaintenanceTaskKind kind)
    {
        _ = work.ContinueWith(
            finished =>
            {
                lock (RunningTasks)
                    RunningTasks.Remove(kind);

                Logger.Warn(
                    $"The abandoned maintenance task \"{MaintenanceTasks.GetId(kind)}\" ended with {finished.Exception?.GetBaseException().Message ?? "no result"}"
                );
            },
            TaskScheduler.Default
        );
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
                if (!schedule.Enabled || !IsClockDriven(schedule.Frequency))
                    continue;

                if (TryGetPendingRetry(kind, out var retry))
                {
                    if (retry.NextAttemptLocal > now)
                        continue;

                    if (ScheduleEvaluator.IsTimeBased(schedule.Frequency)
                        && !ScheduleEvaluator.IsWithinGrace(schedule, now))
                    {
                        ClearRetries(kind);
                        continue;
                    }
                }
                else if (!ScheduleEvaluator.IsDue(schedule, MaintenanceScheduleStore.GetLastRun(kind), now))
                {
                    continue;
                }

                _ = RunAsync(kind);
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
            }
        }
    }

    private static bool IsClockDriven(ScheduleFrequency frequency)
        => frequency is ScheduleFrequency.Interval || ScheduleEvaluator.IsTimeBased(frequency);

    private static bool IsReadyFor(MaintenanceTaskKind kind) => kind switch
    {
        MaintenanceTaskKind.CheckForUpdates or MaintenanceTaskKind.InstallUpdates => _updatesWereLoaded,
        _ => InstalledPackagesLoader.Instance is { IsLoaded: true },
    };

    private static bool TryGetPendingRetry(MaintenanceTaskKind kind, out RetryState retry)
    {
        lock (RetryLock)
            return Retries.TryGetValue(kind, out retry!);
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
        if (UpgradablePackagesLoader.Instance is not { } loader)
            return;

        if (loader.IsLoading)
            await loader.WaitForCurrentLoadAsync();
        else
            await loader.ReloadPackages();

        if (loader.LastLoadReportedFailures)
            throw new InvalidOperationException("The update check reported failures, see the log for details");
    }

    private static async Task PrepareInstalledPackagesForBackupAsync()
    {
        if (InstalledPackagesLoader.Instance is not { } loader)
            throw new InvalidOperationException("The installed package list is unavailable, see the log for details");

        if (loader.IsLoading)
            await loader.WaitForCurrentLoadAsync();
        else if (!loader.IsLoaded || IsInstalledListStale(loader))
            await loader.ReloadPackages();

        if (!loader.Any())
            throw new InvalidOperationException("The installed package list is empty, refusing to overwrite the previous backup");

        if (loader.LastLoadReportedFailures)
            throw new InvalidOperationException("A package manager failed to list its packages, refusing to overwrite the previous backup with an incomplete list");
    }

    private static bool IsInstalledListStale(InstalledPackagesLoader loader)
        => loader.LastLoadFinishedUtc is not { } finished
            || DateTime.UtcNow - finished > InstalledListMaxAge;
}
