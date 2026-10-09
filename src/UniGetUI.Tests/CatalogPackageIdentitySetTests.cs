using System.Diagnostics;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.Models;
using UniGetUI.Avalonia.ViewModels;
using UniGetUI.Avalonia.ViewModels.Pages;
using UniGetUI.PackageEngine.Classes.Manager;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Managers.ChocolateyManager;
using UniGetUI.PackageEngine.Managers.NpmManager;
using UniGetUI.PackageEngine.Operations;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.Serializable;
using Xunit.Abstractions;

namespace UniGetUI.Tests;

public class CatalogPackageIdentitySetTests(ITestOutputHelper output)
{
    private static Npm CreateManager()
    {
        var manager = new Npm();
        var properties = manager.Properties;
        properties.Id = "catalog-test";
        properties.Name = "CatalogTest";
        properties.DisplayName = "Catalog test manager";
        manager.Properties = properties;
        return manager;
    }

    private static CatalogEntry Entry(string manager, string id = "example.tool", string source = "private") => new()
    {
        Name = "Example tool",
        Id = id,
        ManagerName = manager,
        Source = source,
        Version = "unrelated-version",
    };

    private static Package Package(IPackageManager manager, string id = "EXAMPLE.TOOL", string source = "PRIVATE") =>
        new("Example tool", id, "1.0", new ManagerSource(manager, source, new Uri("https://example.test/feed")), manager);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IndexedIdentitiesMatchExistingPredicates(bool installedInventory)
    {
        var manager = CreateManager();
        var chocolatey = new Chocolatey();
        IPackage[] packages =
        [
            Package(manager),
            Package(manager, source: "public"),
            Package(chocolatey, source: "community"),
            Package(manager), // Duplicate inventory versions or operations do not change membership.
        ];
        var identities = new CatalogPackageIdentitySet(packages, installedInventory);
        foreach (string alias in new[]
                 {
                     manager.Id, manager.Name, manager.DisplayName, chocolatey.Id, chocolatey.Name,
                     chocolatey.DisplayName, "missing-manager",
                 })
            foreach (string id in new[] { "EXAMPLE.TOOL", "example.other", "example.tool.extra" })
                foreach (string source in new[] { "private", "PUBLIC", "community", "different" })
                {
                    var entry = Entry(alias.ToUpperInvariant(), id, source);
                    Assert.Equal(packages.Any(installedInventory ? entry.MatchesInstalled : entry.Matches),
                        identities.Contains(entry));
                }
        Assert.False(new CatalogPackageIdentitySet([]).Contains(Entry(manager.Id)));
    }

    [Fact]
    public void ChocolateySourceExceptionIsBasedOnManagerIdNotAnAlias()
    {
        var manager = CreateManager();
        var properties = manager.Properties;
        properties.DisplayName = "Chocolatey";
        manager.Properties = properties;
        var identities = new CatalogPackageIdentitySet([Package(manager)], installedInventory: true);
        Assert.True(identities.Contains(Entry("CHOCOLATEY")));
        Assert.False(identities.Contains(Entry("CHOCOLATEY", source: "different")));
    }

    [Fact]
    public void RebuildingSnapshotsReflectsInventoryRemovalAndSourceChanges()
    {
        var manager = CreateManager();
        var packages = new List<IPackage> { Package(manager) };
        var first = new CatalogPackageIdentitySet(packages, installedInventory: true);
        packages.Clear();
        packages.Add(Package(manager, source: "public"));
        var second = new CatalogPackageIdentitySet(packages, installedInventory: true);
        Assert.True(first.Contains(Entry(manager.Id)));
        Assert.False(second.Contains(Entry(manager.Id)));
        Assert.True(second.Contains(Entry(manager.Name, source: "public")));
        packages.Clear();
        Assert.False(new CatalogPackageIdentitySet(packages, installedInventory: true).Contains(Entry(manager.Id)));
    }

