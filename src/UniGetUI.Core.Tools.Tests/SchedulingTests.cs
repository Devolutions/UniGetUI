using UniGetUI.Core.Tools.Scheduling;

namespace UniGetUI.Core.Tools.Tests;

public class SchedulingTests
{
    private static readonly DateTime MondayMorning = new(2026, 8, 17, 9, 5, 0, DateTimeKind.Local);

    private static MaintenanceTaskSchedule Daily(
        int startMinutes = 9 * 60,
        int graceMinutes = MaintenanceTaskSchedule.UnlimitedGrace) => new()
        {
            Enabled = true,
            Frequency = ScheduleFrequency.Daily,
            StartMinutes = startMinutes,
            GraceMinutes = graceMinutes,
        };

    private static MaintenanceTaskSchedule Weekly(
        DayOfWeek day,
        int startMinutes = 9 * 60,
        int graceMinutes = MaintenanceTaskSchedule.UnlimitedGrace)
    {
        var schedule = new MaintenanceTaskSchedule
        {
            Enabled = true,
            Frequency = ScheduleFrequency.Weekly,
            Days = 0,
            StartMinutes = startMinutes,
            GraceMinutes = graceMinutes,
        };
        schedule.SetDay(day, true);
        return schedule;
    }

    private static DateTime? Utc(DateTime? local) => local?.ToUniversalTime();

    [Fact]
    public void DailyTaskIsDueOnceTheStartTimeHasPassed()
    {
        Assert.True(ScheduleEvaluator.IsDue(Daily(), null, MondayMorning));
    }

    [Fact]
    public void DailyTaskIsNotDueBeforeTheStartTime()
    {
        var now = MondayMorning.Date.AddHours(8);
        var lastRun = Utc(MondayMorning.Date.AddDays(-1).AddHours(9));

        Assert.False(ScheduleEvaluator.IsDue(Daily(), lastRun, now));
    }

    [Fact]
    public void DailyTaskDoesNotRunTwiceForTheSameOccurrence()
    {
        var lastRun = Utc(MondayMorning.Date.AddHours(9).AddMinutes(1));

        Assert.False(ScheduleEvaluator.IsDue(Daily(), lastRun, MondayMorning.AddHours(3)));
    }

    [Fact]
    public void DailyTaskIsDueAgainOnTheFollowingDay()
    {
        var lastRun = Utc(MondayMorning.Date.AddHours(9));
        var now = MondayMorning.AddDays(1);

        Assert.True(ScheduleEvaluator.IsDue(Daily(), lastRun, now));
    }

    [Fact]
    public void AMissedOccurrenceIsSkippedOnceTheGracePeriodHasElapsed()
    {
        var now = MondayMorning.Date.AddHours(14);

        Assert.False(ScheduleEvaluator.IsDue(Daily(graceMinutes: 30), null, now));
        Assert.True(ScheduleEvaluator.IsDue(Daily(graceMinutes: MaintenanceTaskSchedule.UnlimitedGrace), null, now));
    }

    [Fact]
    public void ABoundedGraceOnlyAllowsALateStartWithinItsSpan()
    {
        var schedule = Daily(graceMinutes: 30);

        Assert.True(ScheduleEvaluator.IsDue(schedule, null, MondayMorning.Date.AddHours(9)));
        Assert.True(ScheduleEvaluator.IsDue(schedule, null, MondayMorning.Date.AddHours(9).AddMinutes(30)));
        Assert.False(ScheduleEvaluator.IsDue(schedule, null, MondayMorning.Date.AddHours(9).AddMinutes(31)));
    }

    [Fact]
    public void AZeroGraceStillToleratesTheSchedulerTickInterval()
    {
        var schedule = Daily(graceMinutes: 0);

        Assert.True(ScheduleEvaluator.IsDue(schedule, null, MondayMorning.Date.AddHours(9).AddSeconds(45)));
        Assert.False(ScheduleEvaluator.IsDue(schedule, null, MondayMorning.Date.AddHours(9).AddMinutes(10)));
    }

