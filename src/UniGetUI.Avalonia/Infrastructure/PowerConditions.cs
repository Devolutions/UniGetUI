using System.Runtime.InteropServices;

namespace UniGetUI.Avalonia.Infrastructure;

internal static class PowerConditions
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

#pragma warning disable CA1416
    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
#pragma warning restore CA1416

    public static bool IsOnBattery()
    {
        if (!OperatingSystem.IsWindows()) return false;
#pragma warning disable CA1416
        return GetSystemPowerStatus(out var status) && status.ACLineStatus == 0;
#pragma warning restore CA1416
    }

    public static bool IsBatterySaverOn()
    {
        if (!OperatingSystem.IsWindows()) return false;
#pragma warning disable CA1416
        return GetSystemPowerStatus(out var status) && (status.SystemStatusFlag & 0x01) != 0;
#pragma warning restore CA1416
    }

    public static bool IsOnMeteredConnection()
    {
#if WINDOWS
        var costType = Windows.Networking.Connectivity.NetworkInformation
            .GetInternetConnectionProfile()
            ?.GetConnectionCost()
            .NetworkCostType;
        return costType is Windows.Networking.Connectivity.NetworkCostType.Fixed
            or Windows.Networking.Connectivity.NetworkCostType.Variable;
#else
        return false;
#endif
    }
}
