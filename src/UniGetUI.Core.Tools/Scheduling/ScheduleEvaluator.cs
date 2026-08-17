namespace UniGetUI.Core.Tools.Scheduling;

public static class ScheduleEvaluator
{
    private const int DaysToScan = 7;

    public static bool IsTimeBased(ScheduleFrequency frequency)
        => frequency is ScheduleFrequency.Daily or ScheduleFrequency.Weekly;

    public static TimeSpan GetEffectiveWindow(MaintenanceTaskSchedule schedule)
        => schedule.WindowMinutes > 0
            ? TimeSpan.FromMinutes(schedule.WindowMinutes)
            : TimeSpan.FromDays(1);

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

    public static bool IsInsideWindow(MaintenanceTaskSchedule schedule, DateTime nowLocal)
    {
        DateTime? occurrence = GetMostRecentOccurrence(schedule, nowLocal);
        if (occurrence is null)
            return false;

        TimeSpan elapsed = nowLocal - occurrence.Value;
        return elapsed >= TimeSpan.Zero && elapsed <= GetEffectiveWindow(schedule);
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

        return schedule.RunMissed || nowLocal - occurrence.Value <= GetEffectiveWindow(schedule);
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
