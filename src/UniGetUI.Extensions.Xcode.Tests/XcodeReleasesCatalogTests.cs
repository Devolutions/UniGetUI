using System.Net;
using System.Text;
using System.Text.Json;
using UniGetUI.Extensions.Xcode;
using UniGetUI.Extensions.Xcode.Local;

namespace UniGetUI.Extensions.Xcode.Tests;

public sealed class XcodeReleasesCatalogTests
{
    private const string ValidCatalog = """
        [{
          "version": { "number": "26.4", "build": "17E192", "release": { "release": true } },
          "date": { "year": 2026, "month": 3, "day": 24 },
          "requires": "26.2",
          "links": {
            "download": { "url": "https://download.developer.apple.com/Xcode.xip", "architectures": ["arm64", "x86_64"], "sizeMB": 12500 },
            "notes": { "url": "https://developer.apple.com/documentation/xcode-release-notes" }
          },
          "sdks": { "iOS": [{ "number": "26.4", "build": "23E100" }], "macOS": [{ "number": "26.4" }] },
          "compilers": { "swift": [{ "number": "6.2" }] }
        }]
        """;

    [Fact]
    public void ReadsOnlyMetadataSuppliedByThePublicCatalog()
    {
        XcodeCatalogRelease release = Assert.Single(Parse(ValidCatalog));

        Assert.Equal("26.4", release.Version);
        Assert.Equal("17E192", release.Build);
        Assert.Equal(XcodeArchitecture.Universal, release.Architecture);
        Assert.Equal(new DateOnly(2026, 3, 24), release.ReleaseDate);
        Assert.Equal(new Version(26, 2, 0), release.RequiredMacOS);
        Assert.Equal("https://developer.apple.com/documentation/xcode-release-notes", release.ReleaseNotes?.AbsoluteUri);
        Assert.Equal(["iOS 26.4", "macOS 26.4"], release.SDKs);
        Assert.Equal([new XcodeSdkIdentity("iOS", "26.4", "23E100")], release.SDKIdentities);
        Assert.Equal(["swift 6.2"], release.Compilers);
        Assert.Equal(12500, release.SizeMB);
        Assert.Equal("https://download.developer.apple.com/Xcode.xip", release.Download?.AbsoluteUri);
        Assert.Null(release.Sha1);
    }

    [Fact]
    public void AnUnrecognizedBuildInCatalogJsonIsVisibleButNotInstallable()
    {
        string malformed = ValidCatalog.Replace("17E192", "17E192\\n", StringComparison.Ordinal);
        IReadOnlyList<XcodeCatalogRelease> catalog = Parse(malformed);

        XcodeRelease release = Assert.Single(XcodeNativeBackend.Merge(catalog, [], XcodeArchitecture.AppleSilicon));

        Assert.Null(release.Build);
        Assert.True(release.IsIdentityUnverified);
        Assert.False(release.IsInstalled);
    }

    [Theory]
    [InlineData("https://download.developer.apple.com/Developer_Tools/Xcode_26.4/Xcode_26.4.xip", true)]
    [InlineData("https://download.developer.apple.com/Developer_Tools/Xcode_26/Xcode_26_Apple_silicon.xip.aea", true)]
    [InlineData("http://download.developer.apple.com/Developer_Tools/Xcode_26.4/Xcode_26.4.xip", false)]
    [InlineData("https://download.developer.apple.com.example.com/Xcode.xip", false)]
    [InlineData("https://user@download.developer.apple.com/Xcode.xip", false)]
    [InlineData("https://download.developer.apple.com:8443/Xcode.xip", false)]
    [InlineData("https://download.developer.apple.com/Xcode.xip?redirect=elsewhere", false)]
    [InlineData("https://download.developer.apple.com/Developer_Tools/Xcode_7.3/Xcode_7.3.dmg", false)]
    [InlineData("not a url", false)]
    public void AcceptsOnlyAppleXcodeArchives(string url, bool accepted)
        => Assert.Equal(accepted, XcodeReleasesCatalog.TryDownload(url) is not null);

