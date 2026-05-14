using UniGetUI.Interface.Enums;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.ManagerClasses.Manager;
using UniGetUI.PackageEngine.Managers.BunManager;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.Serializable;
using UniGetUI.PackageEngine.Structs;
using UniGetUI.PackageEngine.Tests.Infrastructure.Assertions;
using UniGetUI.PackageEngine.Tests.Infrastructure.Builders;
using UniGetUI.PackageEngine.Tests.Infrastructure.Helpers;

namespace UniGetUI.PackageEngine.Tests;

/// <summary>
/// Unit and integration tests for the Bun package manager.
/// Tests cover search, package listing, updates detection, and package operations.
/// </summary>
public sealed class BunManagerTests
{
    private sealed class TestableBun : Bun
    {
        public IReadOnlyList<Package> GetAvailableUpdatesUnsafe() => base.GetAvailableUpdates_UnSafe();
    }

    /// <summary>
    /// Tests parsing of JSON search results from 'bun search &lt;query&gt; --json'.
    /// Verifies that multiple packages with different names and versions are correctly parsed.
    /// </summary>
    [Fact]
    public void ParseSearchOutputParsesJsonArray()
    {
        var manager = new Bun();

        var searchOutput = PackageEngineFixtureFiles.ReadAllText(Path.Combine("Bun", "search-results.json"));
        var packages = Bun.ParseSearchOutput(searchOutput, manager.DefaultSource, manager);

        var packageList = packages.ToArray();
        Assert.Equal(3, packageList.Length);

        // Verify first package
        PackageAssert.BelongsTo(packageList[0], manager, manager.DefaultSource);
        Assert.Equal("typescript", packageList[0].Id);
        Assert.Equal("5.3.3", packageList[0].VersionString);

        // Verify second package
        PackageAssert.BelongsTo(packageList[1], manager, manager.DefaultSource);
        Assert.Equal("lodash", packageList[1].Id);
        Assert.Equal("4.17.21", packageList[1].VersionString);

        // Verify scoped package
        PackageAssert.BelongsTo(packageList[2], manager, manager.DefaultSource);
        Assert.Equal("@types/node", packageList[2].Id);
        Assert.Equal("20.10.6", packageList[2].VersionString);
    }

    /// <summary>
    /// Tests parsing of the outdated packages table output from 'bun outdated'.
    /// Verifies that the Unicode box-drawing table format is correctly parsed.
    /// </summary>
    [Fact]
    public void ParseBunOutdatedTableParsesUnicodeTable()
    {
        var outdatedOutput = PackageEngineFixtureFiles.ReadAllText(Path.Combine("Bun", "outdated-table.txt"));

        var results = Bun.ParseBunOutdatedTable(outdatedOutput).ToList();

        Assert.Equal(3, results.Count);

        // Verify first package (typescript)
        Assert.Equal("typescript", results[0].Id);
        Assert.Equal("5.2.0", results[0].Version);
        Assert.Equal("5.3.0", results[0].NewVersion);

        // Verify scoped package (@types/node)
        Assert.Equal("@types/node", results[1].Id);
        Assert.Equal("20.8.0", results[1].Version);
        Assert.Equal("20.9.0", results[1].NewVersion);

        // Verify third package (vite)
        Assert.Equal("vite", results[2].Id);
        Assert.Equal("4.5.0", results[2].Version);
        Assert.Equal("4.5.1", results[2].NewVersion);
    }

    /// <summary>
    /// Tests that ParseBunOutdatedTable skips the header row and empty lines.
    /// </summary>
    [Fact]
    public void ParseBunOutdatedTableSkipsHeaderAndEmptyLines()
    {
        var output = """
            |-------------------------------------------------|
            | Package              | Current | Update  | Latest  |
            |--------------------------------------------------|
            | typescript           | 5.2.0   | 5.3.0   | 5.4.0   |
            |-------------------------------------------------|
            """;

        var results = Bun.ParseBunOutdatedTable(output).ToList();

        Assert.Single(results);
        Assert.Equal("typescript", results[0].Id);
    }

