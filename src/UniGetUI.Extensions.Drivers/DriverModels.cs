using System.Text.Json.Serialization;

namespace UniGetUI.Extensions.Drivers;

public enum DriverRebootBehavior
{
    NeverReboots = 0,
    AlwaysRequiresReboot = 1,
    CanRequestReboot = 2,
}

public sealed record DriverUpdate
{
    public string Id { get; init; } = "";
    public int Revision { get; init; }
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string DriverClass { get; init; } = "";
    public string Manufacturer { get; init; } = "";
    public string Model { get; init; } = "";
    public string Provider { get; init; } = "";
    public string HardwareId { get; init; } = "";
    public string? DriverDate { get; init; }
    public long Size { get; init; }
    public DriverRebootBehavior RebootBehavior { get; init; }
    public string SupportUrl { get; init; } = "";
    public bool EulaAccepted { get; init; } = true;
    public string? DeviceName { get; init; }
    public string? InstalledVersion { get; init; }
    public string? InstalledDate { get; init; }
}

public enum DriverInstallResultCode
{
    NotStarted = 0,
    InProgress = 1,
    Succeeded = 2,
    SucceededWithErrors = 3,
    Failed = 4,
    Aborted = 5,
    NotFound = 100,
    RequiresLicense = 101,
    DownloadFailed = 102,
}

public sealed record DriverInstallResult
{
    public string Id { get; init; } = "";
    public DriverInstallResultCode ResultCode { get; init; }
    public int HResult { get; init; }
    public bool RebootRequired { get; init; }

    public bool Succeeded => ResultCode is DriverInstallResultCode.Succeeded or DriverInstallResultCode.SucceededWithErrors;
}

internal sealed record ScanPayload
{
    public List<DriverUpdate>? Updates { get; init; }
    public string? Error { get; init; }
    public int HResult { get; init; }
}

internal sealed record InstallPayload
{
    public List<DriverInstallResult>? Results { get; init; }
    public bool RebootRequired { get; init; }
    public string? Error { get; init; }
    public int HResult { get; init; }
}

internal sealed record CachePayload
{
    public DateTimeOffset CheckedAt { get; init; }
    public List<DriverUpdate>? Updates { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ScanPayload))]
[JsonSerializable(typeof(InstallPayload))]
[JsonSerializable(typeof(CachePayload))]
internal partial class DriversJsonContext : JsonSerializerContext;
