using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using UniGetUI.Interface;
using UniGetUI.Interface.AgentRegistration;

namespace UniGetUI.Tests;

public class AgentRegistrationTests
{
    private const string Uuid1 = "11111111-2222-3333-4444-555555555555";
    private const string Uuid2 = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private const string SecretGrant = "grant-SECRET.token~1:x";

    private sealed class ChannelStream : Stream
    {
        private readonly Channel<byte[]> _in;
        private readonly Channel<byte[]> _out;
        private byte[] _pending = [];
        private int _offset;

        public ChannelStream(Channel<byte[]> readFrom, Channel<byte[]> writeTo)
        {
            _in = readFrom;
            _out = writeTo;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            while (_offset >= _pending.Length)
            {
                if (!await _in.Reader.WaitToReadAsync(ct))
                    return 0;
                if (_in.Reader.TryRead(out byte[]? next))
                {
                    _pending = next;
                    _offset = 0;
                }
            }

            int count = Math.Min(buffer.Length, _pending.Length - _offset);
            _pending.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            _out.Writer.TryWrite(buffer.ToArray());
            return ValueTask.CompletedTask;
        }

        protected override void Dispose(bool disposing)
        {
            _out.Writer.TryComplete();
            base.Dispose(disposing);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Write(byte[] buffer, int offset, int count) =>
            _out.Writer.TryWrite(buffer.AsSpan(offset, count).ToArray());

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class FakeBackend : IAgentRegistrationBackend
    {
        public ManualResetEventSlim SourcesGate { get; } = new(true);
        public int Calls;

        public IReadOnlyList<IpcRemoteManagerInfo> ListManagers()
        {
            Interlocked.Increment(ref Calls); SourcesGate.Wait();
            return [];
        }
    }
    private sealed class BlockedWriteStream : Stream
    {
        public bool Disposed { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) =>
            await Task.Delay(Timeout.Infinite, ct);

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class FakeConnection : IAgentPipeConnection
    {
        private readonly bool _verified;
        private readonly TimeSpan _verifyDelay;

        public FakeConnection(Stream stream, bool verified, TimeSpan verifyDelay = default)
        {
            Stream = stream;
            _verified = verified;
            _verifyDelay = verifyDelay;
        }

        public Stream Stream { get; }

        public bool VerifyServer()
        {
            if (_verifyDelay > TimeSpan.Zero)
                Thread.Sleep(_verifyDelay);
            return _verified;
        }

        public ValueTask DisposeAsync()
        {
            Stream.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeConnector : IAgentPipeConnector
    {
        private readonly IAgentPipeConnection _connection;

        public FakeConnector(IAgentPipeConnection connection) => _connection = connection;

        public Task<IAgentPipeConnection?> ConnectAsync(CancellationToken ct) =>
            Task.FromResult<IAgentPipeConnection?>(_connection);
    }

    private sealed class Harness
    {
        public FakeBackend Backend { get; } = new();
        public ChannelStream Agent { get; }
        public Task<bool> Session { get; }
        public bool Registered;
        private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(20));

        public Harness(AgentRegistrationOptions? options = null)
        {
            var toClient = Channel.CreateUnbounded<byte[]>();
            var toAgent = Channel.CreateUnbounded<byte[]>();
            var client = new ChannelStream(toClient, toAgent);
            Agent = new ChannelStream(toAgent, toClient);
            var session = new AgentRegistrationSession(
                client,
                new AgentRequestDispatcher(Backend),
                options ?? new AgentRegistrationOptions { HelloTimeout = TimeSpan.FromSeconds(5) },
                TimeProvider.System
            );
            session.Registered += () => Registered = true;
            Session = Task.Run(async () =>
            {
                try
                {
                    return await session.RunAsync(_cts.Token);
                }
                finally
                {
                    client.Dispose();
                }
            });
        }

        public Task SendAsync(string json) =>
            AgentFrameCodec.WriteAsync(Agent, Encoding.UTF8.GetBytes(json), _cts.Token);

        public async Task<JsonElement?> ReadAsync()
        {
            byte[]? payload = await AgentFrameCodec.ReadAsync(Agent, _cts.Token);
            return payload is null ? null : JsonDocument.Parse(payload).RootElement.Clone();
        }

        public async Task WelcomeAsync()
        {
            JsonElement hello = (await ReadAsync())!.Value;
            Assert.Equal("Hello", hello.GetProperty("kind").GetString());
            await SendAsync("""{"kind":"Welcome","protocolVersion":1}""");
        }

        public async Task<JsonElement> RequestAsync(string id, string command, string args = "{}")
        {
            await SendAsync(
                $$"""{"kind":"Request","id":"{{id}}","command":"{{command}}","arguments":{{args}}}"""
            );
            return (await ReadAsync())!.Value;
        }

        public async Task AssertClosedAsync()
        {
            byte[]? tail = await AgentFrameCodec.ReadAsync(Agent, _cts.Token);
            Assert.Null(tail);
            Assert.True(await Task.WhenAny(Session, Task.Delay(5000)) == Session);
        }
    }

    private static string Op(string grant = SecretGrant, string scope = "", string source = "feed", string op = "Install",
        string operationId = Uuid1, string broker = Uuid2) =>
        $$"""{"operationId":"{{operationId}}","brokerRequestId":"{{broker}}","operation":"{{op}}","manager":"WinGet","sourceName":"{{source}}","packageId":"Some.Pkg","version":"","scope":"{{scope}}","grant":"{{grant}}"}""";

    private static string ErrorCode(JsonElement response) =>
        response.GetProperty("error").GetProperty("code").GetString()!;

    private sealed class ManualClock : TimeProvider
    {
        public long Now;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Now;
    }

    [Fact]
    public void BackoffStartsOverOnlyAfterARegisteredSessionStayedUp()
    {
        var clock = new ManualClock();
        var options = new AgentRegistrationOptions
        {
            MinBackoff = TimeSpan.FromSeconds(1),
            StableSessionTime = TimeSpan.FromSeconds(30),
        };
        var client = new AgentRegistrationClient(
            new FakeConnector(new FakeConnection(new BlockedWriteStream(), verified: false)),
            new FakeBackend(),
            options,
            clock,
            _ => { }
        );
        var grown = TimeSpan.FromSeconds(16);
        long registeredAt = clock.GetTimestamp();

        clock.Now += TimeSpan.FromSeconds(29).Ticks;
        Assert.Equal(grown, client.BackoffAfterSession(grown, registeredAt));
        Assert.Equal(grown, client.BackoffAfterSession(grown, null));

        clock.Now += TimeSpan.FromSeconds(1).Ticks;
        Assert.Equal(options.MinBackoff, client.BackoffAfterSession(grown, registeredAt));
    }

    [Fact]
    public async Task FailedServerVerificationWritesNothingAndCloses()
    {
        var toClient = Channel.CreateUnbounded<byte[]>();
        var toAgent = Channel.CreateUnbounded<byte[]>();
        var agent = new ChannelStream(toAgent, toClient);
        var connection = new FakeConnection(new ChannelStream(toClient, toAgent), verified: false);
        var client = new AgentRegistrationClient(
            new FakeConnector(connection),
            new FakeBackend(),
            new AgentRegistrationOptions(),
            TimeProvider.System,
            _ => { }
        );

        Assert.False(await client.RunOnceAsync(() => { }, CancellationToken.None));
        Assert.Null(await AgentFrameCodec.ReadAsync(agent, CancellationToken.None));
    }

    [Fact]
    public async Task BlockedHelloWriteIsClosedByTheHandshakeDeadline()
    {
        var stream = new BlockedWriteStream();
        var client = NewClient(new FakeConnection(stream, verified: true), TimeSpan.FromMilliseconds(200));
        using var guard = new CancellationTokenSource();

        Task<bool> run = client.RunOnceAsync(() => { }, guard.Token);
        bool finished = await Task.WhenAny(run, Task.Delay(3000)) == run;
        guard.Cancel();

        Assert.True(finished);
        Assert.False(await run);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task SlowAuthenticationConsumesTheHandshakeDeadlineBeforeAnyWrite()
    {
        var toClient = Channel.CreateUnbounded<byte[]>();
        var toAgent = Channel.CreateUnbounded<byte[]>();
        var agent = new ChannelStream(toAgent, toClient);
        var connection = new FakeConnection(
            new ChannelStream(toClient, toAgent),
            verified: true,
            verifyDelay: TimeSpan.FromMilliseconds(400)
        );
        var client = NewClient(connection, TimeSpan.FromMilliseconds(100));

        Assert.False(await client.RunOnceAsync(() => { }, CancellationToken.None));
        Assert.Null(await AgentFrameCodec.ReadAsync(agent, CancellationToken.None));
    }

    private static AgentRegistrationClient NewClient(FakeConnection connection, TimeSpan helloTimeout) =>
        new(
            new FakeConnector(connection),
            new FakeBackend(),
            new AgentRegistrationOptions { HelloTimeout = helloTimeout },
            TimeProvider.System,
            _ => { }
        );

    [Fact]
    public async Task HelloIsFirstFrameAndWelcomeRegisters()
    {
        var h = new Harness();
        JsonElement hello = (await h.ReadAsync())!.Value;
        Assert.Equal("Hello", hello.GetProperty("kind").GetString());
        Assert.Equal(1, hello.GetProperty("protocolVersion").GetInt32());
        Assert.InRange(hello.GetProperty("clientVersion").GetString()!.Length, 1, 128);
        Assert.False(h.Registered);

        await h.SendAsync("""{"kind":"Welcome","protocolVersion":1,"extra":[1]}""");
        JsonElement pong = await h.RequestAsync(Uuid1, "ListManagers");
        Assert.True(pong.GetProperty("ok").GetBoolean());
        Assert.True(h.Registered);
    }

    [Fact]
    public async Task WelcomeMayArriveAfterHelloDeadlineButBeforeIdleTimeout()
    {
        var h = new Harness(new AgentRegistrationOptions
        {
            HelloTimeout = TimeSpan.FromMilliseconds(100),
            IdleTimeout = TimeSpan.FromSeconds(2),
        });
        Assert.Equal("Hello", (await h.ReadAsync())!.Value.GetProperty("kind").GetString());
        await Task.Delay(300);

        Assert.False(h.Session.IsCompleted);
        await h.SendAsync("""{"kind":"Welcome","protocolVersion":1}""");
        Assert.True((await h.RequestAsync(Uuid1, "ListManagers")).GetProperty("ok").GetBoolean());
        Assert.True(h.Registered);
    }

    [Fact]
    public async Task SendsPingWhileWaitingForWelcome()
    {
        var h = new Harness(new AgentRegistrationOptions
        {
            PingAfter = TimeSpan.FromMilliseconds(100),
            IdleTimeout = TimeSpan.FromSeconds(2),
        });
        Assert.Equal("Hello", (await h.ReadAsync())!.Value.GetProperty("kind").GetString());
        Assert.Equal(
            "Ping",
            (await h.ReadAsync().WaitAsync(TimeSpan.FromSeconds(1)))!.Value.GetProperty("kind").GetString()
        );
        Assert.False(h.Registered);
        h.Agent.Dispose();
        Assert.False(await h.Session);
    }

    [Fact]
    public async Task WrongProtocolVersionClosesWithoutRegistering()
    {
        var h = new Harness();
        await h.ReadAsync();
        await h.SendAsync("""{"kind":"Welcome","protocolVersion":2}""");
        await h.AssertClosedAsync();
        Assert.False(h.Registered);
        Assert.False(await h.Session);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1_048_577u)]
    public async Task OutOfRangeFrameLengthClosesTheSession(uint length)
    {
        var h = new Harness();
        await h.WelcomeAsync();
        byte[] header = BitConverter.GetBytes(length);
        await h.Agent.WriteAsync(header);
        await h.AssertClosedAsync();
    }

    [Theory]
    [InlineData("""{"kind":"Nope"}""")]
    [InlineData("""{"id":"x"}""")]
    [InlineData("""{"kind":5}""")]
    [InlineData("""not json""")]
    [InlineData("""[1,2]""")]
    public async Task MalformedOrUnknownFramesCloseTheSession(string frame)
    {
        var h = new Harness();
        await h.WelcomeAsync();
        await h.SendAsync(frame);
        await h.AssertClosedAsync();
    }

    [Fact]
    public async Task UnknownMembersAreIgnoredAndUnknownCommandsRejected()
    {
        var h = new Harness();
        await h.WelcomeAsync();
        await h.SendAsync(
            $$"""{"kind":"Request","id":"{{Uuid1}}","command":"ListManagers","arguments":{"x":1},"future":true}"""
        );
        JsonElement ok = (await h.ReadAsync())!.Value;
        Assert.Equal(Uuid1, ok.GetProperty("id").GetString());
        Assert.True(ok.GetProperty("ok").GetBoolean());

        JsonElement unsupported = await h.RequestAsync(Uuid2, "Nope");
        Assert.Equal("UnsupportedCommand", ErrorCode(unsupported));
    }

    [Fact]
    public async Task ResponsesCorrelateByIdAndMayCompleteOutOfOrder()
    {
        var h = new Harness();
        h.Backend.SourcesGate.Reset();
        await h.WelcomeAsync();
        await h.SendAsync($$"""{"kind":"Request","id":"{{Uuid1}}","command":"ListManagers","arguments":{} }""");
        JsonElement second = await h.RequestAsync(Uuid2, "ListSources");
        Assert.Equal(Uuid2, second.GetProperty("id").GetString());

        h.Backend.SourcesGate.Set();
        JsonElement first = (await h.ReadAsync())!.Value;
        Assert.Equal(Uuid1, first.GetProperty("id").GetString());
    }

    [Fact]
    public async Task NinthConcurrentRequestIsBusy()
    {
        var h = new Harness();
        h.Backend.SourcesGate.Reset();
        await h.WelcomeAsync();
        for (int i = 0; i < 8; i++)
        {
            string id = $"00000000-0000-0000-0000-00000000000{i}";
            await h.SendAsync($$"""{"kind":"Request","id":"{{id}}","command":"ListManagers","arguments":{} }""");
        }

        JsonElement busy = await h.RequestAsync(Uuid1, "ListSources");
        Assert.Equal(Uuid1, busy.GetProperty("id").GetString());
        Assert.Equal("Busy", ErrorCode(busy));

        h.Backend.SourcesGate.Set();
        for (int i = 0; i < 8; i++)
            Assert.True((await h.ReadAsync())!.Value.GetProperty("ok").GetBoolean());
    }

    [Theory]
    [InlineData("ListInstalledPackages")]
    [InlineData("ListPackageUpdates")]
    [InlineData("ListSources")]
    public async Task UnisolatedInventoryCommandsFailWithoutInvokingTheBackend(string command)
    {
        var h = new Harness();
        await h.WelcomeAsync();
        JsonElement response = await h.RequestAsync(Uuid1, command);

        Assert.False(response.GetProperty("ok").GetBoolean());
        Assert.False(response.TryGetProperty("result", out _));
        Assert.Equal("Failed", ErrorCode(response));
        Assert.Equal(0, Volatile.Read(ref h.Backend.Calls));
    }

    [Fact]
    public async Task PingIsAnsweredWithPongAndIdleSessionSendsPing()
    {
        var h = new Harness(
            new AgentRegistrationOptions
            {
                HelloTimeout = TimeSpan.FromSeconds(5),
                PingAfter = TimeSpan.FromMilliseconds(100),
            }
        );
        await h.WelcomeAsync();
        await h.SendAsync("""{"kind":"Ping"}""");
        bool receivedPing = false;
        bool receivedPong = false;
        while (!receivedPing || !receivedPong)
        {
            string? kind = (await h.ReadAsync())!.Value.GetProperty("kind").GetString();
            Assert.True(kind is "Ping" or "Pong");
            receivedPing |= kind is "Ping";
            receivedPong |= kind is "Pong";
        }
    }

    [Fact]
    public async Task SilentAgentIsDroppedAfterIdleTimeout()
    {
        var h = new Harness(
            new AgentRegistrationOptions
            {
                HelloTimeout = TimeSpan.FromSeconds(5),
                IdleTimeout = TimeSpan.FromMilliseconds(200),
            }
        );
        await h.WelcomeAsync();
        Assert.True(await Task.WhenAny(h.Session, Task.Delay(5000)) == h.Session);
    }

    [Theory]
    [InlineData("""{"manager":"WinGet","sourceName":"","packageId":"p"}""")]
    [InlineData("""{"manager":"","sourceName":"feed","packageId":"p"}""")]
    [InlineData("""{"manager":"WinGet","sourceName":"feed"}""")]
    [InlineData("""{"manager":"WinGet","sourceName":7,"packageId":"p"}""")]
    public async Task GetPackageVersionsRejectsBadArguments(string args)
    {
        var h = new Harness();
        await h.WelcomeAsync();
        Assert.Equal("InvalidArguments", ErrorCode(await h.RequestAsync(Uuid1, "GetPackageVersions", args)));
    }

    [Fact]
    public async Task GetPackageVersionsFailsClosedWithoutEchoingInput()
    {
        var h = new Harness();
        await h.WelcomeAsync();
        JsonElement response = await h.RequestAsync(
            Uuid1,
            "GetPackageVersions",
            """{"manager":"WinGet","sourceName":"https://user:pw@feed.example/x","packageId":"Secret.Pkg"}"""
        );
        Assert.Equal("Failed", ErrorCode(response));
        string text = response.GetRawText();
        Assert.DoesNotContain("feed.example", text);
        Assert.DoesNotContain("Secret.Pkg", text);
    }

    [Fact]
    public async Task UnavailableVersionAndExecuteCommandsDoNotInspectSources()
    {
        var h = new Harness();
        await h.WelcomeAsync();
        await h.RequestAsync(
            Uuid1,
            "GetPackageVersions",
            """{"manager":"WinGet","sourceName":"feed","packageId":"p"}"""
        );
        await h.RequestAsync(Uuid2, "ExecutePackageOperation", Op(source: "feed"));

        Assert.Equal(0, Volatile.Read(ref h.Backend.Calls));
    }

    [Theory]
    [InlineData("Update")]
    [InlineData("Uninstall")]
    public async Task EmptySourceForUpdatesAndUninstallsIsUnavailableRatherThanInvalid(string operation)
    {
        var h = new Harness();
        await h.WelcomeAsync();
        JsonElement response = await h.RequestAsync(
            Uuid1,
            "ExecutePackageOperation",
            Op(source: "", op: operation)
        );

        Assert.True(response.GetProperty("ok").GetBoolean());
        Assert.Equal("Failed", response.GetProperty("result").GetProperty("status").GetString());
        Assert.Equal(0, Volatile.Read(ref h.Backend.Calls));
    }

    [Theory]
    [InlineData("bad grant!", "", "feed", "Install", Uuid1, Uuid2)]
    [InlineData("", "", "feed", "Install", Uuid1, Uuid2)]
    [InlineData("-leading", "", "feed", "Install", Uuid1, Uuid2)]
    [InlineData(SecretGrant, "Root", "feed", "Install", Uuid1, Uuid2)]
    [InlineData(SecretGrant, "", "feed", "install", Uuid1, Uuid2)]
    [InlineData(SecretGrant, "", "feed", "Install", "11111111-2222-3333-4444-55555555555G", Uuid2)]
    [InlineData(SecretGrant, "", "feed", "Install", "AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE", Uuid2)]
    [InlineData(SecretGrant, "", "feed", "Install", Uuid1, "not-a-uuid")]
    public async Task ExecutePackageOperationRejectsInvalidShapes(
        string grant, string scope, string source, string operation, string operationId, string broker)
    {
        var h = new Harness();
        await h.WelcomeAsync();
        JsonElement response = await h.RequestAsync(
            Uuid1,
            "ExecutePackageOperation",
            Op(grant, scope, source, operation, operationId, broker)
        );
        Assert.Equal("InvalidArguments", ErrorCode(response));
        Assert.DoesNotContain("SECRET", response.GetRawText());
    }

    [Fact]
    public async Task UninstallWithSourceIsRejectedWithoutInspectingSources()
    {
        var h = new Harness();
        await h.WelcomeAsync();
        JsonElement response = await h.RequestAsync(
            Uuid1,
            "ExecutePackageOperation",
            Op(source: "feed", op: "Uninstall")
        );

        Assert.False(response.GetProperty("ok").GetBoolean());
        Assert.Equal("InvalidArguments", ErrorCode(response));
        Assert.Equal(0, Volatile.Read(ref h.Backend.Calls));
    }

    [Theory]
    [InlineData("Update", "origin-unknown")]
    [InlineData("Uninstall", "")]
    [InlineData("Install", "origin-unknown")]
    public async Task UnavailableOperationsDoNotInspectUnknownOrigins(string operation, string source)
    {
        var h = new Harness();
        await h.WelcomeAsync();
        JsonElement response = await h.RequestAsync(
            Uuid1,
            "ExecutePackageOperation",
            Op(source: source, op: operation)
        );

        Assert.Equal("Failed", response.GetProperty("result").GetProperty("status").GetString());
        Assert.DoesNotContain("origin-unknown", response.GetRawText());
        Assert.Equal(0, Volatile.Read(ref h.Backend.Calls));
    }

    [Theory]
    [InlineData("")]
    [InlineData("user")]
    [InlineData("Machine")]
    public async Task ValidExecutePackageOperationReportsNotStartedAndNeverEchoesGrant(string scope)
    {
        var h = new Harness();
        await h.WelcomeAsync();
        JsonElement response = await h.RequestAsync(
            Uuid1,
            "ExecutePackageOperation",
            Op(scope: scope, source: "https://secret-feed.example")
        );

        Assert.True(response.GetProperty("ok").GetBoolean());
        JsonElement result = response.GetProperty("result");
        Assert.Equal("Failed", result.GetProperty("status").GetString());
        Assert.False(string.IsNullOrEmpty(result.GetProperty("error").GetString()));
        string text = response.GetRawText();
        Assert.DoesNotContain("SECRET", text);
        Assert.DoesNotContain("secret-feed", text);
        Assert.DoesNotContain("Some.Pkg", text);
        Assert.Equal(0, Volatile.Read(ref h.Backend.Calls));
    }
}
