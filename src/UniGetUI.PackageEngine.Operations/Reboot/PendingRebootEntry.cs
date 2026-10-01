namespace UniGetUI.PackageEngine.Operations.Reboot;

public sealed class PendingRebootEntry
{
    public string PackageId { get; set; } = "";
    public string PackageName { get; set; } = "";
    public string ManagerName { get; set; } = "";
    public string SourceName { get; set; } = "";
    public string Version { get; set; } = "";
    public string Kind { get; set; } = "";
    public string RecordedAtUtc { get; set; } = "";
    public string BootId { get; set; } = "";
    public long UptimeTicks { get; set; }
}
