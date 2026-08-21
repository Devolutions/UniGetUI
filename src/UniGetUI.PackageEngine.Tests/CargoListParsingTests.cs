using UniGetUI.PackageEngine.Managers.CargoManager;

namespace UniGetUI.PackageEngine.Tests;

public sealed class CargoListParsingTests
{
    private static string[] Lines(string output) =>
        output.Replace("\r\n", "\n").Split('\n', StringSplitOptions.None);

    [Fact]
    public void ParseInstallUpdateList_ParsesRealCargoUpdateOutput()
    {
        var entries = Cargo.ParseInstallUpdateList(
            Lines(
                """
                    Polling registry 'https://index.crates.io/'..

                Package         Installed  Latest   Needs update
                cargo-update    v16.4.1    v22.1.1  Yes
                cargo-binstall  v1.21.1    v1.21.1  No

                cargo-binstall contains removed executables (cargo-binstall.exe), which will be re-installed on update — you can remove it with cargo uninstall cargo-binstall

                """
            )
        );

        Assert.Equal(2, entries.Count);

        Assert.Equal("cargo-update", entries[0].Id);
        Assert.Equal("16.4.1", entries[0].InstalledVersion);
        Assert.Equal("22.1.1", entries[0].LatestVersion);
        Assert.True(entries[0].NeedsUpdate);

        Assert.Equal("cargo-binstall", entries[1].Id);
        Assert.Equal("1.21.1", entries[1].InstalledVersion);
        Assert.Equal("1.21.1", entries[1].LatestVersion);
        Assert.False(entries[1].NeedsUpdate);
    }

    [Fact]
    public void ParseInstallUpdateList_SkipsRowWithoutPackageName()
    {
        List<string> skipped = [];

        var entries = Cargo.ParseInstallUpdateList(
            Lines(
                """
                Package         Installed  Latest   Needs update
                  v1.21.1  v1.21.2  Yes
                cargo-update    v16.4.1    v22.1.1  Yes
                """
            ),
            skipped
        );

        Assert.Equal("cargo-update", Assert.Single(entries).Id);
        Assert.Equal("v1.21.1  v1.21.2  Yes", Assert.Single(skipped));
    }

    [Fact]
    public void ParseInstallUpdateList_AcceptsPrereleaseAndBuildMetadataVersions()
    {
        var entries = Cargo.ParseInstallUpdateList(
            Lines(
                """
                Package  Installed       Latest        Needs update
                bat      v0.24.0-beta.1  v0.26.1       Yes
                eza      v1.0.0+build.5  v1.1.0-rc.2   Yes
                """
            )
        );

        Assert.Equal(2, entries.Count);
        Assert.Equal("0.24.0-beta.1", entries[0].InstalledVersion);
        Assert.Equal("0.26.1", entries[0].LatestVersion);
        Assert.Equal("1.0.0+build.5", entries[1].InstalledVersion);
        Assert.Equal("1.1.0-rc.2", entries[1].LatestVersion);
    }

    [Fact]
    public void ParseInstallUpdateList_StripsAlternativeVersionNote()
    {
        var entry = Assert.Single(
            Cargo.ParseInstallUpdateList(
                Lines(
                    """
                    Package         Installed  Latest                            Needs update
                    cargo-binstall  v1.21.1    v1.21.2 (v1.23.0-rc.1 available)  Yes
                    """
                )
            )
        );

        Assert.Equal("cargo-binstall", entry.Id);
        Assert.Equal("1.21.1", entry.InstalledVersion);
        Assert.Equal("1.21.2", entry.LatestVersion);
        Assert.True(entry.NeedsUpdate);
    }

    [Theory]
    [InlineData("N/A")]
    [InlineData("^1.21")]
    [InlineData("=1.21.1")]
    public void ParseInstallUpdateList_ReportsUnknownLatestVersionAsNull(string latestCell)
    {
        var entry = Assert.Single(
            Cargo.ParseInstallUpdateList(
                Lines(
                    $"""
                    Package         Installed  Latest    Needs update
                    cargo-binstall  v1.21.1    {latestCell}    No
                    """
                )
            )
        );

        Assert.Equal("1.21.1", entry.InstalledVersion);
        Assert.Null(entry.LatestVersion);
        Assert.False(entry.NeedsUpdate);
    }

