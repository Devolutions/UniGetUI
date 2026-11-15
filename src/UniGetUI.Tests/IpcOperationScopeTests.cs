using UniGetUI.Interface;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Managers.PipManager;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.PackageLoader;
using UniGetUI.PackageEngine.Structs;

namespace UniGetUI.Tests;

// Mutating IPC operations must act on the installation of the requested scope and refuse to guess
// between scope copies. Failures happen before any operation is created, so nothing is launched.
public sealed class IpcOperationScopeTests
{
    private static async Task<(Pip Manager, InstalledPackagesLoader Installed, UpgradablePackagesLoader Upgradable)> CreateLoadersAsync(
        string?[] installedScopes,
        string?[] upgradableScopes
    )
    {
        var manager = new Pip();
        _ = new DiscoverablePackagesLoader([manager]);
        var upgradable = new UpgradablePackagesLoader([manager]);
        var installed = new InstalledPackagesLoader([manager]);
        // Unrelated entries keep an otherwise empty loader from falling back to the real manager.
        await installed.AddForeign(new Package("Other", "other", "1.0", manager.DefaultSource, manager));
        await upgradable.AddForeign(new Package("Other", "other", "1.0", "2.0", manager.DefaultSource, manager));
        foreach (string? scope in installedScopes)
        {
            await installed.AddForeign(
                new Package("Requests", "requests", "1.0", manager.DefaultSource, manager, new OverridenInstallationOptions(scope))
            );
        }

        foreach (string? scope in upgradableScopes)
        {
            await upgradable.AddForeign(
                new Package("Requests", "requests", "1.0", "2.0", manager.DefaultSource, manager, new OverridenInstallationOptions(scope))
            );
        }

        return (manager, installed, upgradable);
    }

    private static IpcPackageActionRequest Request(Pip manager, string? scope) => new()
    {
        PackageId = "requests",
        ManagerName = manager.Id,
        Scope = scope,
        WaitForCompletion = false,
    };

    private static readonly Func<IpcPackageActionRequest, Task>[] Operations =
    [
        IpcPackageApi.UninstallPackageAsync,
        IpcPackageApi.ReinstallPackageAsync,
        IpcPackageApi.UninstallThenReinstallPackageAsync,
        IpcPackageApi.UpdatePackageAsync,
    ];

    public static TheoryData<int> OperationIndexes => [0, 1, 2, 3];

    [Theory]
    [MemberData(nameof(OperationIndexes))]
    public async Task EveryMutatingOperationRefusesScopeCopiesWithoutAScope(int operation)
    {
        var (manager, _, _) = await CreateLoadersAsync(
            [PackageScope.User, PackageScope.Machine],
            [PackageScope.User]
        );

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Operations[operation](Request(manager, null))
        );

