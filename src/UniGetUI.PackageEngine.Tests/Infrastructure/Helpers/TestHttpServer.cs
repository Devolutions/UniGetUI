using System.Net;
using System.Net.Sockets;

namespace UniGetUI.PackageEngine.Tests.Infrastructure.Helpers;

internal sealed class TestHttpServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Func<HttpListenerRequest, (int StatusCode, string Content, string ContentType)> _handler;
    private readonly bool _handleConcurrently;
    private int _inFlight;
    private int _peakInFlight;
    private readonly List<string> _requestPaths = [];
    private readonly List<string> _requestMethods = [];
    private readonly Task _backgroundTask;

    public TestHttpServer(
        Func<HttpListenerRequest, (int StatusCode, string Content, string ContentType)> handler,
        bool handleConcurrently = false
    )
    {
        _handler = handler;
        _handleConcurrently = handleConcurrently;

        // A free port can be taken between probing it and binding it, which turns into a random
        // failure once many fixtures run in one suite. Retry on a fresh port instead.
        for (int attempt = 1; ; attempt++)
        {
            int port = GetAvailablePort();
            BaseUri = new Uri($"http://127.0.0.1:{port}/");

            try
            {
                _listener.Prefixes.Clear();
                _listener.Prefixes.Add(BaseUri.AbsoluteUri);
                _listener.Start();
                break;
            }
            catch (HttpListenerException) when (attempt < 5)
            {
            }
        }

        _backgroundTask = Task.Run(ListenAsync);
    }

    public Uri BaseUri { get; }

    public IReadOnlyList<string> RequestPaths
    {
        get
        {
            lock (_requestPaths)
            {
                return _requestPaths.ToArray();
            }
        }
    }

    public int PeakConcurrentRequests => Volatile.Read(ref _peakInFlight);

    public IReadOnlyList<string> RequestMethods
    {
        get
        {
            lock (_requestPaths)
            {
                return _requestMethods.ToArray();
            }
        }
    }

    public void Dispose()
    {
        if (_listener.IsListening)
        {
            _listener.Stop();
        }

        _listener.Close();
        _backgroundTask.GetAwaiter().GetResult();
    }

    private async Task ListenAsync()
    {
        while (true)
        {
            try
            {
                HttpListenerContext context = await _listener.GetContextAsync();

                lock (_requestPaths)
                {
                    _requestPaths.Add(context.Request.RawUrl ?? context.Request.Url?.AbsolutePath ?? string.Empty);
                    _requestMethods.Add(context.Request.HttpMethod);
                }

                if (_handleConcurrently)
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await RespondAsync(context);
                        }
                        catch
                        {
                            // a detached response must never surface as an unobserved exception
                        }
                    });
                else
                    await RespondAsync(context);
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
        }
    }

    private async Task RespondAsync(HttpListenerContext context)
    {
        int current = Interlocked.Increment(ref _inFlight);
        int peak = Volatile.Read(ref _peakInFlight);
        while (current > peak
            && Interlocked.CompareExchange(ref _peakInFlight, current, peak) != peak)
        {
            peak = Volatile.Read(ref _peakInFlight);
        }

        try
        {
            var (statusCode, content, contentType) = _handler(context.Request);
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = contentType;
            await using StreamWriter writer = new(context.Response.OutputStream);
            await writer.WriteAsync(content);
            await writer.FlushAsync();
            context.Response.Close();
        }
        catch (HttpListenerException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    private static int GetAvailablePort()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
