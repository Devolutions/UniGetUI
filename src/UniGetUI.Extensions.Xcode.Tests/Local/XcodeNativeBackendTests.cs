using System.Net;
using System.Security.Cryptography;
using System.Text;
using UniGetUI.Extensions.Xcode.AppleAuth;
using UniGetUI.Extensions.Xcode.Local;
using static UniGetUI.Extensions.Xcode.Tests.AppleAuth.AppleAccountTests;
using static UniGetUI.Extensions.Xcode.Tests.AppleAuth.AppleAuthClientTests;
using static UniGetUI.Extensions.Xcode.Tests.XcodeCatalogSessionTests;

namespace UniGetUI.Extensions.Xcode.Tests.Local;

public sealed class XcodeNativeBackendTests : IDisposable
{
    private readonly TemporaryDirectory _applications = new();
    private readonly TemporaryDirectory _archives = new();
    private readonly List<string> _tools = [];

    public void Dispose()
    {
        _applications.Dispose();
        _archives.Dispose();
    }

    private XcodeToolRunner Tools(string? selected = null) => (executable, arguments, _) =>
    {
        _tools.Add($"{executable} {string.Join(' ', arguments)}");
        return Task.FromResult(executable == "/usr/bin/xcode-select" && selected is not null
            ? new XcodesResult(0, selected + "\n", "")
            : new XcodesResult(1, "", "refused by the test"));
    };

    private XcodeNativeBackend Backend(IXcodeReleasesCatalog catalog, AppleAccount? account = null,
        string? selected = null, XcodeArchitecture machine = XcodeArchitecture.AppleSilicon)
        => new(catalog, account ?? new AppleAccount(new MemoryStore(), new FakeApple()), () => _archives.Path,
            Tools(selected), Tools(selected), machine);

    private static XcodesPromptHandler Answers(List<XcodesPrompt>? asked = null) => (prompt, _) =>
    {
        asked?.Add(prompt);
        return Task.FromResult<string?>(prompt.Kind switch
        {
            XcodesPromptKind.AppleId => "test@example.com",
            XcodesPromptKind.Password => "secret-password",
            _ => null,
        });
    };

    private async Task<Uri?> SelectedDownloadAsync(
        XcodeRelease requested, XcodeArchitecture machine, params XcodeCatalogRelease[] candidates)
    {
        Uri? selected = null;
        using var account = new AppleAccount(new MemoryStore(), new FakeApple());
        var backend = new XcodeNativeBackend(new FakeCatalog(candidates), account, () => _archives.Path,
            Tools(), Tools(), machine)
        {
            Download = (address, _, _, _) =>
            {
                selected = address;
                throw new XcodesException("Stopped after selecting the test archive.");
            },
        };
        await Assert.ThrowsAsync<XcodesException>(() => backend.InstallAsync(requested,
            new XcodeInstallOptions(InstallDirectory: _applications.Path), Answers(), null, CancellationToken.None));
        return selected;
    }

    [Fact]
    public void ListsWhatThisMacCanRunAndMarksOnlyTheInstalledBuild()
    {
        XcodeCatalogRelease[] catalog =
        [
            Release("26.4", "17E192"),
            Release("26.4", "17E192"),
            Release("26.4", "17E200"),
            Release("26.5 Beta", "17F5012f", XcodeArchitecture.AppleSilicon),
            Release("15.4", "15F31d", XcodeArchitecture.Intel),
            Release("27.0 Beta", "27A5194q", downloadable: false),
        ];
        InstalledXcode[] installed = [new("26.4", "17E192", XcodeArchitecture.Universal, true, "/Applications/Xcode-26.4.app")];

        IReadOnlyList<XcodeRelease> silicon = XcodeNativeBackend.Merge(catalog, installed, XcodeArchitecture.AppleSilicon);
        IReadOnlyList<XcodeRelease> intel = XcodeNativeBackend.Merge(catalog, installed, XcodeArchitecture.Intel);

        Assert.Equal(
            [
                ("26.5 Beta", "17F5012f", false, false),
                ("26.4", "17E192", true, true),
                ("26.4", "17E200", false, false),
            ],
            silicon.Select(r => (r.Version, r.Build!, r.IsInstalled, r.IsSelected)));
        Assert.Equal(["26.4", "26.4", "15.4"], intel.Select(r => r.Version));
    }

    [Fact]
    public async Task ASecondBuildDestinationConflictStopsBeforeAppleAuthentication()
    {
        FakeXcodeBundle.Create(_applications.Path, "Xcode-26.4.app", "26.4", "17E191");
        string conflict = Directory.CreateDirectory(Path.Combine(_applications.Path, "Xcode-26.4-17E192.app")).FullName;
        using var apple = new FakeApple();
        using var account = new AppleAccount(new MemoryStore(), apple);
        XcodeNativeBackend backend = Backend(new FakeCatalog(Release("26.4", "17E192")), account);
        List<XcodesPrompt> asked = [];

        XcodesException error = await Assert.ThrowsAsync<XcodesException>(() => backend.InstallAsync(
            new XcodeRelease("26.4", "17E192", XcodeArchitecture.Universal, false, false),
            new XcodeInstallOptions(InstallDirectory: _applications.Path), Answers(asked), null, CancellationToken.None));

        Assert.Contains(conflict, error.Message);
        Assert.Empty(asked);
        Assert.Empty(apple.Paths);
        Assert.True(Directory.Exists(conflict));
    }

    [Theory]
    [InlineData(XcodeArchitecture.AppleSilicon)]
    [InlineData(XcodeArchitecture.Intel)]
    public void MergedReleasesAndUpdatesPreferTheNativeVariant(XcodeArchitecture machine)
    {
        XcodeCatalogRelease unknown = Release("26.4", "17E192", XcodeArchitecture.Unspecified);
        XcodeCatalogRelease universal = Release("26.4", "17E192");
        XcodeCatalogRelease native = Release("26.4", "17E192", machine);
        InstalledXcode[] installed = [new("26.3", "17C52", XcodeArchitecture.Universal, true, "/Applications/Xcode.app")];

        IReadOnlyList<XcodeRelease> releases = XcodeNativeBackend.Merge([unknown, universal, native], installed, machine);

        Assert.Equal([machine, XcodeArchitecture.Universal, XcodeArchitecture.Unspecified],
            releases.Select(release => release.Architecture));
        Assert.Equal(machine, XcodesService.FindUpdate(releases, installed, includePrereleases: false)!.Architecture);
    }

    [Fact]
    public void VariantPreferenceKeepsVersionOrderAndDistinctBuilds()
    {
        XcodeCatalogRelease newer = Release("26.5", "17F42");
        XcodeCatalogRelease universal = Release("26.4", "17E192");
        XcodeCatalogRelease native = Release("26.4", "17E200", XcodeArchitecture.AppleSilicon);

        IReadOnlyList<XcodeRelease> releases = XcodeNativeBackend.Merge(
            [universal, native, newer], [], XcodeArchitecture.AppleSilicon);

        Assert.Equal(
            [
                ("26.5", "17F42", XcodeArchitecture.Universal),
                ("26.4", "17E200", XcodeArchitecture.AppleSilicon),
                ("26.4", "17E192", XcodeArchitecture.Universal),
            ],
            releases.Select(release => (release.Version, release.Build, release.Architecture)));
    }

