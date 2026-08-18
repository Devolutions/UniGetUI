using UniGetUI.Core.Data;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools.Scheduling;

namespace UniGetUI.Core.Tools.Tests;

public class MaintenanceScheduleStoreTests : IDisposable
{
    private static readonly int[] LegacyIntervalOptions =
        [600, 1800, 3600, 7200, 14400, 28800, 43200, 86400, 172800, 259200, 604800];

    private readonly string _testRoot;

    public MaintenanceScheduleStoreTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testRoot);
        CoreData.TEST_DataDirectoryOverride = Path.Combine(_testRoot, "Data");
        Directory.CreateDirectory(CoreData.UniGetUIUserConfigurationDirectory);
    }

    public void Dispose()
    {
        CoreData.TEST_DataDirectoryOverride = null;
        Directory.Delete(_testRoot, true);
        GC.SuppressFinalize(this);
    }

    private static void Save(MaintenanceTaskKind kind, Action<MaintenanceTaskSchedule> edit)
    {
        var schedule = MaintenanceScheduleStore.Get(kind);
        edit(schedule);
        MaintenanceScheduleStore.Set(kind, schedule);
    }

    [Fact]
    public void ThePeriodicCheckToggleReadsAndWritesDisableAutoCheckforUpdates()
    {
        Settings.Set(Settings.K.DisableAutoCheckforUpdates, false);
        Assert.True(MaintenanceScheduleStore.Get(MaintenanceTaskKind.CheckForUpdates).Enabled);

        Settings.Set(Settings.K.DisableAutoCheckforUpdates, true);
        Assert.False(MaintenanceScheduleStore.Get(MaintenanceTaskKind.CheckForUpdates).Enabled);

        Save(MaintenanceTaskKind.CheckForUpdates, s => s.Enabled = true);
        Assert.False(Settings.Get(Settings.K.DisableAutoCheckforUpdates));

        Save(MaintenanceTaskKind.CheckForUpdates, s => s.Enabled = false);
        Assert.True(Settings.Get(Settings.K.DisableAutoCheckforUpdates));
    }

    [Fact]
    public void TheCheckIntervalReadsAndWritesUpdatesCheckInterval()
    {
        Settings.SetValue(Settings.K.UpdatesCheckInterval, "7200");
        Assert.Equal(7200, MaintenanceScheduleStore.Get(MaintenanceTaskKind.CheckForUpdates).IntervalSeconds);

        Save(MaintenanceTaskKind.CheckForUpdates, s => s.IntervalSeconds = 43200);
        Assert.Equal("43200", Settings.GetValue(Settings.K.UpdatesCheckInterval));
    }

    [Fact]
    public void AnUnsetCheckIntervalFallsBackToTheLegacyDefault()
    {
        Settings.SetValue(Settings.K.UpdatesCheckInterval, "");
        Assert.Equal(3600, MaintenanceScheduleStore.Get(MaintenanceTaskKind.CheckForUpdates).IntervalSeconds);

        Settings.SetValue(Settings.K.UpdatesCheckInterval, "not-a-number");
        Assert.Equal(3600, MaintenanceScheduleStore.Get(MaintenanceTaskKind.CheckForUpdates).IntervalSeconds);
    }

    [Fact]
    public void EveryIntervalOfferedByTheOldUpdatesPageRoundTrips()
    {
        foreach (int seconds in LegacyIntervalOptions)
        {
            Save(MaintenanceTaskKind.CheckForUpdates, s =>
            {
                s.Frequency = ScheduleFrequency.Interval;
                s.IntervalSeconds = seconds;
            });

            var stored = MaintenanceScheduleStore.Get(MaintenanceTaskKind.CheckForUpdates);
            Assert.Equal(seconds, stored.IntervalSeconds);
            Assert.Equal(ScheduleFrequency.Interval, stored.Frequency);
            Assert.Equal(seconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Settings.GetValue(Settings.K.UpdatesCheckInterval));
        }
    }

    [Fact]
    public void TheAutomaticInstallToggleReadsAndWritesAutomaticallyUpdatePackages()
    {
        Settings.Set(Settings.K.AutomaticallyUpdatePackages, true);
        Assert.True(MaintenanceScheduleStore.Get(MaintenanceTaskKind.InstallUpdates).Enabled);

        Settings.Set(Settings.K.AutomaticallyUpdatePackages, false);
        Assert.False(MaintenanceScheduleStore.Get(MaintenanceTaskKind.InstallUpdates).Enabled);

        Save(MaintenanceTaskKind.InstallUpdates, s => s.Enabled = true);
        Assert.True(Settings.Get(Settings.K.AutomaticallyUpdatePackages));

        Save(MaintenanceTaskKind.InstallUpdates, s => s.Enabled = false);
        Assert.False(Settings.Get(Settings.K.AutomaticallyUpdatePackages));
    }

    [Fact]
    public void TheBackupTogglesReadAndWriteTheBackupSettings()
    {
        Settings.Set(Settings.K.EnablePackageBackup_LOCAL, true);
        Settings.Set(Settings.K.EnablePackageBackup_CLOUD, false);
        Assert.True(MaintenanceScheduleStore.Get(MaintenanceTaskKind.LocalBackup).Enabled);
        Assert.False(MaintenanceScheduleStore.Get(MaintenanceTaskKind.CloudBackup).Enabled);

        Save(MaintenanceTaskKind.LocalBackup, s => s.Enabled = false);
        Save(MaintenanceTaskKind.CloudBackup, s => s.Enabled = true);
        Assert.False(Settings.Get(Settings.K.EnablePackageBackup_LOCAL));
        Assert.True(Settings.Get(Settings.K.EnablePackageBackup_CLOUD));
    }

    [Fact]
    public void ADefaultCheckScheduleMatchesTheLegacyBehaviour()
    {
        Settings.Set(Settings.K.DisableAutoCheckforUpdates, false);
        Settings.SetValue(Settings.K.UpdatesCheckInterval, "3600");
        Settings.SetValue(Settings.K.MaintenanceSchedules, "");

        var check = MaintenanceScheduleStore.Get(MaintenanceTaskKind.CheckForUpdates);
        Assert.True(check.Enabled);
        Assert.Equal(ScheduleFrequency.Interval, check.Frequency);
        Assert.Equal(3600, check.IntervalSeconds);

        Settings.Set(Settings.K.AutomaticallyUpdatePackages, true);
        var install = MaintenanceScheduleStore.Get(MaintenanceTaskKind.InstallUpdates);
        Assert.True(install.Enabled);
        Assert.Equal(ScheduleFrequency.AfterEveryUpdateCheck, install.Frequency);
    }

    [Fact]
    public void ConcurrentReadsAndWritesNeitherThrowNorDropEntries()
    {
        var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        for (int round = 0; round < 60; round++)
        {
            Settings.SetValue(Settings.K.MaintenanceSchedules, "");

            Parallel.For(0, 64, i =>
            {
                try
                {
                    if (i % 2 == 0)
                    {
                        var kind = MaintenanceTasks.All[(i / 2) % MaintenanceTasks.All.Count];
                        Save(kind, s => s.StartMinutes = 60 * (i % 24));
                    }
                    else
                    {
                        foreach (var kind in MaintenanceTasks.All)
                            _ = MaintenanceScheduleStore.Get(kind).StartMinutes;
                    }
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                }
            });
        }

        Assert.Empty(errors);

        Save(MaintenanceTaskKind.LocalBackup, s =>
        {
            s.Frequency = ScheduleFrequency.Daily;
            s.StartMinutes = 600;
        });
        Save(MaintenanceTaskKind.CloudBackup, s => s.StartMinutes = 300);

        var local = MaintenanceScheduleStore.Get(MaintenanceTaskKind.LocalBackup);
        Assert.Equal(ScheduleFrequency.Daily, local.Frequency);
        Assert.Equal(600, local.StartMinutes);
        Assert.Equal(300, MaintenanceScheduleStore.Get(MaintenanceTaskKind.CloudBackup).StartMinutes);
    }

    [Fact]
    public void ConcurrentWritesToDifferentTasksAllSurvive()
    {
        for (int round = 0; round < 40; round++)
        {
            Settings.SetValue(Settings.K.MaintenanceSchedules, "");

            Parallel.ForEach(MaintenanceTasks.All, kind => Save(kind, s => s.StartMinutes = 123));

            string raw = Settings.GetValue(Settings.K.MaintenanceSchedules);
            foreach (var kind in MaintenanceTasks.All)
            {
                Assert.Contains(MaintenanceTasks.GetId(kind), raw);
                Assert.Equal(123, MaintenanceScheduleStore.Get(kind).StartMinutes);
            }
        }
    }

    [Fact]
    public void ANewScheduleRunsAMissedOccurrenceRatherThanSkippingIt()
    {
        Settings.SetValue(Settings.K.MaintenanceSchedules, "");

        foreach (var kind in MaintenanceTasks.All)
        {
            var schedule = MaintenanceScheduleStore.Get(kind);
            Assert.Equal(MaintenanceTaskSchedule.UnlimitedGrace, schedule.GraceMinutes);
            Assert.Equal(TimeSpan.MaxValue, ScheduleEvaluator.GetGracePeriod(schedule));
        }
    }

    [Fact]
    public void OneUnreadableEntryDoesNotDiscardTheOtherTasks()
    {
        Save(MaintenanceTaskKind.LocalBackup, s =>
        {
            s.Frequency = ScheduleFrequency.Weekly;
            s.StartMinutes = 480;
            s.GraceMinutes = 60;
        });

        string good = Settings.GetValue(Settings.K.MaintenanceSchedules);
        Assert.Contains("local-backup", good);

        string corrupted = good.TrimEnd().TrimEnd('}').TrimEnd().TrimEnd(',')
            + ",\"install-updates\": {\"Frequency\": \"NotAFrequency\"} }";
        Settings.SetValue(Settings.K.MaintenanceSchedules, corrupted);

        var salvaged = MaintenanceScheduleStore.Get(MaintenanceTaskKind.LocalBackup);
        Assert.Equal(ScheduleFrequency.Weekly, salvaged.Frequency);
        Assert.Equal(480, salvaged.StartMinutes);
        Assert.Equal(60, salvaged.GraceMinutes);

        var lost = MaintenanceScheduleStore.Get(MaintenanceTaskKind.InstallUpdates);
        Assert.Equal(MaintenanceTasks.GetDefaultFrequency(MaintenanceTaskKind.InstallUpdates), lost.Frequency);
    }

    [Fact]
    public void SyntacticallyBrokenSchedulesAreKeptAsideForRecovery()
    {
        Settings.SetValue(Settings.K.MaintenanceSchedules, "{ this is not json");

        var schedule = MaintenanceScheduleStore.Get(MaintenanceTaskKind.LocalBackup);
        Assert.Equal(MaintenanceTasks.GetDefaultFrequency(MaintenanceTaskKind.LocalBackup), schedule.Frequency);

        string backup = Path.Join(
            CoreData.UniGetUIUserConfigurationDirectory,
            $"{Settings.ResolveKey(Settings.K.MaintenanceSchedules)}.invalid"
        );
        Assert.True(File.Exists(backup));
        Assert.Contains("this is not json", File.ReadAllText(backup));
    }

    [Fact]
    public void ClearingTheLastRunMakesATaskDueAgain()
    {
        MaintenanceScheduleStore.SetLastRun(MaintenanceTaskKind.LocalBackup, DateTime.UtcNow);
        Assert.NotNull(MaintenanceScheduleStore.GetLastRun(MaintenanceTaskKind.LocalBackup));

        MaintenanceScheduleStore.ClearLastRun(MaintenanceTaskKind.LocalBackup);
        Assert.Null(MaintenanceScheduleStore.GetLastRun(MaintenanceTaskKind.LocalBackup));
    }

    [Fact]
    public void AFailedAttemptIsRecordedSeparatelyFromTheLastRun()
    {
        var runTime = DateTime.UtcNow.AddHours(-2);
        MaintenanceScheduleStore.SetLastRun(MaintenanceTaskKind.LocalBackup, runTime);
        Assert.Null(MaintenanceScheduleStore.GetLastFailure(MaintenanceTaskKind.LocalBackup));

        var failureTime = DateTime.UtcNow;
        MaintenanceScheduleStore.SetLastFailure(MaintenanceTaskKind.LocalBackup, failureTime);

        var storedFailure = MaintenanceScheduleStore.GetLastFailure(MaintenanceTaskKind.LocalBackup);
        Assert.NotNull(storedFailure);
        Assert.Equal(failureTime, storedFailure.Value, TimeSpan.FromSeconds(1));

        var storedRun = MaintenanceScheduleStore.GetLastRun(MaintenanceTaskKind.LocalBackup);
        Assert.NotNull(storedRun);
        Assert.Equal(runTime, storedRun.Value, TimeSpan.FromSeconds(1));

        MaintenanceScheduleStore.ClearLastFailure(MaintenanceTaskKind.LocalBackup);
        Assert.Null(MaintenanceScheduleStore.GetLastFailure(MaintenanceTaskKind.LocalBackup));
        Assert.NotNull(MaintenanceScheduleStore.GetLastRun(MaintenanceTaskKind.LocalBackup));
    }

    [Fact]
    public void FailureStateIsTrackedPerTask()
    {
        MaintenanceScheduleStore.SetLastFailure(MaintenanceTaskKind.CloudBackup, DateTime.UtcNow);

        Assert.NotNull(MaintenanceScheduleStore.GetLastFailure(MaintenanceTaskKind.CloudBackup));
        Assert.Null(MaintenanceScheduleStore.GetLastFailure(MaintenanceTaskKind.LocalBackup));
        Assert.Null(MaintenanceScheduleStore.GetLastFailure(MaintenanceTaskKind.CheckForUpdates));
    }

    [Fact]
    public void TimingFieldsSurviveARoundTripThroughTheSettingsFile()
    {
        Save(MaintenanceTaskKind.InstallUpdates, s =>
        {
            s.Enabled = true;
            s.Frequency = ScheduleFrequency.Weekly;
            s.Days = 0;
            s.SetDay(DayOfWeek.Saturday, true);
            s.StartMinutes = 150;
            s.GraceMinutes = 120;
        });

        var stored = MaintenanceScheduleStore.Get(MaintenanceTaskKind.InstallUpdates);
        Assert.Equal(ScheduleFrequency.Weekly, stored.Frequency);
        Assert.True(stored.HasDay(DayOfWeek.Saturday));
        Assert.False(stored.HasDay(DayOfWeek.Sunday));
        Assert.Equal(150, stored.StartMinutes);
        Assert.Equal(120, stored.GraceMinutes);
        Assert.NotNull(stored.ConfiguredAtUtc);
    }
}
