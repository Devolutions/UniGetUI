namespace UniGetUI.Extensions.Xcode.Tests;

public sealed class XcodesOutputParserTests
{
    // Taken from `xcodes list` on an Apple Silicon Mac: old releases carry no architecture, some versions are
    // listed twice with different builds, and the footer follows a blank line.
    private const string SampleList =
        "1.0 (7B85)\n"
        + "3.2.3 (10M2262)\n"
        + "3.2.3 (10M2262)\n"
        + "4.1 (4B110f)\n"
        + "4.1 (4B110i)\n"
        + "10.0 GM Seed (10A254a)\n"
        + "12.0 Beta (12A8158a)\n"
        + "12.0 Beta (12A6159)\n"
        + "12.1.1 Release Candidate (12A7605b)\n"
        + "26.1 Beta 2 (17B5035f) [Apple Silicon]\n"
        + "26.3 Release Candidate 2 (17C528) [Apple Silicon]\n"
        + "26.4 (17E192) [Apple Silicon]\n"
        + "26.6 (17F113) [Apple Silicon] (Installed, Selected)\n"
        + "27.0 Beta (27A5194q) [Apple Silicon]\n"
        + "\n"
        + "Showing Xcodes for this Mac by default: Apple Silicon (arm64). Switch with `--architecture arm64`, `--architecture x86_64`, `--architecture appleSilicon`, or `--architecture universal`.\n";

    [Fact]
    public void ParseReleases_ReadsEveryReleaseAndNothingElse()
    {
        IReadOnlyList<XcodeRelease> releases = XcodesOutputParser.ParseReleases(SampleList);

        Assert.Equal(14, releases.Count);
        Assert.DoesNotContain(releases, release => release.Version.StartsWith("Showing", StringComparison.Ordinal));
    }

    [Fact]
    public void ParseReleases_ReadsBuildArchitectureAndFlags()
    {
        IReadOnlyList<XcodeRelease> releases = XcodesOutputParser.ParseReleases(SampleList);

        Assert.Contains(new XcodeRelease("1.0", "7B85", XcodeArchitecture.Unspecified, false, false), releases);
        Assert.Contains(new XcodeRelease("26.4", "17E192", XcodeArchitecture.AppleSilicon, false, false), releases);
        Assert.Contains(new XcodeRelease("26.6", "17F113", XcodeArchitecture.AppleSilicon, true, true), releases);
        Assert.Contains(new XcodeRelease("26.1 Beta 2", "17B5035f", XcodeArchitecture.AppleSilicon, false, false), releases);
        Assert.Contains(new XcodeRelease("26.3 Release Candidate 2", "17C528", XcodeArchitecture.AppleSilicon, false, false), releases);
        Assert.Contains(new XcodeRelease("10.0 GM Seed", "10A254a", XcodeArchitecture.Unspecified, false, false), releases);
    }

    [Fact]
    public void ParseReleases_KeepsVersionsThatAreListedTwice()
    {
        IReadOnlyList<XcodeRelease> releases = XcodesOutputParser.ParseReleases(SampleList);

        Assert.Equal(["4B110f", "4B110i"], releases.Where(r => r.Version == "4.1").Select(r => r.Build));
        Assert.Equal(["12A8158a", "12A6159"], releases.Where(r => r.Version == "12.0 Beta").Select(r => r.Build));
    }

    [Fact]
    public void ParseReleases_UnderstandsEveryArchitectureTag()
    {
        IReadOnlyList<XcodeRelease> releases = XcodesOutputParser.ParseReleases(
            "26.4 (17E192) [Universal]\n26.4 (17E192) [Apple Silicon]\n26.4 (17E192) [Intel]\n26.4 (17E192) [Something New]\n");

        Assert.Equal(
            [XcodeArchitecture.Universal, XcodeArchitecture.AppleSilicon, XcodeArchitecture.Intel, XcodeArchitecture.Unspecified],
            releases.Select(r => r.Architecture));
    }

