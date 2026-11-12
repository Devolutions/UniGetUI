using System.Net;
using System.Text;
using UniGetUI.Extensions.Xcode.AppleAuth;

namespace UniGetUI.Extensions.Xcode.Tests.AppleAuth;

public sealed class AppleDownloadsCatalogTests
{
    [Fact]
    public async Task PostsForTheAuthenticatedDownloadListAndReadsOnlyXcodeArchives()
    {
        const string data = """
            {
              "resultCode": 0,
              "downloads": [
                {"name": "Xcode 16.4", "dateModified": "06/09/25 14:30", "files": [
                  {"remotePath": "/Developer_Tools/Xcode_16.4/Xcode_16.4.xip", "fileSize": 34567890},
                  {"remotePath": "/Developer_Tools/Xcode_16.4/release-notes.pdf"}
                ]},
                {"name": "Command Line Tools for Xcode 16.4", "files": [
                  {"remotePath": "/Developer_Tools/Command_Line_Tools/Command_Line_Tools.dmg"}
                ]},
                {"name": "Xcode 26 Beta 2", "files": [
                  {"remotePath": "/Developer_Tools/Xcode_26_beta_2/Xcode_26_beta_2.xip.aea"}
                ]}
              ]
            }
            """;
        var handler = new FakeHandler(HttpStatusCode.OK, data);
        using var client = new HttpClient(handler);

        IReadOnlyList<AppleXcodeDownload> downloads = await new AppleDownloadsCatalog(client).GetAsync(CancellationToken.None);

        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal(AppleDownloadsCatalog.DownloadsUri, handler.Address);
        Assert.Equal(["16.4", "26 Beta 2"], downloads.Select(item => item.Version));
        Assert.Equal("https://download.developer.apple.com/Developer_Tools/Xcode_16.4/Xcode_16.4.xip", downloads[0].Address.AbsoluteUri);
        Assert.Equal(34567890, downloads[0].SizeBytes);
        Assert.Equal(new DateOnly(2025, 6, 9), downloads[0].ModifiedDate);
        Assert.Null(downloads[1].ModifiedDate);
    }

    [Fact]
    public void IgnoresUntrustedPathsAndNamesRatherThanGuessingIdentities()
    {
        const string data = """
            {"downloads":[
              {"name":"Xcode 17.0","files":[
                {"remotePath":"//evil.example/Xcode.xip"},
                {"remotePath":"https://evil.example/Xcode.xip"},
                {"remotePath":"/Developer_Tools/Xcode.xip?token=synthetic-secret"},
                {"remotePath":"/Developer_Tools/Xcode.xip#fragment"},
                {"remotePath":"\\evil\\Xcode.xip"},
                {"remotePath":"/Developer_Tools/ok.xip"}
              ]},
              {"name":"Xcode unknown","files":[{"remotePath":"/Developer_Tools/unknown.xip"}]},
              null,
              {"name":"Xcode 17.1","files":null}
            ]}
            """;

        AppleXcodeDownload download = Assert.Single(AppleDownloadsCatalog.Parse(Encoding.UTF8.GetBytes(data)));

        Assert.Equal("17.0", download.Version);
        Assert.Equal("/Developer_Tools/ok.xip", download.Address.AbsolutePath);
        Assert.Null(download.SizeBytes);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"resultCode\":42,\"resultsString\":\"synthetic-private-data\",\"downloads\":[]}")]
    [InlineData("not json")]
    public void InvalidAppleResponsesFailWithoutExposingTheirContents(string data)
    {
        XcodesException error = Assert.Throws<XcodesException>(() => AppleDownloadsCatalog.Parse(Encoding.UTF8.GetBytes(data)));

        Assert.DoesNotContain("synthetic-private-data", error.Message);
        Assert.DoesNotContain("not json", error.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, true)]
    [InlineData(HttpStatusCode.Forbidden, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, false)]
    public async Task ReportsHttpErrorsWithoutTreatingEveryFailureAsAnAccountProblem(HttpStatusCode status, bool accessFailure)
    {
        using var client = new HttpClient(new FakeHandler(status, "synthetic-private-response"));

        XcodesException error = await Assert.ThrowsAsync<XcodesException>(() =>
            new AppleDownloadsCatalog(client).GetAsync(CancellationToken.None));

        Assert.Contains(((int)status).ToString(), error.Message);
        Assert.Equal(accessFailure, error.IsAccessFailure);
        Assert.DoesNotContain("synthetic-private-response", error.Message);
    }

    [Fact]
    public async Task RejectsOversizedResponses()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "{}") { DeclaredLength = AppleDownloadsCatalog.MaximumResponseBytes + 1 };
        using var client = new HttpClient(handler);

        XcodesException error = await Assert.ThrowsAsync<XcodesException>(() =>
            new AppleDownloadsCatalog(client).GetAsync(CancellationToken.None));

        Assert.Contains("too large", error.Message);
    }

    [Fact]
    public async Task TimesOutWhenTheListDoesNotArrive()
    {
        using var client = new HttpClient(new HangingHandler());

        XcodesException error = await Assert.ThrowsAsync<XcodesException>(() =>
            new AppleDownloadsCatalog(client, TimeSpan.FromMilliseconds(100)).GetAsync(CancellationToken.None));

        Assert.Contains("time", error.Message);
    }

    private sealed class FakeHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public Uri? Address { get; private set; }
        public long? DeclaredLength { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            Address = request.RequestUri;
            var content = new StringContent(body, Encoding.UTF8, "application/json");
            if (DeclaredLength is { } length)
                content.Headers.ContentLength = length;
            return Task.FromResult(new HttpResponseMessage(status) { Content = content });
        }
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The delay should have been canceled.");
        }
    }
}
