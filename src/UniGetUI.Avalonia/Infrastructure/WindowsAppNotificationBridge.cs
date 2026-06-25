using System.Collections.Generic;
using UniGetUI.Avalonia.Views;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageOperations;

namespace UniGetUI.Avalonia.Infrastructure;

/// <summary>
/// Delivers UniGetUI's notifications via native Windows Action-Center toasts using the
/// UWP <c>Windows.UI.Notifications</c> COM surface, with no Windows App SDK / WinUI
/// dependency. Toasts are show-only: clicking a toast launches the app through the
/// <c>unigetui://</c> deep-link (registered by the installer) so the existing
/// single-instance handler foregrounds the window. The inline action buttons carried
/// by the old WinRT toasts are intentionally dropped; activation is surfaced via
/// <see cref="NotificationActivated"/> only when the app is already running and the toast
/// is clicked, so the main-window view model keeps its handler. On non-Windows, or if
/// the toast COM surface is unavailable, every call falls back to the in-app Avalonia
/// banner via <see cref="MainWindow.ShowRuntimeNotification"/>.
/// </summary>
internal static class WindowsAppNotificationBridge
{
    /// <summary>Invoked when a notification's default action is triggered (toast clicked).</summary>
    public static event Action<string>? NotificationActivated;

    /// <summary>
    /// Parses a <c>unigetui://&lt;action&gt;</c> launch argument produced by a toast
    /// click, returning the encoded action or <c>null</c> if the argument is not a toast
    /// deep-link. Used by the secondary-instance handler to route toast activations.
    /// </summary>
    public static string? TryParseToastLaunchArgument(string arg)
    {
        if (string.IsNullOrEmpty(arg))
            return null;

        const string prefix = "unigetui://";
        return arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? arg[prefix.Length..]
            : null;
    }

    /// <summary>Raises <see cref="NotificationActivated"/> with the given action.</summary>
    public static void RaiseActivation(string action)
    {
        try { NotificationActivated?.Invoke(action); }
        catch (Exception ex)
        {
            Logger.Warn("NotificationActivated raise failed");
            Logger.Warn(ex);
        }
    }

    public static bool ShowProgress(AbstractOperation operation)
    {
        string title = operation.Metadata.Title.Length > 0
            ? operation.Metadata.Title
            : CoreTools.Translate("Operation in progress");

        string message = operation.Metadata.Status.Length > 0
            ? operation.Metadata.Status
            : CoreTools.Translate("Please wait...");

        return Show(title, message, MainWindow.RuntimeNotificationLevel.Progress, launchAction: NotificationArguments.Show);
    }

    public static bool ShowSuccess(AbstractOperation operation)
    {
        string title = operation.Metadata.SuccessTitle.Length > 0
            ? operation.Metadata.SuccessTitle
            : CoreTools.Translate("Success!");

        string message = operation.Metadata.SuccessMessage.Length > 0
            ? operation.Metadata.SuccessMessage
            : CoreTools.Translate("Success!");

        return Show(title, message, MainWindow.RuntimeNotificationLevel.Success, launchAction: NotificationArguments.Show);
    }

    public static bool ShowError(AbstractOperation operation)
    {
        string title = operation.Metadata.FailureTitle.Length > 0
            ? operation.Metadata.FailureTitle
            : CoreTools.Translate("Failed");

        string message = operation.Metadata.FailureMessage.Length > 0
            ? operation.Metadata.FailureMessage
            : CoreTools.Translate("An error occurred while processing this package");

        return Show(title, message, MainWindow.RuntimeNotificationLevel.Error, launchAction: NotificationArguments.Show);
    }

    public static void RemoveProgress(AbstractOperation operation)
    {
        // The UWP toast surface has no tag-based remove without the WinAppSDK helper.
        // Progress toasts are short-lived and replaced by the subsequent success/error toast.
        _ = operation;
    }

    // ── updates-available notification ───────────────────────────────────────

    /// <summary>
    /// Shows a Windows toast notification listing available package updates. No-op on
    /// non-Windows or when notifications are disabled (mirrors the previous bridge's
    /// platform guard so macOS/Linux stay untouched).
    /// </summary>
    public static void ShowUpdatesAvailableNotification(IReadOnlyList<IPackage> upgradable)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (Settings.AreUpdatesNotificationsDisabled()) return;

        bool sendNotification = upgradable.Any(p =>
            !Settings.GetDictionaryItem<string, bool>(
                Settings.K.DisabledPackageManagerNotifications, p.Manager.Name));
        if (!sendNotification) return;

        try
        {
            if (upgradable.Count == 1)
            {
                Show(
                    CoreTools.Translate("An update was found!"),
                    CoreTools.Translate("{0} can be updated to version {1}",
                        upgradable[0].Name, upgradable[0].NewVersionString),
                    MainWindow.RuntimeNotificationLevel.Success,
                    launchAction: NotificationArguments.ShowOnUpdatesTab);
            }
            else
            {
                Show(
                    CoreTools.Translate("Updates found!"),
                    CoreTools.Translate("{0} packages can be updated", upgradable.Count),
                    MainWindow.RuntimeNotificationLevel.Success,
                    launchAction: NotificationArguments.ShowOnUpdatesTab);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not show updates-available notification");
            Logger.Warn(ex);
        }
    }

