using NUnit.Framework;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Tui.Infrastructure;
using NAssert = NUnit.Framework.Assert;

namespace UniGetUI.Tui.Tests;

[TestFixture, NonParallelizable]
public sealed class TuiWinGetRepairTests
{
    [TestCase("pinget", 23)]
    [TestCase("custom-cli", 23)]
    [TestCase("pinget", null)]
    public async Task Failure_PreservesPreferenceAndDoesNotReloadOrReportSuccess(string preference, int? exit)
    {
        string old = Settings.GetValue(Settings.K.WinGetCliToolPreference);
        var notifications = new List<TuiNotification>();
        void Observe(TuiNotification notification) => notifications.Add(notification);
        TuiNotifications.NotificationRaised += Observe;
        int reloads = 0, calls = 0;
        try
        {
            Settings.SetValue(Settings.K.WinGetCliToolPreference, preference);
            bool result = await TuiWinGetRepair.RepairAsync(() =>
            {
                calls++;
                return exit is int code ? Task.FromResult(code)
                    : Task.FromException<int>(new IOException("runner failed-5447"));
            }, () => reloads++);

            NAssert.That(result, Is.False);
            NAssert.That(calls, Is.EqualTo(1));
            NAssert.That(reloads, Is.Zero);
            NAssert.That(Settings.GetValue(Settings.K.WinGetCliToolPreference), Is.EqualTo(preference));
            NAssert.That(notifications, Has.Count.EqualTo(1));
            NAssert.That(notifications[0].Severity, Is.EqualTo(TuiNotificationSeverity.Error));
            NAssert.That(notifications[0].Title, Is.EqualTo("WinGet could not be repaired"));
            NAssert.That(notifications[0].Message, Does.Contain(exit is null ? "runner failed-5447" : "23"));
        }
        finally
        {
            TuiNotifications.NotificationRaised -= Observe;
            Settings.SetValue(Settings.K.WinGetCliToolPreference, old);
        }
    }

    [TestCase("pinget", "default")]
    [TestCase("PiNgEt", "default")]
    [TestCase("custom-cli", "custom-cli")]
    [TestCase("", "")]
    public async Task SuccessfulExit_NotifiesAndReloadsPackagesOnce(string preference, string expected)
    {
        string old = Settings.GetValue(Settings.K.WinGetCliToolPreference);
        var notifications = new List<TuiNotification>();
        void Observe(TuiNotification notification) => notifications.Add(notification);
        TuiNotifications.NotificationRaised += Observe;
        int reloads = 0, calls = 0;
        try
        {
            Settings.SetValue(Settings.K.WinGetCliToolPreference, preference);
            bool result = await TuiWinGetRepair.RepairAsync(() =>
            {
                calls++;
                return Task.FromResult(0);
            }, () => reloads++);

            NAssert.That(result, Is.True);
            NAssert.That(calls, Is.EqualTo(1));
            NAssert.That(reloads, Is.EqualTo(1));
            NAssert.That(Settings.GetValue(Settings.K.WinGetCliToolPreference), Is.EqualTo(expected));
            NAssert.That(notifications, Has.Count.EqualTo(1));
            NAssert.That(notifications[0].Severity, Is.EqualTo(TuiNotificationSeverity.Success));
            NAssert.That(notifications[0].Title, Is.EqualTo("WinGet was repaired successfully"));
            NAssert.That(notifications[0].Message, Is.EqualTo("It is recommended to restart UniGetUI after WinGet has been repaired"));
        }
        finally
        {
            TuiNotifications.NotificationRaised -= Observe;
            Settings.SetValue(Settings.K.WinGetCliToolPreference, old);
        }
    }

    [TestCase("pinget", -1)]
    public async Task Failure_NegativeExitPreservesPreferenceAndDoesNotReloadOrReportSuccess(string preference, int exitCode)
    {
        string old = Settings.GetValue(Settings.K.WinGetCliToolPreference);
        var notifications = new List<TuiNotification>();
        void Observe(TuiNotification notification) => notifications.Add(notification);
        TuiNotifications.NotificationRaised += Observe;
        int reloads = 0, calls = 0;
        try
        {
            Settings.SetValue(Settings.K.WinGetCliToolPreference, preference);
            bool result = await TuiWinGetRepair.RepairAsync(() =>
            {
                calls++;
                return Task.FromResult(exitCode);
            }, () => reloads++);

            NAssert.That(result, Is.False);
            NAssert.That(calls, Is.EqualTo(1));
            NAssert.That(reloads, Is.Zero);
            NAssert.That(Settings.GetValue(Settings.K.WinGetCliToolPreference), Is.EqualTo(preference));
            NAssert.That(notifications, Has.Count.EqualTo(1));
            NAssert.That(notifications[0].Severity, Is.EqualTo(TuiNotificationSeverity.Error));
            NAssert.That(notifications[0].Title, Is.EqualTo("WinGet could not be repaired"));
            NAssert.That(notifications[0].Message, Is.EqualTo(
                $"An unexpected issue occurred while attempting to repair WinGet. Please try again later — WinGet repair exited with code {exitCode}."));
        }
        finally
        {
            TuiNotifications.NotificationRaised -= Observe;
            Settings.SetValue(Settings.K.WinGetCliToolPreference, old);
        }
    }
}
