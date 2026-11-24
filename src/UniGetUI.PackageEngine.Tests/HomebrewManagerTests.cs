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

    private const string UnavailableCaskError =
        "Error: Cask 'powershell' is unavailable: No Cask with this name exists.";

    // A cask renamed or removed upstream makes `brew list --cask --versions` exit 1 before it prints
    // any cask, which used to empty the installed casks and fail the update check.
    [Fact]
    public void AnUnavailableCaskDoesNotHideTheOtherInstalledCasks()
    {
        string caskroom = Path.Combine(_testRoot, "Caskroom");
        CreateCask(caskroom, "firefox", "152.0.5", "20260101000000.000", "json");
        CreateCask(caskroom, "powershell", "7.4.6", "20250101000000.000", "rb");
        var brew = new ListingHomebrew(FakeBrew(caskroomExitCode: 0, caskroom));

        IReadOnlyList<Package> installed = brew.ListInstalled();

        Assert.Equal(
            ["wget 1.25.0", "firefox 152.0.5", "powershell 7.4.6"],
            installed.Select(p => $"{p.Id} {p.VersionString}")
        );
        Assert.Equal("Homebrew Cask", installed[1].Source.Name);
        // The broken cask is offered for removal on the Manager Health page, by name.
        Assert.Equal(["powershell"], brew.BrokenCasks);
        ManagerMaintenanceAction action = Assert.Single(
            brew.ExtraActions(),
            a => a.Id == "uninstall-broken-casks"
        );
        Assert.Contains("powershell", action.Description);
        // Recovered listings are complete, so a remote rescan must not fail on them.
        using var scope = PackageListingScope.EnterListingOnly();
        Assert.Equal(3, brew.ListInstalled().Count);
    }

    [Fact]
    public void ACaskListingThatCannotBeRecoveredIsStillAFailedListing()
    {
        var brew = new ListingHomebrew(FakeBrew(caskroomExitCode: 1, Path.Combine(_testRoot, "missing")));

        Assert.Equal(["wget"], brew.ListInstalled().Select(p => p.Id));
        Assert.Empty(brew.BrokenCasks);
        Assert.DoesNotContain(brew.ExtraActions(), a => a.Id == "uninstall-broken-casks");
        using var scope = PackageListingScope.EnterListingOnly();
        Assert.Throws<InvalidOperationException>(brew.ListInstalled);
    }

    [Fact]
    public void TheCaskroomIsReadLikeHomebrewReadsIt()
    {
        string caskroom = Path.Combine(_testRoot, "Caskroom");
        // The newest timestamp wins across versions, as in Caskroom.cask_installed_version.
        CreateCask(caskroom, "firefox", "151.0", "20260101000000.000", "json");
        CreateCask(caskroom, "firefox", "152.0.5", "20260201000000.000", "json");
        CreateCask(caskroom, "iterm2", "3.5.0", "20250101000000.000", "rb");
        // A half-removed cask whose newest install has no caskfile is not installed.
        CreateCask(caskroom, "broken", "1.0", "20240101000000.000", "json");
        Directory.CreateDirectory(Path.Combine(caskroom, "broken", ".metadata", "2.0", "20250101000000.000"));
        Directory.CreateDirectory(Path.Combine(caskroom, "no-metadata"));
        File.WriteAllText(Path.Combine(caskroom, ".DS_Store"), "");
        if (!OperatingSystem.IsWindows())
            Directory.CreateSymbolicLink(Path.Combine(caskroom, "iterm"), Path.Combine(caskroom, "iterm2"));

        Assert.Equal(
            [("firefox", "152.0.5"), ("iterm2", "3.5.0")],
            Homebrew.ReadInstalledCasks(caskroom)
        );
    }

    [Fact]
    public void BrokenCasksAreNamedFromBrewsErrors()
    {
        Assert.Equal(
            ["powershell", "old-app"],
            Homebrew.ParseBrokenCasks(
                $"Warning: something\n{UnavailableCaskError}\n"
                    + "Error: Cask 'old-app' definition is invalid: bad\n"
                    + UnavailableCaskError
            )
        );
        Assert.Empty(Homebrew.ParseBrokenCasks("Error: Another active Homebrew process is already in progress."));
        // Tokens go back to `brew uninstall`, so anything outside Homebrew's token charset is ignored.
        Assert.Empty(Homebrew.ParseBrokenCasks("Error: Cask 'x; rm -rf ~' is unavailable"));
    }

    [Fact]
    public void ListedVersionsWinOverTheCaskroom()
    {
        Assert.Equal(
            [("firefox", "152.0.5"), ("iterm2", "3.5.0")],
            Homebrew.MergeInstalledEntries(
                Homebrew.ParseListVersionsOutput(["firefox 152.0.5", "", "garbage"]),
                [("firefox", "151.0"), ("iterm2", "3.5.0")]
            )
        );
    }

    private sealed class ListingHomebrew : Homebrew
    {
        public ListingHomebrew(string executable)
        {
            Status = new UniGetUI.PackageEngine.Classes.Manager.ManagerHelpers.ManagerStatus
            {
                Found = true,
                ExecutablePath = executable,
                ExecutableCallArgs = "",
            };
        }

        public IReadOnlyList<Package> ListInstalled() => GetInstalledPackages_UnSafe();

        public IEnumerable<ManagerMaintenanceAction> ExtraActions() => GetExtraMaintenanceActions();
    }

    private static void CreateCask(string caskroom, string token, string version, string timestamp, string ext)
    {
        string casks = Path.Combine(caskroom, token, ".metadata", version, timestamp, "Casks");
        Directory.CreateDirectory(casks);
        Directory.CreateDirectory(Path.Combine(caskroom, token, version));
        File.WriteAllText(Path.Combine(casks, $"{token}.{ext}"), "");
    }

    // Answers like Homebrew 7 with an unavailable cask installed: formulae list fine, casks fail.
    private string FakeBrew(int caskroomExitCode, string caskroom)
    {
        if (OperatingSystem.IsWindows())
        {
            string path = Path.Combine(_testRoot, $"brew-{Guid.NewGuid():N}.cmd");
            File.WriteAllLines(path,
            [
                // Labels instead of ( ) blocks, so a ")" in the test path cannot end a block early.
                "@echo off",
                "if \"%1\"==\"--caskroom\" goto caskroom",
                "if \"%2\"==\"--cask\" goto cask",
                "echo wget 1.25.0",
                "exit /b 0",
                ":caskroom",
                $"echo({EscapeBatch(caskroom)}",
                $"exit /b {caskroomExitCode}",
                ":cask",
                $"echo({UnavailableCaskError} 1>&2",
                "exit /b 1",
            ]);
            return path;
        }

        string script = Path.Combine(_testRoot, $"brew-{Guid.NewGuid():N}.sh");
        File.WriteAllLines(script,
        [
            "#!/bin/sh",
            $"if [ \"$1\" = --caskroom ]; then echo '{caskroom}'; exit {caskroomExitCode}; fi",
            $"if [ \"$2\" = --cask ]; then echo \"{UnavailableCaskError}\" >&2; exit 1; fi",
            "echo 'wget 1.25.0'",
        ]);
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return script;
    }

    private static string EscapeBatch(string value) =>
        value.Replace("^", "^^").Replace("%", "%%").Replace("&", "^&")
            .Replace("|", "^|").Replace("<", "^<").Replace(">", "^>");

    private static string[] ReadFixtureLines(string relativePath)
    {
        return PackageEngineFixtureFiles.ReadAllText(relativePath).Replace("\r\n", "\n").Split('\n');
    }
}