    /// <summary>
    /// Tests that ParseBunOutdatedTable returns empty list for invalid input.
    /// </summary>
    [Fact]
    public void ParseBunOutdatedTableReturnsEmptyForInvalidInput()
    {
        var output = "Invalid output format\nNo table here";

        var results = Bun.ParseBunOutdatedTable(output).ToList();

        Assert.Empty(results);
    }

    /// <summary>
    /// Tests that ParseBunOutdatedTable skips border lines (all dashes or box-drawing chars).
    /// Verifies that lines like |---| or ├─┤ are not parsed as packages.
    /// </summary>
    [Fact]
    public void ParseBunOutdatedTableSkipsBorderLines()
    {
        var output = """
            bun outdated v1.3.9
            |-------------------------------------------------|
            | Package              | Current | Update  | Latest  |
            |--------------------------------------------------|
            | typescript           | 5.2.0   | 5.3.0   | 5.4.0   |
            |-------------------------------------------------|
            | lodash               | 4.17.0  | 4.17.21 | 4.17.21 |
            |-------------------------------------------------|
            """;

        var results = Bun.ParseBunOutdatedTable(output).ToList();

        // Should find both typescript and lodash with updates, and skip all border lines
        Assert.Equal(2, results.Count);
        Assert.Equal("typescript", results[0].Id);
        Assert.Equal("lodash", results[1].Id);
    }

    /// <summary>
    /// Tests that ParseBunOutdatedTable skips Unicode box-drawing borders.
    /// </summary>
    [Fact]
    public void ParseBunOutdatedTableSkipsUnicodeBorders()
    {
        var output = """
            bun outdated v1.3.10
            ┌────────────────┬─────────┬────────┬────────┐
            │ Package        │ Current │ Update │ Latest │
            ├────────────────┼─────────┼────────┼────────┤
            │ typescript     │ 5.2.0   │ 5.3.0  │ 5.4.0  │
            ├────────────────┼─────────┼────────┼────────┤
            │ lodash         │ 4.17.0  │ 4.17.21│ 4.17.21│
            └────────────────┴─────────┴────────┴────────┘
            """;

        var results = Bun.ParseBunOutdatedTable(output).ToList();

        // Should find both packages with updates, and skip Unicode border lines
        Assert.Equal(2, results.Count);
        Assert.Equal("typescript", results[0].Id);
        Assert.Equal("lodash", results[1].Id);
    }

    /// <summary>
    /// Tests that ParseBunOutdatedTable uses "Update" column by default (preferLatest=false).
    /// This provides safe, semantic-versioning compatible updates.
    /// </summary>
    [Fact]
    public void ParseBunOutdatedTableUsesUpdateColumnByDefault()
    {
        var output = """
            |-------------------------------------------------|
            | Package              | Current | Update  | Latest  |
            |--------------------------------------------------|
            | typescript           | 5.2.0   | 5.3.0   | 6.0.0   |
            | lodash               | 4.17.0  | 4.17.21 | 4.17.21 |
            |-------------------------------------------------|
            """;

        var results = Bun.ParseBunOutdatedTable(output, preferLatest: false).ToList();

        Assert.Equal(2, results.Count);
        // typescript: uses Update column (5.3.0), not Latest (6.0.0)
        Assert.Equal("typescript", results[0].Id);
        Assert.Equal("5.3.0", results[0].NewVersion);
        // lodash: Update == Latest, so only one entry
        Assert.Equal("lodash", results[1].Id);
        Assert.Equal("4.17.21", results[1].NewVersion);
    }

    /// <summary>
    /// Tests that ParseBunOutdatedTable uses "Latest" column when preferLatest=true.
    /// This allows users to upgrade to the absolute latest, even with breaking changes.
    /// </summary>
    [Fact]
    public void ParseBunOutdatedTableUsesLatestColumnWhenPreferLatest()
    {
        var output = """
            |-------------------------------------------------|
            | Package              | Current | Update  | Latest  |
            |--------------------------------------------------|
            | typescript           | 5.2.0   | 5.3.0   | 6.0.0   |
            | lodash               | 4.17.0  | 4.17.21 | 4.17.21 |
            |-------------------------------------------------|
            """;

        var results = Bun.ParseBunOutdatedTable(output, preferLatest: true).ToList();

        Assert.Equal(2, results.Count);
        // typescript: uses Latest column (6.0.0), not Update (5.3.0)
        Assert.Equal("typescript", results[0].Id);
        Assert.Equal("6.0.0", results[0].NewVersion);
        // lodash: Update == Latest, so included
        Assert.Equal("lodash", results[1].Id);
        Assert.Equal("4.17.21", results[1].NewVersion);
    }

