using NUnit.Framework;
using UniGetUI.Tui.Infrastructure;
using NAssert = NUnit.Framework.Assert;

namespace UniGetUI.Tui.Tests;

[TestFixture, NonParallelizable]
public sealed class TuiWinGetRepairTests
{
    [Test]
    public async Task Failure_DoesNotReloadOrReportSuccess()
    {
        var notifications = new List<TuiNotification>();
        void Observe(TuiNotification notification) => notifications.Add(notification);
        TuiNotifications.NotificationRaised += Observe;
        int reloads = 0, calls = 0;
        try
        {
            bool result = await TuiWinGetRepair.RepairAsync(() =>
            {
                calls++;
                return Task.FromException(new IOException("runner failed-5447"));
            }, () => reloads++);

            NAssert.That(result, Is.False);
            NAssert.That(calls, Is.EqualTo(1));
            NAssert.That(reloads, Is.Zero);
            NAssert.That(notifications, Has.Count.EqualTo(1));
            NAssert.That(notifications[0].Severity, Is.EqualTo(TuiNotificationSeverity.Error));
            NAssert.That(notifications[0].Title, Is.EqualTo("WinGet could not be repaired"));
            NAssert.That(notifications[0].Message, Is.EqualTo(
                "An unexpected issue occurred while attempting to repair WinGet. Please try again later — runner failed-5447"));
        }
        finally
        {
            TuiNotifications.NotificationRaised -= Observe;
        }
    }

    [Test]
    public async Task Success_NotifiesAndReloadsPackagesOnce()
    {
        var notifications = new List<TuiNotification>();
        void Observe(TuiNotification notification) => notifications.Add(notification);
        TuiNotifications.NotificationRaised += Observe;
        int reloads = 0, calls = 0;
        try
        {
            bool result = await TuiWinGetRepair.RepairAsync(() =>
            {
                calls++;
                return Task.CompletedTask;
            }, () => reloads++);

            NAssert.That(result, Is.True);
            NAssert.That(calls, Is.EqualTo(1));
            NAssert.That(reloads, Is.EqualTo(1));
            NAssert.That(notifications, Has.Count.EqualTo(1));
            NAssert.That(notifications[0].Severity, Is.EqualTo(TuiNotificationSeverity.Success));
            NAssert.That(notifications[0].Title, Is.EqualTo("WinGet was repaired successfully"));
            NAssert.That(notifications[0].Message, Is.EqualTo("It is recommended to restart UniGetUI after WinGet has been repaired"));
        }
        finally
        {
            TuiNotifications.NotificationRaised -= Observe;
        }
    }
}
