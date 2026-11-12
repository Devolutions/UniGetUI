namespace UniGetUI.Extensions.Xcode.Tests;

public sealed class XcodesServiceTests
{
    private static readonly XcodeInstallOptions NoOptions = new();

    /// <summary>A backend that records what reaches it.</summary>
    private sealed class FakeBackend : IXcodeBackend
    {
        public List<string> Calls { get; } = [];

        public XcodeRelease? Installed { get; private set; }

        public Task<IReadOnlyList<InstalledXcode>> GetInstalledAsync(string? installDirectory, CancellationToken cancellationToken)
        {
            Calls.Add($"installed {installDirectory}");
            return Task.FromResult<IReadOnlyList<InstalledXcode>>([]);
        }

        public Task<IReadOnlyList<XcodeRelease>> GetReleasesAsync(bool refresh, string? installDirectory, CancellationToken cancellationToken)
        {
            Calls.Add($"releases {refresh} {installDirectory}");
            return Task.FromResult<IReadOnlyList<XcodeRelease>>([]);
        }

        public Task<XcodeInstallationResult> InstallAsync(XcodeRelease release, XcodeInstallOptions options, XcodesPromptHandler? prompts,
            Action<XcodesOutputLine>? output, CancellationToken cancellationToken)
        {
            Calls.Add($"install {release.Version}");
            Installed = release;
            return Task.FromResult(new XcodeInstallationResult("/Applications/Xcode.app", release.Build ?? "", release.Architecture));
        }

        public Task<string> DownloadAsync(XcodeRelease release, XcodesPromptHandler? prompts,
            Action<XcodesOutputLine>? output, CancellationToken cancellationToken, string? archiveDirectory = null)
        {
            Calls.Add(archiveDirectory is null ? $"download {release.Version}" : $"download {release.Version} {archiveDirectory}");
            return Task.FromResult("/cache/Xcode.xip");
        }

        public Task SelectAsync(string appPath, Action<XcodesOutputLine>? output, CancellationToken cancellationToken)
        {
            Calls.Add($"select {appPath}");
            return Task.CompletedTask;
        }

        public XcodeAccountStatus GetAccount() => XcodeAccountStatus.None;

        public Task<XcodeAccountStatus> RefreshAccountAsync(CancellationToken cancellationToken) => Task.FromResult(XcodeAccountStatus.None);

        public Task<XcodeAccountStatus> SignInAsync(XcodesPromptHandler prompts, string? suggestedAppleId, CancellationToken cancellationToken)
        {
            Calls.Add($"signin {suggestedAppleId}");
            return Task.FromResult(new XcodeAccountStatus(suggestedAppleId, null));
        }

        public Task SignOutAsync(CancellationToken cancellationToken)
        {
            Calls.Add("signout");
            return Task.CompletedTask;
        }
    }

    private static XcodeRelease Release(string version, bool installed = false)
        => new(version, null, XcodeArchitecture.AppleSilicon, installed, false);

    private static InstalledXcode Installed(string version)
        => new(version, null, XcodeArchitecture.AppleSilicon, false, "/Applications/Xcode.app");

