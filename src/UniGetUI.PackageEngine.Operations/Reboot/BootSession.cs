using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using UniGetUI.Core.Logging;

namespace UniGetUI.PackageEngine.Operations.Reboot;

public static class BootSession
{
    public static string? TestIdOverride { get; set; }
    public static long? TestUptimeOverride { get; set; }

    [SupportedOSPlatform("windows")]
    [DllImport("kernelbase.dll", SetLastError = false)]
    private static extern void QueryInterruptTime(out ulong lpInterruptTime);

    private const string WindowsBootCounterKey =
        @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters";

    private static readonly Lazy<string> _id = new(ReadId);

    public static string GetId() => TestIdOverride ?? _id.Value;

    private static string ReadId()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                if (Microsoft.Win32.Registry.GetValue(WindowsBootCounterKey, "BootId", null) is int counter
                    && counter > 0)
                {
                    return "windows-boot-" + counter.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                Logger.Warn("The Windows boot counter is unavailable; reboots are detected from uptime alone");
            }
            catch (Exception ex)
            {
                Logger.Warn("Could not read the Windows boot counter");
                Logger.Warn(ex);
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            try
            {
                string id = File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim();
                if (id.Length > 0) return id;
            }
            catch (Exception ex)
            {
                Logger.Warn("Could not read /proc/sys/kernel/random/boot_id");
                Logger.Warn(ex);
            }
        }

        return "";
    }

    public static long GetUptimeTicks()
    {
        if (TestUptimeOverride is { } overriden) return overriden;

        if (OperatingSystem.IsWindows())
        {
            try
            {
                QueryInterruptTime(out ulong interruptTime);
                if (interruptTime > 0) return (long)interruptTime;
            }
            catch (Exception ex)
            {
                Logger.Warn("QueryInterruptTime is unavailable; falling back to the monotonic tick count");
                Logger.Warn(ex);
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            try
            {
                string uptime = File.ReadAllText("/proc/uptime").Split(' ')[0];
                if (double.TryParse(uptime, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double seconds)
                    && seconds > 0)
                {
                    return (long)(seconds * TimeSpan.TicksPerSecond);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("Could not read the uptime from /proc/uptime");
                Logger.Warn(ex);
            }
        }

        return Environment.TickCount64 * TimeSpan.TicksPerMillisecond;
    }
}
