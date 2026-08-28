using System.Text.Json.Serialization;

namespace UniGetUI.Core.Tools.Scheduling;

public sealed class MaintenanceTaskSchedule
{
    public const byte AllDays = 0b0111_1111;
    public const int MinutesPerDay = 24 * 60;
    public const int UnlimitedGrace = -1;

    [JsonIgnore]
    public bool Enabled { get; set; }

    public ScheduleFrequency Frequency { get; set; }

    public int IntervalSeconds { get; set; } = 3600;

    public byte Days { get; set; } = AllDays;

    public int StartMinutes { get; set; } = 9 * 60;

    public int GraceMinutes { get; set; } = UnlimitedGrace;

    public ScheduleInstallTargets InstallTargets { get; set; }

    public DateTime? ConfiguredAtUtc { get; set; }

    public bool HasDay(DayOfWeek day) => (Days & (1 << (int)day)) != 0;

    public void SetDay(DayOfWeek day, bool enabled)
    {
        int mask = 1 << (int)day;
        Days = (byte)(enabled ? Days | mask : Days & ~mask & AllDays);
    }

    public MaintenanceTaskSchedule Clone() => new()
    {
        Enabled = Enabled,
        Frequency = Frequency,
        IntervalSeconds = IntervalSeconds,
        Days = Days,
        StartMinutes = StartMinutes,
        GraceMinutes = GraceMinutes,
        InstallTargets = InstallTargets,
        ConfiguredAtUtc = ConfiguredAtUtc,
    };

    public void Normalize()
    {
        if (IntervalSeconds < 60)
            IntervalSeconds = 3600;

        Days = (byte)(Days & AllDays);

        StartMinutes = Math.Clamp(StartMinutes, 0, MinutesPerDay - 1);

        if (GraceMinutes < 0)
            GraceMinutes = UnlimitedGrace;

        if (!Enum.IsDefined(InstallTargets))
            InstallTargets = ScheduleInstallTargets.AllPackages;
    }
}
