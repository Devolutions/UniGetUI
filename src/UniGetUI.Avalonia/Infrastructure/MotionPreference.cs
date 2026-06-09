using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using UniGetUI.Core.Logging;

namespace UniGetUI.Avalonia.Infrastructure;

/// <summary>
/// Reads the OS "reduce motion / show animations" accessibility preference so UI
/// transitions can honor it (Windows: Settings → Accessibility → Visual effects →
/// Animation effects; macOS: Accessibility → Display → Reduce motion). Windows is read
/// live on each access (cheap P/Invoke); macOS/Linux are read once and cached because
/// they spawn a process. Defaults to "animations on" if the preference can't be read.
/// </summary>
internal static class MotionPreference
{
    private static bool? _cachedUnix;

    /// <summary>True when the user has asked the OS to minimize animations.</summary>
    public static bool ReducedMotion
    {
        get
        {
            if (OperatingSystem.IsWindows())
                return GetWindowsReducedMotion();
            return _cachedUnix ??= GetUnixReducedMotion();
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool GetWindowsReducedMotion()
    {
        try
        {
            // Same signal WinUI's UISettings.AnimationsEnabled reads; pvParam is a BOOL.
            int enabled = 1;
            if (NativeMethods.SystemParametersInfoW(NativeMethods.SPI_GETCLIENTAREAANIMATION, 0, ref enabled, 0))
                return enabled == 0;
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not read SPI_GETCLIENTAREAANIMATION; assuming animations enabled");
            Logger.Warn(ex);
        }
        return false;
    }

    private static bool GetUnixReducedMotion()
    {
        try
        {
            if (OperatingSystem.IsMacOS())
                return ReadCommand("/usr/bin/defaults", ["read", "com.apple.universalaccess", "reduceMotion"]) is "1";

            if (OperatingSystem.IsLinux())
                return string.Equals(
                    ReadCommand("/usr/bin/gsettings", ["get", "org.gnome.desktop.interface", "gtk-enable-animations"]),
                    "false", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not read OS reduce-motion preference; assuming animations enabled");
            Logger.Warn(ex);
        }
        return false;
    }

    private static string? ReadCommand(string fileName, string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var proc = Process.Start(psi);
        if (proc is null)
            return null;

        string output = proc.StandardOutput.ReadToEnd();
        if (!proc.WaitForExit(2000))
        {
            try { proc.Kill(); } catch { /* best effort */ }
            return null;
        }
        return proc.ExitCode == 0 ? output.Trim() : null;
    }

    private static class NativeMethods
    {
        public const uint SPI_GETCLIENTAREAANIMATION = 0x1042;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SystemParametersInfoW(uint uiAction, uint uiParam, ref int pvParam, uint fWinIni);
    }
}
