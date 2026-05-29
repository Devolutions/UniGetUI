using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Platform;

namespace UniGetUI.Avalonia.Infrastructure;

/// <summary>
/// Gives a standard Avalonia <see cref="Window"/> the native Windows 11 look:
/// the Mica backdrop (whole-window), rounded corners, and an accent-colored border
/// that follows focus. Windows-only and gated on Windows 11; a no-op elsewhere, so
/// the window keeps its opaque look when Mica isn't available.
/// Used by the secondary windows/dialogs; MainWindow has its own (custom-frame) variant.
/// </summary>
internal static class MicaWindowHelper
{
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMWCP_ROUND = 2;
    private const int DWMSBT_MAINWINDOW = 2; // Mica — same backdrop as the rest of the app, so menus match
    private const int DWMWA_COLOR_DEFAULT = unchecked((int)0xFFFFFFFF);

    private static bool _acrylicPopupsHooked;

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
            ApplyBorderAccent(window, window.IsActive);
        };

        window.GetObservable(WindowBase.IsActiveProperty).Subscribe(active => ApplyBorderAccent(window, active));

        if (Application.Current?.PlatformSettings is { } settings)
        {
            void Handler(object? s, PlatformColorValues e)
            {
                if (window.IsActive) ApplyBorderAccent(window, true);
            }
            settings.ColorValuesChanged += Handler;
            window.Closed += (_, _) => settings.ColorValuesChanged -= Handler;
        }
    }

    // Gives flyouts / menus / combo dropdowns / tooltips a native Win11 acrylic backdrop:
    // the popup window is made transparent and DWM paints the (blurred, theme-adaptive)
    // acrylic material behind it. Registered once at startup when Mica is enabled.
    public static void EnableAcrylicPopups()
    {
        if (!IsMicaEnabled() || _acrylicPopupsHooked)
            return;
        _acrylicPopupsHooked = true;

        // Every flyout/menu/tooltip/combo popup is hosted in a PopupRoot; style it as it loads.
        Control.LoadedEvent.AddClassHandler<PopupRoot>((root, _) => ApplyAcrylicToPopup(root));
    }

    private static void ApplyAcrylicToPopup(PopupRoot root)
    {
        // Transparent surface so the DWM acrylic shows; the presenter backgrounds are also
        // transparent (Styles.WindowsMica) so only the acrylic + menu items are painted.
        root.TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        root.Background = Brushes.Transparent;

        if (root.TryGetPlatformHandle()?.Handle is not { } handle || handle == 0)
            return;

        int corner = DWMWCP_ROUND;
        NativeMethods.DwmSetWindowAttribute(handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        int backdrop = DWMSBT_MAINWINDOW;
        NativeMethods.DwmSetWindowAttribute(handle, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
    }

    // Accent-coloured window border that follows focus (kept on the dialogs).
    private static void ApplyBorderAccent(Window window, bool active)
    {
        if (window.TryGetPlatformHandle()?.Handle is not { } handle || handle == 0)
            return;

        int colorRef = DWMWA_COLOR_DEFAULT;
        if (active)
        {
            Color accent = Application.Current?.PlatformSettings?.GetColorValues().AccentColor1
                           ?? Colors.Transparent;
            colorRef = accent.R | (accent.G << 8) | (accent.B << 16); // Color -> COLORREF (0x00BBGGRR)
        }
        NativeMethods.DwmSetWindowAttribute(handle, DWMWA_BORDER_COLOR, ref colorRef, sizeof(int));
    }

    /// <summary>
    /// True when the native Mica look should be used: Windows 11+ AND the user has
    /// "Transparency effects" enabled. When transparency is off we keep the solid look,
    /// since the Mica backdrop otherwise lingers (DWM keeps painting it).
    /// </summary>
    public static bool IsMicaEnabled()
        => OperatingSystem.IsWindows()
           && Environment.OSVersion.Version.Build >= 22000
           && IsOsTransparencyEnabled();

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
