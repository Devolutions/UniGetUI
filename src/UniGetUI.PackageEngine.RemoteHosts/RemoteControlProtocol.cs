using System.Text.Json;
using System.Text.Json.Serialization;

namespace UniGetUI.PackageEngine.RemoteHosts;

public static class RemoteControlProtocol
{
    public const int Version = 1;
    public const string LinuxInventoryMarker = "__UGUI_LINUX_V1__";
    public const string LinuxActionOkMarker = "__UGUI_LINUX_ACTION_OK__";
}

public enum RemoteBackendKind
{
    Unknown,
    Agent,
    LinuxAgentless,
}

public enum RemoteHostOsKind
{
    Unknown,
    Linux,
    MacOs,
    Windows,
}

public sealed class RemoteControlResponse
{
    [JsonPropertyName("protocol")]
    public int Protocol { get; set; } = RemoteControlProtocol.Version;

    [JsonPropertyName("ok")]
    public bool Ok { get; set; } = true;

    [JsonPropertyName("backend")]
    public string Backend { get; set; } = "agent";

    [JsonPropertyName("os")]
    public string? Os { get; set; }

    [JsonPropertyName("hostDescription")]
    public string? HostDescription { get; set; }

    [JsonPropertyName("canElevate")]
    public bool? CanElevate { get; set; }

    [JsonPropertyName("systemPackageManager")]
    public string? SystemPackageManager { get; set; }

    [JsonPropertyName("packages")]
    public List<RemoteInventoryPackageDto> Packages { get; set; } = [];

    [JsonPropertyName("errors")]
    public List<string> Errors { get; set; } = [];

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonIgnore]
    public RemoteBackendKind BackendKind =>
        Backend.Equals("linux-agentless", StringComparison.OrdinalIgnoreCase)
            ? RemoteBackendKind.LinuxAgentless
            : RemoteBackendKind.Agent;
}

public sealed class RemoteInventoryPackageDto
{
    [JsonPropertyName("managerId")]
    public string ManagerId { get; set; } = "";

    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("newVersion")]
    public string? NewVersion { get; set; }

    [JsonPropertyName("source")]
    public string Source { get; set; } = "";

    [JsonPropertyName("isUpgradable")]
    public bool IsUpgradable { get; set; }

    [JsonPropertyName("canRunAsAdmin")]
    public bool CanRunAsAdmin { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    AllowTrailingCommas = true
)]
[JsonSerializable(typeof(RemoteControlResponse))]
[JsonSerializable(typeof(RemoteInventoryPackageDto))]
[JsonSerializable(typeof(List<RemoteInventoryPackageDto>))]
[JsonSerializable(typeof(List<RemoteHost>))]
[JsonSerializable(typeof(RemoteHost))]
[JsonSerializable(typeof(List<string>))]
internal sealed partial class RemoteHostsJsonContext : JsonSerializerContext;

public static class RemoteHostsJson
{
    public static string SerializeHosts(IReadOnlyList<RemoteHost> hosts)
        => JsonSerializer.Serialize(hosts.ToList(), RemoteHostsJsonContext.Default.ListRemoteHost);

    public static List<RemoteHost> DeserializeHosts(string json)
        => JsonSerializer.Deserialize(json, RemoteHostsJsonContext.Default.ListRemoteHost) ?? [];

    public static string SerializeResponse(RemoteControlResponse response)
        => JsonSerializer.Serialize(response, RemoteHostsJsonContext.Default.RemoteControlResponse);

    public static RemoteControlResponse? DeserializeResponse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, RemoteHostsJsonContext.Default.RemoteControlResponse);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string SerializeStringList(List<string> values)
        => JsonSerializer.Serialize(values, RemoteHostsJsonContext.Default.ListString);

    public static List<string> DeserializeStringList(string json)
        => JsonSerializer.Deserialize(json, RemoteHostsJsonContext.Default.ListString) ?? [];
}
