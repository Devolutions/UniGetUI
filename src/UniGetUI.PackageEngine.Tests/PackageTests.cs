using UniGetUI.Core.Data;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.SettingsEngine.SecureSettings;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Managers.BunManager;
using UniGetUI.PackageEngine.Managers.CargoManager;
using UniGetUI.PackageEngine.Managers.DotNetManager;
using UniGetUI.PackageEngine.Managers.FlatpakManager;
using UniGetUI.PackageEngine.Managers.HomebrewManager;
using UniGetUI.PackageEngine.Managers.NpmManager;
using UniGetUI.PackageEngine.Managers.PipManager;
using UniGetUI.PackageEngine.Managers.SnapManager;

using UniGetUI.PackageEngine.PackageLoader;
using UniGetUI.PackageEngine.Tests.Infrastructure.Builders;

namespace UniGetUI.PackageEngine.Tests;

public sealed class NewerVersionIsInstalledTests : IDisposable
{
    private readonly string _testRoot;

    public NewerVersionIsInstalledTests()
    {
        _testRoot = Path.Combine(
            Path.GetTempPath(),
            nameof(NewerVersionIsInstalledTests),
            Guid.NewGuid().ToString("N")
        );
        CoreData.TEST_DataDirectoryOverride = Path.Combine(_testRoot, "Data");
        SecureSettings.TEST_SecureSettingsRootOverride = Path.Combine(_testRoot, "SecureSettings");
        Directory.CreateDirectory(CoreData.UniGetUIUserConfigurationDirectory);
        Settings.ResetSettings();
    }

