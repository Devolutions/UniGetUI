using System.Net;
using System.Net.Sockets;
using System.Text;
using UniGetUI.Core.Logging;

namespace UniGetUI.Avalonia.Infrastructure;

/// <summary>
/// Tiny loopback HTTP server that catches the GitHub OAuth redirect and extracts the
/// authorization code. Uses a raw TcpListener so no ASP.NET Core dependency is pulled into
/// the cross-platform Avalonia app.
/// </summary>
internal sealed class GHAuthApiRunner : IDisposable
{
    private const int Port = 58642;

    public event EventHandler<string>? OnLogin;
    public event EventHandler<string>? OnCancelled;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public Task Start()
    {
        _listener = new TcpListener(IPAddress.Loopback, Port);
        _listener.Start();
        _cts = new CancellationTokenSource();
        _ = AcceptLoopAsync(_cts.Token);
        Logger.Info($"GitHub auth loopback server running on http://127.0.0.1:{Port}");
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener!.AcceptTcpClientAsync(ct); }
            catch (Exception) { break; }
            _ = HandleClientAsync(client);
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        try
        {
            using (client)
            await using (var stream = client.GetStream())
            {
                var buffer = new byte[8192];
                int read = await stream.ReadAsync(buffer);
                string requestLine = Encoding.ASCII.GetString(buffer, 0, read).Split("\r\n")[0];

                string? code = ExtractParam(requestLine, "code");
                string? error = ExtractParam(requestLine, "error");

                // GitHub redirects here with an "error" parameter when the user cancels/denies authorization.
                bool isCallback = code is not null || error is not null;
                string body = code is not null
                    ? ResultPage("Authentication successful")
                    : error is not null
                        ? ResultPage("Authentication cancelled")
                        : "<html><body><h1>Authentication failed</h1></body></html>";

                var response = Encoding.UTF8.GetBytes(
                    $"HTTP/1.1 {(isCallback ? "200 OK" : "400 Bad Request")}\r\n" +
                    "Content-Type: text/html; charset=utf-8\r\n" +
                    $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n" +
                    "Connection: close\r\n\r\n" +
                    body);
                await stream.WriteAsync(response);
                await stream.FlushAsync();

                if (code is not null)
                {
                    Logger.ImportantInfo("[AUTH API] Received authentication code from GitHub");
                    OnLogin?.Invoke(this, code);
                }
                else if (error is not null)
                {
                    Logger.Warn($"[AUTH API] GitHub authentication was cancelled or failed (error: {error})");
                    OnCancelled?.Invoke(this, error);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn(ex);
        }
    }

    private static string? ExtractParam(string requestLine, string key)
    {
        // requestLine looks like: GET /?code=XXXX&state=YYYY HTTP/1.1
        int q = requestLine.IndexOf('?');
        if (q < 0) return null;
        int end = requestLine.IndexOf(' ', q);
        string query = end < 0 ? requestLine[(q + 1)..] : requestLine[(q + 1)..end];

        foreach (var pair in query.Split('&'))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length == 2 && kv[0] == key && kv[1].Length > 0)
                return Uri.UnescapeDataString(kv[1]);
        }
        return null;
    }

    private static string ResultPage(string title) =>
        $$"""
        <html><style>
            div {
                display: flex;
                flex-direction: column;
                align-items: center;
                justify-content: center;
                height: 100vh;
                font-family: sans-serif;
                text-align: center;
            }
        </style><script>
            window.close();
        </script><div>
            <title>UniGetUI authentication</title>
            <h1>{{title}}</h1>
            <p>You can now close this window and return to UniGetUI</p>
        </div></html>
        """;

    public async Task Stop()
    {
        try
        {
            if (_cts is not null) await _cts.CancelAsync();
            _listener?.Stop();
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
        }
    }

    public void Dispose()
    {
        _cts?.Dispose();
    }
}
