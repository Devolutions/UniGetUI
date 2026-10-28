using System.Text.Json;
using System.Text.Json.Serialization;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.PackageEngine.Managers.InstallerFeeds;

namespace UniGetUI.PackageEngine.Managers.WinInstallerManager.Internal;

internal static class InstallReceipts
{
    private static readonly Lock Gate = new();

    internal static string? TestPathOverride { get; set; }

    private static string FilePath =>
        TestPathOverride ?? Path.Combine(CoreData.UniGetUIDataDirectory, "InstallerFeeds", "receipts.json");

    public static string InstalledVersion(string packageId, ArpEntry entry)
    {
        string registered = entry.DisplayVersion ?? "0";
        lock (Gate)
        {
            return Load().FirstOrDefault(r => string.Equals(r.Id, packageId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(r.Key, entry.Key, StringComparison.OrdinalIgnoreCase)
                && r.RegisteredVersion == registered)?.FeedVersion ?? registered;
        }
    }

    public static void Record(string packageId, ArpEntry entry, string feedVersion)
    {
        lock (Gate)
        {
            var receipts = Load().Where(r => !string.Equals(r.Id, packageId, StringComparison.OrdinalIgnoreCase)).ToList();
            if (entry.DisplayVersion != feedVersion)
                receipts.Add(new InstallReceipt { Id = packageId, Key = entry.Key, RegisteredVersion = entry.DisplayVersion ?? "0", FeedVersion = feedVersion });
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllBytes(FilePath, FeedJson.SerializeToUtf8Bytes(receipts.ToArray(), InstallReceiptJsonContext.Default.InstallReceiptArray));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logger.Warn($"Could not remember the installed version of {packageId}: {ex.Message}");
            }
        }
    }

    private static InstallReceipt[] Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? FeedJson.Deserialize(File.ReadAllBytes(FilePath), InstallReceiptJsonContext.Default.InstallReceiptArray) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }
}

internal sealed class InstallReceipt
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("key")]
    public required string Key { get; init; }

    [JsonPropertyName("registeredVersion")]
    public required string RegisteredVersion { get; init; }

    [JsonPropertyName("feedVersion")]
    public required string FeedVersion { get; init; }
}

[JsonSerializable(typeof(InstallReceipt[]))]
internal partial class InstallReceiptJsonContext : JsonSerializerContext;