    /// <summary>
    /// Shows a Windows toast notification when packages are actively being updated (auto-update triggered).
    /// </summary>
    public static void ShowUpgradingPackagesNotification(IReadOnlyList<IPackage> upgradable)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (Settings.AreUpdatesNotificationsDisabled()) return;

        bool sendNotification = upgradable.Any(p =>
            !Settings.GetDictionaryItem<string, bool>(
                Settings.K.DisabledPackageManagerNotifications, p.Manager.Name));
        if (!sendNotification) return;

        try
        {
            if (upgradable.Count == 1)
            {
                Show(
                    CoreTools.Translate("An update was found!"),
                    CoreTools.Translate("{0} is being updated to version {1}",
                        upgradable[0].Name, upgradable[0].NewVersionString),
                    MainWindow.RuntimeNotificationLevel.Progress,
                    launchAction: NotificationArguments.ShowOnUpdatesTab);
            }
            else
            {
                string attribution = string.Join(", ", upgradable
                    .Where(p => !Settings.GetDictionaryItem<string, bool>(
                        Settings.K.DisabledPackageManagerNotifications, p.Manager.Name))
                    .Select(p => p.Name));

                Show(
                    CoreTools.Translate("{0} packages are being updated", upgradable.Count),
                    attribution,
                    MainWindow.RuntimeNotificationLevel.Progress,
                    launchAction: NotificationArguments.ShowOnUpdatesTab);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not show upgrading-packages notification");
            Logger.Warn(ex);
        }
    }

    /// <summary>
    /// Shows a Windows toast offering a UniGetUI self-update.
    /// </summary>
    public static void ShowSelfUpdateAvailableNotification(string newVersion)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            Show(
                CoreTools.Translate("{0} can be updated to version {1}", "UniGetUI", newVersion),
                CoreTools.Translate("You have currently version {0} installed", CoreData.VersionName),
                MainWindow.RuntimeNotificationLevel.Success,
                launchAction: NotificationArguments.Show);
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not show self-update notification");
            Logger.Warn(ex);
        }
    }

    /// <summary>
    /// Shows a Windows toast notification after new desktop shortcuts are detected.
    /// </summary>
    public static void ShowNewShortcutsNotification(IReadOnlyList<string> shortcuts)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (Settings.AreNotificationsDisabled()) return;

        try
        {
            string title;
            string message;

            if (shortcuts.Count == 1)
            {
                title = CoreTools.Translate("Desktop shortcut created");
                message = CoreTools.Translate(
                    "UniGetUI has detected a new desktop shortcut that can be deleted automatically.")
                    + "\n" + shortcuts[0].Split('\\')[^1];
            }
            else
            {
                string names = string.Join(", ", shortcuts.Select(s => s.Split('\\')[^1]));
                title = CoreTools.Translate("{0} desktop shortcuts created", shortcuts.Count);
                message = CoreTools.Translate(
                    "UniGetUI has detected {0} new desktop shortcuts that can be deleted automatically.",
                    shortcuts.Count) + "\n" + names;
            }

            Show(
                title,
                message,
                MainWindow.RuntimeNotificationLevel.Success,
                launchAction: NotificationArguments.Show);
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not show new shortcuts notification");
            Logger.Warn(ex);
        }
    }

    /// <summary>
    /// Shows a native toast when available, otherwise falls back to the in-app banner.
    /// The <paramref name="launchAction"/> is encoded into the <c>unigetui://</c> launch
    /// argument so the single-instance handler can route the activation when the toast
    /// is clicked. Returns <c>true</c> once the notification has been surfaced (toast or
    /// banner), so callers skip their own fallback banner and avoid double-showing.
    /// </summary>
    private static bool Show(
        string title,
        string message,
        MainWindow.RuntimeNotificationLevel level,
        string launchAction)
    {
#if WINDOWS
        if (OperatingSystem.IsWindows() && Win32ToastNotifier.IsAvailable())
        {
            string launchArg = BuildLaunchArgument(launchAction);
            if (Win32ToastNotifier.Show(title, message, launchArg))
            {
                // When the app is already running, raise activation immediately so the
                // in-process handler fires; a subsequent toast click routes through the
                // single-instance redirector on launch via TryParseToastLaunchArgument.
                if (MainWindow.Instance is not null)
                    RaiseActivation(launchAction);
                return true;
            }
        }
#endif

        return ShowInAppBanner(title, message, level);
    }

    private static bool ShowInAppBanner(
        string title,
        string message,
        MainWindow.RuntimeNotificationLevel level)
    {
        var mainWindow = MainWindow.Instance;
        if (mainWindow is null)
            return false;

        mainWindow.ShowRuntimeNotification(title, message, level);
        return true;
    }

    private static string BuildLaunchArgument(string action)
        => $"unigetui://{action}";
}
