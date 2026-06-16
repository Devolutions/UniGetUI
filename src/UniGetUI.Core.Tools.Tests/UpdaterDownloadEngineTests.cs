using System.Net;
using System.Net.Http.Headers;
using UniGetUI.Core.Tools;

namespace UniGetUI.Core.Tools.Tests;

public sealed class UpdaterDownloadEngineTests
{
    [Fact]
    public void DefaultAutomaticUpdateCheckInterval_IsOneHour()
    {
        Assert.Equal(TimeSpan.FromMinutes(60), UpdaterDownloadEngine.DefaultAutomaticUpdateCheckInterval);
    }

    [Fact]
    public async Task DownloadInstallerPartAsync_ResumesInterruptedDownloadWithRangeAndIfRange()
    {
        string destination = GetTempDestination();
        byte[] content = "abcdefghij"u8.ToArray();
        DateTimeOffset lastModified = new(2026, 6, 15, 17, 46, 1, TimeSpan.Zero);
        int requestCount = 0;
        using HttpClient client = new(new QueueHttpMessageHandler(request =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                Assert.Null(request.Headers.Range);
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new FaultingReadStream(content, 5)),
                };
                response.Content.Headers.ContentLength = content.Length;
                response.Content.Headers.LastModified = lastModified;
                return response;
            }

            Assert.Equal(2, requestCount);
            Assert.NotNull(request.Headers.Range);
            RangeItemHeaderValue range = Assert.Single(request.Headers.Range!.Ranges);
            Assert.Equal(5, range.From);
            Assert.Null(range.To);
            Assert.NotNull(request.Headers.IfRange?.Date);
            Assert.Equal(lastModified, request.Headers.IfRange!.Date);

