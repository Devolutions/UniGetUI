using UniGetUI.Tui.Infrastructure;

namespace UniGetUI.Tui.Tests;

public class TuiNotificationsTests
{
    [Fact]
    public void Success_RaisesNotification_WithNormalizedPayload()
    {
        TuiNotification? seen = null;
        void Handler(TuiNotification notification) => seen = notification;

        try
        {
            TuiNotifications.NotificationRaised += Handler;
            TuiNotifications.Success(" Saved\nbundle ", " ok\r\nnow ");
        }
        finally
        {
            TuiNotifications.NotificationRaised -= Handler;
        }

        Assert.NotNull(seen);
        Assert.Equal(TuiNotificationSeverity.Success, seen.Severity);
        Assert.Equal("Saved bundle", seen.Title);
        Assert.Equal("ok  now", seen.Message);
        Assert.True(seen.Duration > TimeSpan.Zero);
    }

    [Fact]
    public void Error_UsesLongerDefaultDuration()
    {
        TuiNotification? seen = null;
        void Handler(TuiNotification notification) => seen = notification;

        try
        {
            TuiNotifications.NotificationRaised += Handler;
            TuiNotifications.Error("Failure", "details");
        }
        finally
        {
            TuiNotifications.NotificationRaised -= Handler;
        }

        Assert.NotNull(seen);
        Assert.Equal(TuiNotificationSeverity.Error, seen.Severity);
        Assert.True(seen.Duration >= TimeSpan.FromSeconds(8));
    }

    [Fact]
    public void Show_RejectsBlankTitle()
    {
        Assert.Throws<ArgumentException>(() => TuiNotifications.Info(" ", "message"));
    }
}