    [Fact]
    public void ReadsTheChecksumWhenItIsWellFormed()
    {
        string withSha = ValidCatalog.Replace("\"date\"",
            "\"checksums\": { \"sha1\": \"B25E1E5206C835AC0B88DFF66AD291CD432A1877\" }, \"date\"");
        Assert.Equal("b25e1e5206c835ac0b88dff66ad291cd432a1877", Assert.Single(Parse(withSha)).Sha1);
        string badSha = ValidCatalog.Replace("\"date\"", "\"checksums\": { \"sha1\": \"zz\" }, \"date\"");
        Assert.Null(Assert.Single(Parse(badSha)).Sha1);
    }

    [Fact]
    public void ReadsTheLiveCatalogSnapshotWhenOneIsGiven()
    {
        // Set UNIGETUI_XCODE_RELEASES_JSON to a downloaded https://xcodereleases.com/data.json to check it parses.
        if (Environment.GetEnvironmentVariable("UNIGETUI_XCODE_RELEASES_JSON") is not { Length: > 0 } file)
            return;
        IReadOnlyList<XcodeCatalogRelease> releases = XcodeReleasesCatalog.Parse(File.ReadAllBytes(file));
        Assert.Contains(releases, release => release.Download is not null && release.Sha1 is not null);
    }

    [Theory]
    [InlineData("\"beta\":1", "26.4 Beta")]
    [InlineData("\"beta\":2", "26.4 Beta 2")]
    [InlineData("\"rc\":1", "26.4 Release Candidate")]
    [InlineData("\"rc\":2", "26.4 Release Candidate 2")]
    [InlineData("\"gmSeed\":1", "26.4 GM Seed")]
    [InlineData("\"gmSeed\":3", "26.4 GM Seed 3")]
    [InlineData("\"dp\":2", "26.4 DP 2")]
    [InlineData("\"gm\":true", "26.4")]
    [InlineData("\"release\":true", "26.4")]
    public void FormatsStagesLikeXcodes(string stage, string expected)
    {
        string json = """[{"version":{"number":"26.4.0","build":"17E192","release":{ STAGE }}}]"""
            .Replace("STAGE", stage, StringComparison.Ordinal);
        Assert.Equal(expected, Assert.Single(Parse(json)).Version);
    }

    [Fact]
    public void RejectsCatalogsWithUnknownIdentities()
    {
        string json = """
            [
              null,
              {"version":{"number":"26.4","build":"A","release":{"beta":1,"rc":1}}},
              {"version":{"number":"26.4","build":"B","release":{"release":true}},
               "links":{"download":{"architectures":["unknown"]}}}
            ]
            """;

        Assert.Throws<XcodesException>(() => Parse(json));
    }

    [Fact]
    public void DoesNotInventOptionalFieldsOrTrustNonAppleNotes()
    {
        string json = """
            [
              {"version":{"number":"26.4","build":"C","release":{"release":true}},
               "date":{"year":2026,"month":99,"day":3},
               "links":{"notes":{"url":"https://apple.com.attacker.test/notes"}},
               "sdks":{"iOS":null,"macOS":[null,{"number":"26.4"}]}}
            ]
            """;

        XcodeCatalogRelease release = Assert.Single(Parse(json));
        Assert.Equal("C", release.Build);
        Assert.Null(release.ReleaseDate);
        Assert.Null(release.RequiredMacOS);
        Assert.Null(release.ReleaseNotes);
        Assert.Equal(["macOS 26.4"], release.SDKs);
        Assert.Empty(release.SDKIdentities);
    }

