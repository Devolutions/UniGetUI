namespace UniGetUI.Extensions.Xcode.Tests;

public sealed class XcodesServiceTests
{
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
        + "27.0 Beta (27A5194q) [Apple Silicon]\n"
        + "26.1 Beta 2 (17B5035f) [Apple Silicon]\n"
        + "26.3 Release Candidate 2 (17C528) [Apple Silicon]\n"
        + "26.4 (17E192) [Apple Silicon]\n"
        + "26.6 (17F113) [Apple Silicon] (Installed, Selected)\n"
        + "\n"
        + "Showing Xcodes for this Mac by default: Apple Silicon (arm64).\n";

    private static readonly XcodeInstallOptions NoOptions = new(false, false, false, false);

    /// <summary>An <see cref="XcodesService"/> whose commands are answered by the test.</summary>
    private sealed class Harness
    {
        public Harness()
        {
            Service = new XcodesService(
                (arguments, _, _) =>
                {
                    Commands.Add([.. arguments]);
                    return Task.FromResult(Respond([.. arguments]));
                },
                (arguments, prompts, output, _) =>
                {
                    TerminalCommands.Add([.. arguments]);
                    Prompts = prompts;
                    Output = output;
                    return Task.FromResult(Respond([.. arguments]));
                },
                () => Executable);
        }

        public XcodesService Service { get; }

        public string? Executable { get; set; } = "/opt/homebrew/bin/xcodes";

        public Func<string[], XcodesResult> Respond { get; set; } = _ => new XcodesResult(0, "", "");

        public List<string[]> Commands { get; } = [];

        public List<string[]> TerminalCommands { get; } = [];

        public XcodesPromptHandler? Prompts { get; private set; }

        public Action<XcodesOutputLine>? Output { get; private set; }
    }

    private static XcodeRelease Release(string version, bool installed = false)
        => new(version, null, XcodeArchitecture.AppleSilicon, installed, false);

    private static InstalledXcode Installed(string version)
        => new(version, null, XcodeArchitecture.AppleSilicon, false, "/Applications/Xcode.app");

    [Fact]
    public void FindExecutable_ReturnsWhereXcodesIs_OrNull()
    {
        var xcodes = new Harness();
        Assert.Equal("/opt/homebrew/bin/xcodes", xcodes.Service.FindExecutable());

        xcodes.Executable = null;
        Assert.Null(xcodes.Service.FindExecutable());
    }

    [Fact]
    public async Task GetVersionAsync_ReturnsTheVersionXcodesPrints()
    {
        var xcodes = new Harness { Respond = _ => new XcodesResult(0, "2.1.0\n", "") };

        Assert.Equal("2.1.0", await xcodes.Service.GetVersionAsync());
        Assert.Equal(["version"], Assert.Single(xcodes.Commands));
    }

    [Fact]
    public async Task GetVersionAsync_ReturnsNullWhenXcodesFailsOrCannotBeRun()
    {
        var failing = new Harness { Respond = _ => new XcodesResult(1, "", "boom") };
        Assert.Null(await failing.Service.GetVersionAsync());

        var missing = new Harness { Respond = _ => throw new XcodesException("Could not find xcodes.") };
        Assert.Null(await missing.Service.GetVersionAsync());
    }

    [Fact]
    public async Task GetReleasesAsync_ListsTheReleasesNewestFirst()
    {
        var xcodes = new Harness { Respond = _ => new XcodesResult(0, SampleList, "") };

        IReadOnlyList<XcodeRelease> releases = await xcodes.Service.GetReleasesAsync(refresh: false);

        Assert.Equal(["list"], Assert.Single(xcodes.Commands));
        Assert.Equal(
            [
                "27.0 Beta", "26.6", "26.4", "26.3 Release Candidate 2", "26.1 Beta 2", "12.1.1 Release Candidate",
                "12.0 Beta", "12.0 Beta", "10.0 GM Seed", "4.1", "4.1", "3.2.3", "1.0",
            ],
            releases.Select(release => release.Version));
    }

    [Fact]
    public async Task GetReleasesAsync_KeepsOneRowForABuildThatIsListedTwice()
    {
        var xcodes = new Harness { Respond = _ => new XcodesResult(0, SampleList, "") };

        IReadOnlyList<XcodeRelease> releases = await xcodes.Service.GetReleasesAsync(refresh: false);

        Assert.Single(releases, release => release.Version == "3.2.3");
        Assert.Equal(["4B110f", "4B110i"], releases.Where(release => release.Version == "4.1").Select(release => release.Build));
    }

