using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageOperations;

namespace UniGetUI.Avalonia.Infrastructure;

internal static class WindowsAppNotificationBridge
{
    /// <summary>Invoked on a thread-pool thread when a toast notification button is clicked.</summary>
    public static event Action<string>? NotificationActivated;

    public static bool ShowProgress(AbstractOperation operation) => false;

    public static bool ShowSuccess(AbstractOperation operation) => false;

    public static bool ShowError(AbstractOperation operation) => false;

    public static void RemoveProgress(AbstractOperation operation) { }

    public static void ShowUpdatesAvailableNotification(IReadOnlyList<IPackage> upgradable) { }

    public static void ShowUpgradingPackagesNotification(IReadOnlyList<IPackage> upgradable) { }

    public static void ShowSelfUpdateAvailableNotification(string newVersion) { }

    public static void ShowNewShortcutsNotification(IReadOnlyList<string> shortcuts) { }
}
