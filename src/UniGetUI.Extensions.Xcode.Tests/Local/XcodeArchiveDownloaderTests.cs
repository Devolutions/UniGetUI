using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using UniGetUI.Extensions.Xcode.Local;

namespace UniGetUI.Extensions.Xcode.Tests.Local;

public sealed class XcodeArchiveDownloaderTests : IDisposable
{
    private static readonly Uri Download = new("https://download.developer.apple.com/Developer_Tools/Xcode_26.4/Xcode_26.4.xip");
    private readonly TemporaryDirectory _directory = new();
    private readonly byte[] _archive = Enumerable.Range(0, 300_000).Select(i => (byte)(i * 7)).ToArray();

    public void Dispose() => _directory.Dispose();

    private string Sha1 => Convert.ToHexStringLower(SHA1.HashData(_archive));

    [Fact]
    public async Task DownloadsAndVerifiesTheArchive()
    {
        var server = new FakeApple(_archive);
        List<double?> progress = [];

        string path = await Downloader(server).DownloadAsync(Download, Sha1, progress.Add, CancellationToken.None);

        Assert.Equal(_archive, await File.ReadAllBytesAsync(path));
        Assert.Equal("Xcode_26.4.xip", Path.GetFileName(path));
        Assert.Equal(1, progress[^1]);
        Assert.Contains(server.SentRequests, request => request.Method == HttpMethod.Post
            && request.Uri == UniGetUI.Extensions.Xcode.AppleAuth.AppleDownloadsCatalog.DownloadsUri
            && request.Accept.Contains("application/json", StringComparison.OrdinalIgnoreCase));
        Assert.False(File.Exists(path + ".part"));
    }

    [Theory]
    [InlineData(XcodeArchiveDownloader.AdcSessionExpiredResultCode, true)]
    [InlineData(2170, false)]
    public async Task MarksRefusedDownloadsWhoseDeveloperSessionExpired(int resultCode, bool expectedExpired)
    {
        var server = new FakeApple(_archive)
        {
            AuthorizeBody = $$"""{"resultCode":{{resultCode}},"resultString":"synthetic"}""",
            Refuse = (true, HttpStatusCode.OK),
        };

        XcodesException error = await Assert.ThrowsAsync<XcodesException>(() =>
            Downloader(server).DownloadAsync(Download, Sha1, _ => { }, CancellationToken.None));

        Assert.True(error.IsAccessFailure);
        Assert.Equal(expectedExpired, error.IsAdcSessionExpired);
    }