    [Fact]
    public async Task PassesEachRequestToTheBackend()
    {
        var backend = new FakeBackend();
        var service = new XcodesService(backend);
        XcodeRelease release = new("26.4", "17E192", XcodeArchitecture.AppleSilicon, false, false);

        await service.GetReleasesAsync(true);
        await service.GetInstalledAsync();
        XcodeInstallationResult installed = await service.InstallAsync(release, NoOptions, prompts: null);
        Assert.Equal(new XcodeInstallationResult("/Applications/Xcode.app", "17E192", XcodeArchitecture.AppleSilicon), installed);
        Assert.Equal("/cache/Xcode.xip", await service.DownloadAsync(release, prompts: null));
        await service.SelectAsync(Installed("26.6") with { Path = "/Applications/Xcode-26.6.app" });
        await service.SignInAsync((_, _) => Task.FromResult<string?>(null), "someone@example.com");
        await service.SignOutAsync();

        Assert.Equal(
            ["releases True ", "installed ", "install 26.4", "download 26.4", "select /Applications/Xcode-26.6.app", "signin someone@example.com", "signout"],
            backend.Calls);
        Assert.Same(release, backend.Installed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("--select")]
    [InlineData("latest")]
    public async Task InstallAsync_RefusesAnythingThatIsNotAVersion(string version)
    {
        var backend = new FakeBackend();

        await Assert.ThrowsAsync<ArgumentException>(() => new XcodesService(backend)
            .InstallAsync(Release(version), NoOptions, prompts: null));
        Assert.Empty(backend.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("--select")]
    [InlineData("latest")]
    public async Task DownloadAsync_RefusesAnythingThatIsNotAVersion(string version)
    {
        var backend = new FakeBackend();

        await Assert.ThrowsAsync<ArgumentException>(() => new XcodesService(backend)
            .DownloadAsync(Release(version), prompts: null));
        Assert.Empty(backend.Calls);
    }

    [Fact]
    public async Task DownloadAsync_RefusesAnUnverifiedIdentity()
    {
        var backend = new FakeBackend();
        XcodeRelease release = Release("27.0 Beta") with { IsIdentityUnverified = true };

        XcodesException error = await Assert.ThrowsAsync<XcodesException>(() =>
            new XcodesService(backend).DownloadAsync(release, prompts: null));

        Assert.Equal(XcodesService.UnknownBuildMessage, error.Message);
        Assert.Empty(backend.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-build")]
    [InlineData("17E192\n")]
    public async Task InstallAndDownloadRefuseMalformedExplicitBuilds(string build)
    {
        var backend = new FakeBackend();
        var service = new XcodesService(backend);
        XcodeRelease release = Release("26.4") with { Build = build };

        XcodesException install = await Assert.ThrowsAsync<XcodesException>(() =>
            service.InstallAsync(release, NoOptions, prompts: null));
        XcodesException download = await Assert.ThrowsAsync<XcodesException>(() =>
            service.DownloadAsync(release, prompts: null));

        Assert.Equal(XcodesService.UnknownBuildMessage, install.Message);
        Assert.Equal(XcodesService.UnknownBuildMessage, download.Message);
        Assert.Empty(backend.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Xcode.app")]
    [InlineData("/Applications/Xcode")]
    public async Task SelectAsync_RefusesAPathThatIsNotAnAbsoluteAppPath(string path)
    {
        var backend = new FakeBackend();

        await Assert.ThrowsAsync<ArgumentException>(() => new XcodesService(backend)
            .SelectAsync(Installed("26.4") with { Path = path }));
        Assert.Empty(backend.Calls);
    }

    [Theory]
    [InlineData("relative/path")]
    [InlineData("--directory")]
    [InlineData(" ")]
    public void InvalidInstallationDirectoryIsRejected(string directory)
        => Assert.Throws<XcodesException>(() => XcodesService.ValidateInstallDirectory(directory));

    [Fact]
    public void MissingInstallationDirectoryDoesNotFallBackToApplications()
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        XcodesException error = Assert.Throws<XcodesException>(() => XcodesService.ValidateInstallDirectory(directory));

        Assert.Equal("The Xcode installation directory does not exist or is inaccessible.", error.Message);
        Assert.Null(XcodesService.ValidateInstallDirectory(""));
    }

    [Theory]
    [InlineData("relative/path")]
    [InlineData("--archive")]
    [InlineData(" ")]
    public void InvalidArchiveDirectoryIsRejected(string directory)
        => Assert.Throws<XcodesException>(() => XcodesService.ValidateArchiveDirectory(directory));

    [Fact]
    public void MissingArchiveDirectoryDoesNotFallBackToDefaultCache()
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        XcodesException error = Assert.Throws<XcodesException>(() => XcodesService.ValidateArchiveDirectory(directory));

        Assert.Equal("The Xcode archive directory does not exist or is inaccessible.", error.Message);
        Assert.Null(XcodesService.ValidateArchiveDirectory(""));
    }

    [Fact]
    public async Task DownloadAsync_ForwardsCustomArchiveDirectoryToBackend()
    {
        var backend = new FakeBackend();
        XcodeRelease release = new("26.4", "17E192", XcodeArchitecture.AppleSilicon, false, false);

        await new XcodesService(backend).DownloadAsync(release, prompts: null, archiveDirectory: "/custom/archives");

        Assert.Equal(["download 26.4 /custom/archives"], backend.Calls);
    }

    [Theory]
    [InlineData("26.6 Beta 2", "26.6", true)]
    [InlineData("26.6 Release Candidate", "26.6.0", true)]
    [InlineData("26.6.1 GM", "26.6.1", true)]
    [InlineData("26.6 Beta", "26.6.1", false)]
    [InlineData("26.6 Beta", "26.7", false)]
    [InlineData("26.6 Beta", "27.6", false)]
    [InlineData("26.6", "26.6", false)]
    [InlineData("26.6 Beta", "26.6 Beta 2", false)]
    [InlineData("unknown", "26.6", false)]
    public void FindStableCounterpart_RequiresTheSameReleaseVersion(string current, string stable, bool matches)
    {
        var active = new InstalledXcode(current, null, XcodeArchitecture.AppleSilicon, true, "/Applications/Beta.app");

        string? found = XcodesService.FindStableCounterpart(active, [Release(stable)], []);

        Assert.Equal(matches ? stable : null, found);
    }

    [Theory]
    [InlineData(XcodeArchitecture.AppleSilicon, XcodeArchitecture.AppleSilicon, true, true)]
    [InlineData(XcodeArchitecture.AppleSilicon, XcodeArchitecture.Universal, true, false)]
    [InlineData(XcodeArchitecture.AppleSilicon, XcodeArchitecture.Unspecified, true, false)]
    [InlineData(XcodeArchitecture.Unspecified, XcodeArchitecture.Unspecified, true, false)]
    [InlineData(XcodeArchitecture.AppleSilicon, XcodeArchitecture.AppleSilicon, false, false)]
    public void FindStableCounterpart_RequiresAnActivePrereleaseAndTheSameKnownArchitecture(
        XcodeArchitecture activeArchitecture, XcodeArchitecture releaseArchitecture, bool selected, bool matches)
    {
        var active = new InstalledXcode("26.6 Beta", null, activeArchitecture, selected, "/Applications/Beta.app");
        var release = new XcodeRelease("26.6", null, releaseArchitecture, false, false);

        Assert.Equal(matches ? "26.6" : null, XcodesService.FindStableCounterpart(active, [release], []));
    }

    [Fact]
    public void FindStableCounterpart_FindsAnInstalledReleaseEvenWhenXcodesDoesNotListIt()
    {
        var active = new InstalledXcode("26.6 Beta", null, XcodeArchitecture.AppleSilicon, true, "/Applications/Beta.app");
        var stable = new InstalledXcode("26.6", null, XcodeArchitecture.AppleSilicon, false, "/Applications/Stable.app");

        Assert.Equal("26.6", XcodesService.FindStableCounterpart(active, [], [stable]));
        Assert.Null(XcodesService.FindStableCounterpart(active, [], [stable with { Architecture = XcodeArchitecture.Intel }]));
    }

    [Fact]
    public void FindStableCounterpart_DoesNotClaimAnUnverifiedReleaseIsAvailable()
    {
        var active = new InstalledXcode("26.6 Beta", "17F5012f", XcodeArchitecture.AppleSilicon, true, "/Applications/Beta.app");
        XcodeRelease unknown = Release("26.6") with { IsIdentityUnverified = true };
        XcodeRelease malformed = Release("26.6") with { Build = "17F42\n" };

        Assert.Null(XcodesService.FindStableCounterpart(active, [unknown], []));
        Assert.Null(XcodesService.FindStableCounterpart(active, [malformed], []));
    }

    [Fact]
    public void FindUpdate_SkipsMalformedBuildsEvenIfTheBackendDidNotFlagThem()
    {
        XcodeRelease valid = Release("26.6") with { Build = "17F42" };
        XcodeRelease malformed = Release("27.0") with { Build = "18A1\n" };
        XcodeRelease falselyInstalled = Release("26.6", installed: true) with { Build = "17F42\n" };

        XcodeRelease? update = XcodesService.FindUpdate(
            [malformed, falselyInstalled, valid], [Installed("26.5")], includePrereleases: true);

        Assert.Same(valid, update);
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