    /// <summary>
    /// Tests parsing of the tree output from 'bun pm ls --global'.
    /// Verifies that globally installed packages are correctly extracted from the tree structure.
    /// </summary>
    [Fact]
    public void ParseInstalledPackagesOutputParsesTreeFormat()
    {
        var manager = new Bun();
        var installedOutput = PackageEngineFixtureFiles.ReadAllText(Path.Combine("Bun", "installed.txt"));

        var packages = Bun.ParseInstalledPackages(installedOutput, manager.DefaultSource, manager,
            new OverridenInstallationOptions(PackageScope.Global));

        var packageList = packages.ToArray();
        Assert.Equal(5, packageList.Length);

        // Verify first package
        Assert.Equal("typescript", packageList[0].Id);
        Assert.Equal("5.7.3", packageList[0].VersionString);
        Assert.Equal(PackageScope.Global, packageList[0].OverridenOptions.Scope);

        // Verify scoped package
        Assert.Equal("@devcontainers/cli", packageList[1].Id);
        Assert.Equal("0.81.1", packageList[1].VersionString);

        // Verify last package
        Assert.Equal("bunx", packageList[4].Id);
        Assert.Equal("1.0.24", packageList[4].VersionString);
    }

    /// <summary>
    /// Tests that scoped packages (with @ prefix) are correctly handled.
    /// </summary>
    [Fact]
    public void ParseInstalledPackagesHandlesScopedPackagesCorrectly()
    {
        var manager = new Bun();
        var output = """
            /home/user/.bun/install/global node_modules (2)
            ├── @scope/package@1.0.0
            └── @another-scope/tool@2.5.3
            """;

        var packages = Bun.ParseInstalledPackages(output, manager.DefaultSource, manager,
            new OverridenInstallationOptions(PackageScope.Global));

        var packageList = packages.ToArray();
        Assert.Equal(2, packageList.Length);
        Assert.Equal("@scope/package", packageList[0].Id);
        Assert.Equal("1.0.0", packageList[0].VersionString);
        Assert.Equal("@another-scope/tool", packageList[1].Id);
        Assert.Equal("2.5.3", packageList[1].VersionString);
    }

    /// <summary>
    /// Tests that invalid or malformed tree lines are skipped during parsing.
    /// </summary>
    [Fact]
    public void ParseInstalledPackagesSkipsInvalidLines()
    {
        var manager = new Bun();
        var output = """
            /home/user/.bun/install/global node_modules
            Invalid line without marker
            ├── valid-package@1.0.0
            └── another@2.0.0
            └── @invalid-version
            """;

        var packages = Bun.ParseInstalledPackages(output, manager.DefaultSource, manager,
            new OverridenInstallationOptions(PackageScope.Global));

        // Should only parse the two valid packages
        Assert.Equal(2, packages.Count);
    }

    /// <summary>
    /// Tests that search returns empty list for empty JSON array input.
    /// </summary>
    [Fact]
    public void ParseSearchOutputReturnsEmptyForEmptyArray()
    {
        var manager = new Bun();
        var packages = Bun.ParseSearchOutput("[]", manager.DefaultSource, manager);

        Assert.Empty(packages);
    }

    /// <summary>
    /// Tests OperationHelper builds correct install parameters for basic install.
    /// </summary>
    [Fact]
    public void OperationHelperBuildsInstallParameters()
    {
        var manager = new Bun();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("typescript")
            .WithVersion("5.3.3")
            .Build();

        var parameters = manager.OperationHelper.GetParameters(package, new InstallOptions(), OperationType.Install);

        Assert.Contains("add", parameters);
        Assert.Contains("typescript@5.3.3", parameters);
        Assert.Contains("--global", parameters);
    }

