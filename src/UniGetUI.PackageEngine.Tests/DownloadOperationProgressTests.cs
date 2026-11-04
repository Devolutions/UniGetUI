using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Operations;
using UniGetUI.PackageEngine.Tests.Infrastructure.Builders;
using UniGetUI.PackageOperations;

namespace UniGetUI.PackageEngine.Tests;

/// <summary>
/// Proves <see cref="DownloadOperation"/> reports bounded determinate progress:
/// one structured report per integer percent (not per socket read), with 0→100
/// propagation and usable throughput under the reduced sampling rate.
/// Uses an in-memory HTTP handler so no loopback server is needed.
/// </summary>
public sealed class DownloadOperationProgressTests
{
    private sealed class ManualTimestampClock
    {
        private long _ticks;

        public long Now() => _ticks;

        public void Advance(TimeSpan delta) =>
            _ticks += (long)(delta.TotalSeconds * Stopwatch.Frequency);
    }

    private sealed class FragmentedReadStream(byte[] payload, int maxChunk, ManualTimestampClock? clock, TimeSpan perRead)
        : MemoryStream(payload, writable: false)
    {
        private readonly ManualTimestampClock? _clock = clock;
        public int ReadCalls;

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            int count = await base.ReadAsync(
                buffer[..Math.Min(buffer.Length, maxChunk)],
                cancellationToken
            );
            if (count > 0)
            {
                ReadCalls++;
                _clock?.Advance(perRead);
            }

            return count;
        }
    }

    private sealed class FakeDownloadHandler(
        byte[] payload,
        int maxChunk,
        ManualTimestampClock? clock,
        TimeSpan perRead,
        FragmentedReadStream? streamSink
    ) : HttpMessageHandler
    {
        public int StreamReadCalls => _stream?.ReadCalls ?? 0;
        public int Requests { get; private set; }
        private FragmentedReadStream? _stream = streamSink;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Requests++;
            var stream = new FragmentedReadStream(payload, maxChunk, clock, perRead);
            _stream = stream;
            var content = new StreamContent(stream);
            content.Headers.ContentLength = payload.Length;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            return Task.FromResult(response);
        }
    }

    private sealed class ProbeDownloadOperation(
        IPackage package,
        string downloadPath,
        HttpMessageHandler handler
    ) : DownloadOperation(package, downloadPath)
    {
        private readonly HttpMessageHandler _handler = handler;

        protected override HttpClient CreateHttpClient() =>
            new(_handler, disposeHandler: false);

        public SignatureCheck? Signature { get; set; }

        public TimeSpan SignatureDelay { get; set; }

        protected override SignatureCheck? CheckSignature(string path)
        {
            Thread.Sleep(SignatureDelay);
            return Signature;
        }

        public void ApplyRetryActionForTests(string retryMode) => ApplyRetryAction(retryMode);

        public Task<OperationVeredict> InvokePerformOperationForTests() =>
            PerformOperation();
    }

    private static (IPackage Package, ManualTimestampClock Clock) CreatePackage(
        out ManualTimestampClock clock
    )
    {
        clock = new ManualTimestampClock();
        var manager = new PackageManagerBuilder()
            .ConfigureDetails(helper =>
            {
                helper.PopulateDetails = details =>
                {
                    details.InstallerUrl = new Uri("http://127.0.0.1/payload.bin");
                    details.InstallerType = "exe";
                };
            })
            .Build();
        IPackage package = new PackageBuilder().WithManager(manager).Build();
        return (package, clock);
    }

    [Fact]
    public async Task ProgressEvents_BoundedByIntegerPercent_NotPerRead()
    {
        const int TotalBytes = 1024 * 1024;
        const int ChunkBytes = 1024;
        byte[] payload = new byte[TotalBytes];
        new Random(42).NextBytes(payload);

        var (package, clock) = CreatePackage(out _);
        var handler = new FakeDownloadHandler(payload, ChunkBytes, clock, TimeSpan.FromMilliseconds(5), null);
        string downloadPath = Path.Join(
            Path.GetTempPath(),
            $"unigetui-bounded-{Guid.NewGuid():N}.bin"
        );
        try
        {
            using var operation = new ProbeDownloadOperation(package, downloadPath, handler);
            operation.SetTimestampProviderForTests(clock.Now);
            var seen = new List<OperationProgress>();
            operation.ProgressChanged += (_, p) => seen.Add(p);

            OperationVeredict verdict = await operation.InvokePerformOperationForTests();

            Assert.Equal(OperationVeredict.Success, verdict);
            int readCalls = handler.StreamReadCalls;
            Assert.True(readCalls > 500, $"expected many small reads, got {readCalls}");

            // One stage marker + at most one report per integer percent.
            var determinate = seen.Where(static p => p.IsDeterminate).ToArray();
            Assert.True(seen.Count <= 102, $"expected bounded reports, got {seen.Count}");
            Assert.True(seen.Count < readCalls);
            Assert.Equal(
                determinate.Select(static p => Math.Round(p.Percentage!.Value)).Distinct().Count(),
                determinate.Length
            );
        }
        finally
        {
            if (File.Exists(downloadPath))
                File.Delete(downloadPath);
        }
    }

    [Fact]
    public async Task ProgressEvents_PropagateZeroAndHundred()
    {
        const int TotalBytes = 1024 * 1024;
        byte[] payload = new byte[TotalBytes];
        new Random(7).NextBytes(payload);

        var (package, clock) = CreatePackage(out _);
        var handler = new FakeDownloadHandler(payload, 1024, clock, TimeSpan.FromMilliseconds(5), null);
        string downloadPath = Path.Join(
            Path.GetTempPath(),
            $"unigetui-zero-hundred-{Guid.NewGuid():N}.bin"
        );
        try
        {
            using var operation = new ProbeDownloadOperation(package, downloadPath, handler);
            operation.SetTimestampProviderForTests(clock.Now);
            var seen = new List<OperationProgress>();
            operation.ProgressChanged += (_, p) => seen.Add(p);

            Assert.Equal(
                OperationVeredict.Success,
                await operation.InvokePerformOperationForTests()
            );

            var determinate = seen.Where(static p => p.IsDeterminate).ToArray();
            Assert.NotEmpty(determinate);
            Assert.Equal(0, Math.Round(determinate.First().Percentage!.Value));
            Assert.Equal(100, Math.Round(determinate.Last().Percentage!.Value));
            Assert.Equal(100, Math.Round(operation.CurrentProgress.Percentage!.Value));
        }
        finally
        {
            if (File.Exists(downloadPath))
                File.Delete(downloadPath);
        }
    }

    [Fact]
    public async Task Throughput_RemainsUsable_UnderReducedSampling()
    {
        const int TotalBytes = 1024 * 1024;
        byte[] payload = new byte[TotalBytes];
        new Random(11).NextBytes(payload);

        var (package, clock) = CreatePackage(out _);
        var handler = new FakeDownloadHandler(payload, 1024, clock, TimeSpan.FromMilliseconds(5), null);
        string downloadPath = Path.Join(
            Path.GetTempPath(),
            $"unigetui-throughput-{Guid.NewGuid():N}.bin"
        );
        try
        {
            using var operation = new ProbeDownloadOperation(package, downloadPath, handler);
            operation.SetTimestampProviderForTests(clock.Now);

            Assert.Equal(
                OperationVeredict.Success,
                await operation.InvokePerformOperationForTests()
            );

            Assert.True(operation.CurrentProgress.HasThroughput);
            Assert.Contains("/s", OperationProgressFormatter.Format(operation.CurrentProgress));
        }
        finally
        {
            if (File.Exists(downloadPath))
                File.Delete(downloadPath);
        }
    }

    [Fact]
    public async Task LocalFeedInstallers_AreCopiedFromDiskWithoutHttp()
    {
        byte[] payload = new byte[64 * 1024];
        new Random(23).NextBytes(payload);

        string sourcePath = Path.Join(
            Path.GetTempPath(),
            $"unigetui-local-source-{Guid.NewGuid():N}.nupkg"
        );
        string downloadPath = Path.Join(
            Path.GetTempPath(),
            $"unigetui-local-copy-{Guid.NewGuid():N}.nupkg"
        );
        File.WriteAllBytes(sourcePath, payload);

        var manager = new PackageManagerBuilder()
            .ConfigureDetails(helper =>
            {
                helper.PopulateDetails = details =>
                {
                    details.InstallerUrl = new Uri(sourcePath);
                    details.InstallerType = "nupkg";
                };
            })
            .Build();
        IPackage package = new PackageBuilder()
            .WithManager(manager)
            .WithSource(
                new SourceBuilder()
                    .WithManager(manager)
                    .WithUrl(new Uri(Path.GetDirectoryName(sourcePath)!).AbsoluteUri)
                    .Build()
            )
            .Build();

        try
        {
            using var operation = new ProbeDownloadOperation(
                package,
                downloadPath,
                new UnreachableHandler()
            );

            Assert.Equal(
                OperationVeredict.Success,
                await operation.InvokePerformOperationForTests()
            );
            Assert.Equal(payload, File.ReadAllBytes(downloadPath));
            Assert.Equal(100, Math.Round(operation.CurrentProgress.Percentage!.Value));
        }
        finally
        {
            File.Delete(sourcePath);
            if (File.Exists(downloadPath))
                File.Delete(downloadPath);
        }
    }

    [Fact]
    public async Task LocalFeedInstallers_RefuseToOverwriteTheSourceFile()
    {
        byte[] payload = new byte[2048];
        new Random(31).NextBytes(payload);

        string sourcePath = Path.Join(
            Path.GetTempPath(),
            $"unigetui-same-file-{Guid.NewGuid():N}.nupkg"
        );
        File.WriteAllBytes(sourcePath, payload);

        var manager = new PackageManagerBuilder()
            .ConfigureDetails(helper =>
            {
                helper.PopulateDetails = details =>
                {
                    details.InstallerUrl = new Uri(sourcePath);
                    details.InstallerType = "nupkg";
                };
            })
            .Build();
        IPackage package = new PackageBuilder()
            .WithManager(manager)
            .WithSource(
                new SourceBuilder()
                    .WithManager(manager)
                    .WithUrl(new Uri(Path.GetDirectoryName(sourcePath)!).AbsoluteUri)
                    .Build()
            )
            .Build();

        try
        {
            using var operation = new ProbeDownloadOperation(
                package,
                sourcePath,
                new UnreachableHandler()
            );

            Assert.Equal(
                OperationVeredict.Failure,
                await operation.InvokePerformOperationForTests()
            );
            Assert.Equal(payload, File.ReadAllBytes(sourcePath));
            Assert.Contains(
                operation.GetOutput(),
                line => line.Item1.Contains("is the package file itself")
            );
            Assert.DoesNotContain(
                operation.GetOutput(),
                line => line.Item1.Contains("System.IO.IOException")
            );
        }
        finally
        {
            File.Delete(sourcePath);
        }
    }

    [Fact]
    public async Task AFileInstallerUrlIsRefusedWhenTheSourceIsNotTheFolderHoldingIt()
    {
        string secretPath = Path.Join(
            Path.GetTempPath(),
            $"unigetui-secret-{Guid.NewGuid():N}.bin"
        );
        string downloadPath = Path.Join(
            Path.GetTempPath(),
            $"unigetui-stolen-{Guid.NewGuid():N}.bin"
        );
        File.WriteAllBytes(secretPath, [1, 2, 3, 4]);

        var manager = new PackageManagerBuilder()
            .ConfigureDetails(helper =>
            {
                helper.PopulateDetails = details =>
                {
                    details.InstallerUrl = new Uri(secretPath);
                    details.InstallerType = "exe";
                };
            })
            .Build();
        var remoteSource = new SourceBuilder()
            .WithManager(manager)
            .WithUrl("https://packages.example.test/api/v2/")
            .Build();
        IPackage package = new PackageBuilder()
            .WithManager(manager)
            .WithSource(remoteSource)
            .Build();

        try
        {
            using var operation = new ProbeDownloadOperation(
                package,
                downloadPath,
                new UnreachableHandler()
            );

            Assert.Equal(
                OperationVeredict.Failure,
                await operation.InvokePerformOperationForTests()
            );
            Assert.False(File.Exists(downloadPath));
            Assert.Contains(
                operation.GetOutput(),
                line => line.Item1.Contains("is not a local folder holding it")
            );
        }
        finally
        {
            File.Delete(secretPath);
            if (File.Exists(downloadPath))
                File.Delete(downloadPath);
        }
    }

    private static IPackage CreatePackageWithHash(string? installerHash, Uri? installerUrl = null)
    {
        var manager = new PackageManagerBuilder()
            .ConfigureDetails(helper =>
            {
                helper.PopulateDetails = details =>
                {
                    details.InstallerUrl = installerUrl ?? new Uri("http://127.0.0.1/payload.bin");
                    details.InstallerType = "exe";
                    details.InstallerHash = installerHash;
                };
            })
            .Build();
        return new PackageBuilder().WithManager(manager).Build();
    }

    [Fact]
    public async Task AnExistingFileMatchingThePublishedHashIsNotDownloadedAgain()
    {
        byte[] payload = new byte[64 * 1024];
        new Random(53).NextBytes(payload);

        IPackage package = CreatePackageWithHash(
            Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant()
        );
        string downloadPath = Path.Join(
            Path.GetTempPath(),
            $"unigetui-already-there-{Guid.NewGuid():N}.bin"
        );
        File.WriteAllBytes(downloadPath, payload);

        try
        {
            using var operation = new ProbeDownloadOperation(
                package,
                downloadPath,
                new UnreachableHandler()
            );

            Assert.Equal(
                OperationVeredict.Success,
                await operation.InvokePerformOperationForTests()
            );
            Assert.Equal(payload, File.ReadAllBytes(downloadPath));
            Assert.Contains(
                operation.GetOutput(),
                line => line.Item1.Contains("the download was skipped")
            );
            Assert.Equal(100, Math.Round(operation.CurrentProgress.Percentage!.Value));
        }
        finally
        {
            File.Delete(downloadPath);
        }
    }

    [Fact]
    public async Task AnExistingFileThatDoesNotMatchThePublishedHashIsDownloadedAgain()
    {
        byte[] payload = new byte[64 * 1024];
        new Random(59).NextBytes(payload);
        byte[] stale = new byte[64 * 1024];
        new Random(61).NextBytes(stale);

        IPackage package = CreatePackageWithHash(Convert.ToHexString(SHA256.HashData(payload)));
        var handler = new FakeDownloadHandler(payload, 4096, null, TimeSpan.Zero, null);
        string downloadPath = Path.Join(
            Path.GetTempPath(),
            $"unigetui-stale-{Guid.NewGuid():N}.bin"
        );
        File.WriteAllBytes(downloadPath, stale);

        try
        {
            using var operation = new ProbeDownloadOperation(package, downloadPath, handler);

            Assert.Equal(
                OperationVeredict.Success,
                await operation.InvokePerformOperationForTests()
            );
            Assert.Equal(payload, File.ReadAllBytes(downloadPath));
        }
        finally
        {
            File.Delete(downloadPath);
        }
    }

    [Fact]
    public async Task AnIntegrityHashFromANonSha256ManagerAlsoSkipsTheDownload()
    {
        byte[] payload = new byte[64 * 1024];
        new Random(67).NextBytes(payload);

        IPackage package = CreatePackageWithHash(
            "sha512-" + Convert.ToBase64String(SHA512.HashData(payload))
        );
        string downloadPath = Path.Join(
            Path.GetTempPath(),
            $"unigetui-integrity-{Guid.NewGuid():N}.bin"
        );
        File.WriteAllBytes(downloadPath, payload);

        try
        {
            using var operation = new ProbeDownloadOperation(
                package,
                downloadPath,
                new UnreachableHandler()
            );

            Assert.Equal(
                OperationVeredict.Success,
                await operation.InvokePerformOperationForTests()
            );
            Assert.Contains(
                operation.GetOutput(),
                line => line.Item1.Contains("the download was skipped")
            );
        }
        finally
        {
            File.Delete(downloadPath);
        }
    }

    private static Uri UniqueInstallerUrl() => new($"http://127.0.0.1/{Guid.NewGuid():N}/payload.bin");

    private static string[] PartialFilesOf(string downloadPath) =>
        Directory.GetFiles(
            Path.GetDirectoryName(downloadPath)!,
            Path.GetFileName(DownloadOperation.StagingPathFor(downloadPath, "*"))
        );

    private sealed class CancelAfterFirstReadHandler(byte[] payload, CancellationTokenSource cancellation)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var content = new StreamContent(new CancelAfterFirstReadStream(payload, cancellation));
            content.Headers.ContentLength = payload.Length;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class CancelAfterFirstReadStream(byte[] payload, CancellationTokenSource cancellation)
        : MemoryStream(payload, writable: false)
    {
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            int count = await base.ReadAsync(buffer[..Math.Min(buffer.Length, 4096)], cancellationToken);
            await cancellation.CancelAsync();
            return count;
        }
    }

    [Fact]
    public void InstallerDownloadsAreNotTransparentlyDecompressed()
    {
        using HttpClientHandler handler = DownloadOperation.CreateDownloadHandler();
        Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression);
    }

    [Fact]
    public async Task CancellingADownloadKeepsTheExistingFileAndRemovesThePartialOne()
    {
        byte[] payload = new byte[256 * 1024];
        new Random(109).NextBytes(payload);
        byte[] existing = new byte[1024];
        new Random(113).NextBytes(existing);

        IPackage package = CreatePackageWithHash(
            Convert.ToHexStringLower(SHA256.HashData(payload)),
            UniqueInstallerUrl()
        );
        using var cancellation = new CancellationTokenSource();
        string downloadPath = Path.Join(Path.GetTempPath(), $"unigetui-cancelled-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(downloadPath, existing);

        try
        {
            using var operation = new ProbeDownloadOperation(
                package,
                downloadPath,
                new CancelAfterFirstReadHandler(payload, cancellation)
            );
            operation.SetRunCancellationSourceForTests(cancellation);

            Assert.Equal(OperationVeredict.Canceled, await operation.InvokePerformOperationForTests());
            Assert.Equal(existing, File.ReadAllBytes(downloadPath));
            Assert.Empty(PartialFilesOf(downloadPath));
        }
        finally
        {
            File.Delete(downloadPath);
        }
    }

    [Fact]
    public async Task AnotherDownloadsStagingFileForTheSameDestinationIsLeftAlone()
    {
        byte[] payload = new byte[16 * 1024];
        new Random(127).NextBytes(payload);
        byte[] othersBytes = new byte[8 * 1024];
        new Random(149).NextBytes(othersBytes);

        IPackage package = CreatePackageWithHash(
            Convert.ToHexStringLower(SHA256.HashData(payload)),
            UniqueInstallerUrl()
        );
        var handler = new FakeDownloadHandler(payload, 4096, null, TimeSpan.Zero, null);
        string downloadPath = Path.Join(Path.GetTempPath(), $"unigetui-shared-target-{Guid.NewGuid():N}.bin");
        string othersStaging = DownloadOperation.StagingPathFor(downloadPath, "0badc0de");
        File.WriteAllBytes(othersStaging, othersBytes);

        try
        {
            using var operation = new ProbeDownloadOperation(package, downloadPath, handler);

            Assert.Equal(OperationVeredict.Success, await operation.InvokePerformOperationForTests());
            Assert.Equal(payload, File.ReadAllBytes(downloadPath));
            Assert.Equal(othersBytes, File.ReadAllBytes(othersStaging));
            Assert.Equal([othersStaging], PartialFilesOf(downloadPath));
        }
        finally
        {
            File.Delete(downloadPath);
            File.Delete(othersStaging);
        }
    }

    private sealed class ReloadDetailsDuringDownloadHandler(HttpMessageHandler inner, Action reload)
        : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            reload();
            return base.SendAsync(request, cancellationToken);
        }
    }

    [Fact]
    public async Task TheResultIsRecordedForTheInstallerThatWasActuallyChecked()
    {
        byte[] payload = new byte[16 * 1024];
        new Random(163).NextBytes(payload);
        Uri installerUrl = UniqueInstallerUrl();
        string publishedHash = Convert.ToHexStringLower(SHA256.HashData(payload));
        Uri reloadedUrl = UniqueInstallerUrl();
        string reloadedHash = Convert.ToHexStringLower(SHA256.HashData([1, 2, 3]));

        IPackage package = CreatePackageWithHash(publishedHash, installerUrl);
        var handler = new ReloadDetailsDuringDownloadHandler(
            new FakeDownloadHandler(payload, 4096, null, TimeSpan.Zero, null),
            () =>
            {
                package.Details.InstallerUrl = reloadedUrl;
                package.Details.InstallerHash = reloadedHash;
            }
        );
        string downloadPath = Path.Join(Path.GetTempPath(), $"unigetui-reloaded-{Guid.NewGuid():N}.bin");

        try
        {
            using var operation = new ProbeDownloadOperation(package, downloadPath, handler);

            Assert.Equal(OperationVeredict.Success, await operation.InvokePerformOperationForTests());
            Assert.Equal(
                InstallerHashStatus.Verified,
                InstallerVerification.Find(installerUrl, publishedHash)?.HashStatus
            );
            Assert.Null(InstallerVerification.Find(reloadedUrl, reloadedHash));
        }
        finally
        {
            File.Delete(downloadPath);
        }
    }

    [Fact]
    public async Task ASkipRetryForADifferentInstallerDoesNotReuseTheRejectedFile()
    {
        byte[] payload = new byte[16 * 1024];
        new Random(167).NextBytes(payload);
        Uri firstUrl = UniqueInstallerUrl();
        string firstHash = Convert.ToHexStringLower(SHA256.HashData([1]));
        Uri secondUrl = UniqueInstallerUrl();
        string secondHash = Convert.ToHexStringLower(SHA256.HashData([2]));
        (Uri Url, string Hash) current = (firstUrl, firstHash);

        var manager = new PackageManagerBuilder()
            .ConfigureDetails(helper =>
            {
                helper.PopulateDetails = details =>
                {
                    details.InstallerUrl = current.Url;
                    details.InstallerType = "exe";
                    details.InstallerHash = current.Hash;
                };
            })
            .Build();
        IPackage package = new PackageBuilder().WithManager(manager).Build();
        var handler = new FakeDownloadHandler(payload, 4096, null, TimeSpan.Zero, null);
        string downloadPath = Path.Join(Path.GetTempPath(), $"unigetui-installer-changed-{Guid.NewGuid():N}.bin");

        try
        {
            using var operation = new ProbeDownloadOperation(package, downloadPath, handler);

            Assert.Equal(OperationVeredict.Failure, await operation.InvokePerformOperationForTests());
            current = (secondUrl, secondHash);
            operation.ApplyRetryActionForTests(AbstractOperation.RetryMode.Retry_SkipIntegrity);

            Assert.Equal(OperationVeredict.Success, await operation.InvokePerformOperationForTests());
            Assert.Equal(2, handler.Requests);
            Assert.Contains(operation.GetOutput(), line => line.Item1.Contains("changed since the rejected attempt"));
            Assert.Equal(
                InstallerHashStatus.MismatchKept,
                InstallerVerification.Find(secondUrl, secondHash)?.HashStatus
            );
            Assert.Empty(PartialFilesOf(downloadPath));
        }
        finally
        {
            File.Delete(downloadPath);
            foreach (string staging in PartialFilesOf(downloadPath))
                File.Delete(staging);
        }
    }

    private sealed class EncodedResponseHandler(byte[] body, string contentEncoding) : HttpMessageHandler
    {
        public string? AcceptEncoding { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            AcceptEncoding = request.Headers.AcceptEncoding.ToString();
            var content = new ByteArrayContent(body);
            content.Headers.ContentEncoding.Add(contentEncoding);
            content.Headers.ContentLength = body.Length;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private static byte[] Gzip(byte[] data)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(buffer, System.IO.Compression.CompressionLevel.Fastest))
            gzip.Write(data);
        return buffer.ToArray();
    }

    [Fact]
    public async Task ATransferCompressedByTheServerIsDecodedBeforeItIsVerified()
    {
        byte[] payload = new byte[64 * 1024];
        new Random(173).NextBytes(payload);
        IPackage package = CreatePackageWithHash(
            Convert.ToHexStringLower(SHA256.HashData(payload)),
            UniqueInstallerUrl()
        );
        var handler = new EncodedResponseHandler(Gzip(payload), "gzip");
        string downloadPath = Path.Join(Path.GetTempPath(), $"unigetui-transport-gzip-{Guid.NewGuid():N}.exe");

        try
        {
            using var operation = new ProbeDownloadOperation(package, downloadPath, handler);

            Assert.Equal(OperationVeredict.Success, await operation.InvokePerformOperationForTests());
            Assert.Equal("identity", handler.AcceptEncoding);
            Assert.Equal(payload, File.ReadAllBytes(downloadPath));
            Assert.Contains(operation.GetOutput(), line => line.Item1.Contains("the file was decoded"));
            Assert.Empty(PartialFilesOf(downloadPath));
        }
        finally
        {
            File.Delete(downloadPath);
        }
    }

    [Fact]
    public async Task AnArchiveServedWithAContentEncodingIsKeptWhenItsBytesMatch()
    {
        byte[] tar = new byte[64 * 1024];
        new Random(179).NextBytes(tar);
        byte[] archive = Gzip(tar);
        IPackage package = CreatePackageWithHash(
            Convert.ToHexStringLower(SHA256.HashData(archive)),
            UniqueInstallerUrl()
        );
        var handler = new EncodedResponseHandler(archive, "gzip");
        string downloadPath = Path.Join(Path.GetTempPath(), $"unigetui-archive-{Guid.NewGuid():N}.tgz");

        try
        {
            using var operation = new ProbeDownloadOperation(package, downloadPath, handler);

            Assert.Equal(OperationVeredict.Success, await operation.InvokePerformOperationForTests());
            Assert.Equal(archive, File.ReadAllBytes(downloadPath));
            Assert.DoesNotContain(operation.GetOutput(), line => line.Item1.Contains("the file was decoded"));
        }
        finally
        {
            File.Delete(downloadPath);
        }
    }

    [Fact]
    public async Task ARelativeInstallerAddressFailsCleanlyAndIsNeverLookedUp()
    {
        Uri relative = new("", UriKind.Relative);
        IPackage package = CreatePackageWithHash(null, relative);
        string downloadPath = Path.Join(Path.GetTempPath(), $"unigetui-relative-{Guid.NewGuid():N}.bin");

        using var operation = new ProbeDownloadOperation(package, downloadPath, new UnreachableHandler());

        Assert.Equal(OperationVeredict.Failure, await operation.InvokePerformOperationForTests());
        Assert.Contains(operation.GetOutput(), line => line.Item1.Contains("is not a complete address"));
        Assert.DoesNotContain(operation.GetOutput(), line => line.Item1.Contains("InvalidOperationException"));
        Assert.Null(InstallerVerification.Find(relative, null));
        Assert.False(File.Exists(downloadPath));
    }

    [Fact]
    public void ALongDestinationNameGetsAShortStagingNameThatKeepsTheExtension()
    {
        string destination = Path.Join("C:", "dl", new string('a', 240) + ".exe");

        string staging = Path.GetFileName(DownloadOperation.StagingPathFor(destination, "1a2b3c4d"));

        Assert.Equal(new string('a', DownloadOperation.MaxStagingStemLength) + ".1a2b3c4d.partial.exe", staging);
    }

    [Fact]
    public void TheStagingFileKeepsTheInstallersExtension()
    {
        string staging = DownloadOperation.StagingPathFor(Path.Join("C:", "dl", "setup.msix"), "1a2b3c4d");

        Assert.Equal("setup.1a2b3c4d.partial.msix", Path.GetFileName(staging));
    }

    [Fact]
    public async Task CancellingDuringTheSignatureCheckLeavesTheDestinationUntouched()
    {
        byte[] payload = new byte[16 * 1024];
        new Random(151).NextBytes(payload);
        byte[] existing = new byte[1024];
        new Random(157).NextBytes(existing);

        IPackage package = CreatePackageWithHash(null, UniqueInstallerUrl());
        var handler = new FakeDownloadHandler(payload, 4096, null, TimeSpan.Zero, null);
        string downloadPath = Path.Join(Path.GetTempPath(), $"unigetui-cancel-signature-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(downloadPath, existing);
        using var cancellation = new CancellationTokenSource();

        try
        {
            using var operation = new ProbeDownloadOperation(package, downloadPath, handler)
            {
                Signature = new SignatureCheck(0, "Example Corp"),
                SignatureDelay = TimeSpan.FromSeconds(2),
            };
            operation.SetRunCancellationSourceForTests(cancellation);
            cancellation.CancelAfter(TimeSpan.FromMilliseconds(200));

            Assert.Equal(OperationVeredict.Canceled, await operation.InvokePerformOperationForTests());
            Assert.Equal(existing, File.ReadAllBytes(downloadPath));
            Assert.Empty(PartialFilesOf(downloadPath));
        }
        finally
        {
            File.Delete(downloadPath);
        }
    }

    [Fact]
    public void TheVerificationRegistryForgetsTheOldestResultsPastItsCapacity()
    {
        Uri first = UniqueInstallerUrl();
        var result = new InstallerVerification(InstallerHashStatus.NotPublished, null, null);

        InstallerVerification.Record(first, null, result);
        for (int i = 0; i < InstallerVerification.Capacity; i++)
            InstallerVerification.Record(UniqueInstallerUrl(), null, result);

        Assert.Null(InstallerVerification.Find(first, null));
    }

    [Fact]
    public async Task ADownloadMatchingThePublishedHashIsVerifiedAndReportsItsSigner()
    {
        byte[] payload = new byte[64 * 1024];
        new Random(71).NextBytes(payload);
        Uri installerUrl = UniqueInstallerUrl();
        string publishedHash = Convert.ToHexStringLower(SHA256.HashData(payload));

        IPackage package = CreatePackageWithHash(publishedHash, installerUrl);
        var handler = new FakeDownloadHandler(payload, 4096, null, TimeSpan.Zero, null);
        string downloadPath = Path.Join(Path.GetTempPath(), $"unigetui-verified-{Guid.NewGuid():N}.bin");

        try
        {
            using var operation = new ProbeDownloadOperation(package, downloadPath, handler)
            {
                Signature = new SignatureCheck(0, "Example Corp"),
            };

            Assert.Equal(OperationVeredict.Success, await operation.InvokePerformOperationForTests());
            Assert.Equal(payload, File.ReadAllBytes(downloadPath));
            Assert.Empty(PartialFilesOf(downloadPath));
            Assert.Contains(operation.GetOutput(), line => line.Item1.Contains("matches the SHA-256 hash"));
            Assert.Contains(operation.GetOutput(), line => line.Item1.Contains("signed by Example Corp"));

            InstallerVerification? recorded = InstallerVerification.Find(installerUrl, publishedHash);
            Assert.NotNull(recorded);
            Assert.Equal(InstallerHashStatus.Verified, recorded.HashStatus);
            Assert.Equal("SHA-256", recorded.HashAlgorithm);
            Assert.Equal("Example Corp", recorded.Signature?.Signer);
        }
        finally
        {
            File.Delete(downloadPath);
        }
    }

    [Fact]
    public async Task ADownloadThatDoesNotMatchThePublishedHashFailsAndLeavesTheExistingFileAlone()
    {
        byte[] payload = new byte[64 * 1024];
        new Random(73).NextBytes(payload);
        byte[] existing = new byte[64 * 1024];
        new Random(79).NextBytes(existing);
        byte[] published = new byte[64 * 1024];
        new Random(83).NextBytes(published);
        Uri installerUrl = UniqueInstallerUrl();
        string publishedHash = "sha512-" + Convert.ToBase64String(SHA512.HashData(published));

        IPackage package = CreatePackageWithHash(publishedHash, installerUrl);
        var handler = new FakeDownloadHandler(payload, 4096, null, TimeSpan.Zero, null);
        string downloadPath = Path.Join(Path.GetTempPath(), $"unigetui-tampered-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(downloadPath, existing);

        try
        {
            using var operation = new ProbeDownloadOperation(package, downloadPath, handler)
            {
                Signature = new SignatureCheck(0, "Example Corp"),
            };

            Assert.Equal(OperationVeredict.Failure, await operation.InvokePerformOperationForTests());
            Assert.Equal(existing, File.ReadAllBytes(downloadPath));
            Assert.Equal(payload, File.ReadAllBytes(PartialFilesOf(downloadPath).Single()));
            Assert.Contains(
                operation.GetOutput(),
                line => line.Item1.Contains("does not match the SHA-512 hash")
                    && line.Item1.Contains(Convert.ToHexStringLower(SHA512.HashData(payload)))
            );
            Assert.DoesNotContain(operation.GetOutput(), line => line.Item1.Contains("signed by"));
            Assert.True(operation.HashMismatched);
            Assert.Equal(
                InstallerHashStatus.Mismatch,
                InstallerVerification.Find(installerUrl, publishedHash)?.HashStatus
            );

            operation.Dispose();
            Assert.Empty(PartialFilesOf(downloadPath));
        }
        finally
        {
            File.Delete(downloadPath);
            foreach (string staging in PartialFilesOf(downloadPath))
                File.Delete(staging);
        }
    }

    [Fact]
    public async Task RetryingAMismatchedDownloadSkippingIntegrityChecksKeepsTheFile()
    {
        byte[] payload = new byte[64 * 1024];
        new Random(101).NextBytes(payload);
        byte[] published = new byte[64 * 1024];
        new Random(103).NextBytes(published);
        Uri installerUrl = UniqueInstallerUrl();
        string publishedHash = Convert.ToHexStringLower(SHA256.HashData(published));

        IPackage package = CreatePackageWithHash(publishedHash, installerUrl);
        var handler = new FakeDownloadHandler(payload, 4096, null, TimeSpan.Zero, null);
        string downloadPath = Path.Join(Path.GetTempPath(), $"unigetui-kept-{Guid.NewGuid():N}.bin");

        try
        {
            using var operation = new ProbeDownloadOperation(package, downloadPath, handler)
            {
                Signature = new SignatureCheck(0, "Example Corp"),
            };

            Assert.Equal(OperationVeredict.Failure, await operation.InvokePerformOperationForTests());
            Assert.True(operation.HashMismatched);
            Assert.False(File.Exists(downloadPath));

            operation.ApplyRetryActionForTests(AbstractOperation.RetryMode.Retry_SkipIntegrity);

            Assert.Equal(OperationVeredict.Success, await operation.InvokePerformOperationForTests());
            Assert.False(operation.HashMismatched);
            Assert.Equal(1, handler.Requests);
            Assert.Contains(operation.GetOutput(), line => line.Item1.Contains("Reusing the installer set aside"));
            Assert.Equal(payload, File.ReadAllBytes(downloadPath));
            Assert.Empty(PartialFilesOf(downloadPath));
            Assert.Contains(operation.GetOutput(), line => line.Item1.Contains("kept because the hash check was skipped"));
            Assert.Contains(operation.GetOutput(), line => line.Item1.Contains("signed by Example Corp"));
            Assert.Equal(
                InstallerHashStatus.MismatchKept,
                InstallerVerification.Find(installerUrl, publishedHash)?.HashStatus
            );
        }
        finally
        {
            File.Delete(downloadPath);
        }
    }

    [Fact]
    public async Task ASetAsideFileThatChangedIsDownloadedAgainOnTheSkipRetry()
    {
        byte[] payload = new byte[16 * 1024];
        new Random(131).NextBytes(payload);
        byte[] published = new byte[16 * 1024];
        new Random(137).NextBytes(published);
        Uri installerUrl = UniqueInstallerUrl();
        string publishedHash = Convert.ToHexStringLower(SHA256.HashData(published));

        IPackage package = CreatePackageWithHash(publishedHash, installerUrl);
        var handler = new FakeDownloadHandler(payload, 4096, null, TimeSpan.Zero, null);
        string downloadPath = Path.Join(Path.GetTempPath(), $"unigetui-swapped-{Guid.NewGuid():N}.bin");

        try
        {
            using var operation = new ProbeDownloadOperation(package, downloadPath, handler);

            Assert.Equal(OperationVeredict.Failure, await operation.InvokePerformOperationForTests());
            File.WriteAllBytes(PartialFilesOf(downloadPath).Single(), [1, 2, 3]);
            operation.ApplyRetryActionForTests(AbstractOperation.RetryMode.Retry_SkipIntegrity);

            Assert.Equal(OperationVeredict.Success, await operation.InvokePerformOperationForTests());
            Assert.Equal(2, handler.Requests);
            Assert.Equal(payload, File.ReadAllBytes(downloadPath));
        }
        finally
        {
            File.Delete(downloadPath);
            foreach (string staging in PartialFilesOf(downloadPath))
                File.Delete(staging);
        }
    }

    [Fact]
    public async Task ASlowSignatureCheckDoesNotHoldTheDownload()
    {
        byte[] payload = new byte[16 * 1024];
        new Random(139).NextBytes(payload);
        IPackage package = CreatePackageWithHash(null, UniqueInstallerUrl());
        var handler = new FakeDownloadHandler(payload, 4096, null, TimeSpan.Zero, null);
        string downloadPath = Path.Join(Path.GetTempPath(), $"unigetui-slow-signature-{Guid.NewGuid():N}.bin");

        try
        {
            using var operation = new ProbeDownloadOperation(package, downloadPath, handler)
            {
                Signature = new SignatureCheck(0, "Example Corp"),
                SignatureDelay = TimeSpan.FromSeconds(5),
                SignatureCheckTimeout = TimeSpan.FromMilliseconds(100),
            };

            var stopwatch = Stopwatch.StartNew();
            Assert.Equal(OperationVeredict.Success, await operation.InvokePerformOperationForTests());
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(4), $"took {stopwatch.Elapsed}");
            Assert.Equal(payload, File.ReadAllBytes(downloadPath));
            Assert.Contains(operation.GetOutput(), line => line.Item1.Contains("could not be checked within"));
        }
        finally
        {
            File.Delete(downloadPath);
        }
    }

    [Fact]
    public async Task SkippingIntegrityChecksStillReportsAMatchingDownloadAsVerified()
    {
        byte[] payload = new byte[16 * 1024];
        new Random(107).NextBytes(payload);
        Uri installerUrl = UniqueInstallerUrl();
        string publishedHash = Convert.ToHexStringLower(SHA256.HashData(payload));

        IPackage package = CreatePackageWithHash(publishedHash, installerUrl);
        var handler = new FakeDownloadHandler(payload, 4096, null, TimeSpan.Zero, null);
        string downloadPath = Path.Join(Path.GetTempPath(), $"unigetui-skip-match-{Guid.NewGuid():N}.bin");

        try
        {
            using var operation = new ProbeDownloadOperation(package, downloadPath, handler);
            operation.ApplyRetryActionForTests(AbstractOperation.RetryMode.Retry_SkipIntegrity);

            Assert.Equal(OperationVeredict.Success, await operation.InvokePerformOperationForTests());
            Assert.Equal(
                InstallerHashStatus.Verified,
                InstallerVerification.Find(installerUrl, publishedHash)?.HashStatus
            );
        }
        finally
        {
            File.Delete(downloadPath);
        }
    }

    [Theory]
    [InlineData(null, InstallerHashStatus.NotPublished)]
    [InlineData("not-a-hash", InstallerHashStatus.Unreadable)]
    public async Task ADownloadWithoutAUsableHashSucceedsButSaysItWasNotVerified(
        string? publishedHash,
        InstallerHashStatus expected
    )
    {
        byte[] payload = new byte[16 * 1024];
        new Random(89).NextBytes(payload);
        Uri installerUrl = UniqueInstallerUrl();

        IPackage package = CreatePackageWithHash(publishedHash, installerUrl);
        var handler = new FakeDownloadHandler(payload, 4096, null, TimeSpan.Zero, null);
        string downloadPath = Path.Join(Path.GetTempPath(), $"unigetui-unverified-{Guid.NewGuid():N}.bin");

        try
        {
            using var operation = new ProbeDownloadOperation(package, downloadPath, handler);

            Assert.Equal(OperationVeredict.Success, await operation.InvokePerformOperationForTests());
            Assert.Equal(payload, File.ReadAllBytes(downloadPath));
            Assert.Contains(operation.GetOutput(), line => line.Item1.Contains("could not be verified"));
            Assert.Equal(expected, InstallerVerification.Find(installerUrl, publishedHash)?.HashStatus);
        }
        finally
        {
            File.Delete(downloadPath);
        }
    }

    [Fact]
    public async Task ASkippedDownloadIsRecordedAsVerified()
    {
        byte[] payload = new byte[16 * 1024];
        new Random(97).NextBytes(payload);
        Uri installerUrl = UniqueInstallerUrl();
        string publishedHash = Convert.ToHexString(SHA256.HashData(payload));

        IPackage package = CreatePackageWithHash(publishedHash, installerUrl);
        string downloadPath = Path.Join(Path.GetTempPath(), $"unigetui-skip-recorded-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(downloadPath, payload);

        try
        {
            using var operation = new ProbeDownloadOperation(package, downloadPath, new UnreachableHandler())
            {
                Signature = new SignatureCheck(unchecked((int)0x800B0100), null),
            };

            Assert.Equal(OperationVeredict.Success, await operation.InvokePerformOperationForTests());
            Assert.Contains(operation.GetOutput(), line => line.Item1.Contains("The installer is not signed"));
            InstallerVerification? recorded = InstallerVerification.Find(installerUrl, publishedHash);
            Assert.Equal(InstallerHashStatus.Verified, recorded?.HashStatus);
            Assert.False(recorded?.Signature?.IsValid);
        }
        finally
        {
            File.Delete(downloadPath);
        }
    }

    private sealed class UnreachableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => throw new InvalidOperationException("No HTTP request was expected");
    }
}
