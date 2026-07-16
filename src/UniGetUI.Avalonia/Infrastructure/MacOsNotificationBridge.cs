using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using UniGetUI.Avalonia.Views;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageOperations;

namespace UniGetUI.Avalonia.Infrastructure;

/// <summary>
/// macOS system notification delivery through the app-bundled UserNotifications bridge.
/// The bridge owns the Objective-C delegate and completion blocks so this NativeAOT assembly
/// can use a fixed P/Invoke boundary and receive default notification activations.
/// </summary>
internal static partial class MacOsNotificationBridge
{
    private const string NativeBridgePath = "@executable_path/UniGetUIMacNotificationBridge.dylib";
    private static readonly Lock InitializationLock = new();
    private static bool _isInitialized;

    // ── Operation notifications ────────────────────────────────────────────

    public static bool ShowProgress(AbstractOperation operation)
    {
        if (Settings.AreProgressNotificationsDisabled()) return false;
        try
        {
            string title = operation.Metadata.Title.Length > 0
                ? operation.Metadata.Title
                : CoreTools.Translate("Operation in progress");
            string message = operation.Metadata.Status.Length > 0
                ? operation.Metadata.Status
                : CoreTools.Translate("Please wait...");
            DeliverNotification(title, message);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn("macOS progress notification failed");
            Logger.Warn(ex);
            return false;
        }
    }

    public static bool ShowSuccess(AbstractOperation operation)
    {
        if (Settings.AreSuccessNotificationsDisabled()) return false;
        try
        {
            string title = operation.Metadata.SuccessTitle.Length > 0
                ? operation.Metadata.SuccessTitle
                : CoreTools.Translate("Success!");
            string message = operation.Metadata.SuccessMessage.Length > 0
                ? operation.Metadata.SuccessMessage
                : CoreTools.Translate("Success!");
            DeliverNotification(title, message);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn("macOS success notification failed");
            Logger.Warn(ex);
            return false;
        }
    }

    public static bool ShowError(AbstractOperation operation)
    {
        if (Settings.AreErrorNotificationsDisabled()) return false;
        try
        {
            string title = operation.Metadata.FailureTitle.Length > 0
                ? operation.Metadata.FailureTitle
                : CoreTools.Translate("Failed");
            string message = operation.Metadata.FailureMessage.Length > 0
                ? operation.Metadata.FailureMessage
                : CoreTools.Translate("An error occurred while processing this package");
            DeliverNotification(title, message);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn("macOS error notification failed");
            Logger.Warn(ex);
            return false;
        }
    }

    // ── Feature notifications ──────────────────────────────────────────────

    public static void ShowUpdatesAvailableNotification(IReadOnlyList<IPackage> upgradable)
    {
        if (Settings.AreUpdatesNotificationsDisabled()) return;
        try
        {
            string title, message;
            if (upgradable.Count == 1)
            {
                title = CoreTools.Translate("An update was found!");
                message = CoreTools.Translate("{0} can be updated to version {1}",
                    upgradable[0].Name, upgradable[0].NewVersionString);
            }
            else
            {
                title = CoreTools.Translate("Updates found!");
                message = CoreTools.Translate("{0} packages can be updated", upgradable.Count);
            }
            DeliverNotification(title, message);
        }
        catch (Exception ex)
        {
            Logger.Warn("macOS updates-available notification failed");
            Logger.Warn(ex);
        }
    }

    public static void ShowUpgradingPackagesNotification(IReadOnlyList<IPackage> upgradable)
    {
        if (Settings.AreUpdatesNotificationsDisabled()) return;
        try
        {
            string title, message;
            if (upgradable.Count == 1)
            {
                title = CoreTools.Translate("An update was found!");
                message = CoreTools.Translate("{0} is being updated to version {1}",
                    upgradable[0].Name, upgradable[0].NewVersionString);
            }
            else
            {
                title = CoreTools.Translate("{0} packages are being updated", upgradable.Count);
                message = string.Join(", ", upgradable.Select(p => p.Name));
            }
            DeliverNotification(title, message);
        }
        catch (Exception ex)
        {
            Logger.Warn("macOS upgrading-packages notification failed");
            Logger.Warn(ex);
        }
    }

    public static void ShowSelfUpdateAvailableNotification(string newVersion)
    {
        try
        {
            DeliverNotification(
                CoreTools.Translate("{0} can be updated to version {1}", "UniGetUI", newVersion),
                CoreTools.Translate("You have currently version {0} installed", CoreData.VersionName));
        }
        catch (Exception ex)
        {
            Logger.Warn("macOS self-update notification failed");
            Logger.Warn(ex);
        }
    }

    public static void ShowNewShortcutsNotification(IReadOnlyList<string> shortcuts)
    {
        if (Settings.AreNotificationsDisabled()) return;
        try
        {
            string title, message;
            if (shortcuts.Count == 1)
            {
                title = CoreTools.Translate("Desktop shortcut created");
                message = CoreTools.Translate(
                    "UniGetUI has detected a new desktop shortcut that can be deleted automatically.")
                    + "\n" + shortcuts[0].Split('/')[^1];
            }
            else
            {
                title = CoreTools.Translate("{0} desktop shortcuts created", shortcuts.Count);
                message = CoreTools.Translate(
                    "UniGetUI has detected {0} new desktop shortcuts that can be deleted automatically.",
                    shortcuts.Count);
            }
            DeliverNotification(title, message);
        }
        catch (Exception ex)
        {
            Logger.Warn("macOS shortcuts notification failed");
            Logger.Warn(ex);
        }
    }

    // ── Core delivery ──────────────────────────────────────────────────────

    private static void DeliverNotification(string title, string message)
    {
        if (!EnsureInitialized())
            throw new InvalidOperationException("The macOS notification bridge is unavailable.");

        if (ShowNativeNotification(title, message) == 0)
        {
            throw new InvalidOperationException("The macOS notification bridge rejected the notification.");
        }
    }

    private static unsafe bool EnsureInitialized()
    {
        if (_isInitialized)
            return true;

        lock (InitializationLock)
        {
            if (_isInitialized)
                return true;

            try
            {
                _isInitialized = InitializeNativeNotifications(
                    (nint)(delegate* unmanaged[Cdecl]<void>)&OnNotificationActivated) != 0;
                return _isInitialized;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                Logger.Warn("The macOS notification bridge is unavailable.");
                Logger.Warn(ex);
                return false;
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnNotificationActivated()
    {
        Dispatcher.UIThread.Post(() => MainWindow.Instance?.ShowFromTray());
    }

    [LibraryImport(NativeBridgePath, EntryPoint = "UniGetUIInitializeMacNotifications")]
    private static partial int InitializeNativeNotifications(nint activationCallback);

    [LibraryImport(NativeBridgePath, EntryPoint = "UniGetUIShowMacNotification", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int ShowNativeNotification(string title, string message);
}
