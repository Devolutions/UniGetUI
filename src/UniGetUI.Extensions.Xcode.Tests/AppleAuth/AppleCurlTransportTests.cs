using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using UniGetUI.Extensions.Xcode.AppleAuth;
using UniGetUI.Extensions.Xcode.Local;
using UniGetUI.Extensions.Xcode.Tests.Local;

namespace UniGetUI.Extensions.Xcode.Tests.AppleAuth;

[CollectionDefinition("Apple curl transport", DisableParallelization = true)]
public sealed class AppleCurlTransportCollection;

[Collection("Apple curl transport")]
public sealed class AppleCurlTransportTests
{
    [Fact]
    public async Task SendsCookiesAndBodiesWithoutSecretsInArgumentsOrEnvironment()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        const string query = "synthetic-query-secret";
        const string session = "synthetic-cookie-secret";
        const string token = "synthetic-header-secret";
        const string body = """{"value":"synthetic-body-secret","quote":"\"","slash":"\\","text":"é"}""";
        await using var server = new SyntheticServer(async (request, stream, cancellation) =>
        {
            string cookie = request.Path.StartsWith("/start", StringComparison.Ordinal)
                ? $"Set-Cookie: session={session}; Path=/; Secure; HttpOnly\r\n" : "";
            await ReplyAsync(stream, "200 OK", cookie, "ok", cancellation);
        });
        List<ProcessStartInfo> launches = [];
        using var client = Client(server, new CookieContainer(), launches.Add);

        Assert.Equal("ok", await client.GetStringAsync(server.Address("/start?token=" + query)));
        using var request = new HttpRequestMessage(HttpMethod.Post, server.Address("/next"))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("scnt", token);
        using HttpResponseMessage response = await client.SendAsync(request);
        Assert.Equal("ok", await response.Content.ReadAsStringAsync());