    [Fact]
    public void ReadsOnlyCompleteSupportedSDKBuildIdentities()
    {
        string json = """
            [{"version":{"number":"26.4","build":"17E192","release":{"release":true}},
              "sdks":{
                "iOS":[{"number":"26.4.0","build":"23E100"},{"number":"26.4","build":"23E100"},
                       {"number":"26.5"},{"number":"26.6 beta","build":"23F100"}],
                "watchOS":[{"number":"26.4","build":"23S100"}],
                "tvOS":[{"number":"26.4","build":"not-a-build"}],
                "macOS":[{"number":"26.4","build":"23E100"}]
              }}]
            """;

        XcodeCatalogRelease release = Assert.Single(Parse(json));

        Assert.Equal([new XcodeSdkIdentity("iOS", "26.4", "23E100"),
            new XcodeSdkIdentity("watchOS", "26.4", "23S100")], release.SDKIdentities);
    }

    [Fact]
    public void AnEmptyOrMalformedCatalogIsAnError()
    {
        Assert.Throws<XcodesException>(() => Parse("[]"));
        Assert.Throws<JsonException>(() => Parse("{"));
    }

    [Fact]
    public async Task OnlyThePublicCatalogIsRequested()
    {
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ValidCatalog, Encoding.UTF8, "application/json"),
        });
        using var client = new HttpClient(handler);
        var catalog = new XcodeReleasesCatalog(client);

        IReadOnlyList<XcodeCatalogRelease> releases = await catalog.GetAsync(CancellationToken.None);

        Assert.Single(releases);
        Assert.Equal("https://xcodereleases.com/data.json", handler.RequestedUri?.AbsoluteUri);
        Assert.Null(handler.Authorization);
    }

    [Fact]
    public async Task AnHttpFailureIsNotReplacedByAnotherSource()
    {
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => new XcodeReleasesCatalog(client).GetAsync(CancellationToken.None));

        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task TheSizeLimitAlsoAppliesWhenTheDeclaredLengthIsWrong()
    {
        byte[] tooLarge = new byte[8 * 1024 * 1024 + 1];
        var handler = new Handler(_ =>
        {
            var content = new StreamContent(new MemoryStream(tooLarge));
            content.Headers.ContentLength = 1;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<XcodesException>(() => new XcodeReleasesCatalog(client).GetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AStalledResponseBodyIsBoundedByTheRequestTimeout()
    {
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new StalledStream()),
        });
        using var client = new HttpClient(handler);
        var catalog = new XcodeReleasesCatalog(client, TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => catalog.GetAsync(CancellationToken.None));
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task AValidatedCatalogCanBeReadAfterRestartWithoutAnotherRequest()
    {
        using var directory = new TestDirectory();
        string path = Path.Combine(directory.Path, "cache", "XcodeReleases.json");
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ValidCatalog, Encoding.UTF8, "application/json"),
        });
        using var client = new HttpClient(handler);
        var first = new XcodeReleasesCatalog(client, cacheFile: () => path);
        Assert.Null(await first.GetSavedAsync(CancellationToken.None));

        XcodeCatalogRelease online = Assert.Single(await first.GetAsync(CancellationToken.None));
        DateTime savedAt = File.GetLastWriteTimeUtc(path);
        var reopened = new XcodeReleasesCatalog(client, cacheFile: () => path);
        XcodeSavedReleaseCatalog saved = Assert.IsType<XcodeSavedReleaseCatalog>(
            await reopened.GetSavedAsync(CancellationToken.None));

        Assert.Equal(online.Version, Assert.Single(saved.Releases).Version);
        Assert.Equal(online.Build, saved.Releases[0].Build);
        Assert.Equal(online.RequiredMacOS, saved.Releases[0].RequiredMacOS);
        Assert.Equal(savedAt, saved.SavedAtUtc);
        Assert.Equal(DateTimeKind.Utc, saved.SavedAtUtc.Kind);
        Assert.Equal(1, handler.RequestCount);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFailedOrInvalidRefreshDoesNotReplaceTheSavedCatalog(bool invalidBody)
    {
        using var directory = new TestDirectory();
        string path = Path.Combine(directory.Path, "XcodeReleases.json");
        bool fail = false;
        var handler = new Handler(_ => fail
            ? invalidBody
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{") }
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ValidCatalog) });
        using var client = new HttpClient(handler);
        var catalog = new XcodeReleasesCatalog(client, cacheFile: () => path);
        await catalog.GetAsync(CancellationToken.None);
        byte[] before = await File.ReadAllBytesAsync(path);
        fail = true;

        if (invalidBody)
            await Assert.ThrowsAsync<JsonException>(() => catalog.GetAsync(CancellationToken.None));
        else
            await Assert.ThrowsAsync<HttpRequestException>(() => catalog.GetAsync(CancellationToken.None));

        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.Equal("26.4", Assert.Single((await catalog.GetSavedAsync(CancellationToken.None))!.Releases).Version);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public async Task ACacheWriteFailureDoesNotTurnAValidOnlineCatalogIntoAFailure()
    {
        using var directory = new TestDirectory();
        string notADirectory = Path.Combine(directory.Path, "file");
        await File.WriteAllTextAsync(notADirectory, "keep");
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ValidCatalog),
        });
        using var client = new HttpClient(handler);
        var catalog = new XcodeReleasesCatalog(client, cacheFile: () => Path.Combine(notADirectory, "cache.json"));

        Assert.Equal("26.4", Assert.Single(await catalog.GetAsync(CancellationToken.None)).Version);
        Assert.Equal("keep", await File.ReadAllTextAsync(notADirectory));
    }

    [Fact]
    public async Task ACorruptOrOversizedSavedCatalogIsRejected()
    {
        using var directory = new TestDirectory();
        string path = Path.Combine(directory.Path, "XcodeReleases.json");
        using var client = new HttpClient(new Handler(_ => throw new InvalidOperationException("No network request expected.")));
        var catalog = new XcodeReleasesCatalog(client, cacheFile: () => path);
        await File.WriteAllTextAsync(path, "{");

        await Assert.ThrowsAsync<JsonException>(() => catalog.GetSavedAsync(CancellationToken.None));
        await using (var file = new FileStream(path, FileMode.Create, FileAccess.Write))
            file.SetLength(XcodePublicCatalogDownload.MaximumBytes + 1);
        await Assert.ThrowsAsync<XcodesException>(() => catalog.GetSavedAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ACancelledCacheReadDoesNotReturnSavedDetails()
    {
        using var directory = new TestDirectory();
        string path = Path.Combine(directory.Path, "XcodeReleases.json");
        await File.WriteAllTextAsync(path, ValidCatalog);
        using var client = new HttpClient(new Handler(_ => throw new InvalidOperationException("No network request expected.")));
        var catalog = new XcodeReleasesCatalog(client, cacheFile: () => path);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => catalog.GetSavedAsync(cancellation.Token));
    }

    [Theory]
    [InlineData("26.4", 26, 4, 0)]
    [InlineData("26.4.1", 26, 4, 1)]
    public void ParsesMinimumMacOSAsAThreeComponentVersion(string text, int major, int minor, int patch)
    {
        Assert.True(XcodeReleasesCatalog.TryMacOSVersion(text, out Version? version));
        Assert.Equal(new Version(major, minor, patch), version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("26")]
    [InlineData("26.4 beta")]
    [InlineData("26.4.0.1")]
    public void UnknownMinimumMacOSIsNotInvented(string? text)
        => Assert.False(XcodeReleasesCatalog.TryMacOSVersion(text, out _));

    private static IReadOnlyList<XcodeCatalogRelease> Parse(string json)
        => XcodeReleasesCatalog.Parse(Encoding.UTF8.GetBytes(json));

    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            nameof(XcodeReleasesCatalogTests), Guid.NewGuid().ToString("N"));

        public TestDirectory() => Directory.CreateDirectory(Path);

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public Uri? RequestedUri { get; private set; }
        public string? Authorization { get; private set; }
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            RequestedUri = request.RequestUri;
            Authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(respond(request));
        }
    }

    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