        Assert.Contains("more than one scope", ex.Message);
    }

    [Theory]
    [MemberData(nameof(OperationIndexes))]
    public async Task EveryMutatingOperationRefusesAScopeThatHasNoInstallation(int operation)
    {
        var (manager, _, _) = await CreateLoadersAsync([PackageScope.User], []);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Operations[operation](Request(manager, PackageScope.Global))
        );

        Assert.Contains("No installation of \"requests\" in the \"machine\" scope", ex.Message);
    }

    [Theory]
    [InlineData(PackageScope.User)]
    [InlineData(PackageScope.Machine)]
    public async Task InstalledSelectionFollowsTheRequestedScopeInEitherOrder(string scope)
    {
        var (manager, _, _) = await CreateLoadersAsync([PackageScope.User, PackageScope.Machine], []);

        var package = IpcPackageApi.ResolveInstalledPackageForMutation(Request(manager, scope.ToUpperInvariant()));

        Assert.Equal(scope, package.NormalizedReportedScope);
    }

    [Fact]
    public async Task ScopeAliasesResolveToTheirLabels()
    {
        var (manager, _, _) = await CreateLoadersAsync([PackageScope.User, PackageScope.Machine], []);

        Assert.Equal(
            PackageScope.Machine,
            IpcPackageApi.ResolveInstalledPackageForMutation(Request(manager, PackageScope.Global)).NormalizedReportedScope
        );
        Assert.Equal(
            PackageScope.User,
            IpcPackageApi.ResolveInstalledPackageForMutation(Request(manager, PackageScope.Local)).NormalizedReportedScope
        );
    }

    [Fact]
    public async Task ASingleInstalledCopyIsStillSelectedWithoutAScopeOrWithAnUnknownScope()
    {
        var (manager, _, _) = await CreateLoadersAsync([PackageScope.User], []);
        Assert.Equal(
            PackageScope.User,
            IpcPackageApi.ResolveInstalledPackageForMutation(Request(manager, null)).NormalizedReportedScope
        );

        var (unknownManager, _, _) = await CreateLoadersAsync([null], []);
        Assert.Null(IpcPackageApi.ResolveInstalledPackageForMutation(Request(unknownManager, PackageScope.Machine)).NormalizedReportedScope);
    }

    [Fact]
    public async Task AnUnknownScopeCopyDoesNotStandInForAScopeWhenAScopedCopyExists()
    {
        var (manager, _, _) = await CreateLoadersAsync([PackageScope.User, null], []);

        Assert.Throws<InvalidOperationException>(() =>
            IpcPackageApi.ResolveInstalledPackageForMutation(Request(manager, PackageScope.Machine))
        );
        Assert.Equal(
            PackageScope.User,
            IpcPackageApi.ResolveInstalledPackageForMutation(Request(manager, PackageScope.User)).NormalizedReportedScope
        );
    }

    [Fact]
    public async Task UpdateRefusesACollapsedUpgradableEntryOfAnotherScopeInsteadOfUpdatingPlainly()
    {
        var (manager, _, _) = await CreateLoadersAsync(
            [PackageScope.User, PackageScope.Machine],
            [PackageScope.User]
        );

        var user = IpcPackageApi.ResolveUpdatePackageForMutation(Request(manager, PackageScope.User));
        Assert.True(user.IsUpgradable);
        Assert.Equal(PackageScope.User, user.NormalizedReportedScope);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            IpcPackageApi.ResolveUpdatePackageForMutation(Request(manager, PackageScope.Machine))
        );
        Assert.Contains("cannot be tied", ex.Message);
    }

    [Fact]
    public async Task UpdateRefusesAnUnscopedUpgradableEntryWhenSeveralScopesAreInstalled()
    {
        var (manager, _, _) = await CreateLoadersAsync(
            [PackageScope.User, PackageScope.Machine],
            [null]
        );

        var ex = Assert.Throws<InvalidOperationException>(() =>
            IpcPackageApi.ResolveUpdatePackageForMutation(Request(manager, PackageScope.Machine))
        );
        Assert.Contains("cannot be tied", ex.Message);
    }

    [Fact]
    public async Task UpdateWithoutAnUpgradableEntryKeepsTheInstalledFallbackOfTheScope()
    {
        var (manager, _, _) = await CreateLoadersAsync([PackageScope.User, PackageScope.Machine], []);

        var package = IpcPackageApi.ResolveUpdatePackageForMutation(Request(manager, PackageScope.Machine));

        Assert.False(package.IsUpgradable);
        Assert.Equal(PackageScope.Machine, package.NormalizedReportedScope);
    }

    [Fact]
    public async Task UpdateKeepsOrdinaryBehaviorForASingleCopy()
    {
        var (manager, _, _) = await CreateLoadersAsync([PackageScope.User], [PackageScope.User]);
        Assert.True(IpcPackageApi.ResolveUpdatePackageForMutation(Request(manager, null)).IsUpgradable);
        Assert.True(IpcPackageApi.ResolveUpdatePackageForMutation(Request(manager, PackageScope.User)).IsUpgradable);

        var (unscopedManager, _, _) = await CreateLoadersAsync([null], [null]);
        Assert.True(IpcPackageApi.ResolveUpdatePackageForMutation(Request(unscopedManager, PackageScope.Machine)).IsUpgradable);
    }

    [Fact]
    public async Task UpdateRefusesAnUpgradableEntryOfAnotherScopeWhenNothingIsInstalledThere()
    {
        var (manager, _, _) = await CreateLoadersAsync([], [PackageScope.User]);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            IpcPackageApi.ResolveUpdatePackageForMutation(Request(manager, PackageScope.Machine))
        );
        Assert.Contains("cannot be tied", ex.Message);
    }

    [Fact]
    public async Task UpdateRefusesAnUnscopedEntryWhenNoInstalledCopyEstablishesItsScope()
    {
        var (manager, _, _) = await CreateLoadersAsync([], [null]);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            IpcPackageApi.ResolveUpdatePackageForMutation(Request(manager, PackageScope.Machine))
        );

        Assert.Contains("cannot be tied", ex.Message);
    }

    [Fact]
    public async Task VersionLookupStillIgnoresTheRequestedScope()
    {
        var (manager, _, _) = await CreateLoadersAsync([PackageScope.User, PackageScope.Machine], []);

        var package = IpcPackageApi.ResolvePackage(Request(manager, null), IpcPackageLookupMode.Installed);

        Assert.NotNull(package);
    }
}
