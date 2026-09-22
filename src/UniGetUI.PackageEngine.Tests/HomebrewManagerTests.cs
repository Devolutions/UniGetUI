using UniGetUI.Core.Data;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.PackageEngine.Classes.Manager;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Managers.HomebrewManager;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.Tests.Infrastructure.Assertions;
using UniGetUI.PackageEngine.Tests.Infrastructure.Helpers;

namespace UniGetUI.PackageEngine.Tests;

[CollectionDefinition("Homebrew manager tests", DisableParallelization = true)]
public sealed class HomebrewManagerTestCollection
{
    public const string Name = "Homebrew manager tests";
}

[Collection(HomebrewManagerTestCollection.Name)]
public sealed class HomebrewManagerTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        AppContext.BaseDirectory,
        nameof(HomebrewManagerTests),
        Guid.NewGuid().ToString("N")
    );

    public HomebrewManagerTests()
    {
        Directory.CreateDirectory(_testRoot);
        CoreData.TEST_DataDirectoryOverride = Path.Combine(_testRoot, "Data");
        Directory.CreateDirectory(CoreData.UniGetUIUserConfigurationDirectory);
        Settings.ResetSettings();
    }

    public void Dispose()
    {
        Settings.ResetSettings();
        CoreData.TEST_DataDirectoryOverride = null;
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    // Regression test for issue #5127: "brew outdated --verbose" prints "<" for outdated
    // Formulae but "!=" for outdated Casks. The parser used to match only "<", so Cask
    // updates were silently dropped. Both operators must produce update packages.
    [Fact]
    public void ParseAvailableUpdatesDetectsBothFormulaAndCaskUpdates()
    {
        var manager = new Homebrew();
        IManagerSource formulaSource = manager.SourcesHelper.Factory.GetSourceOrDefault("Homebrew");
        IManagerSource caskSource = manager.SourcesHelper.Factory.GetSourceOrDefault("Homebrew Cask");

        // Installed packages carry the source (Formula vs Cask) that updates should inherit.
        var installed = new List<IPackage>
        {
            new Package("Fontconfig", "fontconfig", "2.17.1", formulaSource, manager),
            new Package("Shaderc", "shaderc", "2026.2", formulaSource, manager),
            new Package("Python@3.14", "python@3.14", "3.14.3_1", formulaSource, manager),
            new Package("Firefox", "firefox", "152.0.5", caskSource, manager),
            new Package("Visual Studio Code", "visual-studio-code", "1.90.0", caskSource, manager),
        };

        var updates = manager.ParseAvailableUpdates(
            ReadFixtureLines(Path.Combine("Homebrew", "outdated-verbose.txt")),
            installed
        );

        Assert.Collection(
            updates,
            package =>
            {
                PackageAssert.Matches(package, "Fontconfig", "fontconfig", "2.17.1", "2.18.2");
                PackageAssert.BelongsTo(package, manager, formulaSource);
            },
            package =>
            {
                PackageAssert.Matches(package, "Shaderc", "shaderc", "2026.2", "2026.3");
                PackageAssert.BelongsTo(package, manager, formulaSource);
            },
            package =>
            {
                PackageAssert.Matches(package, "Python@3.14", "python@3.14", "3.14.3_1", "3.14.6");
                PackageAssert.BelongsTo(package, manager, formulaSource);
            },
            // The two Casks below (matched via "!=") were the ones dropped by the old parser.
            package =>
            {
                PackageAssert.Matches(package, "Firefox", "firefox", "152.0.5", "152.0.6");
                PackageAssert.BelongsTo(package, manager, caskSource);
            },
            package =>
            {
                PackageAssert.Matches(
                    package,
                    "Visual Studio Code",
                    "visual-studio-code",
                    "1.90.0",
                    "1.91.0"
                );
                PackageAssert.BelongsTo(package, manager, caskSource);
            }
        );
    }

    // Issue #5219: on Homebrew 4 and later `brew tap` prints nothing for homebrew/core, so the
    // sources page was empty and the default "Homebrew" source was reported as not configured.
    [Fact]
    public void SourcesListTheBuiltInSourcesWhenBrewTapPrintsNothing()
    {
        var manager = new Homebrew();
        var helper = (HomebrewSourceHelper)manager.SourcesHelper;

        IReadOnlyList<IManagerSource> sources = helper.BuildSourceList([]);

        Assert.Equal(manager.Properties.KnownSources, sources);
        Assert.Contains(sources, source => source.Name == "Homebrew");
    }

    [Fact]
    public void SourcesListOtherTapsOnceAndSkipTheBuiltInTaps()
    {
        var manager = new Homebrew();
        var helper = (HomebrewSourceHelper)manager.SourcesHelper;

        IReadOnlyList<IManagerSource> sources = helper.BuildSourceList(
            ["homebrew/core", "hashicorp/tap", "", "  ", "Homebrew/cask"]
        );

        Assert.Equal(manager.Properties.KnownSources.Length + 1, sources.Count);
        Assert.Equal(manager.Properties.KnownSources, sources.Take(manager.Properties.KnownSources.Length));
        IManagerSource tap = sources[^1];
        Assert.Equal("hashicorp/tap", tap.Name);
        Assert.Equal(new Uri("https://github.com/hashicorp/homebrew-tap"), tap.Url);
    }

    [Fact]
    public void CasksAreABuiltInSourceOnMacOsOnly()
    {
        var manager = new Homebrew();

        Assert.Equal(["Homebrew"], Homebrew.CreateBuiltInSources(manager, isMacOS: false).Select(s => s.Name));
        Assert.Equal(
            ["Homebrew", "Homebrew Cask"],
            Homebrew.CreateBuiltInSources(manager, isMacOS: true).Select(s => s.Name)
        );
        Assert.Equal(
            OperatingSystem.IsMacOS() ? 2 : 1,
            manager.Properties.KnownSources.Length
        );
    }

    // brew rejects "Homebrew" and "Homebrew Cask" ("Error: Invalid tap name: 'Homebrew'"); the
    // parameters must name the tap.
    [Theory]
    [InlineData("Homebrew", "https://github.com/Homebrew/homebrew-core", "tap homebrew/core", "untap homebrew/core")]
    [InlineData("Homebrew Cask", "https://github.com/Homebrew/homebrew-cask", "tap homebrew/cask", "untap homebrew/cask")]
    [InlineData(
        "hashicorp/tap",
        "https://github.com/hashicorp/homebrew-tap",
        "tap hashicorp/tap https://github.com/hashicorp/homebrew-tap",
        "untap hashicorp/tap"
    )]
    public void AddAndRemoveParametersUseTheTapName(string name, string url, string add, string remove)
    {
        var manager = new Homebrew();
        var source = new ManagerSource(manager, name, new Uri(url));

        Assert.Equal(add, string.Join(' ', manager.SourcesHelper.GetAddSourceParameters(source)));
        Assert.Equal(remove, string.Join(' ', manager.SourcesHelper.GetRemoveSourceParameters(source)));
    }

    private static string[] ReadFixtureLines(string relativePath)
    {
        return PackageEngineFixtureFiles.ReadAllText(relativePath).Replace("\r\n", "\n").Split('\n');
    }
}
