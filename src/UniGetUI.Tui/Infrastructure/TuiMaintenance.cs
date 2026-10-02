using Avalonia.Threading;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.Core.Tools.Scheduling;
using UniGetUI.PackageEngine.Classes.Packages.Classes;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.PackageLoader;

namespace UniGetUI.Tui.Infrastructure;

/// <summary>
/// Scheduled maintenance for the TUI: a port of the desktop <c>MaintenanceScheduler</c> (same store,
/// evaluator, retry and timeout rules) driven by a UI-thread timer.
/// </summary>
internal static class TuiMaintenanceScheduler
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PendingInstallLifetime = TimeSpan.FromHours(2);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan TaskTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan InstalledListMaxAge = TimeSpan.FromMinutes(15);
    private const int MaxRetriesPerOccurrence = 2;

    private static readonly HashSet<MaintenanceTaskKind> RunningTasks = [];
    private static readonly Dictionary<MaintenanceTaskKind, (int Attempts, DateTime Next)> Retries = [];
    private static DispatcherTimer? _timer;
    private static bool _started;
    private static volatile bool _updatesWereLoaded;
    private static DateTime? _pendingInstallSince;

    public static event Action<MaintenanceTaskKind>? TaskFinished;

    public static void Start()
    {
        if (_started) return;
        _started = true;
        if (UpgradablePackagesLoader.Instance is { } loader)
        {
            _updatesWereLoaded |= loader.IsLoaded;
            loader.FinishedLoading += (_, _) =>
            {
                _updatesWereLoaded = true;
                MaintenanceScheduleStore.SetLastRun(MaintenanceTaskKind.CheckForUpdates, DateTime.UtcNow);
            };
        }

        MaintenanceScheduleStore.Changed += (_, kind) =>
        {
            lock (Retries) Retries.Remove(kind);
        };
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TickInterval };
        _timer.Tick += (_, _) => Evaluate();
        _timer.Start();
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

    public static bool IsSupported(MaintenanceTaskKind kind) => kind is not MaintenanceTaskKind.CloudBackup || TuiCloudBackup.IsConfigured;

    public static async Task RunAsync(MaintenanceTaskKind kind)
    {
        lock (RunningTasks)
            if (!RunningTasks.Add(kind)) return;

        DateTime? previousRun = MaintenanceScheduleStore.GetLastRun(kind);
        try
        {
            MaintenanceScheduleStore.SetLastRun(kind, DateTime.UtcNow);
            Logger.ImportantInfo($"Running the maintenance task \"{MaintenanceTasks.GetId(kind)}\"");
            Task work = ExecuteAsync(kind);
            if (await Task.WhenAny(work, Task.Delay(TaskTimeout)) != work)
                throw new TimeoutException($"The maintenance task \"{MaintenanceTasks.GetId(kind)}\" did not finish in time");
            await work;
            lock (Retries) Retries.Remove(kind);
            MaintenanceScheduleStore.ClearLastFailure(kind);
        }
        catch (Exception ex)
        {
            Logger.Error($"The maintenance task \"{MaintenanceTasks.GetId(kind)}\" failed");
            Logger.Error(ex);
            MaintenanceScheduleStore.SetLastFailure(kind, DateTime.UtcNow);
            ScheduleRetry(kind, previousRun);
            TuiNotifications.Error(CoreTools.Translate("Scheduled maintenance"), ex.Message);
        }
        finally
        {
            lock (RunningTasks) RunningTasks.Remove(kind);
            Dispatcher.UIThread.Post(() => TaskFinished?.Invoke(kind));
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
                if (await TuiBackup.DoLocalBackupAsync() is null)
                    throw new InvalidOperationException("The local backup did not complete, see the log for details");
                break;
            case MaintenanceTaskKind.CloudBackup:
                await PrepareInstalledPackagesForBackupAsync();
                if (!await TuiCloudBackup.BackupNowAsync())
                    throw new InvalidOperationException("The cloud backup did not complete, see the log for details");
                break;
        }
    }

    private static void Evaluate()
    {
        DateTime now = DateTime.Now;
        foreach (var kind in MaintenanceTasks.All)
        {
            try
            {
                if (!IsSupported(kind)) continue;
                bool ready = kind is MaintenanceTaskKind.CheckForUpdates or MaintenanceTaskKind.InstallUpdates
                    ? _updatesWereLoaded
                    : InstalledPackagesLoader.Instance is { IsLoaded: true };
                if (!ready) continue;
                var schedule = MaintenanceScheduleStore.Get(kind);
                bool clockDriven = schedule.Frequency is ScheduleFrequency.Interval || ScheduleEvaluator.IsTimeBased(schedule.Frequency);
                if (!schedule.Enabled || !clockDriven) continue;

                (int Attempts, DateTime Next) retry;
                bool hasRetry;
                lock (Retries) hasRetry = Retries.TryGetValue(kind, out retry);
                if (hasRetry)
                {
                    if (retry.Next > now) continue;
                    if (ScheduleEvaluator.IsTimeBased(schedule.Frequency) && !ScheduleEvaluator.IsWithinGrace(schedule, now))
                    {
                        lock (Retries) Retries.Remove(kind);
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

    private static void ScheduleRetry(MaintenanceTaskKind kind, DateTime? previousRun)
    {
        int attempts;
        lock (Retries)
        {
            attempts = (Retries.TryGetValue(kind, out var retry) ? retry.Attempts : 0) + 1;
            if (attempts > MaxRetriesPerOccurrence)
            {
                Retries.Remove(kind);
                return;
            }

            Retries[kind] = (attempts, DateTime.Now + RetryDelay);
        }

        if (previousRun is { } stamp) MaintenanceScheduleStore.SetLastRun(kind, stamp);
        else MaintenanceScheduleStore.ClearLastRun(kind);
    }

    private static async Task ReloadUpdatesAsync()
    {
        var loader = UpgradablePackagesLoader.Instance;
        if (loader.IsLoading) await loader.WaitForCurrentLoadAsync();
        else await loader.ReloadPackages();
        if (loader.LastLoadReportedFailures)
            throw new InvalidOperationException("The update check reported failures, see the log for details");
    }

    private static async Task PrepareInstalledPackagesForBackupAsync()
    {
        var loader = InstalledPackagesLoader.Instance;
        if (loader.IsLoading) await loader.WaitForCurrentLoadAsync();
        else if (!loader.IsLoaded || loader.LastLoadFinishedUtc is not { } finished || DateTime.UtcNow - finished > InstalledListMaxAge)
            await loader.ReloadPackages();
        if (!loader.Any())
            throw new InvalidOperationException("The installed package list is empty, refusing to overwrite the previous backup");
        if (loader.LastLoadReportedFailures)
            throw new InvalidOperationException("A package manager failed to list its packages, refusing to overwrite the previous backup with an incomplete list");
    }
}

/// <summary>Local backups of the installed-package list (desktop <c>BackupViewModel.DoLocalBackupStatic</c>).</summary>
internal static class TuiBackup
{
    /// <summary>Writes a backup bundle of the installed packages. Returns the file path, or null on failure.</summary>
    public static async Task<string?> DoLocalBackupAsync()
    {
        try
        {
            var packages = InstalledPackagesLoader.Instance.Packages.ToList();
            string contents = await TuiBundleService.CreateBundleJsonAsync(packages);
            string path = await LocalBackupManager.SaveBackupAsync(contents);
            Logger.ImportantInfo("Local backup saved to " + path);
            await Task.Run(LocalBackupManager.ApplyRetentionLimit);
            return path;
        }
        catch (Exception ex)
        {
            Logger.Error("An error occurred while performing a LOCAL backup:");
            Logger.Error(ex);
            return null;
        }
    }
}

/// <summary>
/// What happens when the Updates list finishes loading (desktop <c>SoftwareUpdatesPage.WhenPackagesLoaded</c>):
/// a due scheduled auto-install, <c>--updateapps</c>, or the "updates available" notification.
/// Battery / battery-saver / metered-connection checks are desktop power APIs and are not evaluated here.
/// </summary>
internal static class TuiUpdatesPolicy
{
    public static async Task OnUpdatesLoadedAsync(bool firstLoad)
    {
        try
        {
            var upgradable = UpgradablePackagesLoader.Instance.Packages
                .Where(p => p.Tag is not UniGetUI.Interface.Enums.PackageTag.OnQueue and not UniGetUI.Interface.Enums.PackageTag.BeingProcessed)
                .ToList();
            TuiShell.InvalidateChrome();
            if (upgradable.Count == 0) return;

            if (TuiMaintenanceScheduler.IsAutoInstallDue())
            {
                TuiMaintenanceScheduler.MarkAutoInstallHandled();
                bool markedOnly = MaintenanceScheduleStore.GetInstallTargets() is ScheduleInstallTargets.MarkedPackagesOnly;
                var targets = upgradable.Where(p => !markedOnly || AutoUpdatesDatabase.IsAutoUpdated(p)).ToList();
                if (targets.Count > 0)
                {
                    await TuiPackageActions.UpdateAsync(targets);
                    ShowUpgrading(targets);
                }

                var skipped = upgradable.Except(targets).ToList();
                if (skipped.Count > 0) ShowAvailable(skipped);
            }
            else if (firstLoad && TuiStartup.Current?.UpdateAppsOnStart == true)
            {
                await TuiPackageActions.UpdateAllAsync();
                ShowUpgrading(upgradable);
            }
            else
            {
                ShowAvailable(upgradable);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
        }
    }

    private static IReadOnlyList<IPackage> NotifiablePackages(IReadOnlyList<IPackage> packages)
        => packages.Where(p => !Settings.GetDictionaryItem<string, bool>(Settings.K.DisabledPackageManagerNotifications, p.Manager.Name)).ToList();

    private static void ShowAvailable(IReadOnlyList<IPackage> packages)
    {
        if (Settings.AreUpdatesNotificationsDisabled()) return;
        var list = NotifiablePackages(packages);
        if (list.Count == 0) return;
        TuiNotifications.Info(CoreTools.Translate("Updates available!"),
            list.Count == 1
                ? CoreTools.Translate("{0} can be updated to version {1}", list[0].Name, list[0].NewVersionString)
                : CoreTools.Translate("{0} packages can be updated", list.Count));
    }

    private static void ShowUpgrading(IReadOnlyList<IPackage> packages)
    {
        if (Settings.AreUpdatesNotificationsDisabled()) return;
        TuiNotifications.Info(CoreTools.Translate("Updates are being installed"),
            CoreTools.Translate("{0} packages are being updated", packages.Count));
    }
}
