using System.Diagnostics;
using System.Runtime.InteropServices;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageOperations;

namespace UniGetUI.Avalonia.Infrastructure;

/// <summary>
/// macOS system notification delivery via osascript (works on all macOS versions).
/// NSUserNotificationCenter was removed in macOS 14; UNUserNotificationCenter requires
/// ObjC blocks that are impractical via pure P/Invoke. osascript is always available.
/// Callers are responsible for the OperatingSystem.IsMacOS() guard before invoking.
/// </summary>
internal static class MacOsNotificationBridge
{
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
        // NSUserNotificationCenter was removed in macOS 14; osascript works on all versions.
        string script = "display notification " + AppleScriptString(message)
                        + " with title " + AppleScriptString(title);
        Process.Start(new ProcessStartInfo
        {
            FileName = "/usr/bin/osascript",
            ArgumentList = { "-e", script },
            UseShellExecute = false,
            CreateNoWindow = true,
        });
    }

    private static string AppleScriptString(string s) =>
        "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    // ── Dock icon ──────────────────────────────────────────────────────────

    public static void SetDockIcon(byte[] pngBytes)
    {
        try
        {
            var handle = GCHandle.Alloc(pngBytes, GCHandleType.Pinned);
            try
            {
                IntPtr nsData = MsgSendBytes(
                    objc_getClass("NSData"), Sel("dataWithBytes:length:"),
                    handle.AddrOfPinnedObject(), pngBytes.Length);

                IntPtr nsImage = MsgSend(
                    MsgSend(objc_getClass("NSImage"), Sel("alloc")),
                    Sel("initWithData:"), nsData);

                IntPtr nsApp = MsgSend(objc_getClass("NSApplication"), Sel("sharedApplication"));
                MsgSend(nsApp, Sel("setApplicationIconImage:"), nsImage);
                MsgSend(nsImage, Sel("autorelease"));
            }
            finally
            {
                handle.Free();
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Failed to set macOS dock icon");
            Logger.Warn(ex);
        }
    }

    private static IntPtr Sel(string name) => sel_registerName(name);

    // ── ObjC runtime P/Invoke (used by SetDockIcon) ────────────────────────

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern IntPtr objc_getClass(string name);

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern IntPtr sel_registerName(string name);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend(IntPtr receiver, IntPtr sel);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend(IntPtr receiver, IntPtr sel, IntPtr arg);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSendBytes(IntPtr receiver, IntPtr sel, IntPtr bytes, nint length);
}
