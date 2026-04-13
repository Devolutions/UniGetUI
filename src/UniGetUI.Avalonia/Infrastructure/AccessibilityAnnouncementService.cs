using Avalonia.Automation;
using Avalonia.Threading;

namespace UniGetUI.Avalonia.Infrastructure;

public sealed record AccessibilityAnnouncement(
    string Message,
    AutomationLiveSetting LiveSetting = AutomationLiveSetting.Polite);

public static class AccessibilityAnnouncementService
{
    public static event EventHandler<AccessibilityAnnouncement>? AnnouncementRequested;

    public static void Announce(
        string? message,
        AutomationLiveSetting liveSetting = AutomationLiveSetting.Polite)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        Dispatcher.UIThread.Post(() =>
            AnnouncementRequested?.Invoke(
                null,
                new AccessibilityAnnouncement(message, liveSetting)));
    }
}