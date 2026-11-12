using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace UniGetUI.Extensions.Xcode.Tests;

public sealed class XcodePublicSimulatorRuntimeDownloaderTests
{
    private const string PublicUrl =
        "https://devimages-cdn.apple.com/downloads/xcode/simulators/ios-runtime.dmg";
    private static readonly XcodeSimulatorRuntime Runtime = new(
        "iOS 12.4 Simulator", "iOS", "12.4", "16G73", XcodeArchitecture.Unspecified,
        false, XcodeSimulatorRuntimeOrigin.AppleCatalog, 10_000,
        Distribution: XcodeSimulatorRuntimeDistribution.PublicImage, DownloadUrl: PublicUrl);

    [Fact]
    public async Task DownloadsTheSelectedPublicImageWithoutCredentialsOrOverwritingFiles()
    {
        using var fixture = new Fixture();
        byte[] image = [1, 2, 3, 4, 5];
        fixture.Response = _ => Task.FromResult(ImageResponse(image));
        string existing = Path.Combine(fixture.Destination, "existing.dmg");
        await File.WriteAllTextAsync(existing, "keep");
        List<XcodesOutputLine> output = [];

        string result = await fixture.DownloadAsync(output: output.Add);

        Assert.Equal(fixture.Paths!.ImagePath, result);
        Assert.Contains("iOS-16G73-", Path.GetFileName(result));
        Assert.Equal(image, await File.ReadAllBytesAsync(result));
        Assert.Equal("keep", await File.ReadAllTextAsync(existing));
        Assert.False(File.Exists(fixture.Paths.PartialPath));
        Assert.Equal(HttpMethod.Get, fixture.Handler.Method);
        Assert.Equal(PublicUrl, fixture.Handler.Uri?.AbsoluteUri);
        Assert.Null(fixture.Handler.Authorization);
        Assert.False(fixture.Handler.HasCookie);
        Assert.Equal(1, fixture.Handler.RequestCount);
        Assert.Contains(output, line => line.Text == "Downloading runtime image: 0%");
        Assert.Contains(output, line => line.Text == "Downloading runtime image: 100%");
        Assert.Contains(output, line => line.Text == "Verifying copied runtime image...");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("http://devimages-cdn.apple.com/downloads/xcode/simulators/runtime.dmg")]
    [InlineData("https://devimages-cdn.apple.com.attacker.test/downloads/xcode/simulators/runtime.dmg")]
    [InlineData("https://download.developer.apple.com/downloads/xcode/simulators/runtime.dmg")]
    [InlineData("https://user@devimages-cdn.apple.com/downloads/xcode/simulators/runtime.dmg")]
    [InlineData("https://devimages-cdn.apple.com:444/downloads/xcode/simulators/runtime.dmg")]
    [InlineData("https://devimages-cdn.apple.com/downloads/xcode/simulators/runtime.dmg?token=secret")]
    [InlineData("https://devimages-cdn.apple.com/downloads/xcode/simulators/runtime.dmg#fragment")]
    [InlineData("https://devimages-cdn.apple.com/downloads/other/runtime.dmg")]
    [InlineData("https://devimages-cdn.apple.com/downloads/xcode/simulators/runtime.zip")]
    [InlineData("https://devimages-cdn.apple.com/downloads/xcode/simulators/a%2Fb.dmg")]
    [InlineData("https://devimages-cdn.apple.com/downloads/xcode/simulators\\runtime.dmg")]
    [InlineData("file:///downloads/xcode/simulators/runtime.dmg")]
    public async Task RefusesUntrustedOrCredentialBearingUrls(string? url)
    {
        using var fixture = new Fixture();
        XcodeSimulatorRuntime runtime = Runtime with { DownloadUrl = url };

        Assert.False(XcodePublicSimulatorRuntimeDownloader.Supports(runtime));
        await Assert.ThrowsAsync<XcodesException>(() => fixture.DownloadAsync(runtime));
        Assert.Equal(0, fixture.Handler.RequestCount);
        Assert.Null(fixture.Paths);
    }