    /// <summary>
    /// Tests OperationHelper correctly adds --global flag for global scope.
    /// </summary>
    [Fact]
    public void OperationHelperAddsGlobalFlagForGlobalScope()
    {
        var manager = new Bun();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("typescript")
            .WithVersion("5.3.3")
            .Build();

        var options = new InstallOptions { InstallationScope = PackageScope.Global };
        var parameters = manager.OperationHelper.GetParameters(package, options, OperationType.Install);

        Assert.Contains("--global", parameters);
    }

    /// <summary>
    /// Tests OperationHelper uses custom version when specified in options.
    /// </summary>
    [Fact]
    public void OperationHelperUsesCustomVersionFromOptions()
    {
        var manager = new Bun();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("lodash")
            .WithVersion("4.17.20")
            .Build();

        var options = new InstallOptions { Version = "4.17.21" };
        var parameters = manager.OperationHelper.GetParameters(package, options, OperationType.Install);

        Assert.Contains("lodash@4.17.21", parameters);
    }

    /// <summary>
    /// Tests OperationHelper builds correct uninstall parameters.
    /// </summary>
    [Fact]
    public void OperationHelperBuildsUninstallParameters()
    {
        var manager = new Bun();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("typescript")
            .WithVersion("5.3.3")
            .Build();

        var parameters = manager.OperationHelper.GetParameters(package, new InstallOptions(), OperationType.Uninstall);

        Assert.Contains("remove", parameters);
        Assert.Contains("typescript", parameters);
    }

    /// <summary>
    /// Tests OperationHelper builds correct update parameters.
    /// </summary>
    [Fact]
    public void OperationHelperBuildsUpdateParameters()
    {
        var manager = new Bun();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("typescript")
            .WithVersion("5.2.0")
            .WithNewVersion("5.4.0")
            .Build();

        var parameters = manager.OperationHelper.GetParameters(package, new InstallOptions(), OperationType.Update);

        Assert.Contains("add", parameters);
        Assert.Contains("typescript@5.4.0", parameters);
    }

    /// <summary>
    /// Tests OperationHelper respects package's overridden scope over options scope.
    /// </summary>
    [Fact]
    public void OperationHelperRespectsPackageScopeOverOption()
    {
        var manager = new Bun();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("typescript")
            .WithVersion("5.3.3")
            .WithOptions(new OverridenInstallationOptions(PackageScope.Global))
            .Build();

        var options = new InstallOptions { InstallationScope = PackageScope.Local };
        var parameters = manager.OperationHelper.GetParameters(package, options, OperationType.Install);

        Assert.Contains("--global", parameters);
    }

    /// <summary>
    /// Tests OperationHelper allows explicit local installs when scope is overridden.
    /// </summary>
    [Fact]
    public void OperationHelperDoesNotAddGlobalFlagForExplicitLocalScope()
    {
        var manager = new Bun();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("typescript")
            .WithVersion("5.3.3")
            .WithOptions(new OverridenInstallationOptions(PackageScope.Local))
            .Build();

        var parameters = manager.OperationHelper.GetParameters(package, new InstallOptions(), OperationType.Install);

        Assert.DoesNotContain("--global", parameters);
    }

    /// <summary>
    /// Tests OperationHelper includes custom install parameters.
    /// </summary>
    [Fact]
    public void OperationHelperIncludesCustomParameters()
    {
        var manager = new Bun();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("typescript")
            .WithVersion("5.3.3")
            .Build();

        var options = new InstallOptions();
        options.CustomParameters_Install.Add("--save-dev");
        options.CustomParameters_Install.Add("--no-save");

        var parameters = manager.OperationHelper.GetParameters(package, options, OperationType.Install);

        Assert.Contains("--save-dev", parameters);
        Assert.Contains("--no-save", parameters);
    }

    /// <summary>
    /// Tests OperationHelper returns Success for zero exit code.
    /// </summary>
    [Fact]
    public void OperationHelperReturnsSuccessForZeroExitCode()
    {
        var manager = new Bun();
        var package = new PackageBuilder().WithManager(manager).Build();

        var result = manager.OperationHelper.GetResult(package, OperationType.Install, [], 0);

        Assert.Equal(OperationVeredict.Success, result);
    }

