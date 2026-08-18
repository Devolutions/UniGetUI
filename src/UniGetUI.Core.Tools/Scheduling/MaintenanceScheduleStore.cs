using System.Globalization;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;

namespace UniGetUI.Core.Tools.Scheduling;

public static class MaintenanceScheduleStore
{
    public const int DefaultCheckIntervalSeconds = 3600;

    private static readonly object CacheLock = new();
    private static readonly object LastRunLock = new();
    private static Dictionary<string, MaintenanceTaskSchedule>? _cachedSchedules;
    private static string _cachedRaw = "";

    public static MaintenanceTaskSchedule Get(MaintenanceTaskKind kind)
    {
        MaintenanceTaskSchedule schedule = GetStoredCopy(kind) ?? NewDefault(kind);

        schedule.Enabled = IsEnabled(kind);

        if (kind is MaintenanceTaskKind.CheckForUpdates)
            schedule.IntervalSeconds = ReadCheckIntervalSeconds();

        if (!MaintenanceTasks.GetSupportedFrequencies(kind).Contains(schedule.Frequency))
            schedule.Frequency = MaintenanceTasks.GetDefaultFrequency(kind);

        schedule.Normalize();
        return schedule;
    }

    public static void Set(MaintenanceTaskKind kind, MaintenanceTaskSchedule schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        schedule.Normalize();
        schedule.ConfiguredAtUtc = DateTime.UtcNow;

        SetEnabled(kind, schedule.Enabled);

        if (kind is MaintenanceTaskKind.CheckForUpdates)
        {
            Settings.SetValue(
                Settings.K.UpdatesCheckInterval,
                schedule.IntervalSeconds.ToString(CultureInfo.InvariantCulture)
            );
        }

        string raw = Settings.GetValue(Settings.K.MaintenanceSchedules);
        lock (CacheLock)
        {
            Dictionary<string, MaintenanceTaskSchedule> updated = new(GetCachedSchedulesLocked(raw))
            {
                [MaintenanceTasks.GetId(kind)] = schedule.Clone(),
            };
            PersistLocked(updated);
        }
    }

    public static bool IsEnabled(MaintenanceTaskKind kind) => kind switch
    {
        MaintenanceTaskKind.CheckForUpdates => !Settings.Get(Settings.K.DisableAutoCheckforUpdates),
        MaintenanceTaskKind.InstallUpdates => Settings.Get(Settings.K.AutomaticallyUpdatePackages),
        MaintenanceTaskKind.LocalBackup => Settings.Get(Settings.K.EnablePackageBackup_LOCAL),
        MaintenanceTaskKind.CloudBackup => Settings.Get(Settings.K.EnablePackageBackup_CLOUD),
        _ => false,
    };

    public static DateTime? GetLastRun(MaintenanceTaskKind kind)
    {
        string? raw = Settings.GetDictionaryItem<string, string>(
            Settings.K.MaintenanceTaskLastRun,
            MaintenanceTasks.GetId(kind)
        );

        if (string.IsNullOrWhiteSpace(raw))
            return null;

        return DateTime.TryParse(
            raw,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out DateTime parsed
        )
            ? parsed.ToUniversalTime()
            : null;
    }

    public static void SetLastRun(MaintenanceTaskKind kind, DateTime runTimeUtc)
    {
        lock (LastRunLock)
        {
            Settings.SetDictionaryItem(
                Settings.K.MaintenanceTaskLastRun,
                MaintenanceTasks.GetId(kind),
                runTimeUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
            );
        }
    }

    public static MaintenanceTaskSchedule NewDefault(MaintenanceTaskKind kind)
    {
        var schedule = new MaintenanceTaskSchedule
        {
            Frequency = MaintenanceTasks.GetDefaultFrequency(kind),
            StartMinutes = kind switch
            {
                MaintenanceTaskKind.InstallUpdates => 3 * 60,
                MaintenanceTaskKind.LocalBackup or MaintenanceTaskKind.CloudBackup => 20 * 60,
                _ => 9 * 60,
            },
            IntervalSeconds = DefaultCheckIntervalSeconds,
        };
        schedule.Normalize();
        return schedule;
    }

    private static void SetEnabled(MaintenanceTaskKind kind, bool value)
    {
        switch (kind)
        {
            case MaintenanceTaskKind.CheckForUpdates:
                Settings.Set(Settings.K.DisableAutoCheckforUpdates, !value);
                break;
            case MaintenanceTaskKind.InstallUpdates:
                Settings.Set(Settings.K.AutomaticallyUpdatePackages, value);
                break;
            case MaintenanceTaskKind.LocalBackup:
                Settings.Set(Settings.K.EnablePackageBackup_LOCAL, value);
                break;
            case MaintenanceTaskKind.CloudBackup:
                Settings.Set(Settings.K.EnablePackageBackup_CLOUD, value);
                break;
        }
    }

    private static int ReadCheckIntervalSeconds()
    {
        return int.TryParse(
            Settings.GetValue(Settings.K.UpdatesCheckInterval),
            CultureInfo.InvariantCulture,
            out int parsed
        ) && parsed >= 60
            ? parsed
            : DefaultCheckIntervalSeconds;
    }

    private static MaintenanceTaskSchedule? GetStoredCopy(MaintenanceTaskKind kind)
    {
        string raw = Settings.GetValue(Settings.K.MaintenanceSchedules);

        lock (CacheLock)
        {
            return GetCachedSchedulesLocked(raw)
                .TryGetValue(MaintenanceTasks.GetId(kind), out var found) && found is not null
                    ? found.Clone()
                    : null;
        }
    }

    private static Dictionary<string, MaintenanceTaskSchedule> GetCachedSchedulesLocked(string raw)
    {
        if (_cachedSchedules is not null && raw == _cachedRaw)
            return _cachedSchedules;

        Dictionary<string, MaintenanceTaskSchedule> parsed = [];
        if (!string.IsNullOrWhiteSpace(raw))
        {
            try
            {
                parsed = SchedulingJson.DeserializeSchedules(raw) ?? [];
            }
            catch (Exception ex)
            {
                Logger.Warn("Could not read the stored maintenance schedules, falling back to defaults");
                Logger.Warn(ex);
            }
        }

        _cachedRaw = raw;
        _cachedSchedules = parsed;
        return parsed;
    }

    private static void PersistLocked(Dictionary<string, MaintenanceTaskSchedule> schedules)
    {
        try
        {
            string raw = SchedulingJson.SerializeSchedules(schedules);
            Settings.SetValue(Settings.K.MaintenanceSchedules, raw);
            _cachedRaw = raw;
            _cachedSchedules = schedules;
        }
        catch (Exception ex)
        {
            Logger.Error("Could not save the maintenance schedules");
            Logger.Error(ex);
        }
    }
}
