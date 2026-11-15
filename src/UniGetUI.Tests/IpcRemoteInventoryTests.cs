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

    [Fact]
    public void CredentialBearingAndAmbiguousSourcesAreBlankedNeverSent()
    {
        var manager = new Pip();
        var credentialed = new ManagerSource(
            manager,
            $"registry+https://user:{Secret}@private.example.test/index",
            new Uri($"https://user:{Secret}@private.example.test/index"));
        var safe = new ManagerSource(manager, "mirror", new Uri("https://mirror.example.test/"));
        var ambiguousA = new ManagerSource(manager, "corp", new Uri("https://a.example.test/"));
        var ambiguousB = new ManagerSource(manager, "corp", new Uri("https://b.example.test/"));
        IPackage[] packages =
        [
            new Package("Secret", "secret", "1.0", credentialed, manager),
            new Package("Safe", "safe", "1.0", safe, manager),
            new Package("A", "a", "1.0", ambiguousA, manager),
            new Package("B", "b", "1.0", ambiguousB, manager),
        ];

        var projected = IpcRemoteInventory.ProjectPackages(packages, installed: true, new HashSet<string>());
        var sources = IpcRemoteInventory.BuildSources([manager], packages.Select(package => package.Source));
        var json = IpcJson.Serialize(projected.ToArray()) + IpcJson.Serialize(sources.ToArray());

        Assert.DoesNotContain(Secret, json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"sourceName\":", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SourceLabelsContainingLocalPathsAreNotSent()
    {
        var manager = new PowerShell7();
        const string localPath = @"C:\Private\packages";
        var source = new ManagerSource(manager, localPath, new Uri(localPath));
        IPackage[] packages = [new Package("Package", "package", "1.0", source, manager)];

        var projected = IpcRemoteInventory.ProjectPackages(packages, installed: true, new HashSet<string>());
        var sources = IpcRemoteInventory.BuildSources([manager], [source]);
        var json = IpcJson.Serialize(projected.ToArray()) + IpcJson.Serialize(sources.ToArray());

        Assert.DoesNotContain(@"C:\\Private\\packages", json, StringComparison.Ordinal);
    }

    [Fact]
    public void UnsafeAndAmbiguousSourcesAreOmitted()
    {
        var manager = new PowerShell7();
        IManagerSource[] observed =
        [
            new ManagerSource(manager, @"C:\Private\packages", new Uri(@"C:\Private\packages")),
            new ManagerSource(manager, "corp", new Uri("https://a.example.test/")),
            new ManagerSource(manager, "corp", new Uri("https://b.example.test/")),
            new ManagerSource(manager, "my-token-feed", new Uri("https://example.test/")),
        ];

        var sources = IpcRemoteInventory.BuildSources([manager], observed);

        Assert.DoesNotContain(sources, source => string.IsNullOrEmpty(source.Name) || source.Name == "corp" || source.Name == "my-token-feed");
        Assert.Contains(sources, source => source.Name == "PSGallery");
        Assert.All(sources, source => Assert.Equal("", source.Url));
        Assert.Equal(RemoteSourceResolution.NotFound,
            IpcRemoteInventory.ResolveSource([manager], observed, manager.Id, "my-token-feed"));
    }

    [Fact]
    public void SourceResolutionCannotInferInstalledOrigin()
    {
        var manager = new Pip();
        var capabilities = manager.Capabilities;
        capabilities.InstalledPackagesKeepTheirSource = false;
        manager.Capabilities = capabilities;
        var feed = new ManagerSource(manager, "nuget.org", new Uri("https://api.nuget.org/v3/index.json"));
        IPackage[] packages = [new Package("Tool", "tool", "1.0", feed, manager)];

        foreach (var installed in new[] { true, false })
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
