using System.Net;
using System.Net.Sockets;

namespace UniGetUI.PackageEngine.Tests.Infrastructure.Helpers;

internal sealed class TestHttpServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Func<HttpListenerRequest, (int StatusCode, string Content, string ContentType)> _handler;
    private readonly List<string> _requestPaths = [];
    private readonly Task _backgroundTask;

    public TestHttpServer(
        Func<HttpListenerRequest, (int StatusCode, string Content, string ContentType)> handler
    )
    {
        _handler = handler;
        int port = GetAvailablePort();
        BaseUri = new Uri($"http://127.0.0.1:{port}/");
        _listener.Prefixes.Add(BaseUri.AbsoluteUri);
        _listener.Start();
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
                }

                var (statusCode, content, contentType) = _handler(context.Request);
                context.Response.StatusCode = statusCode;
                context.Response.ContentType = contentType;
                using StreamWriter writer = new(context.Response.OutputStream);
                await writer.WriteAsync(content);
                await writer.FlushAsync();
                context.Response.Close();
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

    private static int GetAvailablePort()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