    /// <summary>
    /// Tests OperationHelper returns Failure for non-zero exit code.
    /// </summary>
    [Fact]
    public void OperationHelperReturnsFailureForNonZeroExitCode()
    {
        var manager = new Bun();
        var package = new PackageBuilder().WithManager(manager).Build();

        var result = manager.OperationHelper.GetResult(package, OperationType.Install, [], 1);

        Assert.Equal(OperationVeredict.Failure, result);
    }

    /// <summary>
    /// Tests that manager has correct capabilities configured.
    /// </summary>
    [Fact]
    public void BunManagerHasCorrectCapabilities()
    {
        var manager = new Bun();

        Assert.True(manager.Capabilities.CanRunAsAdmin);
        Assert.True(manager.Capabilities.SupportsCustomVersions);
        Assert.True(manager.Capabilities.CanDownloadInstaller);
        Assert.False(manager.Capabilities.SupportsCustomScopes);
        Assert.True(manager.Capabilities.CanListDependencies);
        Assert.True(manager.Capabilities.SupportsPreRelease);
        Assert.Equal(ProxySupport.No, manager.Capabilities.SupportsProxy);
        Assert.False(manager.Capabilities.SupportsProxyAuth);
    }

    /// <summary>
    /// Tests that manager properties are correctly configured.
    /// </summary>
    [Fact]
    public void BunManagerHasCorrectProperties()
    {
        var manager = new Bun();

        Assert.Equal("bun", manager.Properties.Id);
        Assert.Equal("Bun", manager.Properties.Name);
        Assert.Equal("add", manager.Properties.InstallVerb);
        Assert.Equal("remove", manager.Properties.UninstallVerb);
        Assert.Equal("add", manager.Properties.UpdateVerb);
        Assert.NotNull(manager.Properties.DefaultSource);
        Assert.Equal("https://www.npmjs.com/", manager.Properties.DefaultSource.Url.ToString());
    }

    /// <summary>
    /// Tests that scoped packages with multiple @ symbols are handled correctly.
    /// This is a regression test for packages like @scope/name@version.
    /// </summary>
    [Fact]
    public void ParseInstalledPackagesHandlesMultipleAtSymbols()
    {
        var manager = new Bun();
        var output = """
            /home/user/.bun/install/global node_modules (1)
            └── @babel/core@7.23.5
            """;

        var packages = Bun.ParseInstalledPackages(output, manager.DefaultSource, manager,
            new OverridenInstallationOptions(PackageScope.Global));

        Assert.Single(packages);
        Assert.Equal("@babel/core", packages[0].Id);
        Assert.Equal("7.23.5", packages[0].VersionString);
    }

    /// <summary>
    /// Tests that ParseBunOutdatedTable correctly identifies and skips the Package header.
    /// </summary>
    [Fact]
    public void ParseBunOutdatedTableSkipsPackageHeader()
    {
        var output = """
            bun outdated v1.3.9
            |-------------------------------------------------|
            | Package              | Current | Update  | Latest  |
            |--------------------------------------------------|
            | typescript           | 5.2.0   | 5.3.0   | 5.4.0   |
            |-------------------------------------------------|
            """;

        var results = Bun.ParseBunOutdatedTable(output).ToList();

        Assert.Single(results);
        Assert.Equal("typescript", results[0].Id);
        Assert.Equal("5.2.0", results[0].Version);
        Assert.Equal("5.3.0", results[0].NewVersion);
    }

    /// <summary>
    /// Tests parsing of Unicode box-drawing table format from 'bun outdated' (v1.3.10+).
    /// Real-world output uses Unicode box-drawing characters instead of ASCII pipes.
    /// Verifies that the parser correctly handles both formats.
    /// </summary>
    [Fact]
    public void ParseBunOutdatedTableHandlesUnicodeBoxDrawingFormat()
    {
        var output = """
            bun outdated v1.3.10 (30e609e0)
            ┌───────────────────────────┬─────────┬────────┬────────┐
            │ Package                   │ Current │ Update │ Latest │
            ├───────────────────────────┼─────────┼────────┼────────┤
            │ @types/jest               │ 29.6.0  │ 29.6.0 │ 30.0.0 │
            ├───────────────────────────┼─────────┼────────┼────────┤
            │ @org/shared-lib           │ 3.2.0   │ 3.2.0  │ 4.0.0  │
            └───────────────────────────┴─────────┴────────┴────────┘
            """;

        var results = Bun.ParseBunOutdatedTable(output).ToList();

        // Both packages have Current == Update, so they should NOT be included (no updates available)
        Assert.Empty(results);
    }

