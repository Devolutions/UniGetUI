using System.Text.Json.Serialization;

namespace UniGetUI.PackageEngine.Operations.History;

/// <summary>Source-generated JSON metadata for the persisted operation history (NativeAOT-safe).</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(List<OperationHistoryRecord>))]
[JsonSerializable(typeof(OperationHistoryRecord))]
[JsonSerializable(typeof(OperationHistoryOutputLine))]
internal sealed partial class OperationHistoryJsonContext : JsonSerializerContext;