    [Fact]
    public void UnrecognizedBuildsRemainVisibleButCannotDisplaceVerifiedVariantsOrUpdates()
    {
        XcodeCatalogRelease invalidNative = Release("26.6", "17F42\n", XcodeArchitecture.AppleSilicon);
        XcodeCatalogRelease anotherInvalidNative = Release("26.6", "not-a-build", XcodeArchitecture.AppleSilicon);
        XcodeCatalogRelease validUniversal = Release("26.6", "17F42");
        XcodeCatalogRelease invalidNewer = Release("27.0", "not-a-build");
        InstalledXcode[] installed =
        [
            new("26.5", "17F42\n", XcodeArchitecture.AppleSilicon, true, "/Applications/Xcode.app"),
        ];

        IReadOnlyList<XcodeRelease> releases = XcodeNativeBackend.Merge(
            [invalidNative, anotherInvalidNative, validUniversal, invalidNewer], installed, XcodeArchitecture.AppleSilicon);

        XcodeRelease valid = Assert.Single(releases, release => release.Build == "17F42");
        Assert.False(valid.IsIdentityUnverified);
        XcodeRelease invalid = Assert.Single(releases,
            release => release.Version == "26.6" && release.IsIdentityUnverified);
        Assert.Null(invalid.Build);
        Assert.False(invalid.IsInstalled);
        Assert.False(invalid.IsSelected);
        Assert.Null(invalid.AppleSizeMB);
        Assert.Same(valid, releases.First(release => release.Version == "26.6"));
        Assert.True(Assert.Single(releases, release => release.Version == "27.0").IsIdentityUnverified);
        Assert.Same(valid, XcodesService.FindUpdate(releases, installed, includePrereleases: true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnInvalidCatalogBuildStopsDownloadOrInstallBeforeAuthentication(bool install)
    {
        using var apple = new FakeApple();
        using var account = new AppleAccount(new MemoryStore(), apple);
        bool downloaded = false;
        int prompts = 0;
        var backend = new XcodeNativeBackend(new FakeCatalog(Release("26.4", "17E192\n")),
            account, () => _archives.Path, Tools(), Tools(), XcodeArchitecture.AppleSilicon)
        {
            Download = (_, _, _, _) =>
            {
                downloaded = true;
                throw new XcodesException("The invalid build reached the downloader.");
            },
        };
        XcodesPromptHandler prompt = (_, _) =>
        {
            prompts++;
            return Task.FromResult<string?>(null);
        };
        var requested = new XcodeRelease("26.4", null, XcodeArchitecture.Unspecified, false, false);

        XcodesException error = await Assert.ThrowsAsync<XcodesException>(() => install
            ? backend.InstallAsync(requested, new XcodeInstallOptions(InstallDirectory: _applications.Path),
                prompt, null, CancellationToken.None)
            : backend.DownloadAsync(requested, prompt, null, CancellationToken.None));

        Assert.Equal(XcodesService.UnknownBuildMessage, error.Message);
        Assert.Empty(apple.Paths);
        Assert.Equal(0, prompts);
        Assert.False(downloaded);
        Assert.Empty(_tools);
    }

    [Fact]
    public async Task AnUnspecifiedBuildSelectsAValidVariantInsteadOfAnInvalidNativeVariant()
    {
        XcodeCatalogRelease invalidNative = Release("26.4", "not-a-build", XcodeArchitecture.AppleSilicon) with
        {
            Download = new Uri("https://download.developer.apple.com/Xcode_Invalid.xip"),
        };
        XcodeCatalogRelease validUniversal = Release("26.4", "17E192") with
        {
            Download = new Uri("https://download.developer.apple.com/Xcode_Valid.xip"),
        };

        Uri? selected = await SelectedDownloadAsync(
            new XcodeRelease("26.4", null, XcodeArchitecture.Unspecified, false, false),
            XcodeArchitecture.AppleSilicon, invalidNative, validUniversal);

        Assert.Equal(validUniversal.Download, selected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("17E192\n")]
    public async Task AnInvalidExplicitBuildIsRejectedEvenWhenCallingTheBackendDirectly(string build)
    {
        using var apple = new FakeApple();
        using var account = new AppleAccount(new MemoryStore(), apple);
        XcodeNativeBackend backend = Backend(new FakeCatalog(Release("26.4", "17E192")), account);
        var requested = new XcodeRelease("26.4", build, XcodeArchitecture.Universal, false, false);

        XcodesException install = await Assert.ThrowsAsync<XcodesException>(() => backend.InstallAsync(
            requested, new XcodeInstallOptions(InstallDirectory: _applications.Path), Answers(), null, CancellationToken.None));
        XcodesException download = await Assert.ThrowsAsync<XcodesException>(() =>
            backend.DownloadAsync(requested, Answers(), null, CancellationToken.None));

        Assert.Equal(XcodesService.UnknownBuildMessage, install.Message);
        Assert.Equal(XcodesService.UnknownBuildMessage, download.Message);
        Assert.Empty(apple.Paths);
        Assert.Empty(_tools);
    }

    [Fact]
    public void MergesAppleDataOnlyByArchiveAddressAndShowsUnknownBuildsWithoutOfferingThemAsUpdates()
    {
        XcodeCatalogRelease known = Release("26.4", "17E192");
        AppleXcodeDownload[] apple =
        [
            new("26.4", known.Download!, 1_234_500_000, null),
            new("26.4", new Uri("https://download.developer.apple.com/other/Xcode_26.4.xip"), 9_999_000_000, null),
            new("27.0 Beta", new Uri("https://download.developer.apple.com/other/Xcode_27_Beta.xip"), 2_000_000_000, null),
        ];
        InstalledXcode[] installed = [new("26.3", "17C52", XcodeArchitecture.Universal, true, "/Applications/Xcode.app")];

        IReadOnlyList<XcodeRelease> releases = XcodeNativeBackend.Merge([known], installed, XcodeArchitecture.AppleSilicon, apple);

        XcodeRelease unknown = Assert.Single(releases, release => release.IsIdentityUnverified);
        Assert.Equal("27.0 Beta", unknown.Version);
        Assert.Null(unknown.Build);
        Assert.Equal(XcodeArchitecture.Unspecified, unknown.Architecture);
        XcodeRelease matched = Assert.Single(releases, release => !release.IsIdentityUnverified);
        Assert.Equal("17E192", matched.Build);
        Assert.Equal(1235, matched.AppleSizeMB);
        Assert.Same(matched, XcodesService.FindUpdate(releases, installed, includePrereleases: true));

        XcodeRelease unmatched = Assert.Single(XcodeNativeBackend.Merge([known], [], XcodeArchitecture.AppleSilicon, [apple[1]]));
        Assert.Null(unmatched.AppleSizeMB);
    }

    [Fact]
    public async Task RefreshesAppleDownloadsOnlyForASignedInAccountAndClearsThemOnSignOut()
    {
        using var account = new AppleAccount(new MemoryStore(), new FakeApple());
        int appleRequests = 0;
        var backend = new XcodeNativeBackend(new FakeCatalog(Release("26.4", "17E192")), account,
            () => _archives.Path, Tools(), Tools(), XcodeArchitecture.AppleSilicon)
        {
            FetchAppleDownloads = _ =>
            {
                appleRequests++;
                return Task.FromResult<IReadOnlyList<AppleXcodeDownload>>(
                    [new("27.0 Beta", new Uri("https://download.developer.apple.com/other/Xcode_27.xip"), null, null)]);
            },
        };

        Assert.Single(await backend.GetReleasesAsync(true, _applications.Path, CancellationToken.None));
        Assert.Equal(0, appleRequests);
        await backend.SignInAsync(Answers(), null, CancellationToken.None);
        Assert.Contains(await backend.GetReleasesAsync(true, _applications.Path, CancellationToken.None), release => release.IsIdentityUnverified);
        Assert.Contains(await backend.GetReleasesAsync(false, _applications.Path, CancellationToken.None), release => release.IsIdentityUnverified);
        Assert.Equal(1, appleRequests);

        await backend.SignOutAsync(CancellationToken.None);
        Assert.Single(await backend.GetReleasesAsync(false, _applications.Path, CancellationToken.None));
        Assert.Equal(1, appleRequests);
    }

    [Fact]
    public async Task DiscardsAnInFlightAppleListWhenTheAccountChangesEvenIfTheIdIsTheSame()
    {
        using var account = new AppleAccount(new MemoryStore(), new FakeApple());
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reply = new TaskCompletionSource<IReadOnlyList<AppleXcodeDownload>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new XcodeNativeBackend(new FakeCatalog(Release("26.4", "17E192")), account,
            () => _archives.Path, Tools(), Tools(), XcodeArchitecture.AppleSilicon)
        {
            FetchAppleDownloads = _ =>
            {
                started.TrySetResult();
                return reply.Task;
            },
        };
        await backend.SignInAsync(Answers(), null, CancellationToken.None);
        Task<IReadOnlyList<XcodeRelease>> loading = backend.GetReleasesAsync(true, _applications.Path, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task signingOut = backend.SignOutAsync(CancellationToken.None);
        Assert.False(signingOut.IsCompleted);
        reply.SetResult([new("27.0 Beta", new Uri("https://download.developer.apple.com/other/Xcode_27.xip"), null, null)]);

        Assert.Single(await loading);
        await signingOut;
        await backend.SignInAsync(Answers(), null, CancellationToken.None);
        Assert.Single(await backend.GetReleasesAsync(false, _applications.Path, CancellationToken.None));
    }

    [Fact]
    public async Task PersistsCookiesUpdatedByAnAppleCatalogRefresh()
    {
        var store = new MemoryStore();
        using var account = new AppleAccount(store, new FakeApple());
        var backend = new XcodeNativeBackend(new FakeCatalog(Release("26.4", "17E192")), account,
            () => _archives.Path, Tools(), Tools(), XcodeArchitecture.AppleSilicon)
        {
            FetchAppleDownloads = _ =>
            {
                account.Cookies.Add(new Cookie("catalog-session", "synthetic", "/", ".apple.com"));
                return Task.FromResult<IReadOnlyList<AppleXcodeDownload>>([]);
            },
        };
        await backend.SignInAsync(Answers(), null, CancellationToken.None);

        await backend.GetReleasesAsync(true, _applications.Path, CancellationToken.None);

        using var restored = new AppleAccount(store, new FakeApple());
        restored.LoadSaved();
        Assert.Contains(restored.Cookies.GetAllCookies().Cast<Cookie>(), c => c.Name == "catalog-session" && c.Value == "synthetic");
    }

    [Fact]
    public async Task CatalogRefreshReportsAnAccountStorageFailureWithoutCachingItsResult()
    {
        var store = new MemoryStore();
        using var account = new AppleAccount(store, new FakeApple());
        var backend = new XcodeNativeBackend(new FakeCatalog(Release("26.4", "17E192")), account,
            () => _archives.Path, Tools(), Tools(), XcodeArchitecture.AppleSilicon)
        {
            FetchAppleDownloads = _ => Task.FromResult<IReadOnlyList<AppleXcodeDownload>>(
                [new("27.0 Beta", new Uri("https://download.developer.apple.com/other/Xcode_27.xip"), null, null)]),
        };
        await backend.SignInAsync(Answers(), null, CancellationToken.None);
        store.FailWrite = true;

        XcodesException error = await Assert.ThrowsAsync<XcodesException>(() =>
            backend.GetReleasesAsync(true, _applications.Path, CancellationToken.None));

        Assert.Contains("Could not save the Apple session", error.Message);
        Assert.Single(await backend.GetReleasesAsync(false, _applications.Path, CancellationToken.None));
    }

    [Fact]
    public async Task CatalogRefreshDoesNotUseAnExternallyChangedAccount()
    {
        var store = new MemoryStore();
        using var account = new AppleAccount(store, new FakeApple());
        int requests = 0;
        var backend = new XcodeNativeBackend(new FakeCatalog(Release("26.4", "17E192")), account,
            () => _archives.Path, Tools(), Tools(), XcodeArchitecture.AppleSilicon)
        {
            FetchAppleDownloads = _ =>
            {
                requests++;
                return Task.FromResult<IReadOnlyList<AppleXcodeDownload>>([]);
            },
        };
        await backend.SignInAsync(Answers(), null, CancellationToken.None);
        store.Items[AppleAccount.StoreItem] = Encoding.UTF8.GetBytes("""{"appleId":"other@example.com"}""");

        XcodesException error = await Assert.ThrowsAsync<XcodesException>(() =>
            backend.GetReleasesAsync(true, _applications.Path, CancellationToken.None));

        Assert.Contains("saved Apple account changed", error.Message);
        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task CachedAppleCatalogIsNotReturnedAfterTheSavedAccountChanges()
    {
        var store = new MemoryStore();
        using var account = new AppleAccount(store, new FakeApple());
        int requests = 0;
        var backend = new XcodeNativeBackend(new FakeCatalog(Release("26.4", "17E192")), account,
            () => _archives.Path, Tools(), Tools(), XcodeArchitecture.AppleSilicon)
        {
            FetchAppleDownloads = _ =>
            {
                requests++;
                return Task.FromResult<IReadOnlyList<AppleXcodeDownload>>(
                    [new("27.0 Beta", new Uri("https://download.developer.apple.com/other/Xcode_27.xip"), null, null)]);
            },
        };
        await backend.SignInAsync(Answers(), null, CancellationToken.None);
        Assert.Equal(2, (await backend.GetReleasesAsync(true, _applications.Path, CancellationToken.None)).Count);
        store.Items[AppleAccount.StoreItem] = Encoding.UTF8.GetBytes("""{"appleId":"other@example.com"}""");

        XcodesException error = await Assert.ThrowsAsync<XcodesException>(() =>
            backend.GetReleasesAsync(false, _applications.Path, CancellationToken.None));

        Assert.Contains("saved Apple account changed", error.Message);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task InstallDoesNotDownloadWhenTheSavedAccountChangesAfterSessionValidation()
    {
        var store = new MemoryStore();
        var apple = new FakeApple();
        using var account = new AppleAccount(store, apple);
        bool downloaded = false;
        var backend = new XcodeNativeBackend(new FakeCatalog(Release("26.4", "17E192")), account,
            () => _archives.Path, Tools(), Tools(), XcodeArchitecture.AppleSilicon)
        {
            Download = (_, _, _, _) =>
            {
                downloaded = true;
                throw new XcodesException("Unexpected download.");
            },
        };
        await backend.SignInAsync(Answers(), null, CancellationToken.None);
        void ChangeAccountAfterValidation(XcodesOutputLine line)
        {
            if (line.Text.StartsWith("Signed in as ", StringComparison.Ordinal))
                store.Items[AppleAccount.StoreItem] = Encoding.UTF8.GetBytes("""{"appleId":"other@example.com"}""");
        }

        XcodesException error = await Assert.ThrowsAsync<XcodesException>(() =>
            backend.InstallAsync(new XcodeRelease("26.4", "17E192", XcodeArchitecture.Universal, false, false),
                new XcodeInstallOptions(InstallDirectory: _applications.Path), Answers(), ChangeAccountAfterValidation, CancellationToken.None));

        Assert.Contains("saved Apple account changed", error.Message);
        Assert.False(downloaded);
    }

    [Fact]
    public async Task ExpiringAndRestoringTheSameAccountDiscardsItsAppleCatalog()
    {
        var apple = new FakeApple();
        using var account = new AppleAccount(new MemoryStore(), apple);
        var backend = new XcodeNativeBackend(new FakeCatalog(Release("26.4", "17E192")), account,
            () => _archives.Path, Tools(), Tools(), XcodeArchitecture.AppleSilicon)
        {
            FetchAppleDownloads = _ => Task.FromResult<IReadOnlyList<AppleXcodeDownload>>(
                [new("27.0 Beta", new Uri("https://download.developer.apple.com/other/Xcode_27.xip"), null, null)]),
        };
        await backend.SignInAsync(Answers(), null, CancellationToken.None);
        Assert.Contains(await backend.GetReleasesAsync(true, _applications.Path, CancellationToken.None), release => release.IsIdentityUnverified);

        apple.SessionStatus = 401;
        await backend.RefreshAccountAsync(CancellationToken.None);
        apple.SessionStatus = 200;
        await backend.RefreshAccountAsync(CancellationToken.None);

        Assert.Single(await backend.GetReleasesAsync(false, _applications.Path, CancellationToken.None));
    }

    [Fact]
    public async Task CatalogRefreshWaitsForAnInProgressSignIn()
    {
        using var account = new AppleAccount(new MemoryStore(), new FakeApple());
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int appleRequests = 0;
        var backend = new XcodeNativeBackend(new FakeCatalog(Release("26.4", "17E192")), account,
            () => _archives.Path, Tools(), Tools(), XcodeArchitecture.AppleSilicon)
        {
            FetchAppleDownloads = _ =>
            {
                Interlocked.Increment(ref appleRequests);
                return Task.FromResult<IReadOnlyList<AppleXcodeDownload>>([]);
            },
        };
        await backend.SignInAsync(Answers(), null, CancellationToken.None);
        XcodesPromptHandler answers = Answers();
        Task<XcodeAccountStatus> signingIn = backend.SignInAsync(async (prompt, cancellation) =>
        {
            started.TrySetResult();
            await reply.Task.WaitAsync(cancellation);
            return await answers(prompt, cancellation);
        }, null, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task<IReadOnlyList<XcodeRelease>> loading = backend.GetReleasesAsync(true, _applications.Path, CancellationToken.None);
        Assert.False(loading.IsCompleted);
        Assert.Equal(0, Volatile.Read(ref appleRequests));
        reply.SetResult();

        await signingIn;
        Assert.Single(await loading);
    }

    [Fact]
    public async Task SignOutWaitsForADownloadAndClearsItsCookiesBeforeAnotherSignIn()
    {
        using var account = new AppleAccount(new MemoryStore(), new FakeApple());
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new XcodeNativeBackend(new FakeCatalog(Release("26.4", "17E192")), account,
            () => _archives.Path, Tools(), Tools(), XcodeArchitecture.AppleSilicon)
        {
            Download = async (_, _, _, cancellation) =>
            {
                started.TrySetResult();
                await reply.Task.WaitAsync(cancellation);
                account.Cookies.Add(new Cookie("old-download", "synthetic", "/", ".apple.com"));
                throw new XcodesException("The test download stopped.");
            },
        };
        await backend.SignInAsync(Answers(), null, CancellationToken.None);
        Task installation = backend.InstallAsync(new XcodeRelease("26.4", "17E192", XcodeArchitecture.Universal, false, false),
            new XcodeInstallOptions(InstallDirectory: _applications.Path), Answers(), null, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task signingOut = backend.SignOutAsync(CancellationToken.None);
        Assert.False(signingOut.IsCompleted);
        reply.SetResult();
        await Assert.ThrowsAsync<XcodesException>(() => installation);
        await signingOut;
        await backend.SignInAsync(Answers(), null, CancellationToken.None);

        Assert.DoesNotContain(account.Cookies.GetAllCookies().Cast<Cookie>(), c => c.Name == "old-download" && !c.Expired);
    }

    [Fact]
    public async Task WaitingToSignOutCanBeCancelledWithoutStoppingTheDownload()
    {
        using var account = new AppleAccount(new MemoryStore(), new FakeApple());
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new XcodeNativeBackend(new FakeCatalog(Release("26.4", "17E192")), account,
            () => _archives.Path, Tools(), Tools(), XcodeArchitecture.AppleSilicon)
        {
            Download = async (_, _, _, cancellation) =>
            {
                started.TrySetResult();
                await reply.Task.WaitAsync(cancellation);
                throw new XcodesException("The test download stopped.");
            },
        };
        await backend.SignInAsync(Answers(), null, CancellationToken.None);
        Task installation = backend.InstallAsync(new XcodeRelease("26.4", "17E192", XcodeArchitecture.Universal, false, false),
            new XcodeInstallOptions(InstallDirectory: _applications.Path), Answers(), null, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();

        Task signingOut = backend.SignOutAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => signingOut);
        Assert.True(account.IsSignedIn);
        Assert.False(installation.IsCompleted);
        reply.SetResult();
        await Assert.ThrowsAsync<XcodesException>(() => installation);
    }

    [Fact]
    public async Task InstallKeepsTheArchiveLockedDuringExpansionButReleasesTheAccount()
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var installationCancellation = CancellationTokenSource.CreateLinkedTokenSource(limit.Token);
        using var account = new AppleAccount(new MemoryStore(), new FakeApple());
        XcodeCatalogRelease release = Release("26.4", "17E192");
        string cache = XcodeArchiveDownloader.CacheDirectory(_archives.Path, release.Download!);
        Directory.CreateDirectory(cache);
        string archive = Path.Combine(cache, Path.GetFileName(release.Download!.AbsolutePath));
        await File.WriteAllBytesAsync(archive, [1, 2, 3], limit.Token);
        var expanding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishExpansion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        XcodeToolRunner slow = async (_, _, cancellation) =>
        {
            expanding.TrySetResult();
            await finishExpansion.Task.WaitAsync(cancellation);
            return new XcodesResult(1, "", "stopped by the test");
        };
        var backend = new XcodeNativeBackend(new FakeCatalog(release), account, () => _archives.Path,
            Tools(), slow, XcodeArchitecture.AppleSilicon);
        await backend.SignInAsync(Answers(), null, limit.Token);
        Task installing = backend.InstallAsync(new XcodeRelease("26.4", "17E192", XcodeArchitecture.Universal, false, false),
            new XcodeInstallOptions(InstallDirectory: _applications.Path), Answers(), null, installationCancellation.Token);
        await expanding.Task.WaitAsync(limit.Token);

        try
        {
            using var client = new HttpClient(new FakeApple());
            var other = new XcodeArchiveDownloader(client, () => _archives.Path);
            XcodesException error = await Assert.ThrowsAsync<XcodesException>(() =>
                other.DownloadAsync(release.Download!, null, _ => { }, limit.Token));
            Assert.Contains("locked", error.Message);

            await backend.SignOutAsync(limit.Token);
            Assert.False(account.IsSignedIn);
            Assert.False(installing.IsCompleted);

            installationCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => installing);
            Assert.Equal(archive, await other.DownloadAsync(release.Download!, null, _ => { }, limit.Token));
        }
        finally
        {
            finishExpansion.TrySetResult();
            if (!installing.IsCompleted)
                await Assert.ThrowsAsync<XcodesException>(() => installing);
        }
    }

    [Fact]
    public async Task FailedAppleRefreshCanShowThePublicListWithoutRetryingTheAuthenticatedRequest()
    {
        using var account = new AppleAccount(new MemoryStore(), new FakeApple());
        int appleRequests = 0;
        var backend = new XcodeNativeBackend(new FakeCatalog(Release("26.4", "17E192")), account,
            () => _archives.Path, Tools(), Tools(), XcodeArchitecture.AppleSilicon)
        {
            FetchAppleDownloads = _ =>
            {
                appleRequests++;
                throw new XcodesException("Apple returned HTTP 403.") { IsAccessFailure = true };
            },
        };
        await backend.SignInAsync(Answers(), null, CancellationToken.None);

        XcodesException error = await Assert.ThrowsAsync<XcodesException>(() =>
            backend.GetReleasesAsync(true, _applications.Path, CancellationToken.None));
        Assert.True(error.IsAccessFailure);
        Assert.Single(await backend.GetReleasesAsync(false, _applications.Path, CancellationToken.None));
        Assert.Equal(1, appleRequests);
    }

    [Fact]
    public async Task DownloadReauthenticatesOnceWhenAdcSessionExpiredWhileOlympusRemainedValid()
    {
        byte[] archiveBytes = "synthetic-xcode-archive"u8.ToArray();
        int authCalls = 0;
        var apple = new FakeApple
        {
            ArchiveBytes = archiveBytes,
            ArchiveRefuseOnce = true,
            DownloadListResponse = () => ++authCalls == 1
                ? """{"resultCode":1100,"resultString":"Your session has expired. Please log in."}"""
                : """{"resultCode":0,"downloads":[]}""",
        };
        using var account = new AppleAccount(new MemoryStore(), apple);
        XcodeCatalogRelease release = Release("26.4", "17E192") with
        {
            Sha1 = Convert.ToHexStringLower(SHA1.HashData(archiveBytes)),
        };
        var backend = new XcodeNativeBackend(new FakeCatalog(release), account, () => _archives.Path,
            Tools(), Tools(), XcodeArchitecture.AppleSilicon);
        await backend.SignInAsync(Answers(), null, CancellationToken.None);
        apple.Requests.Clear();
        List<XcodesPrompt> prompts = [];
        List<string> lines = [];

        string downloaded = await backend.DownloadAsync(
            new XcodeRelease("26.4", "17E192", XcodeArchitecture.Universal, false, false),
            Answers(prompts), line => lines.Add(line.Text), CancellationToken.None);

        Assert.Equal(archiveBytes, await File.ReadAllBytesAsync(downloaded));
        Assert.Empty(prompts);
        Assert.Equal(2, authCalls);
        Assert.Contains(apple.Paths, path => path == "/appleauth/auth/signin/complete");
        Assert.Contains("Refreshing the Apple developer session", lines);
    }

    [Fact]
    public void NamesAnInstalledXcodeAfterItsReleaseElseAfterItsBundle()
    {
        XcodeCatalogRelease[] catalog = [Release("26.5 Beta 2", "17F5035f")];

        Assert.Equal("26.5 Beta 2", XcodeNativeBackend.Label(new("/A.app", "26.5", "17F5035f", true, XcodeArchitecture.Universal), catalog));
        Assert.Equal("26.6 Beta", XcodeNativeBackend.Label(new("/B.app", "26.6", "17G5001a", true, XcodeArchitecture.Universal), catalog));
        Assert.Equal("26.4", XcodeNativeBackend.Label(new("/C.app", "26.4", "17E192", false, XcodeArchitecture.Universal), null));
    }

    [Fact]
    public async Task ReadsInstalledXcodesWithTheSavedListAndWithoutDownloading()
    {
        string selected = FakeXcodeBundle.Create(_applications.Path, "Xcode-beta.app", "26.5", "17F5035f", beta: true);
        FakeXcodeBundle.Create(_applications.Path, "Xcode.app", "26.4", "17E192");
        var catalog = new FakeCatalog { Saved = new([Release("26.5 Beta 2", "17F5035f")], DateTime.UtcNow) };

        IReadOnlyList<InstalledXcode> installed = await Backend(catalog, selected: selected + "/Contents/Developer")
            .GetInstalledAsync(_applications.Path, CancellationToken.None);

        Assert.Equal(
            [("26.5 Beta 2", true, selected), ("26.4", false, Path.Combine(_applications.Path, "Xcode.app"))],
            installed.Select(x => (x.Version, x.IsSelected, x.Path)));
        Assert.Equal(0, catalog.Downloads);
    }

    [Fact]
    public async Task ReleasesUseTheSavedListUnlessARefreshIsAsked()
    {
        var catalog = new FakeCatalog(Release("26.4", "17E192"))
        {
            Saved = new([Release("26.3", "17C52")], DateTime.UtcNow),
        };
        XcodeNativeBackend backend = Backend(catalog);

        Assert.Equal("26.3", Assert.Single(await backend.GetReleasesAsync(false, _applications.Path, CancellationToken.None)).Version);
        Assert.Equal(0, catalog.Downloads);
        Assert.Equal("26.4", Assert.Single(await backend.GetReleasesAsync(true, _applications.Path, CancellationToken.None)).Version);
        Assert.Equal(1, catalog.Downloads);

        catalog.Saved = null;
        Assert.Equal("26.4", Assert.Single(await backend.GetReleasesAsync(false, _applications.Path, CancellationToken.None)).Version);
    }

    [Fact]
    public async Task InstallRefusesAnExistingDestinationBeforeSigningIn()
    {
        Directory.CreateDirectory(Path.Combine(_applications.Path, "Xcode-26.4.app"));
        var asked = new List<XcodesPrompt>();

        XcodesException error = await Assert.ThrowsAsync<XcodesException>(() => Backend(new FakeCatalog(Release("26.4", "17E192")))
            .InstallAsync(new XcodeRelease("26.4", "17E192", XcodeArchitecture.Universal, false, false),
                new XcodeInstallOptions(InstallDirectory: _applications.Path), Answers(asked), null, CancellationToken.None));

        Assert.Contains("already exists", error.Message);
        Assert.Empty(asked);
    }

    [Fact]
    public async Task InstallRefusesAReleaseNoLongerOffered()
    {
        XcodesException error = await Assert.ThrowsAsync<XcodesException>(() => Backend(new FakeCatalog(Release("26.4", "17E192")))
            .InstallAsync(new XcodeRelease("26.4", "17E999", XcodeArchitecture.Universal, false, false),
                new XcodeInstallOptions(InstallDirectory: _applications.Path), Answers(), null, CancellationToken.None));

        Assert.Contains("no longer offered", error.Message);
    }

    [Fact]
    public async Task InstallRefusesAnAppleOnlyReleaseBeforeSigningIn()
    {
        var asked = new List<XcodesPrompt>();
        XcodeRelease unknown = new("27.0 Beta", null, XcodeArchitecture.Unspecified, false, false) { IsIdentityUnverified = true };

        XcodesException error = await Assert.ThrowsAsync<XcodesException>(() => Backend(new FakeCatalog())
            .InstallAsync(unknown, new XcodeInstallOptions(InstallDirectory: _applications.Path), Answers(asked), null, CancellationToken.None));

        Assert.Equal(XcodesService.UnknownBuildMessage, error.Message);
        Assert.Empty(asked);
    }

    [Fact]
    public async Task InstallSignsInDownloadsTheExactBuildAndReportsProgress()
    {
        using var account = new AppleAccount(new MemoryStore(), new FakeApple());
        var output = new List<XcodesOutputLine>();
        var asked = new List<XcodesPrompt>();
        (Uri Address, string? Sha1)? downloaded = null;
        XcodeCatalogRelease wanted = Release("26.4", "17E200") with { Sha1 = "abc" };
        var backend = new XcodeNativeBackend(new FakeCatalog(Release("26.4", "17E192"), wanted), account,
            () => _archives.Path, Tools(), Tools(), XcodeArchitecture.AppleSilicon)
        {
            Download = (address, sha1, progress, _) =>
            {
                downloaded = (address, sha1);
                progress(null);
                progress(0.5);
                progress(0.504);
                progress(1);
                return Task.FromResult(Path.Combine(_archives.Path, "Xcode_26.4.xip"));
            },
        };

        // The archive cannot be expanded here: the test's tools refuse everything.
        XcodesException error = await Assert.ThrowsAsync<XcodesException>(() => backend.InstallAsync(
            new XcodeRelease("26.4", "17E200", XcodeArchitecture.Universal, false, false),
            new XcodeInstallOptions(InstallDirectory: _applications.Path), Answers(asked), output.Add, CancellationToken.None));

        Assert.Equal((wanted.Download!, "abc"), downloaded);
        Assert.Equal([XcodesPromptKind.AppleId, XcodesPromptKind.Password], asked.Select(p => p.Kind));
        Assert.Equal("test@example.com", account.AppleId);
        Assert.Equal(
            [
                ("Signing in to Apple", false),
                ("(1/6) Downloading Xcode 26.4", false),
                ("(1/6) Downloading Xcode 26.4: 50%", false),
                ("(1/6) Downloading Xcode 26.4: 100%", true),
                ("(2/6) Unarchiving Xcode (This can take a while)", false),
            ],
            output.Where(line => !line.Text.StartsWith("Signed in as", StringComparison.Ordinal)).Select(line => (line.Text, line.ReplacesPrevious)));
        Assert.Contains(_tools, call => call.Contains("/usr/bin/xip", StringComparison.Ordinal));
        Assert.DoesNotContain("secret-password", string.Join('\n', output.Select(line => line.Text)));
    }

    [Fact]
    public async Task DownloadOnlyUsesTheRequestedBuildAndArchitectureWithoutRunningInstallerTools()
    {
        using var account = new AppleAccount(new MemoryStore(), new FakeApple());
        var output = new List<XcodesOutputLine>();
        var asked = new List<XcodesPrompt>();
        (Uri Address, string? Sha1)? downloaded = null;
        XcodeCatalogRelease wanted = Release("26.4", "17E200", XcodeArchitecture.Universal) with { Sha1 = "abc" };
        var backend = new XcodeNativeBackend(
            new FakeCatalog(Release("26.4", "17E200", XcodeArchitecture.AppleSilicon), wanted), account,
            () => _archives.Path, Tools(), Tools(), XcodeArchitecture.AppleSilicon)
        {
            Download = (address, sha1, progress, _) =>
            {
                downloaded = (address, sha1);
                progress(0.5);
                progress(1);
                return Task.FromResult(Path.Combine(_archives.Path, "Xcode_26.4.xip"));
            },
        };

        string path = await backend.DownloadAsync(
            new XcodeRelease("26.4", "17E200", XcodeArchitecture.Universal, false, false),
            Answers(asked), output.Add, CancellationToken.None);

        Assert.Equal(Path.Combine(_archives.Path, "Xcode_26.4.xip"), path);
        Assert.Equal((wanted.Download!, "abc"), downloaded);
        Assert.Equal([XcodesPromptKind.AppleId, XcodesPromptKind.Password], asked.Select(p => p.Kind));
        Assert.Contains(output, line => line.Text == "(1/1) Downloading Xcode 26.4: 50%");
        Assert.Contains(output, line => line.Text == "(1/1) Downloading Xcode 26.4: 100%" && line.ReplacesPrevious);
        Assert.Empty(_tools);
        Assert.DoesNotContain("secret-password", string.Join('\n', output.Select(line => line.Text)));
    }

    [Fact]
    public async Task DownloadOnlyReusesTheVerifiedCacheAndReleasesItsLock()
    {
        using var account = new AppleAccount(new MemoryStore(), new FakeApple());
        byte[] content = [1, 2, 3];
        string sha1 = Convert.ToHexStringLower(SHA1.HashData(content));
        XcodeCatalogRelease release = Release("26.4", "17E192") with { Sha1 = sha1 };
        string cache = XcodeArchiveDownloader.CacheDirectory(_archives.Path, release.Download!, sha1);
        Directory.CreateDirectory(cache);
        string archive = Path.Combine(cache, Path.GetFileName(release.Download!.AbsolutePath));
        await File.WriteAllBytesAsync(archive, content);
        var backend = new XcodeNativeBackend(new FakeCatalog(release), account, () => _archives.Path,
            Tools(), Tools(), XcodeArchitecture.AppleSilicon);

        string path = await backend.DownloadAsync(
            new XcodeRelease("26.4", "17E192", XcodeArchitecture.Universal, false, false),
            Answers(), null, CancellationToken.None);

        Assert.Equal(archive, path);
        Assert.Equal(content, await File.ReadAllBytesAsync(path));
        Assert.Empty(_tools);
        using var client = new HttpClient(new FakeApple());
        var other = new XcodeArchiveDownloader(client, () => _archives.Path);
        Assert.Equal(archive, await other.DownloadAsync(release.Download!, sha1, _ => { }, CancellationToken.None));
    }

    [Fact]
    public async Task SignOutWaitsForADownloadOnlyOperationAndClearsItsCookies()
    {
        using var account = new AppleAccount(new MemoryStore(), new FakeApple());
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string path = Path.Combine(_archives.Path, "Xcode.xip");
        var backend = new XcodeNativeBackend(new FakeCatalog(Release("26.4", "17E192")), account,
            () => _archives.Path, Tools(), Tools(), XcodeArchitecture.AppleSilicon)
        {
            Download = async (_, _, _, cancellation) =>
            {
                started.TrySetResult();
                await reply.Task.WaitAsync(cancellation);
                account.Cookies.Add(new Cookie("old-download", "synthetic", "/", ".apple.com"));
                return path;
            },
        };
        await backend.SignInAsync(Answers(), null, CancellationToken.None);
        Task<string> downloading = backend.DownloadAsync(
            new XcodeRelease("26.4", "17E192", XcodeArchitecture.Universal, false, false),
            Answers(), null, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task signingOut = backend.SignOutAsync(CancellationToken.None);
        Assert.False(signingOut.IsCompleted);
        reply.TrySetResult();

        Assert.Equal(path, await downloading);
        await signingOut;
        Assert.Null(account.AppleId);
        Assert.DoesNotContain(account.Cookies.GetAllCookies().Cast<Cookie>(), c => !c.Expired);
        Assert.Empty(_tools);
    }

    [Fact]
    public async Task DownloadOnlyDoesNotOverwriteAnExternallyChangedAccount()
    {
        var store = new MemoryStore();
        using var account = new AppleAccount(store, new FakeApple());
        var backend = new XcodeNativeBackend(new FakeCatalog(Release("26.4", "17E192")), account,
            () => _archives.Path, Tools(), Tools(), XcodeArchitecture.AppleSilicon)
        {
            Download = (_, _, _, _) =>
            {
                store.Items[AppleAccount.StoreItem] = Encoding.UTF8.GetBytes("""{"appleId":"other@example.com"}""");
                return Task.FromResult(Path.Combine(_archives.Path, "Xcode.xip"));
            },
        };
        await backend.SignInAsync(Answers(), null, CancellationToken.None);

        XcodesException error = await Assert.ThrowsAsync<XcodesException>(() => backend.DownloadAsync(
            new XcodeRelease("26.4", "17E192", XcodeArchitecture.Universal, false, false),
            Answers(), null, CancellationToken.None));

        Assert.Contains("Could not save the Apple session", error.Message);
        account.LoadSaved();
        Assert.Equal("other@example.com", account.AppleId);
        Assert.Empty(_tools);
    }

    [Theory]
    [InlineData(XcodeArchitecture.AppleSilicon, null)]
    [InlineData(XcodeArchitecture.AppleSilicon, "17E192")]
    [InlineData(XcodeArchitecture.Intel, null)]
    [InlineData(XcodeArchitecture.Intel, "17E192")]
    public async Task InstallWithoutAnArchitecturePrefersTheNativeVariant(XcodeArchitecture machine, string? build)
    {
        XcodeCatalogRelease universal = Release("26.4", "17E192") with
        {
            Download = new Uri("https://download.developer.apple.com/Xcode_Universal.xip"),
        };
        XcodeCatalogRelease native = Release("26.4", "17E192", machine) with
        {
            Download = new Uri("https://download.developer.apple.com/Xcode_Native.xip"),
        };
        Uri? selected = await SelectedDownloadAsync(
            new XcodeRelease("26.4", build, XcodeArchitecture.Unspecified, false, false), machine, universal, native);
        Assert.Equal(native.Download, selected);
    }

    [Fact]
    public async Task InstallWithoutAnArchitecturePrefersUniversalOverUnknown()
    {
        XcodeCatalogRelease unknown = Release("26.4", "17E192", XcodeArchitecture.Unspecified) with
        {
            Download = new Uri("https://download.developer.apple.com/Xcode_Unknown.xip"),
        };
        XcodeCatalogRelease universal = Release("26.4", "17E192") with
        {
            Download = new Uri("https://download.developer.apple.com/Xcode_Universal.xip"),
        };
        Uri? selected = await SelectedDownloadAsync(
            new XcodeRelease("26.4", "17E192", XcodeArchitecture.Unspecified, false, false),
            XcodeArchitecture.AppleSilicon, unknown, universal);
        Assert.Equal(universal.Download, selected);
    }

    [Fact]
    public async Task InstallWithAnUnspecifiedArchitectureDoesNotChooseAnotherBuild()
    {
        XcodeCatalogRelease otherBuild = Release("26.4", "17E200", XcodeArchitecture.AppleSilicon) with
        {
            Download = new Uri("https://download.developer.apple.com/Xcode_Other.xip"),
        };
        XcodeCatalogRelease requested = Release("26.4", "17E192") with
        {
            Download = new Uri("https://download.developer.apple.com/Xcode_Requested.xip"),
        };
        Uri? selected = await SelectedDownloadAsync(
            new XcodeRelease("26.4", "17E192", XcodeArchitecture.Unspecified, false, false),
            XcodeArchitecture.AppleSilicon, otherBuild, requested);
        Assert.Equal(requested.Download, selected);
    }

    [Fact]
    public async Task AnExplicitUniversalArchitectureIsNotReplacedByANativeVariant()
    {
        XcodeCatalogRelease native = Release("26.4", "17E192", XcodeArchitecture.AppleSilicon) with
        {
            Download = new Uri("https://download.developer.apple.com/Xcode_Native.xip"),
        };
        XcodeCatalogRelease universal = Release("26.4", "17E192") with
        {
            Download = new Uri("https://download.developer.apple.com/Xcode_Universal.xip"),
        };
        Uri? selected = await SelectedDownloadAsync(
            new XcodeRelease("26.4", "17E192", XcodeArchitecture.Universal, false, false),
            XcodeArchitecture.AppleSilicon, native, universal);
        Assert.Equal(universal.Download, selected);
    }

    [Fact]
    public async Task CancelledSignInStopsTheInstallBeforeDownloading()
    {
        bool downloaded = false;
        var backend = new XcodeNativeBackend(new FakeCatalog(Release("26.4", "17E192")),
            new AppleAccount(new MemoryStore(), new FakeApple()), () => _archives.Path, Tools(), Tools(), XcodeArchitecture.AppleSilicon)
        {
            Download = (_, _, _, _) =>
            {
                downloaded = true;
                return Task.FromResult("");
            },
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => backend.InstallAsync(
            new XcodeRelease("26.4", null, XcodeArchitecture.Unspecified, false, false),
            new XcodeInstallOptions(InstallDirectory: _applications.Path), (_, _) => Task.FromResult<string?>(null), null,
            CancellationToken.None));

        Assert.False(downloaded);
    }

    [Fact]
    public async Task AnInstallWithoutPromptsDoesNotSignInAgainAfterTheSessionExpires()
    {
        var apple = new FakeApple();
        using var account = new AppleAccount(new MemoryStore(), apple);
        bool downloaded = false;
        var backend = new XcodeNativeBackend(new FakeCatalog(Release("26.4", "17E192")),
            account, () => _archives.Path, Tools(), Tools(), XcodeArchitecture.AppleSilicon)
        {
            Download = (_, _, _, _) =>
            {
                downloaded = true;
                return Task.FromResult("");
            },
        };
        await backend.SignInAsync(Answers(), null, CancellationToken.None);
        int signIns = apple.Paths.Count(path => path == "/appleauth/auth/signin/complete");
        apple.SessionStatus = 401;

        XcodesException error = await Assert.ThrowsAsync<XcodesException>(() => backend.InstallAsync(
            new XcodeRelease("26.4", "17E192", XcodeArchitecture.Universal, false, false),
            new XcodeInstallOptions(InstallDirectory: _applications.Path), null, null, CancellationToken.None));

        Assert.True(error.IsAccessFailure);
        Assert.Contains("session is no longer valid", error.Message);
        Assert.False(downloaded);
        Assert.Equal(signIns, apple.Paths.Count(path => path == "/appleauth/auth/signin/complete"));
    }

    [Theory]
    [InlineData(System.Net.HttpStatusCode.Forbidden, true)]
    [InlineData(System.Net.HttpStatusCode.Unauthorized, true)]
    [InlineData(System.Net.HttpStatusCode.NotFound, false)]
    public async Task RefusedDownloadsAreReportedAsPossibleAccessFailures(System.Net.HttpStatusCode status, bool accessFailure)
    {
        var backend = new XcodeNativeBackend(new FakeCatalog(Release("26.4", "17E192")),
            new AppleAccount(new MemoryStore(), new FakeApple()), () => _archives.Path, Tools(), Tools(), XcodeArchitecture.AppleSilicon)
        {
            Download = (_, _, _, _) => throw new HttpRequestException("refused", null, status),
        };

        XcodesException error = await Assert.ThrowsAsync<XcodesException>(() => backend.InstallAsync(
            new XcodeRelease("26.4", null, XcodeArchitecture.Unspecified, false, false),
            new XcodeInstallOptions(InstallDirectory: _applications.Path), Answers(), null, CancellationToken.None));

        Assert.Equal($"The Xcode download failed with HTTP {(int)status}.", error.Message);
        Assert.Equal(accessFailure, error.IsAccessFailure);
    }

    [Fact]
    public async Task FailedCookieSaveDoesNotHideTheDownloadFailure()
    {
        var store = new MemoryStore();
        using var account = new AppleAccount(store, new FakeApple());
        var backend = new XcodeNativeBackend(new FakeCatalog(Release("26.4", "17E192")),
            account, () => _archives.Path, Tools(), Tools(), XcodeArchitecture.AppleSilicon)
        {
            Download = (_, _, _, _) =>
            {
                store.FailWrite = true;
                throw new HttpRequestException("refused", null, System.Net.HttpStatusCode.Forbidden);
            },
        };

        XcodesException error = await Assert.ThrowsAsync<XcodesException>(() => backend.InstallAsync(
            new XcodeRelease("26.4", "17E192", XcodeArchitecture.Universal, false, false),
            new XcodeInstallOptions(InstallDirectory: _applications.Path), Answers(), null, CancellationToken.None));

        Assert.Equal("The Xcode download failed with HTTP 403.", error.Message);
        Assert.True(error.IsAccessFailure);
    }

    [Fact]
    public async Task FailedCookieSaveDoesNotHideCancellation()
    {
        var store = new MemoryStore();
        using var account = new AppleAccount(store, new FakeApple());
        var backend = new XcodeNativeBackend(new FakeCatalog(Release("26.4", "17E192")),
            account, () => _archives.Path, Tools(), Tools(), XcodeArchitecture.AppleSilicon)
        {
            Download = (_, _, _, _) =>
            {
                store.FailWrite = true;
                throw new OperationCanceledException();
            },
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => backend.InstallAsync(
            new XcodeRelease("26.4", "17E192", XcodeArchitecture.Universal, false, false),
            new XcodeInstallOptions(InstallDirectory: _applications.Path), Answers(), null, CancellationToken.None));
    }

    [Fact]
    public async Task SuccessfulDownloadReportsACookieSaveFailureBeforeInstalling()
    {
        var store = new MemoryStore();
        using var account = new AppleAccount(store, new FakeApple());
        var backend = new XcodeNativeBackend(new FakeCatalog(Release("26.4", "17E192")),
            account, () => _archives.Path, Tools(), Tools(), XcodeArchitecture.AppleSilicon)
        {
            Download = (_, _, _, _) =>
            {
                store.FailWrite = true;
                return Task.FromResult(Path.Combine(_archives.Path, "Xcode_26.4.xip"));
            },
        };

        XcodesException error = await Assert.ThrowsAsync<XcodesException>(() => backend.InstallAsync(
            new XcodeRelease("26.4", "17E192", XcodeArchitecture.Universal, false, false),
            new XcodeInstallOptions(InstallDirectory: _applications.Path), Answers(), null, CancellationToken.None));

        Assert.Contains("Could not save the Apple session", error.Message);
        Assert.DoesNotContain(_tools, call => call.Contains("/usr/bin/xip", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CookieSaveFailureReleasesTheDownloadedArchiveLock()
    {
        var store = new MemoryStore();
        using var account = new AppleAccount(store, new FakeApple());
        XcodeCatalogRelease release = Release("26.4", "17E192");
        string cache = XcodeArchiveDownloader.CacheDirectory(_archives.Path, release.Download!);
        Directory.CreateDirectory(cache);
        string archive = Path.Combine(cache, Path.GetFileName(release.Download!.AbsolutePath));
        await File.WriteAllBytesAsync(archive, [1, 2, 3]);
        var backend = new XcodeNativeBackend(new FakeCatalog(release), account, () => _archives.Path,
            Tools(), Tools(), XcodeArchitecture.AppleSilicon);
        await backend.SignInAsync(Answers(), null, CancellationToken.None);

        XcodesException error = await Assert.ThrowsAsync<XcodesException>(() => backend.InstallAsync(
            new XcodeRelease("26.4", "17E192", XcodeArchitecture.Universal, false, false),
            new XcodeInstallOptions(InstallDirectory: _applications.Path), Answers(),
            line =>
            {
                if (line.Text.Contains("100%", StringComparison.Ordinal))
                    store.FailWrite = true;
            }, CancellationToken.None));

        Assert.Contains("Could not save the Apple session", error.Message);
        Assert.DoesNotContain(_tools, call => call.Contains("/usr/bin/xip", StringComparison.Ordinal));
        using var client = new HttpClient(new FakeApple());
        var other = new XcodeArchiveDownloader(client, () => _archives.Path);
        Assert.Equal(archive, await other.DownloadAsync(release.Download!, null, _ => { }, CancellationToken.None));
    }

    [Fact]
    public async Task AccountCanBeSignedInAndOut()
    {
        var store = new MemoryStore();
        XcodeNativeBackend backend = Backend(new FakeCatalog(), new AppleAccount(store, new FakeApple()));

        Assert.Equal(XcodeAccountStatus.None.AppleId, backend.GetAccount().AppleId);
        XcodeAccountStatus signedIn = await backend.SignInAsync(Answers(), null, CancellationToken.None);
        Assert.Equal("test@example.com", signedIn.AppleId);
        Assert.True(signedIn.IsSignedIn);
        Assert.True(signedIn.HasSavedPassword);

        await backend.SignOutAsync(CancellationToken.None);
        XcodeAccountStatus signedOut = backend.GetAccount();
        Assert.Null(signedOut.AppleId);
        Assert.False(signedOut.IsSignedIn);
        Assert.Empty(store.Items);
    }

    [Fact]
    public async Task CustomArchiveDirectoryStoresDownloadsOutsideTheDefaultCache()
    {
        using var customArchives = new TemporaryDirectory();
        XcodeCatalogRelease release = Release("26.4", "17E192");
        using var account = new AppleAccount(new MemoryStore(), new FakeApple { ArchiveBytes = [1, 2, 3, 4] });
        var backend = new XcodeNativeBackend(new FakeCatalog(release), account, () => _archives.Path,
            Tools(), Tools(), XcodeArchitecture.AppleSilicon);

        string downloaded = await backend.DownloadAsync(
            new XcodeRelease("26.4", "17E192", XcodeArchitecture.Universal, false, false),
            Answers(), null, CancellationToken.None, customArchives.Path);

        Assert.StartsWith(customArchives.Path, downloaded, StringComparison.Ordinal);
        Assert.Equal([1, 2, 3, 4], await File.ReadAllBytesAsync(downloaded));
        Assert.Empty(Directory.Exists(_archives.Path) ? Directory.GetFileSystemEntries(_archives.Path) : []);
    }

    [Fact]
    public async Task MissingCustomArchiveDirectoryFailsBeforePromptingForSignIn()
    {
        string missing = Path.Combine(_archives.Path, "unmounted-drive");
        XcodeCatalogRelease release = Release("26.4", "17E192");
        using var account = new AppleAccount(new MemoryStore(), new FakeApple());
        var backend = new XcodeNativeBackend(new FakeCatalog(release), account, () => _archives.Path,
            Tools(), Tools(), XcodeArchitecture.AppleSilicon);
        int prompts = 0;
        Task<string?> FailIfPrompted(XcodesPrompt _, CancellationToken __)
        {
            prompts++;
            throw new InvalidOperationException("Sign-in must not run when the custom archive directory is missing.");
        }

        XcodesException installError = await Assert.ThrowsAsync<XcodesException>(() => backend.InstallAsync(
            new XcodeRelease("26.4", "17E192", XcodeArchitecture.Universal, false, false),
            new XcodeInstallOptions(InstallDirectory: _applications.Path, ArchiveDirectory: missing),
            FailIfPrompted, null, CancellationToken.None));
        XcodesException downloadError = await Assert.ThrowsAsync<XcodesException>(() => backend.DownloadAsync(
            new XcodeRelease("26.4", "17E192", XcodeArchitecture.Universal, false, false),
            FailIfPrompted, null, CancellationToken.None, missing));

        Assert.Equal("The Xcode archive directory does not exist or is inaccessible.", installError.Message);
        Assert.Equal("The Xcode archive directory does not exist or is inaccessible.", downloadError.Message);
        Assert.Equal(0, prompts);
    }
}
