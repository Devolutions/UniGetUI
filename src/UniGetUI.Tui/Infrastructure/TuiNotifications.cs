using System;

namespace UniGetUI.Tui.Infrastructure;

internal enum TuiNotificationSeverity
{
    Info,
    Success,
    Warning,
    Error,
}

internal sealed record TuiNotification(
    TuiNotificationSeverity Severity,
    string Title,
    string Message,
    TimeSpan Duration);

internal static class TuiNotifications
{
    private static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ErrorDuration = TimeSpan.FromSeconds(8);

    public static event Action<TuiNotification>? NotificationRaised;

    public static void Info(string title, string message, TimeSpan? duration = null)
        => Show(TuiNotificationSeverity.Info, title, message, duration);

    public static void Success(string title, string message, TimeSpan? duration = null)
        => Show(TuiNotificationSeverity.Success, title, message, duration);

    public static void Warning(string title, string message, TimeSpan? duration = null)
        => Show(TuiNotificationSeverity.Warning, title, message, duration);

    public static void Error(string title, string message, TimeSpan? duration = null)
        => Show(TuiNotificationSeverity.Error, title, message, duration ?? ErrorDuration);

    public static void Show(
        TuiNotificationSeverity severity,
        string title,
        string message,
        TimeSpan? duration = null)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("Notification title cannot be empty.", nameof(title));

        if (message is null)
            throw new ArgumentNullException(nameof(message));

        NotificationRaised?.Invoke(
            new TuiNotification(
                severity,
                OneLine(title.Trim()),
                OneLine(message.Trim()),
                duration ?? DefaultDuration));
    }

    private static string OneLine(string value)
        => value.Replace('\r', ' ').Replace('\n', ' ');
}
