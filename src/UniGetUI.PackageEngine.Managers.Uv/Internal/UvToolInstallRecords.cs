using System.Text.Json;
using System.Text.Json.Serialization;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;

namespace UniGetUI.PackageEngine.Managers.UvManager.Internal;

/// <summary>
/// Remembers the python request UniGetUI used to install or upgrade each uv tool, so updates can
/// repeat it instead of silently switching the interpreter. One row per tool id, latest wins.
/// </summary>
internal static class UvToolInstallRecords
{
    internal const int SchemaVersion = 1;
    private static readonly Lock Gate = new();

    internal static string? TestPathOverride { get; set; }

    private static string FilePath =>
        TestPathOverride ?? Path.Combine(CoreData.UniGetUIDataDirectory, "uv_tool_install_records.json");

    internal static UvToolInstallRecord? Get(string packageId)
    {
        lock (Gate)
        {
            return Load()
                .FirstOrDefault(record => string.Equals(
                    record.Id,
                    packageId,
                    StringComparison.OrdinalIgnoreCase
                ));
        }
    }

    internal static void Record(UvToolInstallRecord record)
    {
        lock (Gate)
        {
            List<UvToolInstallRecord> records = Load()
                .Where(existing => !string.Equals(
                    existing.Id,
                    record.Id,
                    StringComparison.OrdinalIgnoreCase
                ))
                .ToList();
            records.Add(record);
            Write(records);
        }
    }

    internal static void Forget(string packageId)
    {
        lock (Gate)
        {
            Write(
                Load()
                    .Where(existing => !string.Equals(
                        existing.Id,
                        packageId,
                        StringComparison.OrdinalIgnoreCase
                    ))
                    .ToList()
            );
        }
    }

    private static void Write(List<UvToolInstallRecord> records)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllBytes(
                FilePath,
                JsonSerializer.SerializeToUtf8Bytes(
                    [.. records],
                    UvToolInstallRecordJson.Default.UvToolInstallRecordArray
                )
            );
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warn($"Could not remember how uv tool {records.FirstOrDefault()?.Id} was installed: {ex.Message}");
        }
    }

    private static UvToolInstallRecord[] Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize(
                    File.ReadAllBytes(FilePath),
                    UvToolInstallRecordJson.Default.UvToolInstallRecordArray
                ) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }
}

internal sealed class UvToolInstallRecord
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }

    [JsonPropertyName("pythonRequest")]
    public string? PythonRequest { get; init; }

    [JsonPropertyName("managedPython")]
    public bool? ManagedPython { get; init; }

    [JsonPropertyName("noPythonDownloads")]
    public bool NoPythonDownloads { get; init; }

    [JsonPropertyName("installedAt")]
    public string? InstalledAt { get; init; }

    [JsonPropertyName("source")]
    public required string Source { get; init; }

    [JsonPropertyName("schemaVersion")]
    public required int SchemaVersion { get; init; }
}

[JsonSerializable(typeof(UvToolInstallRecord[]))]
internal partial class UvToolInstallRecordJson : JsonSerializerContext;
