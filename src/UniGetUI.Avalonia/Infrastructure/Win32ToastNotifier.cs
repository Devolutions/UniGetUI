#if WINDOWS
using System;
using System.Runtime.InteropServices;
using UniGetUI.Core.Logging;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace UniGetUI.Avalonia.Infrastructure;

/// <summary>
/// Show-only Windows toast notifications via the OS-provided UWP
/// <c>Windows.UI.Notifications</c> surface, with no Windows App SDK / WinUI dependency.
/// </summary>
/// <remarks>
/// The notifier is keyed by an AppUserModelID (AUMID). For an unpackaged Win32 app the
/// shell will only surface toasts for that AUMID once a Start Menu shortcut carrying the
/// same AUMID exists; <see cref="AppShortcutAumidStamper"/> handles that side. Toast
/// activation (button clicks) is intentionally not wired — clicking the toast launches
/// the app with its <c>unigetui://</c> deep-link so the single-instance handler can
/// foreground the window, which keeps this dependency-free.
/// </remarks>
internal static class Win32ToastNotifier
{
    /// <summary>Stable AUMID for UniGetUI. Must match the one stamped on the Start Menu shortcut.</summary>
    public const string AppUserModelId = "Devolutions.UniGetUI";

    private static bool _availabilityChecked;
    private static bool _isAvailable;

    /// <summary>
    /// Stamps <see cref="AppUserModelId"/> onto the current process so the shell attributes
    /// toasts to UniGetUI. Safe to call repeatedly; a no-op on non-Windows.
    /// </summary>
    public static void SetProcessAumid()
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            _ = SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not set process AppUserModelID for toast notifications");
            Logger.Warn(ex);
        }
    }

    /// <summary>
    /// True once we have confirmed the UWP toast surface is usable on this Windows install.
    /// Non-Windows platforms report <c>false</c>.
    /// </summary>
    public static bool IsAvailable()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        if (_availabilityChecked)
            return _isAvailable;

        _availabilityChecked = true;
        try
        {
            _ = ToastNotificationManager.CreateToastNotifier(AppUserModelId);
            _isAvailable = true;
        }
        catch (Exception ex)
        {
            Logger.Warn("UWP toast notifier is not available; falling back to in-app banners");
            Logger.Warn(ex);
            _isAvailable = false;
        }

        return _isAvailable;
    }

    /// <summary>
    /// Shows a toast with the supplied title and message and a launch argument that is
    /// forwarded to the app when the user clicks the toast. Returns <c>false</c> if the
    /// toast could not be shown (caller should fall back to an in-app banner).
    /// </summary>
    public static bool Show(string title, string message, string launchArgument)
    {
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            string xml = BuildToastXml(title, message, launchArgument);

            var xmlDoc = new XmlDocument();
            xmlDoc.LoadXml(xml);

            var notification = new ToastNotification(xmlDoc);
            ToastNotificationManager.CreateToastNotifier(AppUserModelId).Show(notification);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not show Windows toast notification");
            Logger.Warn(ex);
            return false;
        }
    }

    private static string BuildToastXml(string title, string message, string launchArgument)
    {
        string titleEsc = XmlEscape(title);
        string messageEsc = XmlEscape(message);
        string launchEsc = XmlEscape(launchArgument);

        return $"""
                <toast activationType="protocol" launch="{launchEsc}" scenario="default">
                    <visual>
                        <binding template="ToastGeneric">
                            <text>{titleEsc}</text>
                            <text>{messageEsc}</text>
                        </binding>
                    </visual>
                    <audio silent="true"/>
                </toast>
                """;
    }

    private static string XmlEscape(string value)
        => value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appUserModelID);
}
#endif
