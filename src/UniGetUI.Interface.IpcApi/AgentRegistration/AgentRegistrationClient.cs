using UniGetUI.Core.Logging;

namespace UniGetUI.Interface.AgentRegistration;

/// <summary>
/// Keeps this process registered with the Devolutions Agent while it is running: connect,
/// authenticate the server, run the session, and reconnect with jittered exponential backoff.
/// It never elevates and does nothing on platforms other than Windows.
/// </summary>
public sealed class AgentRegistrationClient
{
    private readonly IAgentPipeConnector _connector;
    private readonly AgentRequestDispatcher _dispatcher;
    private readonly AgentRegistrationOptions _options;
    private readonly TimeProvider _time;
    private readonly Action<string> _log;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    internal AgentRegistrationClient(
        IAgentPipeConnector connector,
        IAgentRegistrationBackend backend,
        AgentRegistrationOptions options,
        TimeProvider time,
        Action<string> log
    )
    {
        _connector = connector;
        _dispatcher = new AgentRequestDispatcher(backend);
        _options = options;
        _time = time;
        _log = log;
    }

    /// <summary>The production client, or null when this platform has no Agent registration.</summary>
    public static AgentRegistrationClient? CreateDefault()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        return new AgentRegistrationClient(
            new WindowsAgentPipeConnector(),
            new IpcRemoteInventoryBackend(),
            new AgentRegistrationOptions(),
            TimeProvider.System,
            message => Logger.Debug(message)
        );
    }

    public void Start()
    {
        if (_loop is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;
        _loop = Task.Run(() => RunAsync(token), CancellationToken.None);
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts = _cts;
        Task? loop = _loop;
        if (cts is null || loop is null)
        {
            return;
        }

        _cts = null;
        _loop = null;
        await cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await loop.ConfigureAwait(false);
        }
        catch (Exception) { }

        cts.Dispose();
    }

    internal async Task RunAsync(CancellationToken ct)
    {
        TimeSpan backoff = _options.MinBackoff;
        bool unavailableLogged = false;
        while (!ct.IsCancellationRequested)
        {
            bool registered = false;
            long? registeredAt = null;
            try
            {
                registered = await RunOnceAsync(() => registeredAt = _time.GetTimestamp(), ct)
                    .ConfigureAwait(false);
            }
            catch (Exception) when (!ct.IsCancellationRequested) { }
            catch (OperationCanceledException)
            {
                break;
            }

            if (registered)
            {
                _log("Devolutions Agent registration closed.");
                unavailableLogged = false;
            }
            else if (!unavailableLogged)
            {
                _log("Devolutions Agent registration is not available; will keep retrying.");
                unavailableLogged = true;
            }

            backoff = BackoffAfterSession(backoff, registeredAt);
            double jitter = 0.5 + Random.Shared.NextDouble() * 0.5;
            try
            {
                await Task.Delay(backoff * jitter, _time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, _options.MaxBackoff.Ticks));
        }
    }

    // An Agent that welcomes and then drops the session at once must not be redialled every
    // MinBackoff, so the backoff starts over only after a session that stayed up.
    internal TimeSpan BackoffAfterSession(TimeSpan backoff, long? registeredAt) =>
        registeredAt is long at && _time.GetElapsedTime(at) >= _options.StableSessionTime
            ? _options.MinBackoff
            : backoff;

    /// <summary>One connection attempt; returns whether the Agent registered this process.</summary>
    internal async Task<bool> RunOnceAsync(Action onRegistered, CancellationToken ct)
    {
        // Hello is due within HelloTimeout of connecting, so the deadline starts before the
        // connection and authentication and ends once Hello is written.
        using var handshake = new CancellationTokenSource(_options.HelloTimeout, _time);
        using var connecting = CancellationTokenSource.CreateLinkedTokenSource(ct, handshake.Token);
        IAgentPipeConnection? connection = await _connector
            .ConnectAsync(connecting.Token)
            .ConfigureAwait(false);
        if (connection is null)
        {
            return false;
        }

        await using (connection.ConfigureAwait(false))
        {
            if (!connection.VerifyServer())
            {
                return false;
            }

            var session = new AgentRegistrationSession(
                connection.Stream,
                _dispatcher,
                _options,
                _time
            );
            session.Registered += () =>
            {
                onRegistered();
                _log("Registered with the Devolutions Agent.");
            };
            return await session.RunAsync(ct, handshake.Token).ConfigureAwait(false);
        }
    }
}
