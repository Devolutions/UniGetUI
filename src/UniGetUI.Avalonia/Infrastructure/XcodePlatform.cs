namespace UniGetUI.Avalonia.Infrastructure;

internal static class XcodePlatform
{
    public static bool IsSupported =>
        IsSupportedPlatform(OperatingSystem.IsMacOS(), OperatingSystem.IsMacOSVersionAtLeast(13));

    internal static bool IsSupportedPlatform(bool isMacOS, bool isMacOS13OrLater) =>
        isMacOS && isMacOS13OrLater;
}
