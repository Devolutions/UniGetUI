using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Devolutions.Now.Policy.Api;
using UniGetUI.Interface;
using UniGetUI.Interface.AgentRegistration;
using UniGetUI.PackageEngine.AgentBroker;

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
        public RemoteInventorySnapshot? Snapshot = new(DateTime.UtcNow, null, [], [], [], []);
        public bool UniqueRow = true;
        public RemoteGrantDelivery Delivery = RemoteGrantDelivery.MaybeSent;
        public readonly List<ManagerName> FoundManagers = [];
        public readonly List<string> ResolvedSources = [];
        public readonly List<(string Manager, string Package, string Scope)> UniqueRowChecks = [];
        public readonly List<(RemoteGrantPlan Plan, string Grant)> Submitted = [];

        public RemoteInventorySnapshot? TryGetInventorySnapshot()
        {
            Interlocked.Increment(ref Calls);
            SourcesGate.Wait();
            return Snapshot;
        }

        public string? FindManagerId(ManagerName manager)
        {
            lock (FoundManagers)
                FoundManagers.Add(manager);
            return manager is ManagerName.Npm ? null : manager.ToString().ToLowerInvariant();
        }

        // Resolves by name so tests choose the outcome without a real manager.
        public RemoteSourceResolution ResolveSource(string managerId, string sourceName)
        {
            lock (ResolvedSources)
                ResolvedSources.Add(sourceName);
            return sourceName switch
            {
                "ambiguous" => RemoteSourceResolution.Ambiguous,
                "missing" => RemoteSourceResolution.NotFound,
                _ => RemoteSourceResolution.Resolved,
            };
        }

        public bool IsOnlyInstalledRow(string managerId, string packageId, string reportedScope)
        {
            lock (UniqueRowChecks)
                UniqueRowChecks.Add((managerId, packageId, reportedScope));
            return UniqueRow;
        }

        public bool ManagerEnabled = true;

        public bool IsManagerEnabled(string managerId) => ManagerEnabled;

        public Task<RemoteGrantDelivery> SubmitGrantAsync(
            RemoteGrantPlan plan,
            string grant,
            CancellationToken cancellationToken
        )
        {
            lock (Submitted)
                Submitted.Add((plan, grant));
            return Task.FromResult(Delivery);
        }

        public TaskCompletionSource<bool> Rescan { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<bool> RescanInventoryAsync(CancellationToken cancellationToken) =>
            Rescan.Task.WaitAsync(cancellationToken);

        public int InventoryRefreshes;

        public bool RefreshInstalledInventory()
        {
            Interlocked.Increment(ref InventoryRefreshes);
            return true;
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

        public Harness(AgentRegistrationOptions? options = null, TimeProvider? time = null)
        {
            var toClient = Channel.CreateUnbounded<byte[]>();
            var toAgent = Channel.CreateUnbounded<byte[]>();
            var client = new ChannelStream(toClient, toAgent);
            Agent = new ChannelStream(toAgent, toClient);
            var session = new AgentRegistrationSession(
                client,
                new AgentRequestDispatcher(Backend),
                options ?? new AgentRegistrationOptions { HelloTimeout = TimeSpan.FromSeconds(5) },
                time ?? TimeProvider.System
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

    // reportedScope: null omits the member, "#" sends a number instead of a string.
    private static string Op(string op = "Install", string manager = "WinGet", string scope = "User",
        string source = "feed", string? reportedScope = null, string grant = SecretGrant,
        string operationId = Uuid1, string broker = Uuid2, string packageId = "Some.Pkg")
    {
        string reported = reportedScope switch
        {
            null => "",
            "#" => ""","reportedScope":7""",
            _ => $$""","reportedScope":"{{reportedScope}}" """.TrimEnd(),
        };
        return $$"""{"operationId":"{{operationId}}","brokerRequestId":"{{broker}}","operation":"{{op}}","manager":"{{manager}}","sourceName":"{{source}}","packageId":"{{packageId}}","version":"","scope":"{{scope}}"{{reported}},"grant":"{{grant}}"}""";
    }

    private static JsonElement Result(JsonElement response)
    {
        Assert.True(response.GetProperty("ok").GetBoolean());
        return response.GetProperty("result");
    }

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

    // Fires the registration backoff waits at once and records them; every other timer is real.
    private sealed class BackoffClock(TimeSpan minBackoff, TimeSpan maxBackoff) : TimeProvider
    {
        public readonly List<TimeSpan> Waits = [];

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime < minBackoff / 2 || dueTime > maxBackoff)
                return TimeProvider.System.CreateTimer(callback, state, dueTime, period);
            lock (Waits)
                Waits.Add(dueTime);
            ThreadPool.QueueUserWorkItem(_ => callback(state));
            return new FiredTimer();
        }

        private sealed class FiredTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => false;

            public void Dispose() { }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    // Each attempt takes the next entry; null means the Agent's pipe is not there.
    private sealed class ScriptedConnector(params Func<IAgentPipeConnection?>[] attempts) : IAgentPipeConnector
    {
        private int _next;

        public Task<IAgentPipeConnection?> ConnectAsync(CancellationToken ct)
        {
            int index = Interlocked.Increment(ref _next) - 1;
            return Task.FromResult(index < attempts.Length ? attempts[index]() : null);
        }
    }

    private static IAgentPipeConnection WelcomingAgent(bool closeAfterWelcome)
    {
        var toClient = Channel.CreateUnbounded<byte[]>();
        var toAgent = Channel.CreateUnbounded<byte[]>();
        var agent = new ChannelStream(toAgent, toClient);
        _ = Task.Run(async () =>
        {
            await AgentFrameCodec.ReadAsync(agent, CancellationToken.None);
            await AgentFrameCodec.WriteAsync(
                agent,
                Encoding.UTF8.GetBytes("""{"kind":"Welcome","protocolVersion":1}"""),
                CancellationToken.None
            );
            if (closeAfterWelcome)
                agent.Dispose();
        });
        return new FakeConnection(new ChannelStream(toClient, toAgent), verified: true);
    }

    [Fact]
    public async Task RegistersAgainAfterTheAgentRestartsWithBackoffResetAndCapped()
    {
        TimeSpan second = TimeSpan.FromSeconds(1);
        var clock = new BackoffClock(second, 4 * second);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        int registrations = 0;
        var client = new AgentRegistrationClient(
            new ScriptedConnector(
                () => null,
                () => null,
                () => WelcomingAgent(closeAfterWelcome: true),
                () => null,
                () => null,
                () => null,
                () => WelcomingAgent(closeAfterWelcome: false)
            ),
            new FakeBackend(),
            new AgentRegistrationOptions
            {
                HelloTimeout = TimeSpan.FromHours(1),
                PingAfter = TimeSpan.FromHours(1),
                IdleTimeout = TimeSpan.FromHours(1),
                MinBackoff = second,
                MaxBackoff = 4 * second,
                // The fake clock does not advance while registered; the 30 s rule has its own test.
                StableSessionTime = TimeSpan.Zero,
            },
            clock,
            message =>
            {
                if (
                    message == "Registered with the Devolutions Agent."
                    && Interlocked.Increment(ref registrations) == 2
                )
                    stop.Cancel();
            }
        );

        await client.RunAsync(stop.Token);

        Assert.Equal(2, registrations);
        // Doubling from 1 s while the Agent is away, back to 1 s once it registered, capped at 4 s;
        // each wait is jittered to between half and all of its base.
        double[] bases = [1, 2, 1, 2, 4, 4];
        Assert.Equal(bases.Length, clock.Waits.Count);
        for (int i = 0; i < bases.Length; i++)
            Assert.InRange(clock.Waits[i].TotalSeconds, bases[i] / 2, bases[i]);
    }

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
        JsonElement pong = await h.RequestAsync(Uuid1, "GetInventorySnapshot");
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
        Assert.True((await h.RequestAsync(Uuid1, "GetInventorySnapshot")).GetProperty("ok").GetBoolean());
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
            $$"""{"kind":"Request","id":"{{Uuid1}}","command":"GetInventorySnapshot","arguments":{"x":1},"future":true}"""
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
        await h.SendAsync($$"""{"kind":"Request","id":"{{Uuid1}}","command":"GetInventorySnapshot","arguments":{} }""");
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
            await h.SendAsync($$"""{"kind":"Request","id":"{{id}}","command":"GetInventorySnapshot","arguments":{} }""");
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
    [InlineData("GetPackageVersions")]
    [InlineData("ListManagers")]
    public async Task UnlistedCommandsAreUnsupportedWithoutInvokingTheBackend(string command)
    {
        var h = new Harness();
        await h.WelcomeAsync();
        JsonElement response = await h.RequestAsync(Uuid1, command);

        Assert.False(response.GetProperty("ok").GetBoolean());
        Assert.False(response.TryGetProperty("result", out _));
        Assert.Equal("UnsupportedCommand", ErrorCode(response));
        Assert.Equal(0, Volatile.Read(ref h.Backend.Calls));
    }

    [Fact]
    public async Task RescanIsAnsweredOnlyOnceTheBackendRescanCompletes()
    {
        var h = new Harness();
        await h.WelcomeAsync();
        await h.SendAsync($$"""{"kind":"Request","id":"{{Uuid1}}","command":"RescanInventory","arguments":{} }""");

        JsonElement ping = await h.RequestAsync(Uuid2, "GetInventorySnapshot");
        Assert.Equal(Uuid2, ping.GetProperty("id").GetString());

        h.Backend.Rescan.SetResult(true);
        JsonElement response = (await h.ReadAsync())!.Value;
        Assert.Equal(Uuid1, response.GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Object, Result(response).ValueKind);
        Assert.Empty(Result(response).EnumerateObject());
    }

    [Fact]
    public async Task FailedRescanIsReportedAsFailed()
    {
        var h = new Harness();
        h.Backend.Rescan.SetResult(false);
        await h.WelcomeAsync();

        JsonElement response = await h.RequestAsync(Uuid1, "RescanInventory");
        Assert.False(response.GetProperty("ok").GetBoolean());
        Assert.Equal("Failed", ErrorCode(response));
        Assert.Equal(
            "The package rescan did not complete for every enabled package manager.",
            response.GetProperty("error").GetProperty("message").GetString()
        );
    }

    [Fact]
    public async Task RegisteredSessionReloadsTheInstalledInventoryEachIntervalUntilItEnds()
    {
        var clock = new RefreshClock(TimeSpan.FromMinutes(15));
        var h = new Harness(time: clock);
        await h.WelcomeAsync();

        await clock.FireAsync();
        await WaitUntilAsync(() => Volatile.Read(ref h.Backend.InventoryRefreshes) == 1);
        await clock.FireAsync();
        await WaitUntilAsync(() => Volatile.Read(ref h.Backend.InventoryRefreshes) == 2);

        h.Agent.Dispose();
        Assert.True(await h.Session.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, clock.Pending);
        Assert.Equal(2, Volatile.Read(ref h.Backend.InventoryRefreshes));
    }

    [Fact]
    public async Task UnwelcomedSessionNeverStartsTheInventoryRefreshTimer()
    {
        var clock = new RefreshClock(TimeSpan.FromMinutes(15));
        var h = new Harness(time: clock);
        Assert.Equal("Hello", (await h.ReadAsync())!.Value.GetProperty("kind").GetString());
        await h.SendAsync("""{"kind":"Welcome","protocolVersion":2}""");

        Assert.False(await h.Session.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, clock.Created);
        Assert.Equal(0, Volatile.Read(ref h.Backend.InventoryRefreshes));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
            await Task.Delay(10, cts.Token);
    }

    // Holds the inventory refresh timers until FireAsync; every other timer is real.
    private sealed class RefreshClock(TimeSpan interval) : TimeProvider
    {
        private readonly List<HeldTimer> _timers = [];
        public int Created;

        public int Pending
        {
            get
            {
                lock (_timers)
                    return _timers.Count(timer => !timer.Disposed);
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime != interval)
                return TimeProvider.System.CreateTimer(callback, state, dueTime, period);
            var timer = new HeldTimer(() => callback(state));
            lock (_timers)
            {
                Created++;
                _timers.Add(timer);
            }
            return timer;
        }

        // Waits for the loop to arm its next wait, then fires it.
        public async Task FireAsync()
        {
            await WaitUntilAsync(() => Pending == 1);
            HeldTimer timer;
            lock (_timers)
                timer = _timers.Single(candidate => !candidate.Disposed);
            timer.Fire();
        }

        private sealed class HeldTimer(Action fire) : ITimer
        {
            public bool Disposed { get; private set; }

            public void Fire()
            {
                Disposed = true;
                fire();
            }

            public bool Change(TimeSpan dueTime, TimeSpan period) => !Disposed;

            public void Dispose() => Disposed = true;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
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
    [InlineData("Install", "User", "feed", null, "bad grant!", Uuid1, Uuid2)]
    [InlineData("Install", "User", "feed", null, "", Uuid1, Uuid2)]
    [InlineData("Install", "User", "feed", null, "-leading", Uuid1, Uuid2)]
    [InlineData("Install", "Root", "feed", null, SecretGrant, Uuid1, Uuid2)]
    [InlineData("Install", "", "feed", null, SecretGrant, Uuid1, Uuid2)]
    [InlineData("Install", "user", "feed", null, SecretGrant, Uuid1, Uuid2)]
    [InlineData("install", "User", "feed", null, SecretGrant, Uuid1, Uuid2)]
    [InlineData("Install", "User", "feed", null, SecretGrant, "11111111-2222-3333-4444-55555555555G", Uuid2)]
    [InlineData("Install", "User", "feed", null, SecretGrant, "AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE", Uuid2)]
    [InlineData("Install", "User", "feed", null, SecretGrant, Uuid1, "not-a-uuid")]
    [InlineData("Install", "User", "", null, SecretGrant, Uuid1, Uuid2)]
    [InlineData("Install", "User", "feed", "", SecretGrant, Uuid1, Uuid2)]
    [InlineData("Update", "User", "", null, SecretGrant, Uuid1, Uuid2)]
    [InlineData("Update", "User", "", "#", SecretGrant, Uuid1, Uuid2)]
    [InlineData("Uninstall", "User", "feed", "", SecretGrant, Uuid1, Uuid2)]
    public async Task ExecutePackageOperationRejectsInvalidShapes(
        string operation, string scope, string source, string? reportedScope, string grant,
        string operationId, string broker)
    {
        var h = new Harness();
        await h.WelcomeAsync();
        JsonElement response = await h.RequestAsync(
            Uuid1,
            "ExecutePackageOperation",
            Op(operation, "WinGet", scope, source, reportedScope, grant, operationId, broker)
        );

        Assert.Equal("InvalidArguments", ErrorCode(response));
        Assert.DoesNotContain("SECRET", response.GetRawText());
        Assert.Empty(h.Backend.FoundManagers);
        Assert.Empty(h.Backend.Submitted);
    }

    [Theory]
    [InlineData(""","customParameters":["--unsafe"]}""")]
    [InlineData(""","unrelatedFutureField":true}""")]
    [InlineData(""","scope":"Machine"}""")]
    public async Task ExecutePackageOperationRejectsUnexpectedOrDuplicateArguments(string extra)
    {
        var h = new Harness();
        await h.WelcomeAsync();
        string args = Op()[..^1] + extra;

        JsonElement response = await h.RequestAsync(Uuid1, "ExecutePackageOperation", args);

        Assert.Equal("InvalidArguments", ErrorCode(response));
        Assert.Empty(h.Backend.Submitted);
    }

    [Theory]
    [InlineData("Chocolatey")]
    [InlineData(".NET Tool")]
    [InlineData("Npm")]
    [InlineData("Nope")]
    public async Task UnknownRefusedOrAbsentManagersAreInvalid(string manager)
    {
        var h = new Harness();
        await h.WelcomeAsync();
        JsonElement response = await h.RequestAsync(
            Uuid1,
            "ExecutePackageOperation",
            Op(manager: manager)
        );

        Assert.Equal("InvalidArguments", ErrorCode(response));
        Assert.Empty(h.Backend.Submitted);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("ambiguous")]
    public async Task UnresolvedSourcesAreInvalidAndNothingIsSubmitted(string source)
    {
        var h = new Harness();
        await h.WelcomeAsync();
        JsonElement response = await h.RequestAsync(
            Uuid1,
            "ExecutePackageOperation",
            Op(source: source)
        );

        Assert.Equal("InvalidArguments", ErrorCode(response));
        Assert.Empty(h.Backend.Submitted);
    }

    [Fact]
    public async Task SubmittedOperationIsUnknownAndNeverEchoesInput()
    {
        var h = new Harness();
        await h.WelcomeAsync();
        JsonElement response = await h.RequestAsync(
            Uuid1,
            "ExecutePackageOperation",
            Op(scope: "Machine", source: "secret-feed")
        );

        JsonElement result = Result(response);
        Assert.Equal("Unknown", result.GetProperty("status").GetString());
        Assert.Equal("", result.GetProperty("error").GetString());
        string text = response.GetRawText();
        Assert.DoesNotContain("SECRET", text);
        Assert.DoesNotContain("secret-feed", text);
        Assert.DoesNotContain("Some.Pkg", text);

        var (plan, grant) = Assert.Single(h.Backend.Submitted);
        Assert.Equal(SecretGrant, grant);
        Assert.Equal(Uuid2, plan.Request.RequestId);
        Assert.Equal(ManagerName.Winget, plan.Request.Manager);
        Assert.Equal(Scope.Machine, plan.Request.Options.Scope);
        Assert.Equal(Elevation.Elevated, plan.Elevation);
        Assert.Equal(["secret-feed"], h.Backend.ResolvedSources);
        Assert.Empty(h.Backend.UniqueRowChecks);
    }

    [Fact]
    public async Task OperationThatWasNotSentFails()
    {
        var h = new Harness();
        h.Backend.Delivery = RemoteGrantDelivery.NotSent;
        await h.WelcomeAsync();
        JsonElement result = Result(
            await h.RequestAsync(Uuid1, "ExecutePackageOperation", Op())
        );

        Assert.Equal("Failed", result.GetProperty("status").GetString());
        Assert.False(string.IsNullOrEmpty(result.GetProperty("error").GetString()));
        Assert.Single(h.Backend.Submitted);
    }

    [Theory]
    [InlineData("Pip", "Install", "feed", null)]
    [InlineData("Pip", "Update", "", "machine")]
    [InlineData("pwsh", "Uninstall", "", "machine")]
    public async Task OtherManagersCannotRunInMachineScope(
        string manager, string operation, string source, string? reportedScope)
    {
        var h = new Harness();
        await h.WelcomeAsync();
        JsonElement result = Result(
            await h.RequestAsync(
                Uuid1,
                "ExecutePackageOperation",
                Op(operation, manager, "Machine", source, reportedScope)
            )
        );

        Assert.Equal("Failed", result.GetProperty("status").GetString());
        Assert.Empty(h.Backend.Submitted);
        Assert.Empty(h.Backend.UniqueRowChecks);
    }

    [Fact]
    public async Task UnidentifiedInstallationIsRejectedBeforeSubmitting()
    {
        var h = new Harness();
        h.Backend.UniqueRow = false;
        await h.WelcomeAsync();
        JsonElement result = Result(
            await h.RequestAsync(Uuid1, "ExecutePackageOperation", Op("Update", "Pip", "User", "", "machine"))
        );

        Assert.Equal("Failed", result.GetProperty("status").GetString());
        Assert.Equal(("pip", "Some.Pkg", "machine"), Assert.Single(h.Backend.UniqueRowChecks));
        Assert.Empty(h.Backend.Submitted);
    }

    [Theory]
    [InlineData("WinGet", "Install", "winget", null)]
    [InlineData("Pip", "Update", "", "user")]
    public async Task OperationForADisabledManagerFailsWithNothingStarted(
        string manager, string operation, string source, string? reportedScope)
    {
        var h = new Harness();
        h.Backend.ManagerEnabled = false;
        await h.WelcomeAsync();
        JsonElement result = Result(
            await h.RequestAsync(Uuid1, "ExecutePackageOperation", Op(operation, manager, "User", source, reportedScope))
        );

        Assert.Equal("Failed", result.GetProperty("status").GetString());
        Assert.Contains("disabled", result.GetProperty("error").GetString());
        Assert.Empty(h.Backend.ResolvedSources);
        Assert.Empty(h.Backend.UniqueRowChecks);
        Assert.Empty(h.Backend.Submitted);
    }

    [Theory]
    [InlineData("winps", ManagerName.PowerShell)]
    [InlineData("pwsh", ManagerName.PowerShell7)]
    [InlineData("dotnet-tool", ManagerName.Dotnet)]
    [InlineData("PowerShell7", ManagerName.PowerShell7)]
    public async Task AgentInventoryAliasesBindTheBrokerManager(string manager, ManagerName expected)
    {
        var h = new Harness();
        await h.WelcomeAsync();
        JsonElement result = Result(
            await h.RequestAsync(Uuid1, "ExecutePackageOperation", Op("Update", manager, "User", "", ""))
        );

        Assert.Equal("Unknown", result.GetProperty("status").GetString());
        Assert.Equal(expected, Assert.Single(h.Backend.FoundManagers));
        Assert.Equal(expected, Assert.Single(h.Backend.Submitted).Plan.Request.Manager);
        Assert.Single(h.Backend.UniqueRowChecks);
        Assert.Empty(h.Backend.ResolvedSources);
    }

    [Fact]
    public async Task UnavailableInventoryFails()
    {
        var h = new Harness();
        h.Backend.Snapshot = null;
        await h.WelcomeAsync();
        JsonElement response = await h.RequestAsync(Uuid1, "GetInventorySnapshot");

        Assert.Equal("Failed", ErrorCode(response));
        Assert.False(response.TryGetProperty("result", out _));
    }

    [Fact]
    public async Task InventorySnapshotCarriesScanTimesAndOmitsUpdatesUntilTheirScanCompletes()
    {
        IpcPackageInfo installed = new()
        {
            Name = "Some Package",
            Id = "Some.Pkg",
            Version = "1.0",
            Manager = "Winget",
            ReportedScope = null,
            SystemRestartPending = true,
        };
        IpcPackageInfo update = new()
        {
            Name = "Some Package",
            Id = "Some.Pkg",
            Version = "1.0",
            NewVersion = "2.0",
            Manager = "Winget",
            ReportedScope = "machine",
            IsUpgradable = true,
        };
        var completed = new DateTime(2026, 11, 15, 12, 30, 45, DateTimeKind.Utc).AddTicks(1234567);
        var h = new Harness();
        h.Backend.Snapshot = new RemoteInventorySnapshot(completed, null, [], [], [installed], [update]);
        await h.WelcomeAsync();

        JsonElement result = Result(await h.RequestAsync(Uuid1, "GetInventorySnapshot"));
        Assert.Equal("2026-11-15T12:30:45.1234567Z", result.GetProperty("completedAt").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("updateCompletedAt").ValueKind);
        Assert.Equal(0, result.GetProperty("updates").GetArrayLength());
        JsonElement row = Assert.Single(result.GetProperty("installedPackages").EnumerateArray().ToList());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("reportedScope").ValueKind);
        Assert.False(row.TryGetProperty("systemRestartPending", out _));

        h.Backend.Snapshot = h.Backend.Snapshot with { UpdateCompletedUtc = completed.AddMinutes(1) };
        result = Result(await h.RequestAsync(Uuid2, "GetInventorySnapshot"));
        Assert.Equal("2026-11-15T12:31:45.1234567Z", result.GetProperty("updateCompletedAt").GetString());
        JsonElement updateRow = Assert.Single(result.GetProperty("updates").EnumerateArray().ToList());
        Assert.Equal("2.0", updateRow.GetProperty("newVersion").GetString());
        Assert.Equal("machine", updateRow.GetProperty("reportedScope").GetString());
    }
}
