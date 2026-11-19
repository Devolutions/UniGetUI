using System.Text.Json;
using UniGetUI.Interface;
using UniGetUI.PackageEngine;
using UniGetUI.PackageEngine.ManagerClasses.Classes;

namespace UniGetUI.Tests;

public sealed class IpcRemoteInventoryTests
{
    private static readonly string[] RemoteManagerFields =
    [
        "name", "displayName", "enabled", "ready", "notificationsSuppressed",
        "executablePath", "executableArguments",
        "capabilities", "capabilities.canRunAsAdmin", "capabilities.canSkipIntegrityChecks",
        "capabilities.canRunInteractively", "capabilities.canRemoveDataOnUninstall",
        "capabilities.canDownloadInstaller", "capabilities.supportsCustomVersions",
        "capabilities.supportsCustomArchitectures", "capabilities.supportsCustomScopes",
        "capabilities.supportsPreRelease", "capabilities.supportsCustomLocations",
        "capabilities.supportsCustomSources", "capabilities.mustInstallSourcesAsAdmin",
        "health", "health.state", "health.needsAttention", "health.failingListing", "health.reason",
        "health.consecutiveFailures",
        .. ListingFields("health.search"), .. ListingFields("health.installed"), .. ListingFields("health.updates"),
    ];

    private static string[] ListingFields(string prefix) =>
    [
        prefix,
        .. new[]
        {
            "lastSuccessUtc", "lastFailureUtc", "lastFailureReason", "lastFailureWasTimeout",
            "consecutiveFailures", "failureStreakStartUtc", "lastPackageCount", "lastFastRepairUtc",
            "lastFastRepairReason", "recentDurationsMs",
        }.Select(name => $"{prefix}.{name}"),
    ];

    private static void CollectNames(JsonElement element, string prefix, SortedSet<string> names)
    {
        foreach (var property in element.EnumerateObject())
        {
            string name = prefix.Length == 0 ? property.Name : $"{prefix}.{property.Name}";
            names.Add(name);
            if (property.Value.ValueKind == JsonValueKind.Object)
                CollectNames(property.Value, name, names);
        }
    }

    [Fact]
    public void ListManagersSendsOnlyTheListedFieldsWithLocalDetailsBlanked()
    {
        var managers = IpcRemoteInventory.ListManagers();
        Assert.NotEmpty(managers);
        using var json = JsonDocument.Parse(IpcJson.Serialize(managers));

        foreach (var manager in json.RootElement.EnumerateArray())
        {
            var names = new SortedSet<string>(StringComparer.Ordinal);
            CollectNames(manager, "", names);
            Assert.Equal(RemoteManagerFields.Order(StringComparer.Ordinal), names);
            Assert.Equal("", manager.GetProperty("executablePath").GetString());
            Assert.Equal("", manager.GetProperty("executableArguments").GetString());
            Assert.Equal("unknown", manager.GetProperty("health").GetProperty("state").GetString());
            Assert.Equal(JsonValueKind.Null, manager.GetProperty("health").GetProperty("reason").ValueKind);
        }
    }
    [Fact]
    public void ManagerInventoryDoesNotPersistManagerHealth()
    {
        _ = PEInterface.Managers;
        var oldOverride = ManagerHealthStore.TestPathOverride;
        var directory = Path.Combine(Path.GetTempPath(), $"unigetui-health-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var firstPath = Path.Combine(directory, "first.json");
        var secondPath = Path.Combine(directory, "second.json");
        try
        {
            ManagerHealthStore.ResetForTests();
            ManagerHealthStore.TestPathOverride = firstPath;
            _ = ManagerHealthStore.Get("test");
            ManagerHealthStore.TestPathOverride = secondPath;

            Assert.NotEmpty(IpcRemoteInventory.ListManagers());
            Assert.False(File.Exists(firstPath));
        }
        finally
        {
            ManagerHealthStore.ResetForTests();
            ManagerHealthStore.TestPathOverride = oldOverride;
            if (File.Exists(firstPath))
                File.Delete(firstPath);
            if (File.Exists(secondPath))
                File.Delete(secondPath);
            Directory.Delete(directory);
        }
    }
}