    [Fact]
    public async Task RefusesUnrecognizedRuntimeIdentitiesBeforeStarting()
    {
        using var fixture = new Fixture();
        XcodeSimulatorRuntime[] invalid =
        [
            Runtime with { Origin = XcodeSimulatorRuntimeOrigin.Installed },
            Runtime with { Distribution = XcodeSimulatorRuntimeDistribution.Unknown },
            Runtime with { Platform = "../iOS" },
            Runtime with { Build = "../16G73" },
            Runtime with { Version = "not a version" },
        ];

        foreach (XcodeSimulatorRuntime item in invalid)
            await Assert.ThrowsAsync<XcodesException>(() => fixture.DownloadAsync(item));

        Assert.Equal(0, fixture.Handler.RequestCount);
        Assert.Null(fixture.Paths);
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative/path")]
    [InlineData("/missing-unigetui-runtime-folder")]
    [InlineData("/invalid\nfolder")]
    public async Task RejectsInvalidDestinationsBeforeStarting(string directory)
    {
        using var fixture = new Fixture();

        await Assert.ThrowsAsync<XcodesException>(() => fixture.DownloadAsync(directory: directory));

        Assert.Equal(0, fixture.Handler.RequestCount);
        Assert.Null(fixture.Paths);
    }

    [Fact]
    public async Task RefusesOtherPlatformsBeforeStarting()
    {
        using var fixture = new Fixture { IsMacOS = false };

        await Assert.ThrowsAsync<XcodesException>(() => fixture.DownloadAsync());

        Assert.Equal(0, fixture.Handler.RequestCount);
        Assert.Null(fixture.Paths);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NeverOverwritesAnExistingFinalOrPartialPath(bool partial)
    {
        using var fixture = new Fixture();
        string final = Path.Combine(fixture.Destination, $"iOS-16G73-{fixture.Id:N}.dmg");
        string existing = partial ? final + ".partial" : final;
        await File.WriteAllTextAsync(existing, "do not replace");

        await Assert.ThrowsAsync<IOException>(() => fixture.DownloadAsync());

        Assert.Equal("do not replace", await File.ReadAllTextAsync(existing));
        if (partial)
        {
            Assert.Equal(0, fixture.Handler.RequestCount);
            Assert.Null(fixture.Paths);
        }
        else
        {
            Assert.Equal(1, fixture.Handler.RequestCount);
            Assert.True(File.Exists(fixture.Paths!.PartialPath));
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.Redirect)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.PartialContent)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task ReportsHttpFailuresWithoutFollowingRedirectsOrRetrying(HttpStatusCode status)
    {
        using var fixture = new Fixture
        {
            Response = _ => Task.FromResult(new HttpResponseMessage(status)),
        };

        HttpRequestException error = await Assert.ThrowsAsync<HttpRequestException>(() => fixture.DownloadAsync());

        Assert.Equal(status, error.StatusCode);
        Assert.Equal(1, fixture.Handler.RequestCount);
        Assert.True(File.Exists(fixture.Paths!.PartialPath));
        Assert.False(File.Exists(fixture.Paths.ImagePath));
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("application/json")]
    [InlineData(null)]
    public async Task RefusesResponsesThatAreNotDiskImages(string? mediaType)
    {
        using var fixture = new Fixture
        {
            Response = _ => Task.FromResult(ImageResponse([1, 2, 3], mediaType)),
        };

        await Assert.ThrowsAsync<XcodesException>(() => fixture.DownloadAsync());

        Assert.True(File.Exists(fixture.Paths!.PartialPath));
        Assert.False(File.Exists(fixture.Paths.ImagePath));
    }

    [Fact]
    public async Task RefusesUnexpectedContentEncoding()
    {
        using var fixture = new Fixture
        {
            Response = _ =>
            {
                HttpResponseMessage response = ImageResponse([1, 2, 3]);
                response.Content.Headers.ContentEncoding.Add("gzip");
                return Task.FromResult(response);
            },
        };

        await Assert.ThrowsAsync<XcodesException>(() => fixture.DownloadAsync());

        Assert.False(File.Exists(fixture.Paths!.ImagePath));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public async Task RejectsAnEmptyOrOverLimitDeclaredLength(long length)
    {
        using var fixture = new Fixture { MaximumBytes = 8 };
        fixture.Response = _ =>
        {
            HttpResponseMessage response = ImageResponse([1, 2, 3]);
            response.Content.Headers.ContentLength = length;
            return Task.FromResult(response);
        };

        await Assert.ThrowsAsync<XcodesException>(() => fixture.DownloadAsync());

        Assert.Equal(0, new FileInfo(fixture.Paths!.PartialPath).Length);
        Assert.False(File.Exists(fixture.Paths.ImagePath));
    }

    [Fact]
    public async Task EnforcesTheSizeLimitWithoutADeclaredLength()
    {
        using var fixture = new Fixture { MaximumBytes = 3 };
        fixture.Response = _ => Task.FromResult(StreamResponse(new NonSeekableStream([1, 2, 3, 4])));

        await Assert.ThrowsAsync<XcodesException>(() => fixture.DownloadAsync());

        Assert.False(File.Exists(fixture.Paths!.ImagePath));
    }

    [Fact]
    public async Task DoesNotPublishWhenTheDeclaredLengthDoesNotMatchTheBody()
    {
        using var fixture = new Fixture();
        fixture.Response = _ =>
        {
            HttpResponseMessage response = ImageResponse([1, 2, 3]);
            response.Content.Headers.ContentLength = 4;
            return Task.FromResult(response);
        };

        XcodesException error = await Assert.ThrowsAsync<XcodesException>(() => fixture.DownloadAsync());

        Assert.Contains("incomplete", error.Message);
        Assert.True(File.Exists(fixture.Paths!.PartialPath));
        Assert.False(File.Exists(fixture.Paths.ImagePath));
    }

    [Fact]
    public async Task CanDownloadAResponseWithNoDeclaredLength()
    {
        using var fixture = new Fixture();
        fixture.Response = _ => Task.FromResult(StreamResponse(new NonSeekableStream([1, 2, 3])));
        List<XcodesOutputLine> output = [];

        string result = await fixture.DownloadAsync(output: output.Add);

        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(result));
        Assert.Contains(output, line => line.Text == "Downloading runtime image...");
    }

    [Fact]
    public async Task CancellingKeepsThePartialFileWithoutClaimingSuccess()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.DownloadAsync(
            output: line =>
            {
                if (line.Text == "Downloading runtime image: 0%")
                    cancellation.Cancel();
            }, cancellationToken: cancellation.Token));

        Assert.True(File.Exists(fixture.Paths!.PartialPath));
        Assert.False(File.Exists(fixture.Paths.ImagePath));
    }