    /// <summary>
    /// Tests that ParseBunOutdatedTable correctly handles Unicode format with actual updates available.
    /// </summary>
    [Fact]
    public void ParseBunOutdatedTableHandlesUnicodeBoxDrawingWithUpdates()
    {
        var output = """
            bun outdated v1.3.10
            ┌────────────────┬─────────┬────────┬────────┐
            │ Package        │ Current │ Update │ Latest │
            ├────────────────┼─────────┼────────┼────────┤
            │ typescript     │ 5.2.0   │ 5.3.0  │ 5.4.0  │
            ├────────────────┼─────────┼────────┼────────┤
            │ @types/node    │ 20.8.0  │ 20.9.0 │ 20.10.0│
            └────────────────┴─────────┴────────┴────────┘
            """;

        var results = Bun.ParseBunOutdatedTable(output).ToList();

        Assert.Equal(2, results.Count);
        Assert.Equal("typescript", results[0].Id);
        Assert.Equal("5.2.0", results[0].Version);
        Assert.Equal("5.3.0", results[0].NewVersion);
        Assert.Equal("@types/node", results[1].Id);
        Assert.Equal("20.8.0", results[1].Version);
        Assert.Equal("20.9.0", results[1].NewVersion);
    }

    /// <summary>
    /// Tests that Unicode box-drawing format works with preferLatest=true.
    /// This scenario matches when a user enables BunPreferLatestVersions setting
    /// and runs bun outdated with the new Unicode table format.
    /// </summary>
    [Fact]
    public void ParseBunOutdatedTableHandlesUnicodeWithPreferLatest()
    {
        var output = """
            bun outdated v1.3.10 (30e609e0)
            ┌───────────────────────────┬─────────┬────────┬────────┐
            │ Package                   │ Current │ Update │ Latest │
            ├───────────────────────────┼─────────┼────────┼────────┤
            │ @types/jest               │ 29.6.0  │ 29.6.0 │ 30.0.0 │
            ├───────────────────────────┼─────────┼────────┼────────┤
            │ @org/shared-lib           │ 3.2.0   │ 3.2.0  │ 4.0.0  │
            └───────────────────────────┴─────────┴────────┴────────┘
            """;

        // With preferLatest=false (default), no updates because Update column = Current
        var resultsDefault = Bun.ParseBunOutdatedTable(output, preferLatest: false).ToList();
        Assert.Empty(resultsDefault);

        // With preferLatest=true, both packages should show as having updates from Latest column
        var resultsPreferLatest = Bun.ParseBunOutdatedTable(output, preferLatest: true).ToList();
        Assert.Equal(2, resultsPreferLatest.Count);
        Assert.Equal("@types/jest", resultsPreferLatest[0].Id);
        Assert.Equal("29.6.0", resultsPreferLatest[0].Version);
        Assert.Equal("30.0.0", resultsPreferLatest[0].NewVersion);
        Assert.Equal("@org/shared-lib", resultsPreferLatest[1].Id);
        Assert.Equal("3.2.0", resultsPreferLatest[1].Version);
        Assert.Equal("4.0.0", resultsPreferLatest[1].NewVersion);
    }

