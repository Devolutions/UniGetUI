using UniGetUI.PackageEngine.Managers.CargoManager;

namespace UniGetUI.PackageEngine.Tests;

public sealed class CargoListParsingTests
{
    private static string[] Lines(string output) =>
        output.Replace("\r\n", "\n").Split('\n', StringSplitOptions.None);

    [Fact]
    public void ParseInstallList_PreservesRegistryGitAndPathProvenance()
    {
        var entries = Cargo.ParseInstallList(
            Lines(
                """
                cargo-binstall v1.21.1:
                    cargo-binstall.exe
                cargo-update v16.4.1:
                    cargo-install-update.exe
                rg v14.1.0 (git+https://github.com/publisher/rg?branch=main#abcdef):
                    rg.exe
                private-tool v2.1.0 (registry+https://registry.example.test/index):
                    private-tool.exe
                local-tool v0.1.0 (C:\Program Files (x86)\local-tool):
                    local-tool.exe
                """
            )
        );

        Assert.Equal(["cargo-binstall", "cargo-update", "rg", "private-tool", "local-tool"], entries.Select(e => e.Id));
        Assert.Equal(["1.21.1", "16.4.1", "14.1.0", "2.1.0", "0.1.0"], entries.Select(e => e.InstalledVersion));
        Assert.True(entries[0].IsCratesIo);
        Assert.True(entries[1].IsCratesIo);
        Assert.Equal("git+https://github.com/publisher/rg?branch=main#abcdef", entries[2].SourceDescription);
        Assert.Equal("registry+https://registry.example.test/index", entries[3].SourceDescription);
        Assert.Equal(@"C:\Program Files (x86)\local-tool", entries[4].SourceDescription);
        Assert.All(entries.Skip(2), e => Assert.False(e.IsCratesIo));
        Assert.All(entries, e =>
        {
            Assert.Null(e.LatestVersion);
            Assert.False(e.NeedsUpdate);
        });
    }

    [Fact]
    public void ParseInstallList_EmptyAndIndentedBinaryOutputProducesNoInventedPackages()
    {
        Assert.Empty(Cargo.ParseInstallList([]));
        var entries = Cargo.ParseInstallList(
            Lines(
                """
                    binary.exe
                    fake-header v1.0.0:
                """
            )
        );
        Assert.Empty(entries);
    }

    [Theory]
    [InlineData("warning: unexpected stdout")]
    [InlineData("invalid name v1.0.0:")]
    [InlineData("-option v1.0.0:")]
    [InlineData("missing-version:")]
    [InlineData("tool v:")]
    [InlineData("tool v1.0.0")]
    [InlineData("Package Installed Latest Needs update")]
    public void ParseInstallList_MalformedUnindentedOutputFailsInsteadOfReturningEmptyInventory(string line)
    {
        var error = Assert.Throws<InvalidDataException>(() =>
            Cargo.ParseInstallList(["valid v1.2.3:", "    valid.exe", line]));
        Assert.Contains("Unrecognized cargo install --list output", error.Message);
    }

    [Fact]
    public void ParseInstallList_AcceptsPrereleaseAndBuildMetadataVersions()
    {
        var entries = Cargo.ParseInstallList(
            Lines(
                """
                bat v0.24.0-beta.1:
                    bat
                eza v1.0.0+build.5:
                    eza
                """
            )
        );

        Assert.Equal(2, entries.Count);
        Assert.Equal("0.24.0-beta.1", entries[0].InstalledVersion);
        Assert.Equal("1.0.0+build.5", entries[1].InstalledVersion);
    }

    [Theory]
    [InlineData("legacy", true)]
    [InlineData("legacy-slash", true)]
    [InlineData("index-slash", true)]
    [InlineData("index", true)]
    [InlineData("sparse", true)]
    [InlineData("registry-sparse", true)]
    [InlineData("private-registry", false)]
    [InlineData("private-sparse", false)]
    [InlineData("git", false)]
    [InlineData("path", false)]
    public void ParseInstallList_OnlyCratesIoSourcesQualifyForRegistryUpdates(string scenario, bool cratesIo)
    {
        string source = scenario switch
        {
            "legacy" => "registry+https://github.com/rust-lang/crates.io-index",
            "legacy-slash" => "registry+https://github.com/rust-lang/crates.io-index/",
            "index-slash" => "registry+https://index.crates.io/",
            "index" => "registry+https://index.crates.io",
            "sparse" => "sparse+https://index.crates.io/",
            "registry-sparse" => "registry+sparse+https://index.crates.io/",
            "private-registry" => "registry+https://private.test/index",
            "private-sparse" => "sparse+https://private.test/index/",
            "git" => "https://github.com/publisher/tool#deadbeef",
            _ => "/tmp/local-tool",
        };
        var entry = Assert.Single(
            Cargo.ParseInstallList(Lines($"tool v1.2.3 ({source}):\n    tool"))
        );
        Assert.Equal("tool", entry.Id);
        Assert.Equal("1.2.3", entry.InstalledVersion);
        Assert.Equal(source, entry.SourceDescription);
        Assert.Equal(cratesIo, entry.IsCratesIo);
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