    [Fact]
    public void ParseReleases_ToleratesWindowsLineEndingsAndColour()
    {
        IReadOnlyList<XcodeRelease> releases = XcodesOutputParser.ParseReleases(
            "\u001b[32m26.4 (17E192) [Apple Silicon]\u001b[0m\r\n26.6 (17F113) [Apple Silicon] (Installed)\r\n");

        Assert.Equal(["26.4", "26.6"], releases.Select(r => r.Version));
        Assert.True(releases[1].IsInstalled);
        Assert.False(releases[1].IsSelected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n\n")]
    [InlineData("Error: something went wrong")]
    public void ParseReleases_FindsNothingInOutputThatIsNotAList(string output)
        => Assert.Empty(XcodesOutputParser.ParseReleases(output));

    [Fact]
    public void ParseInstalled_ReadsTheDescriptionAndWhereItIs()
    {
        IReadOnlyList<InstalledXcode> installed = XcodesOutputParser.ParseInstalled(
            "26.6 (17F113) [Apple Silicon] (Selected)\t/Applications/Xcode.app\n"
            + "26.4 (17E192) [Apple Silicon]\t/Applications/Xcode 26.4.app\n"
            + "26.1 Beta 2 (17B5035f)\t/Applications/Xcode-26.1.0-Beta.2.app\n");

        Assert.Equal(3, installed.Count);
        Assert.Equal(new InstalledXcode("26.6", "17F113", XcodeArchitecture.AppleSilicon, true, "/Applications/Xcode.app"), installed[0]);
        Assert.Equal(new InstalledXcode("26.4", "17E192", XcodeArchitecture.AppleSilicon, false, "/Applications/Xcode 26.4.app"), installed[1]);
        Assert.Equal(new InstalledXcode("26.1 Beta 2", "17B5035f", XcodeArchitecture.Unspecified, false, "/Applications/Xcode-26.1.0-Beta.2.app"), installed[2]);
        Assert.True(installed[2].IsPrerelease);
    }

    [Fact]
    public void ParseInstalled_SkipsLinesWithoutAPath()
    {
        Assert.Empty(XcodesOutputParser.ParseInstalled("26.6 (17F113) [Apple Silicon] (Selected)\nnot an xcode\n\n"));
        Assert.Empty(XcodesOutputParser.ParseInstalled("26.6\tnot-a-path\n"));
        Assert.Empty(XcodesOutputParser.ParseInstalled(""));
    }

    [Fact]
    public void ParseInstalled_AlsoReadsAColumnLayoutWithoutATab()
    {
        IReadOnlyList<InstalledXcode> installed = XcodesOutputParser.ParseInstalled("26.4 (17E192) /Applications/Xcode 26.4.app\n");

        InstalledXcode xcode = Assert.Single(installed);
        Assert.Equal("26.4", xcode.Version);
        Assert.Equal("/Applications/Xcode 26.4.app", xcode.Path);
    }

    [Fact]
    public void ParseVersion_ReadsTheFirstLine()
    {
        Assert.Equal("2.1.0", XcodesOutputParser.ParseVersion("2.1.0\n"));
        Assert.Equal("2.1.0", XcodesOutputParser.ParseVersion("\r\n2.1.0\r\n"));
        Assert.Null(XcodesOutputParser.ParseVersion(""));
    }

    [Fact]
    public void TryParseProgress_ReadsTheStepAndTheDownloadPercentage()
    {
        Assert.True(XcodesOutputParser.TryParseProgress("(1/6) Downloading Xcode 26.4: 42%", out XcodeProgress progress));

        Assert.Equal(1, progress.Step);
        Assert.Equal(6, progress.StepCount);
        Assert.Equal("Downloading Xcode 26.4: 42%", progress.Text);
        Assert.Equal(0.42, progress.Fraction);
    }

    [Theory]
    [InlineData("(1/6) Downloading Xcode 26.4: 42 %", 0.42)] // French and German put a space before the sign.
    [InlineData("(1/6) Downloading Xcode 26.4: 42\u00a0%", 0.42)]
    [InlineData("(1/6) Downloading Xcode 26.4: 42\u202f%", 0.42)]
    [InlineData("(1/6) Downloading Xcode 26.4: 7%", 0.07)]
    [InlineData("(1/6) Downloading Xcode 26.4: 100%", 1.0)]
    [InlineData("(1/6) Downloading Xcode 26.4: 250%", 1.0)]
    [InlineData("(1/6) Downloading Xcode 26.4.1: 3%", 0.03)]
    [InlineData("\u001b[1A\u001b[K(1/6) Downloading Xcode 26.4: 7%", 0.07)]
    public void TryParseProgress_ReadsPercentagesTheWayLocalesWriteThem(string line, double fraction)
    {
        Assert.True(XcodesOutputParser.TryParseProgress(line, out XcodeProgress progress));
        Assert.Equal(fraction, progress.Fraction);
    }

    [Theory]
    [InlineData("(2/6) Unarchiving Xcode (This can take a while)", 2, 6)]
    [InlineData("(3/6) Moving Xcode to /Applications/Xcode-26.4.app", 3, 6)]
    [InlineData("(5/6) Checking security assessment and code signing", 5, 6)]
    [InlineData("(1/6) Found existing archive that will be used for installation at /Users/me/Downloads/Xcode_26.4.xip.", 1, 6)]
    [InlineData("(1/2) Downloading Xcode 26.4", 1, 2)]
    public void TryParseProgress_ReadsStepsWithoutAPercentage(string line, int step, int count)
    {
        Assert.True(XcodesOutputParser.TryParseProgress(line, out XcodeProgress progress));
        Assert.Equal(step, progress.Step);
        Assert.Equal(count, progress.StepCount);
        Assert.Null(progress.Fraction);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Downloading Xcode 26.4: 42%")]
    [InlineData("Xcode 26.4 has been installed to /Applications/Xcode-26.4.app")]
    [InlineData("(x/6) Downloading")]
    [InlineData("(1/6)")]
    public void TryParseProgress_RejectsOtherLines(string line)
        => Assert.False(XcodesOutputParser.TryParseProgress(line, out _));

    [Fact]
    public void StripTerminalControl_RemovesEscapeSequencesAndNoise()
    {
        Assert.Equal("(1/6) Downloading", XcodesOutputParser.StripTerminalControl("\u001b[1A\u001b[K(1/6) Downloading"));
        Assert.Equal("green", XcodesOutputParser.StripTerminalControl("\u001b[32mgreen\u001b[0m"));
        Assert.Equal("title text", XcodesOutputParser.StripTerminalControl("\u001b]0;window\u0007title text"));
        Assert.Equal("a\tb\nc", XcodesOutputParser.StripTerminalControl("a\tb\nc"));
        Assert.Equal("ab", XcodesOutputParser.StripTerminalControl("a\u0000\u0007b"));
    }
}
