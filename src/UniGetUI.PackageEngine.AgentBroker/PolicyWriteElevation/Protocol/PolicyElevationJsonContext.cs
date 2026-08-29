using System.Text.Json;
using System.Text.Json.Serialization;

namespace UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation;

/// <summary>
/// Source-generated metadata for every type that crosses the elevated policy-write pipe.
/// Reflection-based serialization is never used on this path so the helper stays NativeAOT-safe.
/// </summary>
[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    NumberHandling = JsonNumberHandling.Strict,
    PropertyNameCaseInsensitive = false,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    WriteIndented = false)]
[JsonSerializable(typeof(PolicyElevationRequestMessage))]
[JsonSerializable(typeof(PolicyElevationResponseMessage))]
[JsonSerializable(typeof(JsonElement))]
public sealed partial class PolicyElevationJsonContext : JsonSerializerContext
{
}
