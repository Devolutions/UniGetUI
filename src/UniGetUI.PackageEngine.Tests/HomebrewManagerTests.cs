using UniGetUI.Core.Data;
using UniGetUI.Core.SettingsEngine;
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

    private static string[] ReadFixtureLines(string relativePath)
    {
        return PackageEngineFixtureFiles.ReadAllText(relativePath).Replace("\r\n", "\n").Split('\n');
    }
}
