using System.Net;
using UniGetUI.PackageEngine.Managers.CargoManager;

namespace UniGetUI.PackageEngine.Tests;

[Collection(nameof(OperationOrchestrationTestCollection))]
public sealed class CargoHttpTests
{
    private const string Url = "https://publisher.test/asset";

    [Fact]
    public async Task HttpsRedirectsPreservePayloadAndReportConcreteProgress()
    {
        using var fixture = new CargoTestHttpFixture();
        fixture.Redirect(Url, "/verified");
        fixture.Bytes("https://publisher.test/verified", [7, 8, 9]);
        using var http = new CargoHttp();
        List<(ulong Downloaded, ulong Total)> progress = [];
        Assert.Equal([7, 8, 9], await http.GetAsync(new Uri(Url), 3, false, CancellationToken.None,
            (downloaded, total) => progress.Add((downloaded, total))));
        Assert.Equal([Url, "https://publisher.test/verified"], fixture.Requests.Select(u => u.AbsoluteUri));
        Assert.Equal((3UL, 3UL), progress[^1]);
    }

    [Theory]
    [InlineData("http://publisher.test/asset")]
    [InlineData("https://user:password@publisher.test/asset")]
    public async Task NonHttpsAndCredentialUrlsFailBeforeAnyRequest(string url)
    {
        using var fixture = new CargoTestHttpFixture();
        using var http = new CargoHttp();
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            http.GetAsync(new Uri(url), 10, false, CancellationToken.None));
        Assert.Contains("non-HTTPS or credential-bearing", error.Message);
        Assert.Empty(fixture.Requests);
    }

    [Fact]
    public async Task RedirectDowngradeAndRedirectLoopsAreRejected()
    {
        using var fixture = new CargoTestHttpFixture();
        fixture.Redirect(Url, "http://publisher.test/asset");
        using var http = new CargoHttp();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            http.GetAsync(new Uri(Url), 10, false, CancellationToken.None));
        Assert.Single(fixture.Requests);
        fixture.Requests.Clear();
        fixture.Redirect(Url, Url);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            http.GetAsync(new Uri(Url), 10, false, CancellationToken.None));
        Assert.Contains("Too many", error.Message);
        Assert.Equal(6, fixture.Requests.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Gone)]
    public async Task MissingOptionalAssetsReturnNullButRequiredDownloadsFail(HttpStatusCode status)
    {
        using var fixture = new CargoTestHttpFixture();
        fixture.Status(Url, status);
        using var http = new CargoHttp();
        Assert.Null(await http.GetAsync(new Uri(Url), 10, true, CancellationToken.None));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            http.GetAsync(new Uri(Url), 10, false, CancellationToken.None));
        Assert.Equal(status, error.StatusCode);
    }

    [Fact]
    public async Task DeclaredAndStreamedBoundsAndTruncationAreEnforced()
    {
        using var fixture = new CargoTestHttpFixture();
        using var http = new CargoHttp();
        fixture.Bytes(Url, [1, 2, 3, 4]);
        Assert.Contains("allowed size", (await Assert.ThrowsAsync<InvalidDataException>(() =>
            http.GetAsync(new Uri(Url), 3, false, CancellationToken.None))).Message);
        fixture.Routes[Url] = () =>
        {
            var content = new StreamContent(new NonSeekableStream([1, 2, 3, 4]));
            return new(HttpStatusCode.OK) { Content = content };
        };
        Assert.Contains("allowed size", (await Assert.ThrowsAsync<InvalidDataException>(() =>
            http.GetAsync(new Uri(Url), 3, false, CancellationToken.None))).Message);
        fixture.Routes[Url] = () =>
        {
            var content = new ByteArrayContent([1, 2]);
            content.Headers.ContentLength = 3;
            return new(HttpStatusCode.OK) { Content = content };
        };
        Assert.Contains("truncated", (await Assert.ThrowsAsync<InvalidDataException>(() =>
            http.GetAsync(new Uri(Url), 3, false, CancellationToken.None))).Message);
    }

    [Fact]
    public async Task CancellationDoesNotBecomeAMissingAsset()
    {
        using var fixture = new CargoTestHttpFixture();
        using var http = new CargoHttp();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            http.GetAsync(new Uri(Url), 10, true, cancellation.Token));
        Assert.Empty(fixture.Requests);
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
