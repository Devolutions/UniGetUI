using System.Text.Json;
using UniGetUI.Core.Data;

namespace UniGetUI.Interface.AgentRegistration;

internal sealed record AgentRegistrationOptions
{
    public TimeSpan HelloTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan PingAfter { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(90);
    public TimeSpan MinBackoff { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>How long a registered session must last before the reconnect backoff starts over.</summary>
    public TimeSpan StableSessionTime { get; init; } = TimeSpan.FromSeconds(30);
    public int MaxOutstandingRequests { get; init; } = 8;
    public string ClientVersion { get; init; } = CoreData.VersionName;
}

/// <summary>
/// One registration connection: Hello, Welcome, then requests, pings and responses until
/// either side closes. The caller has already authenticated the server.
/// </summary>
internal sealed class AgentRegistrationSession
{
    private const int ProtocolVersion = 1;

    private readonly Stream _stream;
    private readonly AgentRequestDispatcher _dispatcher;
    private readonly AgentRegistrationOptions _options;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private long _lastSent;
    private int _outstanding;

    public AgentRegistrationSession(
        Stream stream,
        AgentRequestDispatcher dispatcher,
        AgentRegistrationOptions options,
        TimeProvider time
    )
    {
        _stream = stream;
        _dispatcher = dispatcher;
        _options = options;
        _time = time;
    }

    /// <summary>Raised once, when the Agent's Welcome has been accepted.</summary>
    public event Action? Registered;

    /// <summary>
    /// Runs until the connection ends; returns whether the Agent ever welcomed it.
    /// <paramref name="helloDeadline"/> bounds only the Hello write, so it cannot end a registered session.
    /// </summary>
    public async Task<bool> RunAsync(CancellationToken ct, CancellationToken helloDeadline = default)
    {
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        CancellationToken token = sessionCts.Token;
        bool registered = false;
        Task? pinger = null;
        try
        {
            using (var helloCts = CancellationTokenSource.CreateLinkedTokenSource(token, helloDeadline))
            {
                await SendAsync(BuildHello(), helloCts.Token).ConfigureAwait(false);
            }
            pinger = Task.Run(() => PingLoopAsync(sessionCts), CancellationToken.None);

            JsonElement welcome = AgentFrameCodec.Parse(
                await ReadAsync(_options.IdleTimeout, token).ConfigureAwait(false)
                    ?? throw new AgentProtocolException(),
                out string welcomeKind
            );
            if (
                welcomeKind != "Welcome"
                || !welcome.TryGetProperty("protocolVersion", out JsonElement version)
                || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out int number)
                || number != ProtocolVersion
            )
            {
                return false;
            }

            registered = true;
            Registered?.Invoke();

            while (true)
            {
                byte[]? payload = await ReadAsync(_options.IdleTimeout, token).ConfigureAwait(false);
                if (payload is null)
                {
                    break;
                }

                JsonElement root = AgentFrameCodec.Parse(payload, out string kind);
                switch (kind)
                {
                    case "Ping":
                        await SendAsync(Kind("Pong"), token).ConfigureAwait(false);
                        break;
                    case "Pong":
                        break;
                    case "Request":
                        await HandleRequestAsync(root, sessionCts).ConfigureAwait(false);
                        break;
                    default:
                        throw new AgentProtocolException();
                }
            }
        }
        catch (Exception)
        {
            // Every failure ends the connection; the reason is never surfaced.
        }
        finally
        {
            try
            {
                sessionCts.Cancel();
            }
            catch (ObjectDisposedException) { }

            if (pinger is not null)
            {
                try
                {
                    await pinger.ConfigureAwait(false);
                }
                catch (Exception) { }
            }
        }

        return registered;
    }

    private async Task HandleRequestAsync(JsonElement root, CancellationTokenSource sessionCts)
    {
        CancellationToken token = sessionCts.Token;
        if (
            !root.TryGetProperty("id", out JsonElement idElement)
            || idElement.ValueKind != JsonValueKind.String
            || !AgentRequestDispatcher.IsLowercaseUuid(idElement.GetString())
        )
        {
            throw new AgentProtocolException();
        }

        string id = idElement.GetString()!;
        if (Interlocked.Increment(ref _outstanding) > _options.MaxOutstandingRequests)
        {
            Interlocked.Decrement(ref _outstanding);
            await SendAsync(AgentRequestDispatcher.Busy(id), token).ConfigureAwait(false);
            return;
        }

        string? command =
            root.TryGetProperty("command", out JsonElement commandElement)
            && commandElement.ValueKind == JsonValueKind.String
                ? commandElement.GetString()
                : null;
        root.TryGetProperty("arguments", out JsonElement arguments);

        _ = Task.Run(
            async () =>
            {
                try
                {
                    byte[] response =
                        command is null
                            ? AgentRequestDispatcher.InvalidArguments(id)
                            : _dispatcher.Handle(id, command, arguments);
                    await SendAsync(response, token).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    try
                    {
                        sessionCts.Cancel();
                    }
                    catch (ObjectDisposedException) { }
                }
                finally
                {
                    Interlocked.Decrement(ref _outstanding);
                }
            },
            CancellationToken.None
        );
    }

    private async Task PingLoopAsync(CancellationTokenSource sessionCts)
    {
        CancellationToken token = sessionCts.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                TimeSpan idle = _time.GetElapsedTime(Interlocked.Read(ref _lastSent));
                if (idle >= _options.PingAfter)
                {
                    await SendAsync(Kind("Ping"), token).ConfigureAwait(false);
                    continue;
                }

                await Task.Delay(_options.PingAfter - idle, _time, token).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            try
            {
                sessionCts.Cancel();
            }
            catch (ObjectDisposedException) { }
        }
    }

    private async Task<byte[]?> ReadAsync(TimeSpan timeout, CancellationToken token)
    {
        using var timeoutCts = new CancellationTokenSource(timeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);
        return await AgentFrameCodec.ReadAsync(_stream, linked.Token).ConfigureAwait(false);
    }

    private async Task SendAsync(byte[] payload, CancellationToken token)
    {
        await _writeLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await AgentFrameCodec.WriteAsync(_stream, payload, token).ConfigureAwait(false);
            Interlocked.Exchange(ref _lastSent, _time.GetTimestamp());
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private byte[] BuildHello()
    {
        string clientVersion = _options.ClientVersion;
        if (clientVersion.Length > 128)
        {
            clientVersion = clientVersion[..128];
        }

        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("kind", "Hello");
            writer.WriteNumber("protocolVersion", ProtocolVersion);
            writer.WriteString("clientVersion", clientVersion);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static byte[] Kind(string kind)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("kind", kind);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }
}