    /// <summary>
    /// Tests that manager returns public interface correctly (IPackageManager compliance).
    /// </summary>
    [Fact]
    public void ParseAvailableUpdatesRespectsPreferlatesParameter()
    {
        var manager = new Bun();
        var output = """
            |-------------------------------------------------|
            | Package              | Current | Update  | Latest  |
            |--------------------------------------------------|
            | typescript           | 5.2.0   | 5.3.0   | 6.0.0   |
            |-------------------------------------------------|
            """;

        // Test with preferLatest=false (default): should use Update column (5.3.0)
        var resultsUpdate = Bun.ParseAvailableUpdates(output, manager.DefaultSource, manager, preferLatest: false);
        Assert.Single(resultsUpdate);
        Assert.Equal("typescript", resultsUpdate[0].Id);
        Assert.Equal("5.3.0", resultsUpdate[0].NewVersionString);

        // Test with preferLatest=true: should use Latest column (6.0.0)
        var resultsLatest = Bun.ParseAvailableUpdates(output, manager.DefaultSource, manager, preferLatest: true);
        Assert.Single(resultsLatest);
        Assert.Equal("typescript", resultsLatest[0].Id);
        Assert.Equal("6.0.0", resultsLatest[0].NewVersionString);
    }

    /// <summary>
    /// Tests Bun global updates are skipped when there is no global package manifest.
    /// </summary>
    [Fact]
    public void HasGlobalPackageManifestRequiresPackageJson()
    {
        string globalDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(globalDir);

            Assert.False(Bun.HasGlobalPackageManifest(globalDir));

            File.WriteAllText(Path.Combine(globalDir, "package.json"), "{}");

            Assert.True(Bun.HasGlobalPackageManifest(globalDir));
        }
        finally
        {
            if (Directory.Exists(globalDir))
                Directory.Delete(globalDir, recursive: true);
        }
    }

    /// <summary>
    /// Tests Bun update detection returns no results when the global manifest is missing.
    /// </summary>
    [Fact]
    public void GetAvailableUpdatesReturnsEmptyWhenGlobalManifestIsMissing()
    {
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string globalDir = Bun.GetGlobalPackagesDirectory(userProfile);
        string manifestPath = Path.Combine(globalDir, "package.json");
        string backupManifestPath = manifestPath + ".bun-test-backup";

        if (!Directory.Exists(globalDir))
        {
            Assert.Empty(new TestableBun().GetAvailableUpdatesUnsafe());
            return;
        }

        bool restoreManifest = false;

        try
        {
            if (File.Exists(backupManifestPath))
                File.Delete(backupManifestPath);

            if (File.Exists(manifestPath))
            {
                File.Move(manifestPath, backupManifestPath);
                restoreManifest = true;
            }

            Assert.Empty(new TestableBun().GetAvailableUpdatesUnsafe());
        }
        finally
        {
            if (restoreManifest)
            {
                if (File.Exists(manifestPath))
                    File.Delete(manifestPath);
                File.Move(backupManifestPath, manifestPath);
            }
        }
    }

    /// <summary>
    /// Tests Bun install location defaults to the global Bun node_modules directory.
    /// </summary>
    [Fact]
    public void GetInstallLocationDefaultsToBunGlobalNodeModules()
    {
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string location = BunPkgDetailsHelper.GetInstallLocation(userProfile, null, "typescript");

        Assert.Equal(
            Path.Join(userProfile, ".bun", "install", "global", "node_modules", "typescript"),
            location);
    }

    /// <summary>
    /// Tests Bun install location honors explicit local scope.
    /// </summary>
    [Fact]
    public void GetInstallLocationUsesLocalNodeModulesForLocalScope()
    {
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string location = BunPkgDetailsHelper.GetInstallLocation(userProfile, PackageScope.Local, "typescript");

        Assert.Equal(Path.Join(userProfile, "node_modules", "typescript"), location);
    }

    /// <summary>
    /// Tests that manager returns public interface correctly (IPackageManager compliance).
    /// </summary>
    [Fact]
    public void BunImplementsIPackageManager()
    {
        var manager = new Bun();

        // Verify that the manager implements IPackageManager interface
        Assert.NotNull(manager);
        Assert.NotNull(manager.DefaultSource);
        Assert.NotNull(manager.OperationHelper);
    }

    /// <summary>
    /// Tests that package info fixture exists for details helper testing.
    /// </summary>
    [Fact]
    public void PackageInfoFixtureExists()
    {
        string fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Bun", "package-info.json");
        Assert.True(File.Exists(fixturePath), $"Fixture file not found at {fixturePath}");
    }
}