    [Fact]
    public async Task NewDownloadCachesArePrivateOnUnix()
    {
        if (OperatingSystem.IsWindows())
            return;

        await Downloader(new FakeApple(_archive)).DownloadAsync(Download, null, _ => { }, CancellationToken.None);

        UnixFileMode mode = File.GetUnixFileMode(CacheDirectory(Download));
        Assert.Equal((UnixFileMode)0, mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                                             | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute));
    }

    [Fact]
    public async Task ResumesAPartialDownload()
    {
        var server = new FakeApple(_archive);
        await SeedPartialAsync(_archive[..1000], sha1: Sha1);

        string path = await Downloader(server).DownloadAsync(Download, Sha1, _ => { }, CancellationToken.None);

        Assert.Equal(_archive, await File.ReadAllBytesAsync(path));
        Assert.Equal(1000, server.RangeStart);
    }

    [Fact]
    public async Task StartsOverWhenTheServerIgnoresTheRange()
    {
        var server = new FakeApple(_archive) { HonorsRange = false };
        await SeedPartialAsync(new byte[1000], sha1: Sha1);

        string path = await Downloader(server).DownloadAsync(Download, Sha1, _ => { }, CancellationToken.None);

        Assert.Equal(_archive, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task APartialResponseWithoutContentRangeIsRetriedFromTheBeginning()
    {
        var server = new FakeApple(_archive) { OmitContentRange = true, ETag = "\"same\"" };
        await SeedPartialAsync(_archive[..1000], "\"same\"");

        string path = await Downloader(server).DownloadAsync(Download, null, _ => { }, CancellationToken.None);

        Assert.Equal(_archive, await File.ReadAllBytesAsync(path));
        Assert.Equal([1000L, null], server.RangeStarts);
    }

    [Fact]
    public async Task DifferentDownloadAddressesWithTheSameFilenameDoNotShareAnArchive()
    {
        var firstServer = new FakeApple(_archive);
        byte[] otherArchive = [.. _archive.Select(value => (byte)(value ^ 0xff))];
        var secondServer = new FakeApple(otherArchive);
        var otherAddress = new Uri("https://download.developer.apple.com/other/Xcode_26.4.xip");

        string first = await Downloader(firstServer).DownloadAsync(Download, null, _ => { }, CancellationToken.None);
        string second = await Downloader(secondServer).DownloadAsync(otherAddress, null, _ => { }, CancellationToken.None);

        Assert.NotEqual(first, second);
        Assert.Equal(_archive, await File.ReadAllBytesAsync(first));
        Assert.Equal(otherArchive, await File.ReadAllBytesAsync(second));
        Assert.Contains(otherAddress, secondServer.Requests);
    }

    [Fact]
    public async Task DifferentExpectedChecksumsDoNotReplaceAnArchiveInUse()
    {
        var firstServer = new FakeApple(_archive);
        byte[] replacement = [.. _archive.Select(value => (byte)(value ^ 0xff))];
        string replacementSha1 = Convert.ToHexStringLower(SHA1.HashData(replacement));
        var secondServer = new FakeApple(replacement);

        string first = await Downloader(firstServer).DownloadAsync(Download, Sha1, _ => { }, CancellationToken.None);
        string second = await Downloader(secondServer).DownloadAsync(Download, replacementSha1, _ => { }, CancellationToken.None);

        Assert.NotEqual(first, second);
        Assert.Equal(_archive, await File.ReadAllBytesAsync(first));
        Assert.Equal(replacement, await File.ReadAllBytesAsync(second));
    }

    [Fact]
    public async Task ChecksumCapitalizationDoesNotChangeTheCacheIdentity()
    {
        var server = new FakeApple(_archive);

        string first = await Downloader(server).DownloadAsync(Download, Sha1, _ => { }, CancellationToken.None);
        server.Requests.Clear();
        string second = await Downloader(server).DownloadAsync(Download, Sha1.ToUpperInvariant(), _ => { }, CancellationToken.None);

        Assert.Equal(first, second);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task ReusesAFinishedArchiveThatMatches()
    {
        var server = new FakeApple(_archive);
        Directory.CreateDirectory(CacheDirectory(Download, Sha1));
        await File.WriteAllBytesAsync(ArchivePath(Download, Sha1), _archive);

        await Downloader(server).DownloadAsync(Download, Sha1, _ => { }, CancellationToken.None);

        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task RejectsADamagedArchiveAndDiscardsIt()
    {
        var server = new FakeApple(_archive);

        var error = await Assert.ThrowsAsync<XcodesException>(() =>
            Downloader(server).DownloadAsync(Download, new string('0', 40), _ => { }, CancellationToken.None));

        Assert.Contains("checksum", error.Message);
        string badSha1 = new('0', 40);
        Assert.False(File.Exists(ArchivePath(Download, badSha1)));
        Assert.False(File.Exists(ArchivePath(Download, badSha1) + ".part"));
        Assert.False(File.Exists(ValidatorPath(Download, badSha1)));
    }

    [Fact]
    public async Task ResumesWithoutAChecksumWhenTheServerGaveAStrongValidator()
    {
        var interrupted = new FakeApple(_archive)
        {
            CustomDownload = request =>
            {
                var response = DownloadResponse(request, HttpStatusCode.OK, _archive[..1000]);
                response.Content.Headers.ContentLength = _archive.Length;
                response.Headers.ETag = EntityTagHeaderValue.Parse("\"release-v1\"");
                return response;
            },
        };

        await Assert.ThrowsAsync<XcodesException>(() =>
            Downloader(interrupted).DownloadAsync(Download, null, _ => { }, CancellationToken.None));

        Assert.Equal(_archive[..1000], await File.ReadAllBytesAsync(ArchivePath(Download) + ".part"));
        Assert.Equal("\"release-v1\"", await File.ReadAllTextAsync(ValidatorPath(Download)));
        var resumed = new FakeApple(_archive) { ETag = "\"release-v1\"" };
        string path = await Downloader(resumed).DownloadAsync(Download, null, _ => { }, CancellationToken.None);

        Assert.Equal(_archive, await File.ReadAllBytesAsync(path));
        Assert.Equal([1000L], resumed.RangeStarts);
        Assert.Equal(["\"release-v1\""], resumed.IfRanges);
        Assert.False(File.Exists(ValidatorPath(Download)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("W/\"weak\"")]
    [InlineData("invalid")]
    public async Task RestartsWithoutAChecksumOrStrongValidator(string? validator)
    {
        var server = new FakeApple(_archive);
        await SeedPartialAsync(new byte[1000], validator);

        string path = await Downloader(server).DownloadAsync(Download, null, _ => { }, CancellationToken.None);

        Assert.Equal(_archive, await File.ReadAllBytesAsync(path));
        Assert.Equal([null], server.RangeStarts);
        Assert.False(File.Exists(ValidatorPath(Download)));
    }

    [Fact]
    public async Task AnOversizedSavedValidatorDoesNotEnableResume()
    {
        var server = new FakeApple(_archive);
        await SeedPartialAsync(_archive[..1000], new string('x', 2048));

        string path = await Downloader(server).DownloadAsync(Download, null, _ => { }, CancellationToken.None);

        Assert.Equal(_archive, await File.ReadAllBytesAsync(path));
        Assert.Equal([null], server.RangeStarts);
    }

    [Fact]
    public async Task AWeakResponseValidatorIsNotSavedForAnInterruptedDownload()
    {
        var interrupted = new FakeApple(_archive)
        {
            CustomDownload = request =>
            {
                var response = DownloadResponse(request, HttpStatusCode.OK, _archive[..1000]);
                response.Content.Headers.ContentLength = _archive.Length;
                response.Headers.ETag = EntityTagHeaderValue.Parse("W/\"weak\"");
                return response;
            },
        };

        await Assert.ThrowsAsync<XcodesException>(() =>
            Downloader(interrupted).DownloadAsync(Download, null, _ => { }, CancellationToken.None));

        Assert.False(File.Exists(ValidatorPath(Download)));
        var retried = new FakeApple(_archive);
        string path = await Downloader(retried).DownloadAsync(Download, null, _ => { }, CancellationToken.None);
        Assert.Equal(_archive, await File.ReadAllBytesAsync(path));
        Assert.Equal([null], retried.RangeStarts);
    }

    [Fact]
    public async Task ACancelledDownloadCanResumeWithItsStrongValidator()
    {
        byte[] archive = [.. Enumerable.Range(0, 2_500_000).Select(i => (byte)(i * 13))];
        using var cancellation = new CancellationTokenSource();
        var initial = new FakeApple(archive) { ETag = "\"stable\"" };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Downloader(initial).DownloadAsync(
            Download, null, fraction =>
            {
                if (fraction is > 0 and < 1)
                    cancellation.Cancel();
            }, cancellation.Token));

        long partialLength = new FileInfo(ArchivePath(Download) + ".part").Length;
        Assert.InRange(partialLength, 1, archive.Length - 1);
        var resumed = new FakeApple(archive) { ETag = "\"stable\"" };
        string path = await Downloader(resumed).DownloadAsync(Download, null, _ => { }, CancellationToken.None);
        Assert.Equal(archive, await File.ReadAllBytesAsync(path));
        Assert.Equal([partialLength], resumed.RangeStarts);
        Assert.Equal(["\"stable\""], resumed.IfRanges);
    }

    [Fact]
    public async Task AChangedValidatorStartsANewArchiveInsteadOfAppending()
    {
        byte[] replacement = [.. _archive.Select(value => (byte)(value ^ 0xff))];
        var server = new FakeApple(replacement) { ETag = "\"new\"" };
        await SeedPartialAsync(_archive[..1000], "\"old\"");

        string path = await Downloader(server).DownloadAsync(Download, null, _ => { }, CancellationToken.None);

        Assert.Equal(replacement, await File.ReadAllBytesAsync(path));
        Assert.Equal([1000L], server.RangeStarts);
        Assert.Equal(["\"old\""], server.IfRanges);
    }

    [Theory]
    [InlineData("wrong-start")]
    [InlineData("unknown-total")]
    [InlineData("not-to-end")]
    [InlineData("wrong-content-length")]
    [InlineData("changed-validator")]
    [InlineData("missing-validator")]
    public async Task InvalidPartialResponsesAreRetriedFromTheBeginning(string fault)
    {
        var server = new FakeApple(_archive)
        {
            CustomDownload = request =>
            {
                if (request.Headers.Range is null)
                    return DownloadResponse(request, HttpStatusCode.OK, _archive);
                var response = DownloadResponse(request, HttpStatusCode.PartialContent, _archive[1000..]);
                response.Content.Headers.ContentRange = fault switch
                {
                    "wrong-start" => new ContentRangeHeaderValue(999, _archive.Length - 1, _archive.Length),
                    "unknown-total" => new ContentRangeHeaderValue(1000, _archive.Length - 1),
                    "not-to-end" => new ContentRangeHeaderValue(1000, _archive.Length - 2, _archive.Length),
                    _ => new ContentRangeHeaderValue(1000, _archive.Length - 1, _archive.Length),
                };
                if (fault == "wrong-content-length")
                    response.Content.Headers.ContentLength = _archive.Length - 999;
                if (fault == "changed-validator")
                    response.Headers.ETag = EntityTagHeaderValue.Parse("\"changed\"");
                return response;
            },
        };
        await SeedPartialAsync(_archive[..1000], "\"original\"");

        string path = await Downloader(server).DownloadAsync(Download, null, _ => { }, CancellationToken.None);

        Assert.Equal(_archive, await File.ReadAllBytesAsync(path));
        Assert.Equal([1000L, null], server.RangeStarts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFailedFreshRequestPreservesTheExistingPartial(bool rangeNotSatisfiable)
    {
        var server = new FakeApple(_archive)
        {
            CustomDownload = request => request.Headers.Range is null
                ? DownloadResponse(request, HttpStatusCode.ServiceUnavailable, [])
                : DownloadResponse(request, rangeNotSatisfiable
                    ? HttpStatusCode.RequestedRangeNotSatisfiable : HttpStatusCode.PartialContent, _archive[1000..]),
        };
        await SeedPartialAsync(_archive[..1000], "\"original\"");

        await Assert.ThrowsAsync<XcodesException>(() =>
            Downloader(server).DownloadAsync(Download, null, _ => { }, CancellationToken.None));

        Assert.Equal(_archive[..1000], await File.ReadAllBytesAsync(ArchivePath(Download) + ".part"));
        Assert.Equal("\"original\"", await File.ReadAllTextAsync(ValidatorPath(Download)));
        Assert.Equal([1000L, null], server.RangeStarts);
    }

    [Theory]
    [InlineData(HttpStatusCode.Accepted)]
    [InlineData(HttpStatusCode.NoContent)]
    public async Task RejectsSuccessfulStatusesThatDoNotContainAnArchive(HttpStatusCode status)
    {
        var server = new FakeApple(_archive)
        {
            CustomDownload = request => DownloadResponse(request, status, []),
        };

        await Assert.ThrowsAsync<XcodesException>(() =>
            Downloader(server).DownloadAsync(Download, null, _ => { }, CancellationToken.None));

        Assert.False(File.Exists(ArchivePath(Download)));
    }

    [Fact]
    public async Task RejectsAnUnsolicitedPartialResponse()
    {
        var server = new FakeApple(_archive)
        {
            CustomDownload = request =>
            {
                var response = DownloadResponse(request, HttpStatusCode.PartialContent, _archive);
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, _archive.Length - 1, _archive.Length);
                return response;
            },
        };

        await Assert.ThrowsAsync<XcodesException>(() =>
            Downloader(server).DownloadAsync(Download, null, _ => { }, CancellationToken.None));

        Assert.False(File.Exists(ArchivePath(Download)));
    }

    [Fact]
    public async Task RejectsAnEmptySuccessfulArchiveWithoutDiscardingThePartial()
    {
        var server = new FakeApple(_archive)
        {
            CustomDownload = request => DownloadResponse(request, HttpStatusCode.OK, []),
        };
        await SeedPartialAsync(_archive[..1000], "\"original\"");

        await Assert.ThrowsAsync<XcodesException>(() =>
            Downloader(server).DownloadAsync(Download, null, _ => { }, CancellationToken.None));

        Assert.Equal(_archive[..1000], await File.ReadAllBytesAsync(ArchivePath(Download) + ".part"));
        Assert.Equal("\"original\"", await File.ReadAllTextAsync(ValidatorPath(Download)));
        Assert.False(File.Exists(ArchivePath(Download)));
    }

    [Fact]
    public async Task DoesNotWritePastTheDeclaredArchiveLength()
    {
        var server = new FakeApple(_archive)
        {
            CustomDownload = request =>
            {
                var response = DownloadResponse(request, HttpStatusCode.OK, _archive);
                response.Content.Headers.ContentLength = 100;
                return response;
            },
        };

        await Assert.ThrowsAsync<XcodesException>(() =>
            Downloader(server).DownloadAsync(Download, null, _ => { }, CancellationToken.None));

        Assert.Equal(0, new FileInfo(ArchivePath(Download) + ".part").Length);
        Assert.False(File.Exists(ArchivePath(Download)));
    }

    [Fact]
    public async Task PartialDownloadsWithTheSameFilenameRemainSeparate()
    {
        var otherAddress = new Uri("https://download.developer.apple.com/other/Xcode_26.4.xip");
        byte[] replacement = [.. _archive.Select(value => (byte)(value ^ 0xff))];
        await SeedPartialAsync(_archive[..1000], "\"original\"");
        var server = new FakeApple(replacement) { ETag = "\"other\"" };

        string path = await Downloader(server).DownloadAsync(otherAddress, null, _ => { }, CancellationToken.None);

        Assert.Equal(replacement, await File.ReadAllBytesAsync(path));
        Assert.Equal([null], server.RangeStarts);
        Assert.Equal(_archive[..1000], await File.ReadAllBytesAsync(ArchivePath(Download) + ".part"));
    }

    [Fact]
    public async Task ConcurrentDownloadsCannotMutateTheSameCache()
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var blocking = new BlockingApple(_archive);
        Task<string> first = Downloader(blocking).DownloadAsync(Download, null, _ => { }, limit.Token);
        await blocking.Started.Task.WaitAsync(limit.Token);
        try
        {
            var duplicate = new FakeApple(_archive);
            XcodesException error = await Assert.ThrowsAsync<XcodesException>(() =>
                Downloader(duplicate).DownloadAsync(Download, null, _ => { }, limit.Token));
            Assert.Contains("locked", error.Message);
            Assert.Empty(duplicate.Requests);

            var otherAddress = new Uri("https://download.developer.apple.com/other/Xcode_26.4.xip");
            string other = await Downloader(new FakeApple(_archive))
                .DownloadAsync(otherAddress, null, _ => { }, limit.Token);
            Assert.Equal(_archive, await File.ReadAllBytesAsync(other, limit.Token));
        }
        finally
        {
            blocking.Release.TrySetResult();
            await first;
        }
    }

    [Fact]
    public async Task AnArchiveLeaseKeepsTheCacheLockedUntilItIsDisposed()
    {
        var first = Downloader(new FakeApple(_archive));
        var secondServer = new FakeApple(_archive);
        var second = Downloader(secondServer);
        string path;

        using (XcodeArchiveDownloader.ArchiveLease lease =
               await first.DownloadForInstallAsync(Download, null, _ => { }, CancellationToken.None))
        {
            path = lease.Path;
            XcodesException error = await Assert.ThrowsAsync<XcodesException>(() =>
                second.DownloadAsync(Download, null, _ => { }, CancellationToken.None));
            Assert.Contains("locked", error.Message);
            Assert.Empty(secondServer.Requests);
            Assert.Equal(_archive, await File.ReadAllBytesAsync(path));
        }

        Assert.Equal(path, await second.DownloadAsync(Download, null, _ => { }, CancellationToken.None));
        Assert.Empty(secondServer.Requests);
    }

    [Fact]
    public async Task AFailedLeasedDownloadReleasesTheCache()
    {
        var failed = new FakeApple(_archive);
        string expected = new('0', 40);
        XcodesException firstError = await Assert.ThrowsAsync<XcodesException>(() =>
            Downloader(failed).DownloadForInstallAsync(Download, expected, _ => { }, CancellationToken.None));
        Assert.Contains("checksum", firstError.Message);

        byte[] replacement = [1, 2, 3];
        string replacementHash = Convert.ToHexStringLower(SHA1.HashData(replacement));
        XcodesException secondError = await Assert.ThrowsAsync<XcodesException>(() =>
            Downloader(new FakeApple(replacement)).DownloadForInstallAsync(Download, expected, _ => { }, CancellationToken.None));
        Assert.Contains("checksum", secondError.Message);
        string path = await Downloader(new FakeApple(replacement))
            .DownloadAsync(Download, replacementHash, _ => { }, CancellationToken.None);
        Assert.Equal(replacement, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task AnAlreadyCancelledDownloadDoesNotCreateACache()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var server = new FakeApple(_archive);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Downloader(server).DownloadAsync(Download, null, _ => { }, cancellation.Token));

        Assert.Empty(Directory.GetFileSystemEntries(_directory.Path));
        Assert.Empty(server.Requests);
    }

    [Theory]
    [InlineData(true, HttpStatusCode.OK)]
    [InlineData(false, HttpStatusCode.Forbidden)]
    public async Task ReportsARefusedDownload(bool redirectToUnauthorizedPage, HttpStatusCode status)
    {
        var server = new FakeApple(_archive) { Refuse = (redirectToUnauthorizedPage, status) };

        var error = await Assert.ThrowsAsync<XcodesException>(() =>
            Downloader(server).DownloadAsync(Download, Sha1, _ => { }, CancellationToken.None));

        Assert.Equal(XcodeArchiveDownloader.UnauthorizedMessage, error.Message);
        Assert.True(error.IsAccessFailure);
        Assert.True(XcodesError.IsPossibleAccessFailure(error.Message));
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("Text/HTML")]
    public async Task ReportsAnHtmlResponseAsARefusedDownloadRegardlessOfMediaTypeCasing(string mediaType)
    {
        var server = new FakeApple(_archive) { Refuse = (false, HttpStatusCode.OK), RefusalMediaType = mediaType };

        XcodesException error = await Assert.ThrowsAsync<XcodesException>(() =>
            Downloader(server).DownloadAsync(Download, null, _ => { }, CancellationToken.None));

        Assert.True(error.IsAccessFailure);
        Assert.False(File.Exists(ArchivePath(Download)));
    }

    [Theory]
    [InlineData("https://example.com/Xcode.xip")]
    [InlineData("https://download.developer.apple.com/Developer_Tools/Xcode_7/Xcode_7.dmg")]
    public async Task RefusesAddressesThatAreNotAppleXcodeArchives(string url)
    {
        var server = new FakeApple(_archive);
        await Assert.ThrowsAsync<XcodesException>(() =>
            Downloader(server).DownloadAsync(new Uri(url), null, _ => { }, CancellationToken.None));
        Assert.Empty(server.Requests);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-checksum")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public async Task RefusesMalformedChecksumsBeforeDownloading(string sha1)
    {
        var server = new FakeApple(_archive);

        await Assert.ThrowsAsync<XcodesException>(() =>
            Downloader(server).DownloadAsync(Download, sha1, _ => { }, CancellationToken.None));

        Assert.Empty(server.Requests);
        Assert.Empty(Directory.GetFileSystemEntries(_directory.Path));
    }

    private XcodeArchiveDownloader Downloader(HttpMessageHandler server)
        => new(new HttpClient(server), () => _directory.Path);

    private string CacheDirectory(Uri address, string? sha1 = null)
        => XcodeArchiveDownloader.CacheDirectory(_directory.Path, address, sha1);

    private string ArchivePath(Uri address, string? sha1 = null)
        => Path.Combine(CacheDirectory(address, sha1), Path.GetFileName(address.AbsolutePath));

    private string ValidatorPath(Uri address, string? sha1 = null) => Path.Combine(CacheDirectory(address, sha1), "validator");

    private async Task SeedPartialAsync(byte[] content, string? validator = null, string? sha1 = null)
    {
        Directory.CreateDirectory(CacheDirectory(Download, sha1));
        await File.WriteAllBytesAsync(ArchivePath(Download, sha1) + ".part", content);
        if (validator is not null)
            await File.WriteAllTextAsync(ValidatorPath(Download, sha1), validator, new UTF8Encoding(false));
    }

    private static HttpResponseMessage DownloadResponse(HttpRequestMessage request, HttpStatusCode status, byte[] content)
        => new(status) { RequestMessage = request, Content = new ByteArrayContent(content) };

    private sealed class BlockingApple(byte[] archive) : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri == UniGetUI.Extensions.Xcode.AppleAuth.AppleDownloadsCatalog.DownloadsUri)
                return DownloadResponse(request, HttpStatusCode.OK, """{"resultCode":0,"downloads":[]}"""u8.ToArray());
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return DownloadResponse(request, HttpStatusCode.OK, archive);
        }
    }

    private sealed record RecordedRequest(HttpMethod Method, Uri Uri, string Accept);

    private sealed class FakeApple(byte[] archive) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        public List<RecordedRequest> SentRequests { get; } = [];
        public List<long?> RangeStarts { get; } = [];
        public List<string?> IfRanges { get; } = [];
        public long? RangeStart { get; private set; }
        public bool HonorsRange { get; init; } = true;
        public bool OmitContentRange { get; init; }
        public string? ETag { get; init; }
        public string AuthorizeBody { get; init; } = """{"resultCode":0,"downloads":[]}""";
        public string RefusalMediaType { get; init; } = "text/html";
        public Func<HttpRequestMessage, HttpResponseMessage>? CustomDownload { get; init; }
        public (bool Redirect, HttpStatusCode Status)? Refuse { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri uri = request.RequestUri!;
            Requests.Add(uri);
            SentRequests.Add(new RecordedRequest(request.Method, uri, request.Headers.Accept.ToString()));
            if (uri == UniGetUI.Extensions.Xcode.AppleAuth.AppleDownloadsCatalog.DownloadsUri)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new StringContent(AuthorizeBody, Encoding.UTF8, "application/json"),
                });
            }

            RangeStarts.Add(request.Headers.Range?.Ranges.Single().From);
            IfRanges.Add(request.Headers.IfRange?.EntityTag?.ToString());
            if (CustomDownload is not null)
                return Task.FromResult(CustomDownload(request));
            if (Refuse is { } refuse)
            {
                if (refuse.Redirect)
                    request.RequestUri = new Uri("https://developer.apple.com/unauthorized/");
                var page = new HttpResponseMessage(refuse.Status) { RequestMessage = request, Content = new StringContent("<html/>") };
                page.Content.Headers.ContentType = new MediaTypeHeaderValue(RefusalMediaType);
                return Task.FromResult(page);
            }

            bool matchesValidator = request.Headers.IfRange?.EntityTag is not { } expected
                || ETag is null || expected.Equals(EntityTagHeaderValue.Parse(ETag));
            if (request.Headers.Range?.Ranges.Single().From is { } from && HonorsRange && matchesValidator)
            {
                RangeStart = from;
                var partial = new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    RequestMessage = request,
                    Content = new ByteArrayContent(archive[(int)from..]),
                };
                if (!OmitContentRange)
                    partial.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, archive.Length - 1, archive.Length);
                if (ETag is not null)
                    partial.Headers.ETag = EntityTagHeaderValue.Parse(ETag);
                return Task.FromResult(partial);
            }

            HttpResponseMessage complete = DownloadResponse(request, HttpStatusCode.OK, archive);
            if (ETag is not null)
                complete.Headers.ETag = EntityTagHeaderValue.Parse(ETag);
            return Task.FromResult(complete);
        }
    }
}
