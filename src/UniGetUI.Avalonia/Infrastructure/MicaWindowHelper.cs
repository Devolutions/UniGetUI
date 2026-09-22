using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.VisualTree;
using UniGetUI.Avalonia.Assets.Styles;

namespace UniGetUI.Avalonia.Infrastructure;

/// <summary>
/// Gives a standard Avalonia <see cref="Window"/> the native Windows 11 look:
/// the Mica backdrop (whole-window) and rounded corners. The window border is left
/// at the OS default, so it only shows an accent color if the user enabled that in
/// Personalization. Windows-only and gated on Windows 11; a no-op elsewhere, so
/// the window keeps its opaque look when Mica isn't available.
/// Used by the secondary windows/dialogs; MainWindow has its own (custom-frame) variant.
/// </summary>
internal static class MicaWindowHelper
{
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    private static bool _acrylicPopupsHooked;
    private static bool _micaConfirmedUnavailable;

    public static void Apply(Window window)
    {
        if (!IsMicaEnabled())
            return;

        // Avalonia paints the Mica backdrop from this hint; the transparent window lets it
        // show. The dialog's content panels keep their (translucent) surfaces from the
        // merged Styles.WindowsMica dictionary.
        window.TransparencyLevelHint = new[] { WindowTransparencyLevel.Mica };

        window.Opened += (_, _) =>
        {
            if (window.TryGetPlatformHandle()?.Handle is { } handle && handle != 0)
            {
                int corner = DWMWCP_ROUND;
                NativeMethods.DwmSetWindowAttribute(handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
            }
            window.Background = Brushes.Transparent;
        };
    }

    // Applies the Windows 11 transient-surface treatment to popup hosts. WinUI menu
    // flyouts/context menus use desktop acrylic just like the other transient surfaces.
    // The native tray popup is intentionally excluded. Registered once at startup when
    // Mica is enabled; otherwise the existing opaque surfaces remain as the fallback.
    public static void EnableAcrylicPopups()
    {
        if (!IsMicaEnabled() || _acrylicPopupsHooked)
            return;
        _acrylicPopupsHooked = true;

        // In-app menus, flyouts, tooltips, and combo popups are hosted in a PopupRoot.
        Control.LoadedEvent.AddClassHandler<PopupRoot>((root, _) => ApplyAcrylicToPopup(root));

        // The system-tray context menu is not an app flyout and must keep its existing
        // opaque/native-looking surface. Catch Avalonia's dedicated tray popup host here.
        // The other Windows (MainWindow/dialogs) are handled via Apply().
        Control.LoadedEvent.AddClassHandler<Window>((win, _) =>
        {
            if (win.GetType().Name == "TrayPopupRoot")
                ApplyAcrylicToPopup(win);
        });
    }

    private static void ApplyAcrylicToPopup(TopLevel root)
    {
        if (!IsMicaEnabled())
            return;

        bool isTrayMenu = root.GetType().Name == "TrayPopupRoot";
        bool isAppMenu = !isTrayMenu
                         && root.GetVisualDescendants()
                             .Any(control => control is MenuFlyoutPresenter or ContextMenu);

        if (isTrayMenu)
        {
            ApplyOpaqueMenuFallback(root);
            return;
        }

        // Avalonia's AcrylicBlur is already a real Win32 composition acrylic path:
        // it enables the host-backdrop brush and installs Avalonia's backdrop blur/saturation
        // effect. Keep one acrylic path for menus, flyouts, tooltips and combo popups instead
        // of stacking a second DWM transient backdrop on ordinary Flyout surfaces.
        root.TransparencyLevelHint = new[] { WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur };
        root.Background = Brushes.Transparent;

        if (root.TryGetPlatformHandle()?.Handle is not { } handle || handle == 0)
        {
            if (isAppMenu)
                ApplyOpaqueMenuFallback(root);
            return;
        }

        int corner = DWMWCP_ROUND;
        NativeMethods.DwmSetWindowAttribute(handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
    }

    private static void ApplyOpaqueMenuFallback(TopLevel root)
    {
        root.TransparencyLevelHint = new[] { WindowTransparencyLevel.None };

        if (root.TryFindResource("MenuSurfaceFallbackBrush", root.ActualThemeVariant, out object? resource)
            && resource is IBrush brush)
        {
            // A local resource wins over the Windows-Mica transparent menu resource, so the
            // presenter and root both return to the current solid menu appearance.
            root.Resources["MenuSurfaceBrush"] = brush;
            root.Background = brush;
        }
        else if (root.TryFindResource("MenuSurfaceBrush", root.ActualThemeVariant, out resource)
                 && resource is IBrush existingBrush)
        {
            root.Background = existingBrush;
        }

        ApplyRoundedCorners(root);
    }

    private static void ApplyRoundedCorners(TopLevel root)
    {
        if (root.TryGetPlatformHandle()?.Handle is not { } handle || handle == 0)
            return;

        int corner = DWMWCP_ROUND;
        NativeMethods.DwmSetWindowAttribute(handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
    }

    /// <summary>
    /// True when the native Mica look should be used: Windows 11+, "Transparency effects" on,
    /// GPU-accelerated (software rendering can't be transparent, so Mica shows as black — #5111),
    /// and no window has since found the backdrop didn't engage. Otherwise keep the solid look.
    /// </summary>
    public static bool IsMicaEnabled()
        => OperatingSystem.IsWindows()
           && Environment.OSVersion.Version.Build >= 22000
           && !_micaConfirmedUnavailable
           && !WindowsAvaloniaRenderingPolicy.ShouldUseSoftwareRendering
           && IsOsTransparencyEnabled();

    /// <summary>
    /// Latch Mica off for the session and drop the translucent surface overrides so every surface
    /// falls back to the solid look, once a window confirms the backdrop never engaged (#5111).
    /// </summary>
    public static void NotifyMicaUnavailable()
    {
        if (_micaConfirmedUnavailable)
            return;
        _micaConfirmedUnavailable = true;

        if (Application.Current is { } app)
        {
            for (int i = app.Resources.MergedDictionaries.Count - 1; i >= 0; i--)
            {
                if (app.Resources.MergedDictionaries[i] is WindowsMicaStyles)
                    app.Resources.MergedDictionaries.RemoveAt(i);
            }
        }
    }

    private static bool IsOsTransparencyEnabled()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            var data = new byte[4];
            int size = data.Length;
            int result = NativeMethods.RegGetValueW(
                NativeMethods.HKEY_CURRENT_USER,
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "EnableTransparency",
                NativeMethods.RRF_RT_REG_DWORD,
                out _, data, ref size);
            if (result == 0) // ERROR_SUCCESS
                return BitConverter.ToInt32(data, 0) != 0;
        }
        catch { /* fall through to the default */ }

        return true; // value missing/unreadable -> assume transparency is on
    }

    private static class NativeMethods
    {
        // winreg.h: HKEY_CURRENT_USER = (HKEY)(ULONG_PTR)((LONG)0x80000001) — sign-extended on x64.
        public static readonly nint HKEY_CURRENT_USER = new(unchecked((int)0x80000001));
        public const int RRF_RT_REG_DWORD = 0x00000010;

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        public static extern int RegGetValueW(nint hkey, string lpSubKey, string lpValue, int dwFlags,
            out int pdwType, byte[] pvData, ref int pcbData);
    }
}