    [Fact]
    public async Task GetReleasesAsync_ReadsWhichReleaseIsInstalled()
    {
        var xcodes = new Harness { Respond = _ => new XcodesResult(0, SampleList, "") };

        IReadOnlyList<XcodeRelease> releases = await xcodes.Service.GetReleasesAsync(refresh: false);

        XcodeRelease installed = Assert.Single(releases, release => release.IsInstalled);
        Assert.Equal("26.6", installed.Version);
        Assert.True(installed.IsSelected);
    }

    [Fact]
    public async Task GetReleasesAsync_DownloadsTheListAgainWhenAskedTo()
    {
        var xcodes = new Harness { Respond = _ => new XcodesResult(0, SampleList, "") };

        await xcodes.Service.GetReleasesAsync(refresh: true);

        Assert.Equal(["update"], Assert.Single(xcodes.Commands));
    }

    [Fact]
    public async Task GetReleasesAsync_ReportsWhyXcodesFailed()
    {
        var xcodes = new Harness { Respond = _ => new XcodesResult(1, "", "No release versions available.\n") };

        XcodesException error = await Assert.ThrowsAsync<XcodesException>(() => xcodes.Service.GetReleasesAsync(refresh: false));

        Assert.Equal("No release versions available.", error.Message);
    }

    [Fact]
    public async Task GetInstalledAsync_ListsTheXcodesOnThisMacNewestFirst()
    {
        var xcodes = new Harness
        {
            Respond = _ => new XcodesResult(
                0,
                "26.4 (17E192) [Apple Silicon]\t/Applications/Xcode-26.4.app\n"
                + "26.6 (17F113) [Apple Silicon] (Selected)\t/Applications/Xcode.app\n"
                + "26.1 Beta 2 (17B5035f)\t/Applications/Xcode-26.1.0-Beta.2.app\n",
                ""),
        };

        IReadOnlyList<InstalledXcode> installed = await xcodes.Service.GetInstalledAsync();

        Assert.Equal(["installed"], Assert.Single(xcodes.Commands));
        Assert.Equal(["26.6", "26.4", "26.1 Beta 2"], installed.Select(xcode => xcode.Version));
        Assert.True(installed[0].IsSelected);
        Assert.Equal("/Applications/Xcode.app", installed[0].Path);
    }

    [Fact]
    public async Task GetInstalledAsync_ReportsWhyXcodesFailed()
    {
        var xcodes = new Harness { Respond = _ => new XcodesResult(1, "", "") };

        XcodesException error = await Assert.ThrowsAsync<XcodesException>(() => xcodes.Service.GetInstalledAsync());

        Assert.Equal("xcodes exited with code 1.", error.Message);
    }

    [Fact]
    public async Task InstallAsync_InstallsOnlyTheVersionByDefault()
    {
        var xcodes = new Harness();

        await xcodes.Service.InstallAsync("26.4", NoOptions, prompts: null);

        Assert.Equal(["install", "26.4"], Assert.Single(xcodes.TerminalCommands));
        Assert.Empty(xcodes.Commands);
    }

    [Fact]
    public async Task InstallAsync_PassesEachOptionToXcodes()
    {
        var xcodes = new Harness();

        await xcodes.Service.InstallAsync("26.1 Beta 2", new XcodeInstallOptions(true, true, true, true), prompts: null);

        // The version stays one argument, however many words it has.
        Assert.Equal(
            ["install", "26.1 Beta 2", "--select", "--no-superuser", "--empty-trash", "--experimental-unxip"],
            Assert.Single(xcodes.TerminalCommands));
    }

    [Fact]
    public async Task InstallAsync_TrimsTheVersion()
    {
        var xcodes = new Harness();

        await xcodes.Service.InstallAsync("  26.4  ", NoOptions, prompts: null);

        Assert.Equal(["install", "26.4"], Assert.Single(xcodes.TerminalCommands));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("--latest")]
    [InlineData("-h")]
    [InlineData("26.4 --select")]
    [InlineData("26.4; echo hi")]
    [InlineData("latest")]
    public async Task InstallAsync_RefusesAnythingThatIsNotAVersion(string version)
    {
        var xcodes = new Harness();

        await Assert.ThrowsAsync<ArgumentException>(() => xcodes.Service.InstallAsync(version, NoOptions, prompts: null));

        Assert.Empty(xcodes.TerminalCommands);
        Assert.Empty(xcodes.Commands);
    }

