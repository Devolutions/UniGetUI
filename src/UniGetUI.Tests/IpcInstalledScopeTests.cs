using System.Text.Json;
using UniGetUI.Interface;
using UniGetUI.Interface.Enums;
using UniGetUI.PackageEngine.Classes.Manager;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Managers.PipManager;
using UniGetUI.PackageEngine.Managers.PowerShell7Manager;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.PackageLoader;
using UniGetUI.PackageEngine.Structs;

namespace UniGetUI.Tests;

public sealed class IpcInstalledScopeTests
{
    private static (Pip Manager, InstalledPackagesLoader Loader) CreateInstalledLoader()
    {
        var manager = new Pip();
        _ = new DiscoverablePackagesLoader([manager]);
        _ = new UpgradablePackagesLoader([manager]);
        return (manager, new InstalledPackagesLoader([manager]));
    }

    private static IPackage ResolveInstalled(Pip manager, string? source, string? scope = null) =>
        IpcPackageApi.ResolvePackage(
            new IpcPackageActionRequest
            {
                PackageId = "requests",
                ManagerName = manager.Id,
                PackageSource = source,
                Scope = scope,
            },
            IpcPackageLookupMode.Installed
        );

    [Fact]
    public async Task InstalledInventoryCarriesReportedScopeThroughJson()
    {
        var (manager, loader) = CreateInstalledLoader();
        var source = new ManagerSource(manager, "registry:a", new Uri("https://a.example.test/"));
        await loader.AddForeign(
            new Package("Requests", "requests", "2.0", source, manager, new OverridenInstallationOptions(PackageScope.User))
        );

        string json = IpcJson.Serialize(IpcPackageApi.ListInstalledPackages().ToArray());
        using var document = JsonDocument.Parse(json);
        Assert.True(document.RootElement[0].TryGetProperty("reportedScope", out var reportedScope));
        Assert.Equal(PackageScope.User, reportedScope.GetString());
        Assert.False(document.RootElement[0].TryGetProperty("sourceName", out _));
        var parsed = IpcJson.Deserialize<IpcPackageInfo[]>(json);

        var entry = Assert.Single(parsed!);
        Assert.Equal(PackageScope.User, entry.ReportedScope);
    }

    [Fact]
    public void PackageDetailsJsonDoesNotAddCanonicalSourceName()
    {
        using var document = JsonDocument.Parse(IpcJson.Serialize(new IpcPackageDetailsInfo()));

        Assert.False(document.RootElement.TryGetProperty("sourceName", out _));
    }

    [Fact]
    public void InstalledIdentityDistinguishesScopeAndSourcesSharingADisplayName()
    {
        var manager = new Pip();
        var first = new ManagerSource(manager, "registry:a", new Uri("https://a.example.test/"));
        var second = new ManagerSource(manager, "registry\\b", new Uri("https://b.example.test/"));
        Assert.Equal(first.AsString_DisplayName, second.AsString_DisplayName);

        IPackage Make(ManagerSource source, string? scope) =>
            new Package("Requests", "requests", "2.0", source, manager, new OverridenInstallationOptions(scope));

        long[] hashes =
        [
            Make(first, PackageScope.User).GetInstalledIdentityHash(),
            Make(first, PackageScope.Machine).GetInstalledIdentityHash(),
            Make(first, null).GetInstalledIdentityHash(),
            Make(second, PackageScope.User).GetInstalledIdentityHash(),
        ];

        Assert.Equal(hashes.Length, hashes.Distinct().Count());
        Assert.Equal(hashes[0], Make(first, PackageScope.User.ToUpperInvariant()).GetInstalledIdentityHash());
    }

    private sealed class CountingSource(IManagerSource inner) : IManagerSource
    {
        public int NameReads;
        public IconType IconId => inner.IconId;
        public bool IsVirtualManager => inner.IsVirtualManager;
        public IPackageManager Manager => inner.Manager;
        public string Name
        {
            get
            {
                NameReads++;
                return inner.Name;
            }
        }
        public Uri Url { get => inner.Url; set { } }
        public int? PackageCount => inner.PackageCount;
        public string? UpdateDate => inner.UpdateDate;
        public string AsString => inner.AsString;
        public string AsString_DisplayName => inner.AsString_DisplayName;
        public void RefreshSourceNames() { }
        public override string ToString() => inner.ToString() ?? "";
    }

    // Rows are compared pairwise on every reload, so the identity must not be rebuilt and rehashed per call.
    [Fact]
    public void InstalledIdentityHashIsComputedOncePerPackage()
    {
        var manager = new Pip();
        var source = new CountingSource(new ManagerSource(manager, "registry:a", new Uri("https://a.example.test/")));
        var package = new Package("Requests", "requests", "2.0", source, manager, new OverridenInstallationOptions(PackageScope.User));
        long expected = Package.ComputeInstalledIdentityHash(manager.Id, "registry:a", "requests", "2.0", PackageScope.User);
        int readsAfterConstruction = source.NameReads;

        for (int i = 0; i < 100; i++)
            Assert.Equal(expected, package.GetInstalledIdentityHash());

        Assert.Equal(readsAfterConstruction, source.NameReads);
    }

