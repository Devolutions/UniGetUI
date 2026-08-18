namespace UniGetUI.Core.Tools.Scheduling;

public static class ScheduleEvaluator
{
    private const int DaysToScan = 7;
    private const int MinimumGraceMinutes = 2;

    public static bool IsTimeBased(ScheduleFrequency frequency)
        => frequency is ScheduleFrequency.Daily or ScheduleFrequency.Weekly;

    public static TimeSpan GetGracePeriod(MaintenanceTaskSchedule schedule)
        => schedule.GraceMinutes < 0
            ? TimeSpan.MaxValue
            : TimeSpan.FromMinutes(Math.Max(schedule.GraceMinutes, MinimumGraceMinutes));

    public static DateTime? GetMostRecentOccurrence(MaintenanceTaskSchedule schedule, DateTime nowLocal)
    {
        if (!IsTimeBased(schedule.Frequency))
            return null;

        TimeSpan start = TimeSpan.FromMinutes(schedule.StartMinutes);
        for (int daysBack = 0; daysBack <= DaysToScan; daysBack++)
        {
            DateTime day = nowLocal.Date.AddDays(-daysBack);
            if (schedule.Frequency is ScheduleFrequency.Weekly && !schedule.HasDay(day.DayOfWeek))
                continue;

            DateTime occurrence = day + start;
            if (occurrence <= nowLocal)
                return occurrence;
        }

        return null;
    }

    public static DateTime? GetNextOccurrence(
        MaintenanceTaskSchedule schedule,
        DateTime? lastRunUtc,
        DateTime nowLocal)
    {
        if (schedule.Frequency is ScheduleFrequency.Interval)
        {
            DateTime? lastRunLocal = GetFloor(schedule, lastRunUtc);
            if (lastRunLocal is null)
                return nowLocal;

            DateTime next = lastRunLocal.Value.AddSeconds(schedule.IntervalSeconds);
            return next < nowLocal ? nowLocal : next;
        }

        if (!IsTimeBased(schedule.Frequency))
            return null;

        TimeSpan start = TimeSpan.FromMinutes(schedule.StartMinutes);
        for (int daysAhead = 0; daysAhead <= DaysToScan; daysAhead++)
        {
            DateTime day = nowLocal.Date.AddDays(daysAhead);
            if (schedule.Frequency is ScheduleFrequency.Weekly && !schedule.HasDay(day.DayOfWeek))
                continue;

            DateTime occurrence = day + start;
            if (occurrence > nowLocal)
                return occurrence;
        }

        return null;
    }

    public static bool IsDue(MaintenanceTaskSchedule schedule, DateTime? lastRunUtc, DateTime nowLocal)
    {
        if (!schedule.Enabled)
            return false;

        DateTime? floorLocal = GetFloor(schedule, lastRunUtc);

        if (schedule.Frequency is ScheduleFrequency.Interval)
        {
            return floorLocal is null
                || nowLocal - floorLocal.Value >= TimeSpan.FromSeconds(schedule.IntervalSeconds);
        }

        if (!IsTimeBased(schedule.Frequency))
            return false;

        DateTime? occurrence = GetMostRecentOccurrence(schedule, nowLocal);
        if (occurrence is null)
            return false;

        if (floorLocal is not null && floorLocal.Value >= occurrence.Value)
            return false;

        return IsWithinGrace(schedule, nowLocal);
    }

    public static bool IsWithinGrace(MaintenanceTaskSchedule schedule, DateTime nowLocal)
    {
        DateTime? occurrence = GetMostRecentOccurrence(schedule, nowLocal);
        if (occurrence is null)
            return false;

        TimeSpan grace = GetGracePeriod(schedule);
        return grace == TimeSpan.MaxValue || nowLocal - occurrence.Value <= grace;
    }

    private static DateTime? GetFloor(MaintenanceTaskSchedule schedule, DateTime? lastRunUtc)
    {
        DateTime? lastRunLocal = ToLocal(lastRunUtc);
        DateTime? configuredLocal = ToLocal(schedule.ConfiguredAtUtc);

        if (lastRunLocal is null)
            return configuredLocal;
        if (configuredLocal is null)
            return lastRunLocal;

        return lastRunLocal.Value > configuredLocal.Value ? lastRunLocal : configuredLocal;
    }

    private static DateTime? ToLocal(DateTime? utc) => utc is null
        ? null
        : DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc).ToLocalTime();
}