    [Theory]
    [InlineData(OperationStatus.InQueue, true)]
    [InlineData(OperationStatus.Running, true)]
    [InlineData(OperationStatus.Succeeded, false)]
    [InlineData(OperationStatus.Failed, false)]
    [InlineData(OperationStatus.Canceled, false)]
    public void StateRefreshIndexesOnlyActiveOperationsWithExactSourceIdentity(OperationStatus status, bool pending)
    {
        // This manager is deliberately absent from PEInterface, avoiding icon loading and manager initialization.
        var manager = CreateManager();
        using var operation = new InstallPackageOperation(Package(manager), new InstallOptions()) { Status = status };
        var operationVm = new OperationViewModel(operation);
        AvaloniaOperationRegistry.OperationViewModels.Add(operationVm);
        try
        {
            var vm = new SoftwareCatalogViewModel
            {
                HideUnavailablePackages = false,
                SelectedCatalog = new CatalogDefinition
                {
                    Id = "test",
                    Name = "Test",
                    Packages =
                    [
                        Entry(manager.Id), Entry(manager.Name), Entry(manager.DisplayName),
                        Entry(manager.Id, source: "different"),
                        Entry(manager.Id, id: "different"),
                        Entry("different-manager"),
                    ],
                },
            };
            Assert.Equal([pending, pending, pending, false, false, false], vm.Packages.Select(p => p.IsPending));
            operation.Status = OperationStatus.Canceled;
            vm.UpdateStates();
            Assert.All(vm.Packages, tile => Assert.False(tile.IsPending));
        }
        finally
        {
            AvaloniaOperationRegistry.OperationViewModels.Remove(operationVm);
        }
    }

    [Fact]
    public void PendingChocolateyOperationsStillRequireTheCatalogSource()
    {
        var manager = new Chocolatey();
        var package = Package(manager, source: "community");
        var entry = Entry(manager.DisplayName, source: "private");
        Assert.True(new CatalogPackageIdentitySet([package], installedInventory: true).Contains(entry));
        Assert.False(new CatalogPackageIdentitySet([package]).Contains(entry));
    }

    [Theory]
    [InlineData(100)]
    [InlineData(1000)]
    [InlineData(5000)]
    public void IdentityRefreshEnumeratesInventoryAndOperationsOnceRegardlessOfCatalogSize(int count)
    {
        var manager = CreateManager();
        var packages = Enumerable.Range(0, count).Select(i => Package(manager, id: $"installed-{i}")).ToArray();
        int visits = 0;
        IEnumerable<IPackage> CountedPackages()
        {
            for (int i = 0; i < count; i++)
            {
                visits++;
                yield return packages[i];
            }
        }
        var entries = Enumerable.Range(0, count).Select(i => Entry(manager.Id, id: $"missing-{i}")).ToArray();
        var timer = Stopwatch.StartNew();
        var installed = new CatalogPackageIdentitySet(CountedPackages(), installedInventory: true);
        var pending = new CatalogPackageIdentitySet(CountedPackages());
        foreach (var entry in entries)
        {
            Assert.False(installed.Contains(entry));
            Assert.False(pending.Contains(entry));
        }
        timer.Stop();
        Assert.Equal(2 * count, visits);
        double indexedMs = timer.Elapsed.TotalMilliseconds;
        visits = 0;
        int matches = 0;
        timer.Restart();
        foreach (var entry in entries)
        {
            if (CountedPackages().Any(entry.MatchesInstalled)) matches++;
            if (CountedPackages().Any(entry.Matches)) matches++;
        }
        timer.Stop();
        Assert.Equal(0, matches);
        Assert.Equal(2 * count * count, visits);
        output.WriteLine(
            $"Catalog/inventory/operations={count}: indexed visits={2 * count}, scan visits={visits}; " +
            $"indexed={indexedMs:F3} ms, scans={timer.Elapsed.TotalMilliseconds:F3} ms. " +
            "Timings are diagnostic; deterministic enumeration counts are the scaling assertion.");
    }
}