    public void Dispose()
    {
        Settings.ResetSettings();
        CoreData.TEST_DataDirectoryOverride = null;
        SecureSettings.TEST_SecureSettingsRootOverride = null;
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, recursive: true);
    }

    [Theory]
    [InlineData("1.0.0", "2.0.0", false)]
    [InlineData("2.0.0", "2.0.0", true)]
    [InlineData("3.0.0", "2.0.0", true)]
    [InlineData("10c8e557", "8b640eef", false)]
    [InlineData("10c8e557", "2.0.0", false)]
    [InlineData("2.0.0", "8b640eef", false)]
    [InlineData("", "2.0.0", false)]
    [InlineData("1.0.0;3.0.0", "2.0.0", true)]
    // Issue #5293: a Homebrew build revision ("18.4_1") used to parse as 18.41 and shadow the
    // newer upstream release, silently hiding the update.
    [InlineData("18.4_1", "18.6", false)]
    [InlineData("18.6_1", "18.6", true)]
    // Same bug on a four-part upstream version, where the revision used to be folded into the
    // fourth component (1.2.3.4_1 -> 1.2.3.41) because the parser only kept four segments.
    [InlineData("1.2.3.4_1", "1.2.3.5", false)]
    [InlineData("1.2.3.4_1", "1.2.3.4_2", false)]
    [InlineData("1.2.3.4_2", "1.2.3.4_1", true)]
    // An underscore before a pre-release tag stays unparseable, so upgrading off a pre-release
    // onto the final release must remain visible rather than being read as 2.0.0.1 >= 2.0.0.
    [InlineData("2.0.0_rc1", "2.0.0", false)]
    [InlineData("2.0.0_beta2", "2.0.0", false)]
    public async Task NewerVersionIsInstalled_ReturnsExpectedResult(string installedVersions, string newVersion, bool expected)
    {
        var manager = new PackageManagerBuilder().Build();
        InitializeLoaders();

        foreach (var v in installedVersions.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            await InstalledPackagesLoader.Instance.AddForeign(
                new PackageBuilder().WithManager(manager).WithId("Contoso.Tool").WithVersion(v.Trim()).Build()
            );
        }

        var update = new PackageBuilder()
            .WithManager(manager).WithId("Contoso.Tool").WithVersion("1.0.0").WithNewVersion(newVersion).Build();

        Assert.Equal(expected, update.NewerVersionIsInstalled());
    }

    // Issue #5331: on a NuGet feed a dash introduces a SemVer 2.0 pre-release, so the stable
    // release supersedes it. The shared numeric parser read "2.0.0-rc1" as 2.0.0.1 and so
    // considered it newer than 2.0.0, permanently hiding the upgrade off a pre-release. The
    // underscore spelling of the same thing was already guarded above.
    [Theory]
    [InlineData("2.0.0-rc1", "2.0.0", false)]
    [InlineData("2.0.0-beta.1", "2.0.0", false)]
    [InlineData("2.0.0-preview.7.26381.103", "2.0.0", false)]
    [InlineData("2.0.0-rc1", "2.0.0-rc2", false)]
    [InlineData("1.9.0", "1.10.0", false)]
    [InlineData("2.0.0", "2.0.0-rc1", true)]
    [InlineData("2.0.0-rc2", "2.0.0-rc1", true)]
    [InlineData("2.0.0", "2.0.0", true)]
    [InlineData("1.0.0.4", "1.0.0.3", true)]
    public async Task NewerVersionIsInstalled_UsesNuGetSemanticsOnNuGetManagers(
        string installedVersion,
        string newVersion,
        bool expected
    )
    {
        var manager = new DotNet();
        InitializeLoaders();

        await InstalledPackagesLoader.Instance.AddForeign(
            new PackageBuilder()
                .WithManager(manager)
                .WithId("Contoso.Tool")
                .WithVersion(installedVersion)
                .Build()
        );

        var update = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Tool")
            .WithVersion(installedVersion)
            .WithNewVersion(newVersion)
            .Build();

        Assert.Equal(expected, update.NewerVersionIsInstalled());
    }

    // Regression guard for the fix above: outside NuGet a trailing dash or underscore is a
    // build revision that is NEWER than its base version (Debian, Scoop, Homebrew), so the
    // shared numeric comparison must keep applying there.
    [Theory]
    [InlineData("1.2.3-4", "1.2.3", true)]
    [InlineData("1.2.3", "1.2.3-4", false)]
    [InlineData("1.2.3-1", "1.2.3-2", false)]
    [InlineData("1.2.3-2", "1.2.3-1", true)]
    [InlineData("18.4_1", "18.4", true)]
    public async Task NewerVersionIsInstalled_KeepsRevisionSemanticsOffNuGetManagers(
        string installedVersion,
        string newVersion,
        bool expected
    )
    {
        var manager = new PackageManagerBuilder().Build();
        InitializeLoaders();

        await InstalledPackagesLoader.Instance.AddForeign(
            new PackageBuilder()
                .WithManager(manager)
                .WithId("Contoso.Tool")
                .WithVersion(installedVersion)
                .Build()
        );

        var update = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Tool")
            .WithVersion(installedVersion)
            .WithNewVersion(newVersion)
            .Build();

        Assert.Equal(expected, update.NewerVersionIsInstalled());
    }

    [Theory]
    [InlineData("2.0.0-rc1", "2.0.0", -1)]
    [InlineData("2.0.0", "2.0.0-rc1", 1)]
    [InlineData("1.10.0", "1.9.0", 1)]
    [InlineData("1.0.0", "1.0.0.0", 0)]
    public void CompareVersions_UsesSemVerOnNuGetManagers(string a, string b, int expected)
    {
        var comparison = new DotNet().CompareVersions(a, b);

        Assert.NotNull(comparison);
        Assert.Equal(expected, Math.Sign(comparison.Value));
    }

    [Theory]
    [InlineData("1.2.3-4", "1.2.3", 1)]
    [InlineData("18.4_1", "18.4", 1)]
    [InlineData("1.10.0", "1.9.0", 1)]
    public void CompareVersions_KeepsNumericSemanticsElsewhere(string a, string b, int expected)
    {
        var comparison = new PackageManagerBuilder().Build().CompareVersions(a, b);

        Assert.NotNull(comparison);
        Assert.Equal(expected, Math.Sign(comparison.Value));
    }

    [Theory]
    [InlineData("10c8e557", "2.0.0")]
    [InlineData("2.0.0", "8b640eef")]
    [InlineData("10c8e557", "8b640eef")]
    public void CompareVersions_ReturnsNullWhenAVersionCannotBeParsed(string a, string b)
    {
        Assert.Null(new PackageManagerBuilder().Build().CompareVersions(a, b));
        Assert.Null(new DotNet().CompareVersions(a, b));
    }

    // npm, Bun and crates.io are strict SemVer registries: a dash always introduces a
    // pre-release, which is OLDER than the release it precedes. All three pass the registry's
    // raw version strings straight through (npm outdated's current/latest, bun outdated's
    // table, cargo install-update --list), so the shared numeric parser reading
    // "1.0.0-beta.1" as 1.0.0.1 hid every upgrade off a pre-release onto its stable release.
    [Theory]
    [InlineData("npm", "1.0.0-beta.1", "1.0.0", false)]
    [InlineData("npm", "5.0.0-rc.1", "5.0.0", false)]
    [InlineData("npm", "1.0.0", "1.0.0-beta.1", true)]
    [InlineData("bun", "1.0.0-beta.1", "1.0.0", false)]
    [InlineData("bun", "2.0.0-alpha.3", "2.0.0-alpha.4", false)]
    [InlineData("cargo", "1.0.0-alpha.1", "1.0.0", false)]
    [InlineData("cargo", "0.9.0", "0.10.0", false)]
    [InlineData("cargo", "1.0.0", "1.0.0-alpha.1", true)]
    public async Task NewerVersionIsInstalled_UsesSemVerOnSemVerRegistries(
        string managerName,
        string installedVersion,
        string newVersion,
        bool expected
    )
    {
        IPackageManager manager = managerName switch
        {
            "npm" => new Npm(),
            "bun" => new Bun(),
            "cargo" => new Cargo(),
            _ => throw new ArgumentOutOfRangeException(nameof(managerName)),
        };
        InitializeLoaders();

        await InstalledPackagesLoader.Instance.AddForeign(
            new PackageBuilder()
                .WithManager(manager)
                .WithId("contoso-tool")
                .WithVersion(installedVersion)
                .Build()
        );

        var update = new PackageBuilder()
            .WithManager(manager)
            .WithId("contoso-tool")
            .WithVersion(installedVersion)
            .WithNewVersion(newVersion)
            .Build();

        Assert.Equal(expected, update.NewerVersionIsInstalled());
    }

    // The counterpart guard: managers whose trailing dash, underscore or hash is a build or
    // port revision - and therefore NEWER than the base version - must keep the shared
    // numeric comparison. Getting this wrong would hide real updates on these ecosystems.
    [Theory]
    [InlineData("homebrew", "18.4_1", "18.4", 1)]
    [InlineData("homebrew", "18.4", "18.4_1", -1)]
    [InlineData("homebrew", "1.2.3-4", "1.2.3", 1)]
    [InlineData("flatpak", "1.2.3-4", "1.2.3", 1)]
    [InlineData("snap", "1.2.3-4", "1.2.3", 1)]
    public void CompareVersions_KeepsRevisionSemanticsOnRevisionEcosystems(
        string managerName,
        string a,
        string b,
        int expected
    )
    {
        IPackageManager manager = managerName switch
        {
            "homebrew" => new Homebrew(),
            "flatpak" => new Flatpak(),
            "snap" => new Snap(),
            _ => throw new ArgumentOutOfRangeException(nameof(managerName)),
        };

        var comparison = manager.CompareVersions(a, b);

        Assert.NotNull(comparison);
        Assert.Equal(expected, Math.Sign(comparison.Value));
    }

    [Theory]
    [InlineData("1.0.0-beta.1", "1.0.0", -1)]
    [InlineData("1.0.0", "1.0.0-beta.1", 1)]
    [InlineData("0.9.0", "0.10.0", -1)]
    [InlineData("1.0.0+build.5", "1.0.0", 0)]
    public void CompareVersions_UsesSemVerOnNpmBunAndCargo(string a, string b, int expected)
    {
        foreach (IPackageManager manager in new IPackageManager[] { new Npm(), new Bun(), new Cargo() })
        {
            var comparison = manager.CompareVersions(a, b);
            Assert.NotNull(comparison);
            Assert.Equal(expected, Math.Sign(comparison.Value));
        }
    }

    // PyPI follows PEP 440, where a pre-release carries no dash ("1.0.0rc1") and is OLDER than
    // its release, while a bare trailing dash-number ("1.0.0-1") is an implicit post-release and
    // so is NEWER. The shared numeric parser dropped the letters and glued the trailing digit
    // onto the previous segment, reading "1.0.0rc1" as 1.0.1 - which both outranked 1.0.0 and
    // hid the upgrade onto it. Pip reads the columns of `pip list --outdated` verbatim, so the
    // registry's own spelling reaches this comparison unchanged.
    [Theory]
    [InlineData("1.0.0rc1", "1.0.0", false)]
    [InlineData("1.0.0b1", "1.0.0", false)]
    [InlineData("1.0.0a1", "1.0.0", false)]
    [InlineData("1.0.0.dev1", "1.0.0", false)]
    [InlineData("1.0.0rc1", "1.0.0rc2", false)]
    [InlineData("1.0.0", "1.0.0rc1", true)]
    [InlineData("1.0.0.post1", "1.0.0", true)]
    [InlineData("1.0.0-1", "1.0.0", true)]
    [InlineData("1.0", "1.0.0", true)]
    [InlineData("1!1.0", "2.0", true)]
    public async Task NewerVersionIsInstalled_UsesPep440OnPip(
        string installedVersion,
        string newVersion,
        bool expected
    )
    {
        var manager = new Pip();
        InitializeLoaders();

        await InstalledPackagesLoader.Instance.AddForeign(
            new PackageBuilder()
                .WithManager(manager)
                .WithId("contoso-tool")
                .WithVersion(installedVersion)
                .Build()
        );

        var update = new PackageBuilder()
            .WithManager(manager)
            .WithId("contoso-tool")
            .WithVersion(installedVersion)
            .WithNewVersion(newVersion)
            .Build();

        Assert.Equal(expected, update.NewerVersionIsInstalled());
    }

    [Theory]
    [InlineData("1.0.0rc1", "1.0.0", -1)]
    [InlineData("1.0.0-1", "1.0.0", 1)]
    [InlineData("1.0.0.post1", "1.0.0", 1)]
    [InlineData("1.0.0.dev1", "1.0.0rc1", -1)]
    [InlineData("1.0", "1.0.0", 0)]
    public void CompareVersions_UsesPep440OnPip(string a, string b, int expected)
    {
        var comparison = new Pip().CompareVersions(a, b);

        Assert.NotNull(comparison);
        Assert.Equal(expected, Math.Sign(comparison.Value));
    }

    // The three comparators must stay wired to the right managers: the same string can order
    // differently in each ecosystem, and "1.0.0-1" is the case that separates all three.
    [Fact]
    public void EachEcosystemOrdersATrailingDashNumberByItsOwnRules()
    {
        // SemVer: a dash always starts a pre-release, so -1 is OLDER than the release
        Assert.Equal(-1, Math.Sign(new Npm().CompareVersions("1.0.0-1", "1.0.0")!.Value));
        Assert.Equal(-1, Math.Sign(new Cargo().CompareVersions("1.0.0-1", "1.0.0")!.Value));

        // PEP 440: a bare -N is an implicit post-release, so it is NEWER
        Assert.Equal(1, Math.Sign(new Pip().CompareVersions("1.0.0-1", "1.0.0")!.Value));

        // Revision ecosystems keep the shared numeric reading, where -1 is also NEWER
        Assert.Equal(1, Math.Sign(new Homebrew().CompareVersions("1.0.0-1", "1.0.0")!.Value));
    }

    private static void InitializeLoaders()
    {
        _ = new DiscoverablePackagesLoader([]);
        _ = new UpgradablePackagesLoader([]);
        _ = new InstalledPackagesLoader([]);
    }
}
