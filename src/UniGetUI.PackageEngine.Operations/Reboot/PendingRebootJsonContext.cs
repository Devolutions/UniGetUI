using System.Text.Json.Serialization;

namespace UniGetUI.PackageEngine.Operations.Reboot;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(List<PendingRebootEntry>))]
[JsonSerializable(typeof(PendingRebootEntry))]
internal sealed partial class PendingRebootJsonContext : JsonSerializerContext;