    [Fact]
    public async Task InstallAsync_HandsThePromptsAndTheOutputToTheTerminal()
    {
        var xcodes = new Harness();
        XcodesPromptHandler prompts = (_, _) => Task.FromResult<string?>("answer");
        Action<XcodesOutputLine> output = _ => { };

        await xcodes.Service.InstallAsync("26.4", NoOptions, prompts, output);

        Assert.Same(prompts, xcodes.Prompts);
        Assert.Same(output, xcodes.Output);
    }

    [Fact]
    public async Task InstallAsync_ReportsWhyItFailed()
    {
        var xcodes = new Harness
        {
            Respond = _ => new XcodesResult(
                1,
                "(1/6) Downloading Xcode 26.4: 100%\n(2/6) Unarchiving Xcode (This can take a while)\n"
                + "Using regular unxip. Try passing `--experimental-unxip` for a faster unxip\n"
                + "The archive \"Xcode_26.4.xip\" is damaged and can't be expanded.",
                ""),
        };

        XcodesException error = await Assert.ThrowsAsync<XcodesException>(
            () => xcodes.Service.InstallAsync("26.4", NoOptions, prompts: null));

        Assert.Equal("The archive \"Xcode_26.4.xip\" is damaged and can't be expanded.", error.Message);
    }