        RecordedRequest[] sent = server.Requests.ToArray();
        Assert.Equal(2, sent.Length);
        Assert.Contains($"session={session}", sent[1].Headers["Cookie"]);
        Assert.Equal(token, sent[1].Headers["scnt"]);
        Assert.Equal(body, Encoding.UTF8.GetString(sent[1].Body));
        Assert.All(launches, launch =>
        {
            Assert.Empty(launch.Environment);
            string arguments = string.Join(' ', launch.ArgumentList);
            Assert.DoesNotContain(query, arguments);
            Assert.DoesNotContain(session, arguments);
            Assert.DoesNotContain(token, arguments);
            Assert.DoesNotContain("synthetic-body-secret", arguments);
            Assert.StartsWith("-q ", arguments);
        });
    }

    [Fact]
    public async Task BodyBeginningWithAtSignIsNotReadAsAFile()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        await using var server = new SyntheticServer((_, stream, cancellation) =>
            ReplyAsync(stream, "200 OK", "", "ok", cancellation));
        using var client = Client(server, new CookieContainer());

        using HttpResponseMessage response = await client.PostAsync(server.Address("/submit"),
            new StringContent("@synthetic-not-a-file"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("@synthetic-not-a-file", Encoding.UTF8.GetString(Assert.Single(server.Requests).Body));
    }

    [Fact]
    public async Task SendsAnEmptyPostBody()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        await using var server = new SyntheticServer((_, stream, cancellation) =>
            ReplyAsync(stream, "200 OK", "", "ok", cancellation));
        using var client = Client(server, new CookieContainer());

        using HttpResponseMessage response = await client.PostAsync(server.Address("/"), new ByteArrayContent([]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(Assert.Single(server.Requests).Body);
    }

    [Fact]
    public async Task AuthenticatedCatalogPostsThroughTheIsolatedTransport()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        const string data = """
            {"resultCode":0,"downloads":[{"name":"Xcode 16.4","files":[
              {"remotePath":"/Developer_Tools/Xcode_16.4/Xcode_16.4.xip","fileSize":12345}
            ]}]}
            """;
        await using var server = new SyntheticServer((_, stream, cancellation) =>
            ReplyAsync(stream, "200 OK", "Content-Type: application/json\r\n", data, cancellation));
        using var client = new HttpClient(new RewriteToLoopback(server.Address("/catalog"), Transport(server, new CookieContainer())));

        AppleXcodeDownload result = Assert.Single(await new AppleDownloadsCatalog(client).GetAsync(CancellationToken.None));

        RecordedRequest request = Assert.Single(server.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal("/catalog", request.Path);
        Assert.Empty(request.Body);
        Assert.Equal("16.4", result.Version);
        Assert.Equal(12345, result.SizeBytes);
    }

    [Fact]
    public async Task ResumableArchiveSendsItsRangeAndValidatorThroughTheIsolatedTransport()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        const string archive = "0123456789";
        const string tag = "\"synthetic-release\"";
        await using var server = new SyntheticServer((request, stream, cancellation) =>
            request.Path == "/authorize"
                ? ReplyAsync(stream, "200 OK", "", "", cancellation)
                : ReplyAsync(stream, "206 Partial Content", $"Content-Range: bytes 3-9/10\r\nETag: {tag}\r\n", archive[3..], cancellation));
        using var directory = new TemporaryDirectory();
        using var client = new HttpClient(new RewriteDownloadToLoopback(server, Transport(server, new CookieContainer())));
        var address = new Uri("https://download.developer.apple.com/Developer_Tools/Xcode_26.4/Xcode_26.4.xip");
        string cache = XcodeArchiveDownloader.CacheDirectory(directory.Path, address);
        Directory.CreateDirectory(cache);
        await File.WriteAllTextAsync(Path.Combine(cache, "Xcode_26.4.xip.part"), archive[..3]);
        await File.WriteAllTextAsync(Path.Combine(cache, "validator"), tag);

        string path = await new XcodeArchiveDownloader(client, () => directory.Path)
            .DownloadAsync(address, null, _ => { }, CancellationToken.None);

        Assert.Equal(archive, await File.ReadAllTextAsync(path));
        RecordedRequest[] requests = server.Requests.ToArray();
        Assert.Equal([("POST", "/authorize"), ("GET", "/archive")], requests.Select(request => (request.Method, request.Path)));
        Assert.Equal("bytes=3-", requests[1].Headers["Range"]);
        Assert.Equal(tag, requests[1].Headers["If-Range"]);
    }

    [Fact]
    public async Task VersionOneAndCommaCookiesAreSentAsPlainRfc6265Pairs()
    {
        var cookies = new CookieContainer { PerDomainCapacity = 100 };
        var authUri = AppleDownloadsCatalog.DownloadsUri;
        var downloadUri = new Uri("https://download.developer.apple.com/Developer_Tools/Xcode_27.3_beta/Xcode_27.3_beta.xip");

        AppleCurlTransport.StoreSetCookie(cookies, authUri,
            "ADCDownloadAuth=tok%2B1%3D; Version=1; Comment=; Domain=apple.com; Path=/; Max-Age=108000; Secure; HttpOnly");
        AppleCurlTransport.StoreSetCookie(cookies, authUri, "geo=CA,QC; Domain=.apple.com; Path=/; Secure");

        string header = AppleCurlTransport.FormatCookieHeader(cookies, downloadUri);
        Assert.Contains("ADCDownloadAuth=tok%2B1%3D", header);
        Assert.Contains("geo=CA,QC", header);
        Assert.DoesNotContain("$Version", header);
        Assert.DoesNotContain("$Path", header);
        Assert.DoesNotContain("$Domain", header);

        if (!OperatingSystem.IsMacOS())
            return;
        await using var server = new SyntheticServer(async (request, stream, cancellation) =>
        {
            string setCookies = request.Path == "/authorize"
                ? "Set-Cookie: ADCDownloadAuth=live%2Btok%3D; Version=1; Domain=127.0.0.1; Path=/; Max-Age=3600; Secure; HttpOnly\r\n"
                  + "Set-Cookie: geo=CA,QC; Path=/; Secure\r\n"
                : "";
            await ReplyAsync(stream, "200 OK", setCookies, "archive-bytes", cancellation);
        });
        using var directory = new TemporaryDirectory();
        using var client = new HttpClient(new RewriteDownloadToLoopback(server, Transport(server, new CookieContainer())));

        string path = await new XcodeArchiveDownloader(client, () => directory.Path)
            .DownloadAsync(downloadUri, null, _ => { }, CancellationToken.None);

        Assert.Equal("archive-bytes", await File.ReadAllTextAsync(path));
        string sentCookie = server.Requests.ToArray()[1].Headers["Cookie"];
        Assert.Contains("ADCDownloadAuth=live%2Btok%3D", sentCookie);
        Assert.Contains("geo=CA,QC", sentCookie);
        Assert.DoesNotContain("$Version", sentCookie);
        Assert.DoesNotContain("$Path", sentCookie);
        Assert.DoesNotContain("$Domain", sentCookie);
    }

    [Fact]
    public async Task RejectsOversizedRequestBodiesBeforeStartingCurl()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        await using var server = new SyntheticServer((_, stream, cancellation) =>
            ReplyAsync(stream, "200 OK", "", "ok", cancellation));
        bool started = false;
        using var client = Client(server, new CookieContainer(), _ => started = true);
        using var body = new ByteArrayContent(new byte[1024 * 1024 + 1]);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.PostAsync(server.Address("/"), body));

        Assert.False(started);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task AnAlreadyCancelledRequestDoesNotStartCurl()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        bool started = false;
        using var client = new HttpClient(new AppleCurlTransport(new CookieContainer(),
            new AppleCurlTransportOptions { BeforeStart = _ => started = true }));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GetAsync("https://idmsa.apple.com/", cancellation.Token));

        Assert.False(started);
    }

    [Theory]
    [InlineData("https://example.com/")]
    [InlineData("https://idmsa.apple.com.example.com/")]
    [InlineData("https://idmsa.apple.com@evil.example/")]
    [InlineData("http://idmsa.apple.com/")]
    [InlineData("https://idmsa.apple.com:444/")]
    public async Task RejectsUnapprovedAddressesBeforeStartingCurl(string address)
    {
        if (!OperatingSystem.IsMacOS())
            return;
        bool started = false;
        using var client = new HttpClient(new AppleCurlTransport(new CookieContainer(),
            new AppleCurlTransportOptions { BeforeStart = _ => started = true }));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(address));

        Assert.False(started);
    }

    [Fact]
    public async Task FollowsOnlySameOriginGetRedirectsAndRescopesCookies()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        await using var server = new SyntheticServer(async (request, stream, cancellation) =>
        {
            if (request.Path == "/first")
                await ReplyAsync(stream, "302 Found", "Location: /next\r\nSet-Cookie: scoped=one; Path=/next; Secure\r\n", "", cancellation);
            else
                await ReplyAsync(stream, "200 OK", "", "done", cancellation);
        });
        using var client = Client(server, new CookieContainer());

        Assert.Equal("done", await client.GetStringAsync(server.Address("/first")));

        RecordedRequest[] requests = server.Requests.ToArray();
        Assert.Equal(["/first", "/next"], requests.Select(request => request.Path));
        Assert.DoesNotContain("Cookie", requests[0].Headers.Keys);
        Assert.Contains("scoped=one", requests[1].Headers["Cookie"]);
    }

    [Fact]
    public async Task PublicWidgetLookupNeitherSendsNorStoresCookiesAndNeverFollowsRedirects()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        await using var server = new SyntheticServer((_, stream, cancellation) =>
            ReplyAsync(stream, "302 Found", "Location: /signout\r\nSet-Cookie: injected=other; Path=/; Secure\r\n", "", cancellation));
        var cookies = new CookieContainer();
        cookies.Add(server.Address("/"), new Cookie("saved", "synthetic-account-cookie", "/") { Secure = true });
        AppleCurlTransportOptions options = AppleAuthClient.ServiceKeyTransportOptions with
        {
            AllowAddress = uri => uri.Host == "127.0.0.1" && uri.Port == server.Port,
            CertificateAuthority = server.CertificateFile,
            HeaderTimeout = TimeSpan.FromSeconds(10),
        };
        using var client = new HttpClient(new AppleCurlTransport(cookies, options));

        using HttpResponseMessage response = await client.GetAsync(server.Address("/logout"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        RecordedRequest request = Assert.Single(server.Requests);
        Assert.Equal("/logout", request.Path);
        Assert.DoesNotContain("Cookie", request.Headers.Keys);
        Assert.Equal("saved=synthetic-account-cookie", cookies.GetCookieHeader(server.Address("/")));
    }

    [Theory]
    [InlineData("GET", "https://example.com/stolen")]
    [InlineData("POST", "/other")]
    public async Task RefusesCrossOriginRedirectsAndNeverReplaysPosts(string method, string target)
    {
        if (!OperatingSystem.IsMacOS())
            return;
        await using var server = new SyntheticServer((_, stream, cancellation) =>
            ReplyAsync(stream, "302 Found", $"Location: {target}\r\n", "", cancellation));
        using var client = Client(server, new CookieContainer());
        using var request = new HttpRequestMessage(new HttpMethod(method), server.Address("/start"));
        if (method == "POST")
            request.Content = new StringContent("""{"proof":"synthetic-secret"}""", Encoding.UTF8, "application/json");

        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(request));

        await Task.Delay(150);
        Assert.Single(server.Requests);
    }

    [Theory]
    [InlineData("GET", false)]
    [InlineData("POST", false)]
    [InlineData("POST", true)]
    public async Task DoesNotReplayWhenTheServerDropsTheConnection(string method, bool hasBody)
    {
        if (!OperatingSystem.IsMacOS())
            return;
        await using var server = new SyntheticServer((_, _, _) => Task.CompletedTask);
        using var client = Client(server, new CookieContainer());
        using var request = new HttpRequestMessage(new HttpMethod(method), server.Address("/drop"));
        if (hasBody)
            request.Content = new StringContent("""{"proof":"synthetic-secret"}""", Encoding.UTF8, "application/json");

        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(request));

        await Task.Delay(250);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task RejectsForeignDomainCookies()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        await using var server = new SyntheticServer((_, stream, cancellation) =>
            ReplyAsync(stream, "200 OK", "Set-Cookie: stolen=value; Domain=other.invalid; Secure\r\n", "ok", cancellation));
        var cookies = new CookieContainer();
        using var client = Client(server, cookies);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(server.Address("/")));

        Assert.Empty(cookies.GetAllCookies());
    }

    [Fact]
    public async Task RejectsOversizedResponseHeaders()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        await using var server = new SyntheticServer((_, stream, cancellation) =>
            ReplyAsync(stream, "200 OK", "X-Large: " + new string('x', 70_000) + "\r\n", "ok", cancellation));
        using var client = Client(server, new CookieContainer());

        HttpRequestException error = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(server.Address("/")));

        Assert.Contains("headers", error.Message);
    }

    [Fact]
    public async Task RejectsUnexpectedResponseEncoding()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        await using var server = new SyntheticServer((_, stream, cancellation) =>
            ReplyAsync(stream, "200 OK", "Content-Encoding: gzip\r\n", "not compressed", cancellation));
        using var client = Client(server, new CookieContainer());

        HttpRequestException error = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(server.Address("/")));

        Assert.Contains("encoding", error.Message);
    }

    [Fact]
    public async Task PreservesPartialContentHeadersAndBodyForResumableDownloads()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        await using var server = new SyntheticServer((_, stream, cancellation) =>
            ReplyAsync(stream, "206 Partial Content", "Content-Range: bytes 3-5/6\r\n", "def", cancellation));
        using var client = Client(server, new CookieContainer());
        using var request = new HttpRequestMessage(HttpMethod.Get, server.Address("/archive"));
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(3, null);

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("bytes 3-5/6", response.Content.Headers.ContentRange?.ToString());
        Assert.Equal("def", await response.Content.ReadAsStringAsync());
        Assert.Equal("bytes=3-", Assert.Single(server.Requests).Headers["Range"]);
    }

    [Fact]
    public async Task ReportsTruncatedResponseBodies()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        await using var server = new SyntheticServer(async (_, stream, cancellation) =>
        {
            await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 100\r\nConnection: close\r\n\r\nshort"u8.ToArray(), cancellation);
            await stream.FlushAsync(cancellation);
        });
        using var client = Client(server, new CookieContainer());

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStringAsync(server.Address("/")));
    }

    [Fact]
    public async Task DotNetHttpDiagnosticsDoNotSeeTheSyntheticSecrets()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        const string secret = "synthetic-diagnostics-secret";
        await using var server = new SyntheticServer((_, stream, cancellation) =>
            ReplyAsync(stream, "200 OK", $"Set-Cookie: session={secret}; Secure\r\n", "ok", cancellation));
        using var events = new HttpDiagnosticProbe();
        using var client = Client(server, new CookieContainer());

        Assert.Equal("ok", await client.GetStringAsync(server.Address("/?token=" + secret)));

        Assert.DoesNotContain(secret, events.Output);
    }

    [Fact]
    public async Task DoesNotInheritTlsKeyLogSettings()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        using var files = new TemporaryDirectory();
        string keyLog = Path.Combine(files.Path, "synthetic-tls-keys.log");
        string? previous = Environment.GetEnvironmentVariable("SSLKEYLOGFILE");
        Environment.SetEnvironmentVariable("SSLKEYLOGFILE", keyLog);
        try
        {
            await using var server = new SyntheticServer((_, stream, cancellation) =>
                ReplyAsync(stream, "200 OK", "", "ok", cancellation));
            using var client = Client(server, new CookieContainer());

            Assert.Equal("ok", await client.GetStringAsync(server.Address("/")));
            Assert.False(File.Exists(keyLog));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SSLKEYLOGFILE", previous);
        }
    }

    [Fact]
    public async Task CancellingAStreamingReadStopsTheTransfer()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new SyntheticServer(async (_, stream, cancellation) =>
        {
            await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 100\r\n\r\n"u8.ToArray(), cancellation);
            await stream.FlushAsync(cancellation);
            entered.SetResult();
            await WaitForDisconnectAsync(stream, closed, cancellation);
        });
        using var client = Client(server, new CookieContainer());
        using HttpResponseMessage response = await client.GetAsync(server.Address("/"), HttpCompletionOption.ResponseHeadersRead);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await using Stream input = await response.Content.ReadAsStreamAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await input.ReadExactlyAsync(new byte[16], cancellation.Token));

        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(server.Requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DisposingAResponseOrClientStopsAnAbandonedTransfer(bool disposeClient)
    {
        if (!OperatingSystem.IsMacOS())
            return;
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new SyntheticServer(async (_, stream, cancellation) =>
        {
            await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 100\r\n\r\n"u8.ToArray(), cancellation);
            await stream.FlushAsync(cancellation);
            await WaitForDisconnectAsync(stream, closed, cancellation);
        });
        using var client = Client(server, new CookieContainer());
        using HttpResponseMessage response = await client.GetAsync(server.Address("/"), HttpCompletionOption.ResponseHeadersRead);

        if (disposeClient)
            client.Dispose();
        else
            response.Dispose();

        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task TimesOutWhenTheServerDoesNotSendHeaders()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new SyntheticServer((_, stream, cancellation) =>
            WaitForDisconnectAsync(stream, closed, cancellation));
        using var client = Client(server, new CookieContainer(), headerTimeout: TimeSpan.FromMilliseconds(200));

        HttpRequestException error = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(server.Address("/")));

        Assert.Contains("time", error.Message);
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task WaitForDisconnectAsync(Stream stream, TaskCompletionSource closed, CancellationToken cancellationToken)
    {
        try
        {
            byte[] buffer = new byte[1];
            while (await stream.ReadAsync(buffer, cancellationToken) != 0) { }
        }
        catch (IOException)
        {
        }
        closed.TrySetResult();
    }

    private static HttpClient Client(
        SyntheticServer server, CookieContainer cookies, Action<ProcessStartInfo>? beforeStart = null, TimeSpan? headerTimeout = null)
        => new(Transport(server, cookies, beforeStart, headerTimeout));

    private static AppleCurlTransport Transport(
        SyntheticServer server, CookieContainer cookies, Action<ProcessStartInfo>? beforeStart = null, TimeSpan? headerTimeout = null)
        => new(cookies, new AppleCurlTransportOptions
        {
            AllowAddress = uri => uri.Host == "127.0.0.1" && uri.Port == server.Port,
            CertificateAuthority = server.CertificateFile,
            BeforeStart = beforeStart,
            HeaderTimeout = headerTimeout ?? TimeSpan.FromSeconds(10),
        });

    private static async Task ReplyAsync(Stream stream, string status, string headers, string body, CancellationToken cancellationToken)
    {
        byte[] content = Encoding.UTF8.GetBytes(body);
        byte[] head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status}\r\n{headers}Content-Length: {content.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head, cancellationToken);
        await stream.WriteAsync(content, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private sealed record RecordedRequest(string Method, string Path, IReadOnlyDictionary<string, string> Headers, byte[] Body);

    private sealed class RewriteToLoopback(Uri address, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(AppleDownloadsCatalog.DownloadsUri, request.RequestUri);
            request.RequestUri = address;
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class RewriteDownloadToLoopback(SyntheticServer server, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri switch
            {
                { } uri when uri == AppleDownloadsCatalog.DownloadsUri => "/authorize",
                { Host: "download.developer.apple.com" } => "/archive",
                _ => throw new InvalidOperationException("Unexpected synthetic download host."),
            };
            request.RequestUri = server.Address(path);
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class SyntheticServer : IAsyncDisposable
    {
        private readonly TemporaryDirectory _directory = new();
        private readonly RSA _key = RSA.Create(2048);
        private readonly X509Certificate2 _certificate;
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Func<RecordedRequest, Stream, CancellationToken, Task> _reply;
        private readonly ConcurrentBag<Task> _connections = [];
        private readonly Task _loop;

        public SyntheticServer(Func<RecordedRequest, Stream, CancellationToken, Task> reply)
        {
            _reply = reply;
            var request = new CertificateRequest("CN=127.0.0.1", _key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder();
            names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyCertSign, true));
            _certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
            CertificateFile = Path.Combine(_directory.Path, "synthetic-ca.pem");
            File.WriteAllText(CertificateFile, _certificate.ExportCertificatePem());
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = AcceptAsync();
        }

        public int Port { get; }
        public string CertificateFile { get; }
        public ConcurrentQueue<RecordedRequest> Requests { get; } = new();
        public Uri Address(string path) => new($"https://127.0.0.1:{Port}{path}");

        private async Task AcceptAsync()
        {
            try
            {
                while (true)
                {
                    TcpClient socket = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _connections.Add(HandleAsync(socket));
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
        }

        private async Task HandleAsync(TcpClient socket)
        {
            using (socket)
            await using (var tls = new SslStream(socket.GetStream()))
            {
                try
                {
                    await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate = _certificate,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    }, _stop.Token);
                    RecordedRequest request = await ReadRequestAsync(tls, _stop.Token);
                    Requests.Enqueue(request);
                    await _reply(request, tls, _stop.Token);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
                catch (IOException) when (_stop.IsCancellationRequested) { }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            await _loop;
            await Task.WhenAll(_connections);
            _certificate.Dispose();
            _key.Dispose();
            _stop.Dispose();
            _directory.Dispose();
        }

        private static async Task<RecordedRequest> ReadRequestAsync(Stream stream, CancellationToken cancellationToken)
        {
            using var data = new MemoryStream();
            byte[] one = new byte[1];
            int suffix = 0;
            while (suffix != 0x0d0a0d0a)
            {
                if (await stream.ReadAsync(one, cancellationToken) == 0 || data.Length > 64 * 1024)
                    throw new IOException("The synthetic request headers were incomplete.");
                data.WriteByte(one[0]);
                suffix = (suffix << 8) | one[0];
            }
            string[] lines = Encoding.Latin1.GetString(data.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            string[] requestLine = lines[0].Split(' ');
            string method = requestLine[0];
            string path = requestLine[1];
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in lines.Skip(1))
            {
                int colon = line.IndexOf(':');
                if (colon > 0)
                    headers[line[..colon]] = line[(colon + 1)..].Trim();
            }
            int length = headers.TryGetValue("Content-Length", out string? value) ? int.Parse(value) : 0;
            byte[] body = new byte[length];
            await stream.ReadExactlyAsync(body, cancellationToken);
            return new RecordedRequest(method, path, headers, body);
        }
    }

    private sealed class HttpDiagnosticProbe : EventListener
    {
        private readonly StringBuilder _output = new();

        public string Output
        {
            get { lock (_output) return _output.ToString(); }
        }

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "Private.InternalDiagnostics.System.Net.Http")
                EnableEvents(eventSource, EventLevel.Verbose, EventKeywords.All);
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            lock (_output)
            {
                if (eventData.Payload is not null)
                    foreach (object? item in eventData.Payload)
                        _output.Append(item);
            }
        }
    }
}
