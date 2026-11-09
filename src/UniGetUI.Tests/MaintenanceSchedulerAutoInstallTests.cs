using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Core.Data;
using UniGetUI.Core.Tools.Scheduling;
using CoreSettings = UniGetUI.Core.SettingsEngine.Settings;

namespace UniGetUI.Tests;

public sealed class MaintenanceSchedulerAutoInstallTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        nameof(MaintenanceSchedulerAutoInstallTests),
        Guid.NewGuid().ToString("N"));

    public MaintenanceSchedulerAutoInstallTests()
    {
        CoreData.TEST_DataDirectoryOverride = Path.Combine(_testRoot, "Data");
        Directory.CreateDirectory(CoreData.UniGetUIUserConfigurationDirectory);
        CoreSettings.ResetSettings();
        MaintenanceScheduler.MarkAutoInstallHandled();
    }

    public void Dispose()
    {
        MaintenanceScheduler.MarkAutoInstallHandled();
        CoreSettings.ResetSettings();
        CoreData.TEST_DataDirectoryOverride = null;
        Directory.Delete(_testRoot, recursive: true);
    }

    private static void InstallAfterEveryUpdateCheck()
    {
        var install = MaintenanceScheduleStore.Get(MaintenanceTaskKind.InstallUpdates);
        install.Enabled = true;
        install.Frequency = ScheduleFrequency.AfterEveryUpdateCheck;
        MaintenanceScheduleStore.Set(MaintenanceTaskKind.InstallUpdates, install);
    }

    private static void CheckForUpdates(ScheduleFrequency frequency, int graceMinutes)
    {
        var check = MaintenanceScheduleStore.Get(MaintenanceTaskKind.CheckForUpdates);
        check.Enabled = true;
        check.Frequency = frequency;
        check.StartMinutes = 3 * 60;
        check.GraceMinutes = graceMinutes;
        MaintenanceScheduleStore.Set(MaintenanceTaskKind.CheckForUpdates, check);
    }

    [Theory]
    [InlineData(ScheduleFrequency.Daily, 0)]
    [InlineData(ScheduleFrequency.Weekly, 0)]
    [InlineData(ScheduleFrequency.Daily, MaintenanceTaskSchedule.UnlimitedGrace)]
    public void TheStartupLoadDoesNotInstallWhenUpdateChecksRunAtAScheduledTime(
        ScheduleFrequency checkFrequency,
        int graceMinutes)
    {
        InstallAfterEveryUpdateCheck();
        CheckForUpdates(checkFrequency, graceMinutes);

        Assert.False(MaintenanceScheduler.IsAutoInstallDue());
    }

    [Fact]
    public async Task AScheduledUpdateCheckRunMakesTheInstallDue()
    {
        InstallAfterEveryUpdateCheck();
        CheckForUpdates(ScheduleFrequency.Daily, 0);

        await MaintenanceScheduler.RunAsync(MaintenanceTaskKind.CheckForUpdates);

        Assert.True(MaintenanceScheduler.IsAutoInstallDue());
        MaintenanceScheduler.MarkAutoInstallHandled();
        Assert.False(MaintenanceScheduler.IsAutoInstallDue());
    }

    [Fact]
    public async Task RunningTheInstallTaskNowInstallsWhenUpdateChecksRunAtAScheduledTime()
    {
        InstallAfterEveryUpdateCheck();
        CheckForUpdates(ScheduleFrequency.Daily, 0);

        await MaintenanceScheduler.RunAsync(MaintenanceTaskKind.InstallUpdates);

        Assert.True(MaintenanceScheduler.IsAutoInstallDue());
        MaintenanceScheduler.MarkAutoInstallHandled();
        Assert.False(MaintenanceScheduler.IsAutoInstallDue());
    }

    [Theory]
    [InlineData(MaintenanceTaskSchedule.UnlimitedGrace, true)]
    [InlineData(0, false)]
    public void AnUpdateLoadThatCatchesUpAMissedCheckInstallsOnlyWithinTheGracePeriod(
        int graceMinutes,
        bool expectedDue)
    {
        InstallAfterEveryUpdateCheck();
        CheckForUpdates(ScheduleFrequency.Daily, graceMinutes);

        MaintenanceScheduler.RecordUpdateLoad(DateTime.Now.Date.AddDays(1).AddHours(12));

        Assert.Equal(expectedDue, MaintenanceScheduler.IsAutoInstallDue());
        Assert.NotNull(MaintenanceScheduleStore.GetLastRun(MaintenanceTaskKind.CheckForUpdates));
    }

    [Theory]
    [InlineData(MaintenanceTaskSchedule.UnlimitedGrace, true)]
    [InlineData(0, false)]
    public void AnUpdateLoadThatCatchesUpAMissedCheckInstallsWhenTheUpdatesPageChecksBeforeTheScheduler(
        int graceMinutes,
        bool expectedDue)
    {
        InstallAfterEveryUpdateCheck();
        CheckForUpdates(ScheduleFrequency.Daily, graceMinutes);
        DateTime afterMissedCheck = DateTime.Now.Date.AddDays(1).AddHours(12);

        Assert.Equal(expectedDue, MaintenanceScheduler.IsAutoInstallDue(afterMissedCheck, updatesLoaded: true));
        MaintenanceScheduler.RecordUpdateLoad(afterMissedCheck);

        Assert.Equal(expectedDue, MaintenanceScheduler.IsAutoInstallDue());
        MaintenanceScheduler.MarkAutoInstallHandled();
        Assert.False(MaintenanceScheduler.IsAutoInstallDue());
    }

    [Fact]
    public void AnUnfinishedUpdateLoadDoesNotCatchUpAMissedCheck()
    {
        InstallAfterEveryUpdateCheck();
        CheckForUpdates(ScheduleFrequency.Daily, MaintenanceTaskSchedule.UnlimitedGrace);
        DateTime afterMissedCheck = DateTime.Now.Date.AddDays(1).AddHours(12);

        Assert.False(MaintenanceScheduler.IsAutoInstallDue(afterMissedCheck, updatesLoaded: false));
        Assert.Null(MaintenanceScheduleStore.GetLastRun(MaintenanceTaskKind.CheckForUpdates));
    }

    [Fact]
    public void EveryUpdateLoadInstallsWhenUpdateChecksRunOnAnInterval()
    {
        InstallAfterEveryUpdateCheck();
        CheckForUpdates(ScheduleFrequency.Interval, MaintenanceTaskSchedule.UnlimitedGrace);

        Assert.True(MaintenanceScheduler.IsAutoInstallDue());
    }

    [Fact]
    public void EveryUpdateLoadInstallsWhenPeriodicUpdateChecksAreDisabled()
    {
        InstallAfterEveryUpdateCheck();
        CheckForUpdates(ScheduleFrequency.Daily, 0);
        var check = MaintenanceScheduleStore.Get(MaintenanceTaskKind.CheckForUpdates);
        check.Enabled = false;
        MaintenanceScheduleStore.Set(MaintenanceTaskKind.CheckForUpdates, check);

        Assert.True(MaintenanceScheduler.IsAutoInstallDue());
    }
}
