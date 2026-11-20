using System.Text.Json;
using UniGetUI.Interface;
using UniGetUI.PackageEngine;
using UniGetUI.PackageEngine.Classes.Manager;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.ManagerClasses.Classes;
using UniGetUI.PackageEngine.Managers.PipManager;
using UniGetUI.PackageEngine.Managers.PowerShell7Manager;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.PackageLoader;
using UniGetUI.PackageEngine.Structs;

namespace UniGetUI.Tests;

public sealed class IpcRemoteInventoryTests
{
    private const string Secret = "s3cr3t-token";

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
            var info = IpcRemoteInventory.ProjectPackages(packages, installed, new HashSet<string>()).Single();
            Assert.Equal(feed.AsString_DisplayName, info.Source);
        }

        var unverified = new ManagerSource(new Pip(), "nuget.org", feed.Url);
        IPackage[] withoutProvenance = [new Package("Tool", "tool", "1.0", unverified, unverified.Manager)];
        Assert.Equal(unverified.AsString_DisplayName,
            IpcRemoteInventory.ProjectPackages(withoutProvenance, installed: true, new HashSet<string>()).Single().Source);
        Assert.True(unverified.Manager.Capabilities.InstalledPackagesKeepTheirSource);
        Assert.Equal(
            RemoteSourceResolution.OriginUnknown,
            IpcRemoteInventory.ResolveSource(
                [unverified.Manager], [unverified], unverified.Manager.Id, "nuget.org", requireInstalledOrigin: true));

        IManagerSource[] sources = [feed];
        Assert.Equal(
            RemoteSourceResolution.OriginUnknown,
            IpcRemoteInventory.ResolveSource([manager], sources, manager.Id, "nuget.org", requireInstalledOrigin: true));
        Assert.Equal(
            RemoteSourceResolution.Resolved,
            IpcRemoteInventory.ResolveSource([manager], sources, manager.Id, "nuget.org"));
    }

    [Fact]
    public async Task SideBySideVersionsCanShareAnUnknownSourceAndScope()
    {
        var manager = new PowerShell7();
        _ = new DiscoverablePackagesLoader([manager]);
        _ = new UpgradablePackagesLoader([manager]);
        var loader = new InstalledPackagesLoader([manager]);
        var source = manager.SourcesHelper.Factory.GetSourceOrDefault("PSGallery");
        Assert.True(manager.SourcesHelper.Factory.IsPlaceholder(source));

        await loader.AddForeign(new Package(
            "XKCD", "XKCD", "1.9.0", source, manager, new OverridenInstallationOptions(PackageScope.User)));
        await loader.AddForeign(new Package(
            "XKCD", "XKCD", "1.12.6", source, manager, new OverridenInstallationOptions(PackageScope.User)));

        var packages = IpcRemoteInventory.ProjectPackages(loader.Packages, installed: true, new HashSet<string>());

        Assert.Equal(2, packages.Count);
        Assert.All(packages, package =>
        {
            Assert.Equal("pwsh", package.Manager);
            Assert.Equal("XKCD", package.Id);
            Assert.Equal(PackageScope.User, package.ReportedScope);
        });
        Assert.Equal(["1.12.6", "1.9.0"], packages.Select(package => package.Version).Order(StringComparer.Ordinal));
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

    [Theory]
    [InlineData("mirror", RemoteSourceResolution.Resolved)]
    [InlineData("Mirror", RemoteSourceResolution.NotFound)]
    [InlineData("Mirror Display", RemoteSourceResolution.NotFound)]
    [InlineData("corp", RemoteSourceResolution.Ambiguous)]
    [InlineData("", RemoteSourceResolution.MissingSourceName)]
    public void SourceResolvesOnlyByUniqueOrdinalCanonicalName(string sourceName, RemoteSourceResolution expected)
    {
        var manager = new Pip();
        IManagerSource[] observed =
        [
            new ManagerSource(manager, "mirror", new Uri("https://mirror.example.test/")),
            new ManagerSource(manager, "corp", new Uri("https://a.example.test/")),
            new ManagerSource(manager, "corp", new Uri("https://b.example.test/")),
        ];

        Assert.Equal(expected, IpcRemoteInventory.ResolveSource([manager], observed, manager.Id, sourceName));
        Assert.Equal(
            RemoteSourceResolution.UnknownManager,
            IpcRemoteInventory.ResolveSource([manager], observed, "not-a-manager", "mirror"));
    }

    [Fact]
    public void IncompleteLocalInventoryIsReportedBusyWithoutStartingALoad()
    {
        var manager = new Pip();
        _ = new DiscoverablePackagesLoader([manager]);
        _ = new UpgradablePackagesLoader([manager]);
        var loader = new InstalledPackagesLoader([manager]);

        Assert.False(IpcRemoteInventory.TryListInstalledPackages(out var packages));
        Assert.Empty(packages);
        Assert.False(loader.IsLoading);
        Assert.False(loader.IsLoaded);
    }
}