    [Fact]
    public async Task InstallationsDifferingOnlyByReportedScopeAreAllKept()
    {
        var (manager, loader) = CreateInstalledLoader();
        IPackage Make(string? scope) => new Package(
            "Requests", "requests", "2.0", manager.DefaultSource, manager, new OverridenInstallationOptions(scope));
        var user = Make(PackageScope.User);
        var machine = Make(PackageScope.Machine);
        var unknown = Make(null);

        foreach (var package in new[] { user, machine, unknown, Make(PackageScope.User.ToUpperInvariant()) })
            await loader.AddForeign(package);

        Assert.Equal(3, loader.Count());
        Assert.Collection(
            IpcPackageApi.ListInstalledPackages().Select(p => p.ReportedScope).Order(),
            Assert.Null,
            scope => Assert.Equal(PackageScope.Machine, scope),
            scope => Assert.Equal(PackageScope.User, scope)
        );
        Assert.Same(machine, loader.GetEquivalentPackage(Make(PackageScope.Machine)));

        loader.Remove(Make(PackageScope.Machine));
        Assert.Equal(2, loader.Count());
        Assert.Contains(user, loader.Packages);
        Assert.Contains(unknown, loader.Packages);
    }

    [Fact]
    public async Task ScopeUnawareProbeStillFindsTheOnlyInstalledScope()
    {
        var (manager, loader) = CreateInstalledLoader();
        var installed = new Package(
            "Requests", "requests", "2.0", manager.DefaultSource, manager, new OverridenInstallationOptions(PackageScope.User));
        await loader.AddForeign(installed);
        var probe = new Package("Requests", "requests", "2.0", manager.DefaultSource, manager);

        Assert.Same(installed, loader.GetEquivalentPackage(probe));
        loader.Remove(probe);
        Assert.Equal(0, loader.Count());
    }

    [Fact]
    public async Task AnySourceProbeRejectsAmbiguousScopesButKeepsUniqueAndExactMatches()
    {
        var manager = new Pip();
        var capabilities = manager.Capabilities;
        capabilities.InstalledPackagesKeepTheirSource = false;
        manager.Capabilities = capabilities;
        _ = new DiscoverablePackagesLoader([manager]);
        _ = new UpgradablePackagesLoader([manager]);
        var loader = new InstalledPackagesLoader([manager]);
        var feed = new ManagerSource(manager, "registry:a", new Uri("https://a.example.test/"));
        IPackage Make(string? scope, ManagerSource? source = null) => new Package(
            "Requests", "requests", "2.0", source ?? manager.DefaultSource, manager, new OverridenInstallationOptions(scope));
        var user = Make(PackageScope.User);
        var machine = Make(PackageScope.Machine);
        await loader.AddForeign(user);
        await loader.AddForeign(machine);

        Assert.Null(loader.GetEquivalentPackage(Make(null, feed)));
        loader.Remove(Make(null, feed));
        Assert.Equal(2, loader.Count());

        Assert.Same(machine, loader.GetEquivalentPackage(Make(PackageScope.Machine, feed)));
        Assert.Null(loader.GetEquivalentPackage(Make("other", feed)));

        loader.Remove(Make(PackageScope.User, feed));
        Assert.Equal([machine], loader.Packages);
        Assert.Same(machine, loader.GetEquivalentPackage(Make(null, feed)));
    }

    [Fact]
    public async Task ScopeUnawareProbeDoesNotCrossCanonicalSourcesSharingADisplayName()
    {
        var (manager, loader) = CreateInstalledLoader();
        var first = new ManagerSource(manager, "registry:a", new Uri("https://a.example.test/"));
        var second = new ManagerSource(manager, "registry\\b", new Uri("https://b.example.test/"));
        Assert.Equal(first.AsString_DisplayName, second.AsString_DisplayName);
        var installed = new Package(
            "Requests", "requests", "2.0", first, manager, new OverridenInstallationOptions(PackageScope.User));
        await loader.AddForeign(installed);
        var probe = new Package("Requests", "requests", "2.0", second, manager);

        Assert.Null(loader.GetEquivalentPackage(probe));
        loader.Remove(probe);
        Assert.Same(installed, Assert.Single(loader.Packages));
        Assert.Same(installed, loader.GetEquivalentPackage(new Package("Requests", "requests", "2.0", first, manager)));
    }

    [Fact]
    public async Task SourceIgnoringManagerProbeDoesNotPickOneScopeWhenAnotherSourceHoldsAnother()
    {
        var manager = new Pip();
        var capabilities = manager.Capabilities;
        capabilities.InstalledPackagesKeepTheirSource = false;
        manager.Capabilities = capabilities;
        _ = new DiscoverablePackagesLoader([manager]);
        _ = new UpgradablePackagesLoader([manager]);
        var loader = new InstalledPackagesLoader([manager]);
        var a = new ManagerSource(manager, "registry:a", new Uri("https://a.example.test/"));
        var b = new ManagerSource(manager, "registry:b", new Uri("https://b.example.test/"));
        await loader.AddForeign(new Package(
            "Requests", "requests", "2.0", a, manager, new OverridenInstallationOptions(PackageScope.User)));
        await loader.AddForeign(new Package(
            "Requests", "requests", "2.0", b, manager, new OverridenInstallationOptions(PackageScope.Machine)));
        var probe = new Package("Requests", "requests", "2.0", a, manager);

        Assert.Null(loader.GetEquivalentPackage(probe));
        loader.Remove(probe);
        Assert.Equal(2, loader.Count());
    }

