namespace UniGetUI.Core.Tools.Scheduling;

public enum MaintenanceTaskKind
{
    CheckForUpdates,
    InstallUpdates,
    LocalBackup,
    CloudBackup,
}

public static class MaintenanceTasks
{
    public static readonly IReadOnlyList<MaintenanceTaskKind> All =
    [
        MaintenanceTaskKind.CheckForUpdates,
        MaintenanceTaskKind.InstallUpdates,
        MaintenanceTaskKind.LocalBackup,
        MaintenanceTaskKind.CloudBackup,
    ];

    public static string GetId(MaintenanceTaskKind kind) => kind switch
    {
        MaintenanceTaskKind.CheckForUpdates => "check-updates",
        MaintenanceTaskKind.InstallUpdates => "install-updates",
        MaintenanceTaskKind.LocalBackup => "local-backup",
        MaintenanceTaskKind.CloudBackup => "cloud-backup",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static MaintenanceTaskKind? FromId(string id) => id switch
    {
        "check-updates" => MaintenanceTaskKind.CheckForUpdates,
        "install-updates" => MaintenanceTaskKind.InstallUpdates,
        "local-backup" => MaintenanceTaskKind.LocalBackup,
        "cloud-backup" => MaintenanceTaskKind.CloudBackup,
        _ => null,
    };

    public static IReadOnlyList<ScheduleFrequency> GetSupportedFrequencies(MaintenanceTaskKind kind) => kind switch
    {
        MaintenanceTaskKind.CheckForUpdates =>
        [
            ScheduleFrequency.Interval,
            ScheduleFrequency.Daily,
            ScheduleFrequency.Weekly,
        ],
        MaintenanceTaskKind.InstallUpdates =>
        [
            ScheduleFrequency.AfterEveryUpdateCheck,
            ScheduleFrequency.Daily,
            ScheduleFrequency.Weekly,
        ],
        _ =>
        [
            ScheduleFrequency.AtAppStart,
            ScheduleFrequency.Daily,
            ScheduleFrequency.Weekly,
        ],
    };

    public static ScheduleFrequency GetDefaultFrequency(MaintenanceTaskKind kind) => kind switch
    {
        MaintenanceTaskKind.CheckForUpdates => ScheduleFrequency.Interval,
        MaintenanceTaskKind.InstallUpdates => ScheduleFrequency.AfterEveryUpdateCheck,
        _ => ScheduleFrequency.AtAppStart,
    };
}
