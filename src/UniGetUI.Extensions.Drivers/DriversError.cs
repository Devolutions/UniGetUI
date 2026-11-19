namespace UniGetUI.Extensions.Drivers;

public enum DriversErrorKind
{
    Unknown,
    Offline,
    DisabledByPolicy,
    ServiceUnavailable,
    AccessDenied,
    ElevationProhibited,
    ElevationCancelled,
    NoElevator,
    UnreadableOutput,
    NotSupported,
}

public sealed class DriversException(DriversErrorKind kind, string message, int hResult = 0, Exception? innerException = null)
    : Exception(message, innerException)
{
    public DriversErrorKind Kind { get; } = kind;
    public int ErrorCode { get; } = hResult;

    public static DriversErrorKind Classify(int hResult) => unchecked((uint)hResult) switch
    {
        0x8024402C or 0x80072EE7 or 0x80072EFD or 0x80072EE2 or 0x80240438 or 0x8024401B => DriversErrorKind.Offline,
        0x8024002E or 0x80240025 => DriversErrorKind.DisabledByPolicy,
        0x8024001E or 0x80070422 or 0x8024A000 => DriversErrorKind.ServiceUnavailable,
        0x80070005 or 0x80240044 => DriversErrorKind.AccessDenied,
        _ => DriversErrorKind.Unknown,
    };
}
