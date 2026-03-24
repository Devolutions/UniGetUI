using System.Runtime.InteropServices;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageOperations;

namespace UniGetUI.Avalonia.Infrastructure;

/// <summary>
/// macOS system notification delivery via NSUserNotificationCenter (ObjC runtime P/Invoke).
/// Mirrors the pattern of WindowsAppNotificationBridge: guards on OS check, silent fallback on failure.
/// </summary>
internal static class MacOsNotificationBridge
{
    private static bool? _available;
    private static readonly object _lock = new();

    private static bool IsAvailable()
    {
        if (!OperatingSystem.IsMacOS()) return false;
        lock (_lock)
        {
            if (_available.HasValue) return _available.Value;
            try
            {
                _available = objc_getClass("NSUserNotificationCenter") != IntPtr.Zero;
            }
            catch
            {
                _available = false;
            }
            return _available.Value;
        }
    }

    // ── Operation notifications ────────────────────────────────────────────

    public static bool ShowProgress(AbstractOperation operation)
    {
        if (!IsAvailable() || Settings.AreProgressNotificationsDisabled()) return false;
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
        if (!IsAvailable() || Settings.AreSuccessNotificationsDisabled()) return false;
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
        if (!IsAvailable() || Settings.AreErrorNotificationsDisabled()) return false;
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
        if (!IsAvailable() || Settings.AreUpdatesNotificationsDisabled()) return;
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

    public static void ShowSelfUpdateAvailableNotification(string newVersion)
    {
        if (!IsAvailable()) return;
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
        if (!IsAvailable() || Settings.AreNotificationsDisabled()) return;
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
        var centerClass = objc_getClass("NSUserNotificationCenter");
        var center = MsgSend(centerClass, Sel("defaultUserNotificationCenter"));

        var notifClass = objc_getClass("NSUserNotification");
        var notif = MsgSend(MsgSend(notifClass, Sel("alloc")), Sel("init"));

        MsgSend(notif, Sel("setTitle:"), ToNSString(title));
        MsgSend(notif, Sel("setInformativeText:"), ToNSString(message));
        MsgSend(center, Sel("deliverNotification:"), notif);
        MsgSend(notif, Sel("autorelease"));
    }

    private static IntPtr ToNSString(string s)
    {
        IntPtr ptr = Marshal.StringToCoTaskMemUTF8(s);
        try
        {
            return MsgSend(objc_getClass("NSString"), Sel("stringWithUTF8String:"), ptr);
        }
        finally
        {
            Marshal.FreeCoTaskMem(ptr);
        }
    }

    private static IntPtr Sel(string name) => sel_registerName(name);

    // ── ObjC runtime P/Invoke ──────────────────────────────────────────────

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern IntPtr objc_getClass(string name);

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern IntPtr sel_registerName(string name);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend(IntPtr receiver, IntPtr sel);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend(IntPtr receiver, IntPtr sel, IntPtr arg);
}