    [Fact]
    public async Task ATimeoutIsReportedAsAnErrorRatherThanUserCancellation()
    {
        using var fixture = new Fixture { TransferTimeout = TimeSpan.FromMilliseconds(20) };
        fixture.Response = async cancellationToken =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return ImageResponse([1]);
        };

        XcodesException error = await Assert.ThrowsAsync<XcodesException>(() => fixture.DownloadAsync());

        Assert.Contains("timed out", error.Message);
        Assert.True(File.Exists(fixture.Paths!.PartialPath));
        Assert.False(File.Exists(fixture.Paths.ImagePath));
    }

    [Fact]
    public async Task AStalledResponseBodyTimesOutAndKeepsThePartialFile()
    {
        using var fixture = new Fixture { TransferTimeout = TimeSpan.FromMilliseconds(20) };
        fixture.Response = _ => Task.FromResult(StreamResponse(new StallingStream()));

        XcodesException error = await Assert.ThrowsAsync<XcodesException>(
            () => fixture.DownloadAsync().WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Contains("timed out", error.Message);
        Assert.True(File.Exists(fixture.Paths!.PartialPath));
        Assert.False(File.Exists(fixture.Paths.ImagePath));
    }

    [Fact]
    public async Task AChangedDownloadIsNotPublished()
    {
        using var fixture = new Fixture();

        await Assert.ThrowsAsync<XcodesException>(() => fixture.DownloadAsync(output: line =>
        {
            if (line.Text == "Verifying copied runtime image...")
                File.WriteAllText(fixture.Paths!.PartialPath, "changed");
        }));

        Assert.True(File.Exists(fixture.Paths!.PartialPath));
        Assert.False(File.Exists(fixture.Paths.ImagePath));
    }

    [Fact]
    public async Task AReplacedDownloadSymlinkIsNotFollowed()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        using var fixture = new Fixture();
        string other = Path.Combine(fixture.Destination, "other.dmg");
        await File.WriteAllBytesAsync(other, [1, 2, 3]);
        await Assert.ThrowsAsync<IOException>(() => fixture.DownloadAsync(output: line =>
        {
            if (line.Text == "Verifying copied runtime image...")
            {
                File.Delete(fixture.Paths!.PartialPath);
                File.CreateSymbolicLink(fixture.Paths.PartialPath, other);
            }
        }));

        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(other));
        Assert.False(File.Exists(fixture.Paths!.ImagePath));
    }

    private static HttpResponseMessage ImageResponse(byte[] bytes, string? mediaType = "application/x-apple-diskimage")
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        if (mediaType is not null)
            response.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return response;
    }

    private static HttpResponseMessage StreamResponse(Stream stream)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return response;
    }

    private sealed class Fixture : IDisposable
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string Destination { get; } = Directory.CreateTempSubdirectory("xcodes-public-runtime-tests-").FullName;
        public bool IsMacOS { get; set; } = true;
        public long MaximumBytes { get; set; } = 1_000_000;
        public TimeSpan TransferTimeout { get; set; } = TimeSpan.FromSeconds(5);
        public Func<CancellationToken, Task<HttpResponseMessage>> Response { get; set; }
            = _ => Task.FromResult(ImageResponse([1, 2, 3]));
        public XcodeSimulatorRuntimeDownloadPaths? Paths { get; private set; }
        public Handler Handler { get; }
        private readonly HttpClient _client;

        public Fixture()
        {
            Handler = new Handler(token => Response(token));
            _client = new HttpClient(Handler);
        }

        public Task<string> DownloadAsync(
            XcodeSimulatorRuntime? runtime = null, string? directory = null,
            Action<XcodesOutputLine>? output = null, CancellationToken cancellationToken = default)
        {
            var downloader = new XcodePublicSimulatorRuntimeDownloader(
                _client, () => IsMacOS, () => Id, TransferTimeout, MaximumBytes);
            return downloader.DownloadAsync(runtime ?? Runtime, directory ?? Destination,
                paths => Paths = paths, output, cancellationToken);
        }

        public void Dispose()
        {
            _client.Dispose();
            Directory.Delete(Destination, recursive: true);
        }
    }

    private sealed class Handler(Func<CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }
        public HttpMethod? Method { get; private set; }
        public AuthenticationHeaderValue? Authorization { get; private set; }
        public bool HasCookie { get; private set; }
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            Method = request.Method;
            Authorization = request.Headers.Authorization;
            HasCookie = request.Headers.Contains("Cookie");
            RequestCount++;
            return response(cancellationToken);
        }
    }

    private sealed class NonSeekableStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _input = new(bytes);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _input.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => _input.ReadAsync(buffer, cancellationToken);
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _input.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class StallingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