            var resumed = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(content[5..]),
            };
            resumed.Content.Headers.ContentRange = new ContentRangeHeaderValue(5, 9, 10);
            resumed.Content.Headers.ContentLength = 5;
            resumed.Content.Headers.LastModified = lastModified;
            return resumed;
        }));
        UpdaterDownloadIdentity identity = new("2026.2.1.0", "hash", "https://example.test/installer.exe");

        try
        {
            await Assert.ThrowsAsync<IOException>(
                () => UpdaterDownloadEngine.DownloadInstallerPartAsync(client, identity, destination)
            );
            Assert.Equal(5, new FileInfo(UpdaterDownloadEngine.GetPartialPath(destination)).Length);

            UpdaterDownloadResult result = await UpdaterDownloadEngine.DownloadInstallerPartAsync(
                client,
                identity,
                destination
            );

            Assert.True(result.Resumed);
            Assert.Equal(HttpStatusCode.PartialContent, result.StatusCode);
            Assert.Equal(content.Length, result.BytesInPartialFile);
            Assert.Equal(content, await File.ReadAllBytesAsync(result.PartialPath));
            Assert.Equal(2, requestCount);
        }
        finally
        {
            DeleteUpdaterTempFiles(destination);
        }
    }

    [Fact]
    public async Task DownloadInstallerPartAsync_RestartsWhenServerIgnoresRange()
    {
        string destination = GetTempDestination();
        byte[] content = "abcdefghij"u8.ToArray();
        DateTimeOffset lastModified = new(2026, 6, 15, 17, 46, 1, TimeSpan.Zero);
        int requestCount = 0;
        using HttpClient client = new(new QueueHttpMessageHandler(request =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new FaultingReadStream(content, 5)),
                };
                response.Content.Headers.ContentLength = content.Length;
                response.Content.Headers.LastModified = lastModified;
                return response;
            }

            Assert.NotNull(request.Headers.Range);
            var restart = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content),
            };
            restart.Content.Headers.ContentLength = content.Length;
            restart.Content.Headers.LastModified = lastModified;
            return restart;
        }));
        UpdaterDownloadIdentity identity = new("2026.2.1.0", "hash", "https://example.test/installer.exe");

        try
        {
            await Assert.ThrowsAsync<IOException>(
                () => UpdaterDownloadEngine.DownloadInstallerPartAsync(client, identity, destination)
            );

            UpdaterDownloadResult result = await UpdaterDownloadEngine.DownloadInstallerPartAsync(
                client,
                identity,
                destination
            );

            Assert.False(result.Resumed);
            Assert.Equal(HttpStatusCode.OK, result.StatusCode);
            Assert.Equal(content.Length, result.BytesInPartialFile);
            Assert.Equal(content, await File.ReadAllBytesAsync(result.PartialPath));
        }
        finally
        {
            DeleteUpdaterTempFiles(destination);
        }
    }

    [Fact]
    public async Task DownloadInstallerPartAsync_RestartsWhenResumeValidatorChanges()
    {
        string destination = GetTempDestination();
        byte[] content = "abcdefghij"u8.ToArray();
        int requestCount = 0;
        using HttpClient client = new(new QueueHttpMessageHandler(request =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new FaultingReadStream(content, 5)),
                };
                response.Headers.ETag = new EntityTagHeaderValue("\"old\"");
                response.Content.Headers.ContentLength = content.Length;
                return response;
            }

            if (requestCount == 2)
            {
                Assert.NotNull(request.Headers.Range);
                Assert.Equal("\"old\"", request.Headers.IfRange?.EntityTag?.ToString());
                var changedValidator = new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent(content[5..]),
                };
                changedValidator.Headers.ETag = new EntityTagHeaderValue("\"new\"");
                changedValidator.Content.Headers.ContentRange = new ContentRangeHeaderValue(5, 9, 10);
                return changedValidator;
            }

            Assert.Equal(3, requestCount);
            Assert.Null(request.Headers.Range);
            var restart = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content),
            };
            restart.Headers.ETag = new EntityTagHeaderValue("\"new\"");
            restart.Content.Headers.ContentLength = content.Length;
            return restart;
        }));
        UpdaterDownloadIdentity identity = new("2026.2.1.0", "hash", "https://example.test/installer.exe");

        try
        {
            await Assert.ThrowsAsync<IOException>(
                () => UpdaterDownloadEngine.DownloadInstallerPartAsync(client, identity, destination)
            );

            UpdaterDownloadResult result = await UpdaterDownloadEngine.DownloadInstallerPartAsync(
                client,
                identity,
                destination
            );

            Assert.False(result.Resumed);
            Assert.Equal(HttpStatusCode.OK, result.StatusCode);
            Assert.Equal(content, await File.ReadAllBytesAsync(result.PartialPath));
            Assert.Equal(3, requestCount);
        }
        finally
        {
            DeleteUpdaterTempFiles(destination);
        }
    }

    [Fact]
    public async Task DownloadInstallerPartAsync_ReusesCompletePartialWithoutNetwork()
    {
        string destination = GetTempDestination();
        byte[] content = "abc"u8.ToArray();
        int requestCount = 0;
        using HttpClient firstClient = new(new QueueHttpMessageHandler(_ =>
        {
            requestCount++;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content),
            };
            response.Content.Headers.ContentLength = content.Length;
            response.Content.Headers.LastModified = new DateTimeOffset(2026, 6, 15, 17, 46, 1, TimeSpan.Zero);
            return response;
        }));
        UpdaterDownloadIdentity identity = new("2026.2.1.0", "hash", "https://example.test/installer.exe");

        try
        {
            await UpdaterDownloadEngine.DownloadInstallerPartAsync(firstClient, identity, destination);
            using HttpClient failingClient = new(new QueueHttpMessageHandler(_ =>
                throw new InvalidOperationException("Network should not be used for a complete partial")
            ));

            UpdaterDownloadResult result = await UpdaterDownloadEngine.DownloadInstallerPartAsync(
                failingClient,
                identity,
                destination
            );

            Assert.Equal(1, requestCount);
            Assert.Equal(HttpStatusCode.NotModified, result.StatusCode);
            Assert.Equal(content.Length, result.BytesInPartialFile);
        }
        finally
        {
            DeleteUpdaterTempFiles(destination);
        }
    }

    [Fact]
    public void FailureBackoff_IsPersistentAndKeyedByInstallerIdentity()
    {
        string destination = GetTempDestination();
        string statePath = UpdaterDownloadEngine.GetFailureStatePath(destination);
        DateTime now = DateTime.UtcNow;
        UpdaterDownloadIdentity identity = new("2026.2.1.0", "ABC", "https://example.test/installer.exe");
        UpdaterDownloadIdentity differentHash = identity with { ExpectedHash = "DEF" };

        try
        {
            UpdaterDownloadEngine.RecordFailure(statePath, identity, "download", now);

            Assert.True(
                UpdaterDownloadEngine.IsFailureBackoffActive(
                    statePath,
                    identity,
                    now.AddMinutes(1),
                    out TimeSpan remaining,
                    out UpdaterDownloadFailureState? state
                )
            );
            Assert.True(remaining > TimeSpan.FromMinutes(50));
            Assert.Equal(1, state?.AttemptCount);
            Assert.Equal("download", state?.FailureClass);

            UpdaterDownloadEngine.RecordFailure(statePath, identity, "validation", now.AddMinutes(2));
            Assert.True(
                UpdaterDownloadEngine.IsFailureBackoffActive(
                    statePath,
                    identity,
                    now.AddMinutes(3),
                    out _,
                    out state
                )
            );
            Assert.Equal(1, state?.AttemptCount);
            Assert.Equal("validation", state?.FailureClass);

            Assert.False(
                UpdaterDownloadEngine.IsFailureBackoffActive(
                    statePath,
                    differentHash,
                    now.AddMinutes(1),
                    out _,
                    out _
                )
            );
        }
        finally
        {
            DeleteUpdaterTempFiles(destination);
        }
    }

    [Fact]
    public void CalculateFailureBackoff_UsesOneHourMinimumAndCapsAtOneDay()
    {
        UpdaterDownloadIdentity identity = new("2026.2.1.0", "ABC", "https://example.test/installer.exe");

        TimeSpan first = UpdaterDownloadEngine.CalculateFailureBackoff(identity, "download", 1);
        TimeSpan second = UpdaterDownloadEngine.CalculateFailureBackoff(identity, "download", 2);
        TimeSpan capped = UpdaterDownloadEngine.CalculateFailureBackoff(identity, "download", 20);

        Assert.True(first >= TimeSpan.FromMinutes(60));
        Assert.True(first <= TimeSpan.FromMinutes(72));
        Assert.True(second > first);
        Assert.Equal(TimeSpan.FromHours(24), capped);
    }

    private static string GetTempDestination()
    {
        return Path.Join(Path.GetTempPath(), $"{Guid.NewGuid():N}.exe");
    }

    private static void DeleteUpdaterTempFiles(string destination)
    {
        foreach (
            string path in new[]
            {
                destination,
                UpdaterDownloadEngine.GetPartialPath(destination),
                UpdaterDownloadEngine.GetPartialMetadataPath(destination),
                UpdaterDownloadEngine.GetFailureStatePath(destination),
            }
        )
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private sealed class QueueHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public QueueHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            return Task.FromResult(_handler(request));
        }
    }

    private sealed class FaultingReadStream : Stream
    {
        private readonly byte[] _data;
        private readonly int _failAfterBytes;
        private int _position;

        public FaultingReadStream(byte[] data, int failAfterBytes)
        {
            _data = data;
            _failAfterBytes = failAfterBytes;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _data.Length;
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= _failAfterBytes)
            {
                throw new IOException("Simulated interrupted download");
            }

            int remainingBeforeFailure = _failAfterBytes - _position;
            int bytesToCopy = Math.Min(count, remainingBeforeFailure);
            Array.Copy(_data, _position, buffer, offset, bytesToCopy);
            _position += bytesToCopy;
            return bytesToCopy;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            if (_position >= _failAfterBytes)
            {
                throw new IOException("Simulated interrupted download");
            }

            int remainingBeforeFailure = _failAfterBytes - _position;
            int bytesToCopy = Math.Min(buffer.Length, remainingBeforeFailure);
            _data.AsMemory(_position, bytesToCopy).CopyTo(buffer);
            _position += bytesToCopy;
            return ValueTask.FromResult(bytesToCopy);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
