using UniGetUI.Core.Data;
using UniGetUI.Interface;
using UniGetUI.PackageEngine.Classes.Manager.BaseProviders;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Interfaces.ManagerProviders;
using UniGetUI.PackageEngine.Managers.NpmManager;
using UniGetUI.PackageEngine.Managers.PipManager;
using UniGetUI.PackageEngine.Managers.PowerShell7Manager;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.PackageLoader;
using UniGetUI.PackageEngine.Serializable;
using UniGetUI.PackageEngine.Structs;

namespace UniGetUI.Tests;

// Mutating IPC operations must act on the installation of the requested scope and refuse to guess
// between scope copies. Failures happen before any operation is created, so nothing is launched.
public sealed class IpcOperationScopeTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "UniGetUI.IpcOperationScopeTests",
        Guid.NewGuid().ToString("N")
    );

    public IpcOperationScopeTests()
    {
        Directory.CreateDirectory(Path.Combine(_testRoot, "Data"));
        CoreData.TEST_DataDirectoryOverride = Path.Combine(_testRoot, "Data");
    }

    public void Dispose()
    {
        CoreData.TEST_DataDirectoryOverride = null;
        try
        {
            Directory.Delete(_testRoot, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static async Task<(IPackageManager Manager, InstalledPackagesLoader Installed, UpgradablePackagesLoader Upgradable)> CreateLoadersAsync(
        string?[] installedScopes,
        string?[] upgradableScopes,
        IPackageManager? manager = null
    )
    {
        manager ??= new Pip();
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

    private static IpcPackageActionRequest Request(IPackageManager manager, string? scope) => new()
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
        var (manager, _, _) = await CreateLoadersAsync([PackageScope.User, null], []);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Operations[operation](Request(manager, PackageScope.Global))
        );

        Assert.Contains("No installation of \"requests\" in the \"machine\" scope", ex.Message);
    }

    [Fact]
    public async Task PipMachinePlaceholderDoesNotBlockAUserScopeRequest()
    {
        var manager = new RecordingPip();
        await CreateLoadersAsync([PackageScope.Machine], [PackageScope.Machine], manager);
        var request = Request(manager, "User");
        request.WaitForCompletion = true;

        var result = await IpcPackageApi.UpdatePackageAsync(request);

        Assert.Equal("success", result.Status);
        Assert.Equal(PackageScope.Machine, result.Package?.ReportedScope);
        Assert.Equal(PackageScope.User, Assert.Single(manager.Emitted).Scope);
    }

    [Theory]
    [InlineData("Global", PackageScope.Machine)]
    [InlineData("Local", PackageScope.User)]
    public async Task NpmScopeAliasesSelectTheMatchingRowAndEmitItsParameters(string requested, string reported)
    {
        foreach (var operation in new[] { OperationType.Update, OperationType.Uninstall })
        {
            var manager = new RecordingNpm();
            await CreateLoadersAsync([PackageScope.User, PackageScope.Machine], [reported], manager);
            var request = Request(manager, requested);
            request.WaitForCompletion = true;

            var result = operation is OperationType.Update
                ? await IpcPackageApi.UpdatePackageAsync(request)
                : await IpcPackageApi.UninstallPackageAsync(request);

            Assert.Equal("success", result.Status);
            Assert.Equal(reported, result.Package?.ReportedScope);
            var emitted = Assert.Single(manager.Emitted);
            Assert.Equal(reported, emitted.Scope);
            Assert.Equal(
                reported == PackageScope.Machine,
                emitted.Parameters.Contains("--global")
            );
        }
    }

    [Theory]
    [InlineData("Global", PackageScope.User)]
    [InlineData("Local", PackageScope.Machine)]
    public async Task NpmRejectsASingleRowThatConflictsWithTheRequestedAlias(string requested, string reported)
    {
        var manager = new Npm();
        await CreateLoadersAsync([reported], [reported], manager);

        foreach (var operation in Operations)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => operation(Request(manager, requested)));
        }
    }

    [Theory]
    [InlineData("Global", true)]
    [InlineData("Local", false)]
    public async Task UpdateRunsNpmWithTheCanonicalScopeOfALiteralAliasRequest(string requested, bool expectGlobal)
    {
        var manager = new RecordingNpm();
        await CreateLoadersAsync([null], [null], manager);

        var result = await IpcPackageApi.UpdatePackageAsync(new IpcPackageActionRequest
        {
            PackageId = "requests",
            ManagerName = manager.Id,
            Scope = requested,
            WaitForCompletion = true,
        });

        Assert.Equal("success", result.Status);
        var emitted = Assert.Single(manager.Emitted);
        Assert.Equal(expectGlobal, emitted.Parameters.Contains("--global"));
        Assert.Equal(expectGlobal ? PackageScope.Machine : PackageScope.User, emitted.Scope);
    }

    // Records the parameters npm's own helper emits and completes without starting a process.
    private sealed class RecordingNpm : Npm
    {
        public List<(string Scope, IReadOnlyList<string> Parameters)> Emitted { get; } = [];

        public RecordingNpm()
        {
            OperationHelper = new RecordingHelper(this, OperationHelper, Emitted);
        }

        // Never initialized, so IsReady() stays false and the post-operation loader refresh
        // lists nothing instead of running npm; these overrides guard against any other path.
        public override void Initialize() { }

        public override void AttemptFastRepair() { }

        protected override IReadOnlyList<Package> GetInstalledPackages_UnSafe() => [];

        protected override IReadOnlyList<Package> GetAvailableUpdates_UnSafe() => [];
    }

    private sealed class RecordingPip : Pip
    {
        public List<(string Scope, IReadOnlyList<string> Parameters)> Emitted { get; } = [];

        public RecordingPip()
        {
            OperationHelper = new RecordingHelper(this, OperationHelper, Emitted);
        }

        public override void Initialize() { }

        public override void AttemptFastRepair() { }

        protected override IReadOnlyList<Package> GetInstalledPackages_UnSafe() => [];

        protected override IReadOnlyList<Package> GetAvailableUpdates_UnSafe() => [];
    }

    private sealed class RecordingHelper(
        IPackageManager manager,
        IPackageOperationHelper original,
        List<(string Scope, IReadOnlyList<string> Parameters)> emitted
    ) : BasePkgOperationHelper(manager), IManagedPackageOperationHelper
    {
        public Task<OperationVeredict?> TryPerformAsync(
            IPackage package,
            InstallOptions options,
            OperationType operation,
            Action<string> log,
            Action<OperationProgress> progress,
            CancellationToken cancellationToken
        )
        {
            emitted.Add((options.InstallationScope, original.GetParameters(package, options, operation)));
            return Task.FromResult<OperationVeredict?>(OperationVeredict.Success);
        }

        protected override IReadOnlyList<string> _getOperationParameters(
            IPackage package,
            InstallOptions options,
            OperationType operation
        ) => [];

        protected override OperationVeredict _getOperationResult(
            IPackage package,
            OperationType operation,
            IReadOnlyList<string> processOutput,
            int returnCode
        ) => OperationVeredict.Failure;
    }

    [Fact]
    public async Task AnUnrecognizedScopeReachesTheOperationAsRequested()
    {
        var manager = new RecordingNpm();
        await CreateLoadersAsync([null], [null], manager);
        var request = Request(manager, " Custom-Scope ");
        request.WaitForCompletion = true;

        var result = await IpcPackageApi.UpdatePackageAsync(request);

        Assert.Equal("success", result.Status);
        Assert.Equal(" Custom-Scope ", Assert.Single(manager.Emitted).Scope);
    }

    [Fact]
    public async Task PipRejectsAnExplicitlyUserScopedRowForAMachineRequest()
    {
        var manager = new Pip();
        await CreateLoadersAsync([PackageScope.User], [PackageScope.User], manager);

        foreach (var operation in Operations)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => operation(Request(manager, "Global")));
        }
    }

    [Fact]
    public async Task AGlobalRequestForAnUnscopedNpmRowStillRunsGlobally()
    {
        var manager = new RecordingNpm();
        await CreateLoadersAsync([null], [], manager);
        var request = Request(manager, "Global");
        request.WaitForCompletion = true;

        var result = await IpcPackageApi.UninstallPackageAsync(request);

        Assert.Equal("success", result.Status);
        Assert.Contains("--global", Assert.Single(manager.Emitted).Parameters);
    }

    [Fact]
    public async Task ASingleReliablyScopedInstallationRejectsAConflictingScopeInEveryOperation()
    {
        var manager = new PowerShell7();
        await CreateLoadersAsync([PackageScope.User], [PackageScope.User], manager);

        foreach (var operation in Operations)
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                operation(Request(manager, PackageScope.Global))
            );
            Assert.Contains("No installation of \"requests\" in the \"machine\" scope", ex.Message);
        }

        Assert.Equal(
            PackageScope.User,
            IpcPackageApi.ResolveUpdatePackageForMutation(Request(manager, PackageScope.Local)).NormalizedReportedScope
        );
    }

    [Fact]
    public async Task ASingleInstallationWithoutAnUpdateEntryIsStillSelectedForAnotherScope()
    {
        var (manager, _, _) = await CreateLoadersAsync([PackageScope.Machine], []);

        var package = IpcPackageApi.ResolveUpdatePackageForMutation(Request(manager, PackageScope.User));

        Assert.False(package.IsUpgradable);
        Assert.Equal(PackageScope.Machine, package.NormalizedReportedScope);
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
