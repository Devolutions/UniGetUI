using System.Net;
using System.Net.Sockets;
using System.Text;
using UniGetUI.Core.Logging;

namespace UniGetUI.Avalonia.Infrastructure;

internal sealed class GitHubAuthApiRunner : IDisposable
{
    public event EventHandler<string>? OnLogin;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public Task Start()
    {
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Loopback, 58642);
        _listener.Start();
        Logger.Info("GitHub auth callback server running on http://127.0.0.1:58642");
        _ = ListenAsync(_cts.Token);
        return Task.CompletedTask;
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(ct);
                _ = HandleClientAsync(client);
            }
            catch when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { Logger.Error(ex); break; }
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using var _ = client;
        using var stream = client.GetStream();

        // Read the HTTP request headers
        var buffer = new byte[4096];
        int read = await stream.ReadAsync(buffer);
        var requestText = Encoding.UTF8.GetString(buffer, 0, read);

        // Extract ?code= from the request line: "GET /?code=xxx&... HTTP/1.1"
        string? code = null;
        var firstLine = requestText.Split('\n', 2)[0];
        var codeMatch = System.Text.RegularExpressions.Regex.Match(firstLine, @"[?&]code=([^& ]+)");
        if (codeMatch.Success)
            code = Uri.UnescapeDataString(codeMatch.Groups[1].Value);

        const string body = """
            <html><style>
                div { display:flex; flex-direction:column; align-items:center;
                      justify-content:center; height:100vh; font-family:sans-serif; text-align:center; }
            </style><script>window.close();</script><div>
                <title>UniGetUI authentication</title>
                <h1>Authentication successful</h1>
                <p>You can now close this window and return to UniGetUI</p>
            </div></html>
            """;

        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var header = $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.UTF8.GetBytes(header));
        await stream.WriteAsync(bodyBytes);

        if (!string.IsNullOrEmpty(code))
        {
            Logger.ImportantInfo("[AUTH API] Received authentication token from GitHub");
            OnLogin?.Invoke(this, code);
        }
    }

    public Task Stop()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { /* ignore */ }
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        try { _listener?.Stop(); } catch { /* ignore */ }
    }
}
