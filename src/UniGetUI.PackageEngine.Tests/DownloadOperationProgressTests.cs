using System.Diagnostics;
using System.Net;
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
        private FragmentedReadStream? _stream = streamSink;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
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
}