    [Fact]
    public void AnUnlimitedGraceRunsAMissedOccurrenceHoweverLate()
    {
        var schedule = Daily();

        Assert.Equal(MaintenanceTaskSchedule.UnlimitedGrace, schedule.GraceMinutes);
        Assert.True(ScheduleEvaluator.IsDue(schedule, null, MondayMorning.Date.AddHours(23)));
    }

    [Fact]
    public void TheGraceCheckIsIndependentOfWhenTheTaskLastRan()
    {
        var bounded = Daily(graceMinutes: 30);

        Assert.True(ScheduleEvaluator.IsWithinGrace(bounded, MondayMorning.Date.AddHours(9).AddMinutes(20)));
        Assert.False(ScheduleEvaluator.IsWithinGrace(bounded, MondayMorning.Date.AddHours(11)));
        Assert.True(ScheduleEvaluator.IsWithinGrace(Daily(), MondayMorning.Date.AddHours(23)));
        Assert.False(ScheduleEvaluator.IsWithinGrace(Weekly(DayOfWeek.Saturday, graceMinutes: 60), MondayMorning));
    }

    [Fact]
    public void WeeklyTaskOnlyRunsOnTheSelectedDays()
    {
        var schedule = Weekly(DayOfWeek.Saturday, graceMinutes: 60);
        var saturday = new DateTime(2026, 8, 22, 9, 30, 0, DateTimeKind.Local);

        Assert.True(ScheduleEvaluator.IsDue(schedule, null, saturday));
        Assert.False(ScheduleEvaluator.IsDue(schedule, null, MondayMorning));
    }

    [Fact]
    public void WeeklyTaskCatchesUpOnAMissedDayWhenTheGraceIsUnlimited()
    {
        var schedule = Weekly(DayOfWeek.Saturday);

        Assert.True(ScheduleEvaluator.IsDue(schedule, null, MondayMorning));
        Assert.Equal(
            new DateTime(2026, 8, 15, 9, 0, 0, DateTimeKind.Local),
            ScheduleEvaluator.GetMostRecentOccurrence(schedule, MondayMorning)
        );
    }

    [Fact]
    public void NextOccurrenceSkipsUnselectedDays()
    {
        var schedule = Weekly(DayOfWeek.Saturday);

        Assert.Equal(
            new DateTime(2026, 8, 22, 9, 0, 0, DateTimeKind.Local),
            ScheduleEvaluator.GetNextOccurrence(schedule, null, MondayMorning)
        );
    }

    [Fact]
    public void AWeeklyTaskOnItsOnlyDayRollsForwardAFullWeek()
    {
        var schedule = Weekly(DayOfWeek.Monday);
        var afterTodaysOccurrence = MondayMorning.Date.AddHours(10);

        Assert.Equal(
            MondayMorning.Date.AddDays(7).AddHours(9),
            ScheduleEvaluator.GetNextOccurrence(schedule, null, afterTodaysOccurrence)
        );
    }

    [Fact]
    public void AWeeklyTaskOnItsOnlyDayLooksBackAFullWeek()
    {
        var schedule = Weekly(DayOfWeek.Monday);
        var beforeTodaysOccurrence = MondayMorning.Date.AddHours(7);

        Assert.Equal(
            MondayMorning.Date.AddDays(-7).AddHours(9),
            ScheduleEvaluator.GetMostRecentOccurrence(schedule, beforeTodaysOccurrence)
        );
    }

    [Fact]
    public void NextOccurrenceIsTodayWhenTheStartTimeIsStillAhead()
    {
        Assert.Equal(
            MondayMorning.Date.AddHours(9),
            ScheduleEvaluator.GetNextOccurrence(Daily(), null, MondayMorning.Date.AddHours(7))
        );
    }

    [Fact]
    public void DailyOccurrencesAlwaysKeepTheConfiguredLocalStartTime()
    {
        var schedule = Daily(startMinutes: 2 * 60 + 30);
        var cursor = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Local);