    [Fact]
    public void ParseInstallUpdateList_SkipsRowsWithoutParsableInstalledVersion()
    {
        var entries = Cargo.ParseInstallUpdateList(
            Lines(
                """
                Package         Installed  Latest    Needs update
                cargo-binstall  No         v1.21.1   Yes
                """
            )
        );

        Assert.Empty(entries);
    }

    [Fact]
    public void ParseInstallUpdateList_IgnoresGitPackageTable()
    {
        var entries = Cargo.ParseInstallUpdateList(
            Lines(
                """
                    Checking 1 git package.

                Package  Installed                                 Latest                                    Needs update
                mygitpkg  1a2b3c4d5e6f7890abcdef1234567890abcdef12  9876543210fedcba9876543210fedcba98765432  Yes
                """
            )
        );

        Assert.Empty(entries);
    }

    [Fact]
    public void ParseInstallUpdateList_IgnoresLinesOutsideTheTable()
    {
        var entries = Cargo.ParseInstallUpdateList(
            Lines(
                """
                    Polling registry 'https://index.crates.io/'.
                cargo-update is no longer part of its registry — you can remove it with cargo uninstall cargo-update
                """
            )
        );

        Assert.Empty(entries);
    }

    [Fact]
    public void ParseInstallUpdateList_SkipsRowsWithMissingColumns()
    {
        var entries = Cargo.ParseInstallUpdateList(
            Lines(
                """
                Package  Installed  Latest   Needs update
                bat                 v0.26.1  Yes
                """
            )
        );

        Assert.Empty(entries);
    }

    [Fact]
    public void ParseInstallUpdateList_ReadsNeedsUpdateFromItsOwnColumn()
    {
        var entries = Cargo.ParseInstallUpdateList(
            Lines(
                """
                Package  Installed  Latest   Needs update
                bat      v0.26.1    v0.26.2  No
                eza      v1.0.0     v1.1.0   Yes
                """
            )
        );

        Assert.False(entries[0].NeedsUpdate);
        Assert.True(entries[1].NeedsUpdate);
    }

    [Fact]
    public void ParseInstallUpdateList_IgnoresNotesWhenNotSeparatedByABlankLine()
    {
        var entry = Assert.Single(
            Cargo.ParseInstallUpdateList(
                Lines(
                    """
                    Package  Installed  Latest   Needs update
                    bat      v0.26.1    v0.26.2  Yes
                    bat contains removed executables (bat.exe), which will be re-installed on update — you can remove it with cargo uninstall bat
                    """
                )
            )
        );

        Assert.Equal("bat", entry.Id);
    }

    [Fact]
    public void ParseInstallList_ParsesSourcesContainingParentheses()
    {
        var entry = Assert.Single(
            Cargo.ParseInstallList(Lines(@"mycrate v0.1.0 (C:\Program Files (x86)\mycrate):"))
        );

        Assert.Equal("mycrate", entry.Id);
        Assert.Equal("0.1.0", entry.InstalledVersion);
    }

    [Fact]
    public void ParseInstallList_ParsesPackageLinesAndIgnoresBinaries()
    {
        var entries = Cargo.ParseInstallList(
            Lines(
                """
                cargo-binstall v1.21.1:
                    cargo-binstall.exe
                cargo-update v16.4.1:
                    cargo-install-update-config.exe
                    cargo-install-update.exe
                ripgrep v14.1.0 (https://github.com/BurntSushi/ripgrep?branch=master#1a2b3c4d):
                    rg.exe
                """
            )
        );

        Assert.Equal(3, entries.Count);
        Assert.Equal("cargo-binstall", entries[0].Id);
        Assert.Equal("1.21.1", entries[0].InstalledVersion);
        Assert.Equal("cargo-update", entries[1].Id);
        Assert.Equal("ripgrep", entries[2].Id);
        Assert.Equal("14.1.0", entries[2].InstalledVersion);
        Assert.All(
            entries,
            entry =>
            {
                Assert.Null(entry.LatestVersion);
                Assert.False(entry.NeedsUpdate);
            }
        );
    }
}
