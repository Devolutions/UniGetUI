using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using UniGetUI.Core.Logging;

namespace UniGetUI.Services;

public class GHAuthApiRunner : IDisposable
{
    public event EventHandler<string>? OnLogin;
    public event EventHandler<string>? OnCancelled;
    private IHost? _host;

    public GHAuthApiRunner() { }

    public async Task Start()
    {
        var builder = Host.CreateDefaultBuilder();
        builder.ConfigureWebHostDefaults(webBuilder =>
        {
            webBuilder.UseKestrel();
            webBuilder.SuppressStatusMessages(true);
            webBuilder.Configure(app =>
            {
                app.UseRouting();
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapGet("/", LOGIN_CollectGitHubToken);
                });
            });
            webBuilder.UseUrls("http://localhost:58642");
        });
        _host = builder.Build();
        await _host.StartAsync();
        Logger.Info("Api running on http://localhost:58642");
    }

    private async Task LOGIN_CollectGitHubToken(HttpContext context)
    {
        var code = context.Request.Query["code"];
        if (!string.IsNullOrEmpty(code))
        {
            await context.Response.WriteAsync(ResultPage("Authentication successful", "You can now close this window and return to UniGetUI"));
            Logger.ImportantInfo($"[AUTH API] Received authentication token {code} from GitHub");
            OnLogin?.Invoke(this, code.ToString());
            return;
        }

        var error = context.Request.Query["error"];
        if (!string.IsNullOrEmpty(error))
        {
            // GitHub redirects here with an "error" parameter when the user cancels/denies the authorization.
            await context.Response.WriteAsync(ResultPage("Authentication cancelled", "You can now close this window and return to UniGetUI"));
            Logger.Warn($"[AUTH API] GitHub authentication was cancelled or failed (error: {error})");
            OnCancelled?.Invoke(this, error.ToString());
            return;
        }

        // Not an OAuth callback (e.g. a favicon request or a probe): ignore it.
        context.Response.StatusCode = 400;
    }

    private static string ResultPage(string title, string message) =>
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
            <p>{{message}}</p>
        </div></html>
        """;

    public async Task Stop()
    {
        try
        {
            ArgumentNullException.ThrowIfNull(_host);
            await _host.StopAsync();
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
        }
    }

    public void Dispose()
    {
        _host?.Dispose();
    }
}
