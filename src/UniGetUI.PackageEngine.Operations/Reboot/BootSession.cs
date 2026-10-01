using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using UniGetUI.Core.Logging;

namespace UniGetUI.PackageEngine.Operations.Reboot;

public static class BootSession
{
    public static string? TestIdOverride { get; set; }
    public static long? TestUptimeOverride { get; set; }

    private const int SystemBootEnvironmentInformation = 90;

    [StructLayout(LayoutKind.Sequential)]
    private struct BootEnvironmentInformation
    {
        public Guid BootIdentifier;
        public int FirmwareType;
        public ulong BootFlags;
    }

    [SupportedOSPlatform("windows")]
    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(
        int systemInformationClass,
        out BootEnvironmentInformation systemInformation,
        int systemInformationLength,
        IntPtr returnLength
    );

    [SupportedOSPlatform("windows")]
    [DllImport("kernelbase.dll", SetLastError = false)]
    private static extern void QueryInterruptTime(out ulong lpInterruptTime);

    public static string GetId()
    {
        if (TestIdOverride is { } overriden) return overriden;

        if (OperatingSystem.IsWindows())
        {
            try
            {
                int status = NtQuerySystemInformation(
                    SystemBootEnvironmentInformation,
                    out BootEnvironmentInformation info,
                    Marshal.SizeOf<BootEnvironmentInformation>(),
                    IntPtr.Zero);

                if (status == 0 && info.BootIdentifier != Guid.Empty)
                    return info.BootIdentifier.ToString("N");

                Logger.Warn($"Could not read the Windows boot identifier (NTSTATUS 0x{status:X8})");
            }
            catch (Exception ex)
            {
                Logger.Warn("Could not read the Windows boot identifier");
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