    [Fact]
    public async Task InstallAsync_LetsACancellationThrough()
    {
        var xcodes = new Harness { Respond = _ => throw new OperationCanceledException() };

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => xcodes.Service.InstallAsync("26.4", NoOptions, prompts: null));
    }

    [Fact]
    public async Task SelectAsync_RunsInTheTerminalSoItCanAskForThePassword()
    {
        var xcodes = new Harness();

        await xcodes.Service.SelectAsync("26.4", prompts: null);

        Assert.Equal(["select", "26.4"], Assert.Single(xcodes.TerminalCommands));
        Assert.Empty(xcodes.Commands);
    }

    [Fact]
    public async Task SelectAsync_RefusesAnythingThatIsNotAVersion()
    {
        var xcodes = new Harness();

        await Assert.ThrowsAsync<ArgumentException>(() => xcodes.Service.SelectAsync("--help", prompts: null));

        Assert.Empty(xcodes.TerminalCommands);
    }

    [Fact]
    public async Task SelectAsync_ReportsWhyItFailed()
    {
        var xcodes = new Harness { Respond = _ => new XcodesResult(1, "26.9 is not installed.\nAvailable Xcode versions:\n1) 26.4\n", "") };

        XcodesException error = await Assert.ThrowsAsync<XcodesException>(() => xcodes.Service.SelectAsync("26.9", prompts: null));

        Assert.Equal("26.9 is not installed.", error.Message);
    }

    [Fact]
    public async Task UninstallAsync_MovesTheXcodeToTheTrash()
    {
        var xcodes = new Harness();

        await xcodes.Service.UninstallAsync("26.1 Beta 2", deleteCompletely: false);

        Assert.Equal(["uninstall", "26.1 Beta 2"], Assert.Single(xcodes.Commands));
        Assert.Empty(xcodes.TerminalCommands);
    }

    [Fact]
    public async Task UninstallAsync_CanEmptyTheTrashToo()
    {
        var xcodes = new Harness();

        await xcodes.Service.UninstallAsync("26.4", deleteCompletely: true);

        Assert.Equal(["uninstall", "26.4", "--empty-trash"], Assert.Single(xcodes.Commands));
    }

    [Fact]
    public async Task UninstallAsync_RefusesAnythingThatIsNotAVersion()
    {
        var xcodes = new Harness();

        await Assert.ThrowsAsync<ArgumentException>(() => xcodes.Service.UninstallAsync("--help", deleteCompletely: false));

        Assert.Empty(xcodes.Commands);
    }

    [Fact]
    public async Task UninstallAsync_ReportsWhyItFailed()
    {
        var xcodes = new Harness { Respond = _ => new XcodesResult(1, "26.9 is not installed.\n", "") };

        XcodesException error = await Assert.ThrowsAsync<XcodesException>(
            () => xcodes.Service.UninstallAsync("26.9", deleteCompletely: false));

        Assert.Equal("26.9 is not installed.", error.Message);
    }

    [Fact]
    public async Task SignOutAsync_RunsSignout()
    {
        var xcodes = new Harness();

        await xcodes.Service.SignOutAsync();

        Assert.Equal(["signout"], Assert.Single(xcodes.Commands));
    }

    [Fact]
    public async Task SignOutAsync_ReportsWhyItFailed()
    {
        var xcodes = new Harness { Respond = _ => new XcodesResult(1, "", "keychain locked") };

        XcodesException error = await Assert.ThrowsAsync<XcodesException>(() => xcodes.Service.SignOutAsync());

        Assert.Equal("keychain locked", error.Message);
    }

    [Fact]
    public void FindUpdate_OffersNothingToAMacWithoutXcode()
        => Assert.Null(XcodesService.FindUpdate([Release("26.6")], [], includePrereleases: true));

    [Fact]
    public void FindUpdate_OffersTheNewestReleaseNewerThanWhatIsInstalled()
    {
        XcodeRelease? update = XcodesService.FindUpdate(
            [Release("26.4"), Release("26.6"), Release("26.5.1"), Release("26.2")],
            [Installed("26.4")],
            includePrereleases: false);

        Assert.Equal("26.6", update?.Version);
    }

    [Fact]
    public void FindUpdate_FindsTheNewestEvenWhenTheListIsNotInOrder()
    {
        // xcodes lists a beta of the next major release before the last release of the previous one.
        XcodeRelease? update = XcodesService.FindUpdate(
            [Release("27.0 Beta"), Release("26.6"), Release("26.4")],
            [Installed("26.4")],
            includePrereleases: false);

        Assert.Equal("26.6", update?.Version);
    }

    [Fact]
    public void FindUpdate_SkipsBetasAndReleaseCandidatesUnlessAskedNotTo()
    {
        XcodeRelease[] releases =
        [
            Release("26.6"), Release("27.0 Beta"), Release("26.7 Release Candidate"), Release("26.4"),
        ];
        InstalledXcode[] installed = [Installed("26.4")];

        Assert.Equal("26.6", XcodesService.FindUpdate(releases, installed, includePrereleases: false)?.Version);
        Assert.Equal("27.0 Beta", XcodesService.FindUpdate(releases, installed, includePrereleases: true)?.Version);
    }

    [Fact]
    public void FindUpdate_OffersNothingWhenTheNewestReleaseIsInstalled()
    {
        Assert.Null(XcodesService.FindUpdate(
            [Release("26.4"), Release("26.6", installed: true)],
            [Installed("26.6"), Installed("26.4")],
            includePrereleases: false));
    }

    [Fact]
    public void FindUpdate_OffersNothingWhenAnotherRowOfTheNewestVersionIsInstalled()
    {
        // xcodes lists a few versions twice, and may mark only one of the rows.
        Assert.Null(XcodesService.FindUpdate(
            [Release("26.6"), Release("26.4"), Release("26.6", installed: true)],
            [Installed("26.4")],
            includePrereleases: false));
        Assert.Null(XcodesService.FindUpdate(
            [Release("26.6", installed: true), Release("26.4"), Release("26.6")],
            [Installed("26.4")],
            includePrereleases: false));
    }

    [Fact]
    public void FindUpdate_ReadsTheReleasesOnce()
    {
        int enumerations = 0;
        IEnumerable<XcodeRelease> Releases()
        {
            enumerations++;
            yield return Release("26.6");
            yield return Release("26.4", installed: true);
        }

        XcodeRelease? update = XcodesService.FindUpdate(Releases(), [Installed("26.4")], includePrereleases: false);

        Assert.Equal("26.6", update?.Version);
        Assert.Equal(1, enumerations);
    }

    [Fact]
    public void FindUpdate_OffersNothingWhenAnInstalledXcodeIsAlreadyNewer()
    {
        Assert.Null(XcodesService.FindUpdate(
            [Release("27.0 Beta", installed: true), Release("26.6"), Release("26.4")],
            [Installed("27.0 Beta"), Installed("26.4")],
            includePrereleases: false));
    }

    [Fact]
    public void FindUpdate_CountsAPointReleaseAsNewer()
        => Assert.Equal("26.6.1", XcodesService.FindUpdate([Release("26.6.1"), Release("26.6", installed: true)], [Installed("26.6")], includePrereleases: false)?.Version);

    [Fact]
    public void FindUpdate_OffersAReleaseOnceItsBetaIsInstalled()
        => Assert.Equal("27.0", XcodesService.FindUpdate([Release("27.0"), Release("27.0 Beta")], [Installed("27.0 Beta 3")], includePrereleases: false)?.Version);

    [Fact]
    public void FindUpdate_OffersNothingWhenThereAreNoReleases()
        => Assert.Null(XcodesService.FindUpdate([], [Installed("26.4")], includePrereleases: true));
}