        for (int day = 0; day < 40; day++)
        {
            var next = ScheduleEvaluator.GetNextOccurrence(schedule, null, cursor.AddDays(day));
            Assert.NotNull(next);
            Assert.Equal(TimeSpan.FromMinutes(schedule.StartMinutes), next.Value.TimeOfDay);
        }
    }

    [Fact]
    public void MidnightStartTimeIsSupported()
    {
        var schedule = Daily(startMinutes: 0, graceMinutes: 30);

        Assert.True(ScheduleEvaluator.IsDue(schedule, null, MondayMorning.Date.AddMinutes(5)));
        Assert.False(ScheduleEvaluator.IsDue(schedule, null, MondayMorning.Date.AddMinutes(45)));
    }

    [Fact]
    public void IntervalTaskIsDueOnceTheIntervalHasElapsed()
    {
        var schedule = new MaintenanceTaskSchedule
        {
            Enabled = true,
            Frequency = ScheduleFrequency.Interval,
            IntervalSeconds = 3600,
        };

        Assert.True(ScheduleEvaluator.IsDue(schedule, Utc(MondayMorning.AddHours(-2)), MondayMorning));
        Assert.False(ScheduleEvaluator.IsDue(schedule, Utc(MondayMorning.AddMinutes(-10)), MondayMorning));
        Assert.True(ScheduleEvaluator.IsDue(schedule, null, MondayMorning));
    }

    [Fact]
    public void DisabledSchedulesAreNeverDue()
    {
        var schedule = Daily();
        schedule.Enabled = false;

        Assert.False(ScheduleEvaluator.IsDue(schedule, null, MondayMorning));
    }

    [Fact]
    public void NonTimeBasedFrequenciesAreNotDrivenByTheClock()
    {
        foreach (var frequency in new[] { ScheduleFrequency.AtAppStart, ScheduleFrequency.AfterEveryUpdateCheck })
        {
            var schedule = new MaintenanceTaskSchedule { Enabled = true, Frequency = frequency };

            Assert.False(ScheduleEvaluator.IsTimeBased(frequency));
            Assert.False(ScheduleEvaluator.IsDue(schedule, null, MondayMorning));
            Assert.Null(ScheduleEvaluator.GetNextOccurrence(schedule, null, MondayMorning));
        }
    }

    [Fact]
    public void OccurrencesFromBeforeTheScheduleWasConfiguredAreNotRun()
    {
        var schedule = Weekly(DayOfWeek.Saturday);
        schedule.ConfiguredAtUtc = Utc(MondayMorning.AddHours(-1));

        Assert.False(ScheduleEvaluator.IsDue(schedule, null, MondayMorning));
        Assert.True(ScheduleEvaluator.IsDue(schedule, null, new DateTime(2026, 8, 22, 9, 30, 0, DateTimeKind.Local)));
    }

    [Fact]
    public void AMoreRecentRunTakesPrecedenceOverTheConfigurationTime()
    {
        var schedule = Daily();
        schedule.ConfiguredAtUtc = Utc(MondayMorning.Date.AddDays(-5));
        var lastRun = Utc(MondayMorning.Date.AddHours(9).AddMinutes(1));

        Assert.False(ScheduleEvaluator.IsDue(schedule, lastRun, MondayMorning.AddHours(2)));
        Assert.True(ScheduleEvaluator.IsDue(schedule, lastRun, MondayMorning.AddDays(1)));
    }

    [Fact]
    public void IntervalCountsFromTheConfigurationTimeWhenTheTaskNeverRan()
    {
        var schedule = new MaintenanceTaskSchedule
        {
            Enabled = true,
            Frequency = ScheduleFrequency.Interval,
            IntervalSeconds = 3600,
            ConfiguredAtUtc = Utc(MondayMorning.AddMinutes(-10)),
        };

        Assert.False(ScheduleEvaluator.IsDue(schedule, null, MondayMorning));
        Assert.True(ScheduleEvaluator.IsDue(schedule, null, MondayMorning.AddHours(1)));
    }

    [Fact]
    public void AWeeklyScheduleWithNoDaysNeverRuns()
    {
        var schedule = new MaintenanceTaskSchedule
        {
            Enabled = true,
            Frequency = ScheduleFrequency.Weekly,
            Days = 0,
            StartMinutes = 9 * 60,
        };
        schedule.Normalize();

        Assert.Equal(0, schedule.Days);
        Assert.False(ScheduleEvaluator.IsDue(schedule, null, MondayMorning));
        Assert.Null(ScheduleEvaluator.GetMostRecentOccurrence(schedule, MondayMorning));
        Assert.Null(ScheduleEvaluator.GetNextOccurrence(schedule, null, MondayMorning));
    }

    [Fact]
    public void ReselectingADayAfterClearingThemAllRestoresTheSchedule()
    {
        var schedule = new MaintenanceTaskSchedule
        {
            Enabled = true,
            Frequency = ScheduleFrequency.Weekly,
            Days = 0,
            StartMinutes = 9 * 60,
            GraceMinutes = 60,
        };
        schedule.SetDay(DayOfWeek.Saturday, true);
        schedule.Normalize();

        Assert.True(ScheduleEvaluator.IsDue(schedule, null, new DateTime(2026, 8, 22, 9, 30, 0, DateTimeKind.Local)));
    }

    [Fact]
    public void NormalizeClampsInvalidValues()
    {
        var schedule = new MaintenanceTaskSchedule
        {
            IntervalSeconds = 5,
            StartMinutes = 5000,
            GraceMinutes = -10,
        };
        schedule.Normalize();

        Assert.Equal(MaintenanceScheduleStore.DefaultCheckIntervalSeconds, schedule.IntervalSeconds);
        Assert.Equal(MaintenanceTaskSchedule.MinutesPerDay - 1, schedule.StartMinutes);
        Assert.Equal(MaintenanceTaskSchedule.UnlimitedGrace, schedule.GraceMinutes);
    }

    [Fact]
    public void SetDayTogglesSingleDaysOnly()
    {
        var schedule = new MaintenanceTaskSchedule { Days = MaintenanceTaskSchedule.AllDays };
        schedule.SetDay(DayOfWeek.Sunday, false);

        Assert.False(schedule.HasDay(DayOfWeek.Sunday));
        Assert.True(schedule.HasDay(DayOfWeek.Monday));
        Assert.True(schedule.HasDay(DayOfWeek.Saturday));

        schedule.SetDay(DayOfWeek.Sunday, true);
        Assert.Equal(MaintenanceTaskSchedule.AllDays, schedule.Days);
    }

    [Fact]
    public void SchedulesSurviveAJsonRoundTrip()
    {
        var schedule = Weekly(DayOfWeek.Saturday, startMinutes: 150, graceMinutes: 120);
        schedule.IntervalSeconds = 7200;

        string json = SchedulingJson.SerializeSchedules(
            new Dictionary<string, MaintenanceTaskSchedule>
            {
                [MaintenanceTasks.GetId(MaintenanceTaskKind.InstallUpdates)] = schedule,
            }
        );

        Assert.Contains("\"Weekly\"", json);
        Assert.Contains("install-updates", json);

        var restored = SchedulingJson.DeserializeSchedules(json);
        Assert.NotNull(restored);

        var value = restored[MaintenanceTasks.GetId(MaintenanceTaskKind.InstallUpdates)];
        Assert.Equal(ScheduleFrequency.Weekly, value.Frequency);
        Assert.Equal(schedule.Days, value.Days);
        Assert.Equal(150, value.StartMinutes);
        Assert.Equal(120, value.GraceMinutes);
        Assert.Equal(7200, value.IntervalSeconds);
        Assert.False(value.Enabled);
    }

    [Fact]
    public void EveryTaskKindHasAStableIdAndSupportedFrequencies()
    {
        foreach (var kind in MaintenanceTasks.All)
        {
            string id = MaintenanceTasks.GetId(kind);

            Assert.NotEmpty(id);
            Assert.Equal(kind, MaintenanceTasks.FromId(id));
            Assert.Contains(MaintenanceTasks.GetDefaultFrequency(kind), MaintenanceTasks.GetSupportedFrequencies(kind));
            Assert.Contains(ScheduleFrequency.Daily, MaintenanceTasks.GetSupportedFrequencies(kind));
            Assert.Contains(ScheduleFrequency.Weekly, MaintenanceTasks.GetSupportedFrequencies(kind));
        }

        Assert.Null(MaintenanceTasks.FromId("not-a-task"));
    }
}