    [Fact]
    public async Task ScopeUnawareProbeIsNotGuessedWhenSeveralScopesAreInstalled()
    {
        var (manager, loader) = CreateInstalledLoader();
        foreach (var scope in new[] { PackageScope.User, PackageScope.Machine })
            await loader.AddForeign(new Package(
                "Requests", "requests", "2.0", manager.DefaultSource, manager, new OverridenInstallationOptions(scope)));
        var probe = new Package("Requests", "requests", "2.0", manager.DefaultSource, manager);

        Assert.Null(loader.GetEquivalentPackage(probe));
        loader.Remove(probe);
        Assert.Equal(2, loader.Count());
    }

    [Fact]
    public async Task RequestedCanonicalSourceFindsTheMatchingInstalledPackage()
    {
        var (manager, loader) = CreateInstalledLoader();
        var first = new ManagerSource(manager, "registry:a", new Uri("https://a.example.test/"));
        var second = new ManagerSource(manager, "registry\\b", new Uri("https://b.example.test/"));
        // Different versions keep both rows in the loader, which keys on the display name.
        await loader.AddForeign(new Package("Requests", "requests", "1.0", first, manager));
        await loader.AddForeign(new Package("Requests", "requests", "2.0", second, manager));

        Assert.Equal("registry\\b", ResolveInstalled(manager, "registry\\b").Source.Name);
        Assert.Equal("registry:a", ResolveInstalled(manager, "registry:a").Source.Name);

        Assert.ThrowsAny<InvalidOperationException>(() => ResolveInstalled(manager, "registry:other"));
    }

    [Fact]
    public async Task LocalPackageLookupAcceptsAnAmbiguousDisplaySource()
    {
        var (manager, loader) = CreateInstalledLoader();
        var first = new ManagerSource(manager, "registry:a", new Uri("https://a.example.test/"));
        var second = new ManagerSource(manager, "registry\\b", new Uri("https://b.example.test/"));
        await loader.AddForeign(new Package("Requests", "requests", "1.0", first, manager));
        await loader.AddForeign(new Package("Requests", "requests", "2.0", second, manager));
        await UpgradablePackagesLoader.Instance.AddForeign(new Package("Other", "other", "1.0", first, manager));

        var result = IpcPackageApi.ResolvePackage(new IpcPackageActionRequest
        {
            PackageId = "requests",
            ManagerName = manager.Id,
            PackageSource = first.AsString_DisplayName,
        });

        Assert.Equal(first.AsString_DisplayName, result.Source.AsString_DisplayName);
    }

    [Fact]
    public async Task LocalPackageLookupKeepsTheOriginalScopeSelection()
    {
        var manager = new PowerShell7();
        _ = new DiscoverablePackagesLoader([manager]);
        _ = new UpgradablePackagesLoader([manager]);
        var loader = new InstalledPackagesLoader([manager]);
        var first = new ManagerSource(manager, "registry:a", new Uri("https://a.example.test/"));
        var second = new ManagerSource(manager, "registry:b", new Uri("https://b.example.test/"));
        await loader.AddForeign(
            new Package("Requests", "requests", "1.0", first, manager, new OverridenInstallationOptions(PackageScope.User))
        );
        await loader.AddForeign(
            new Package("Requests", "requests", "2.0", second, manager, new OverridenInstallationOptions(PackageScope.Machine))
        );
        await UpgradablePackagesLoader.Instance.AddForeign(new Package("Other", "other", "1.0", first, manager));

        var previouslySelected = loader.Packages.First(package => package.Id == "requests");
        var otherScope = previouslySelected.ReportedScope == PackageScope.User ? PackageScope.Machine : PackageScope.User;
        var result = IpcPackageApi.ResolvePackage(new IpcPackageActionRequest
        {
            PackageId = "requests",
            ManagerName = manager.Id,
            Scope = otherScope,
        });

        Assert.Equal(previouslySelected.Source.AsString_DisplayName, result.Source.AsString_DisplayName);
    }

    [Fact]
    public async Task ACaseInsensitiveSourceIsAcceptedOnlyWhenItDesignatesOneSource()
    {
        var (manager, loader) = CreateInstalledLoader();
        var source = new ManagerSource(manager, "registry:a", new Uri("https://a.example.test/"));
        await loader.AddForeign(new Package("Requests", "requests", "1.0", source, manager));

        Assert.Equal("registry:a", ResolveInstalled(manager, "REGISTRY:A").Source.Name);
    }

}
