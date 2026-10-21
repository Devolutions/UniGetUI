using System.Diagnostics;
using System.Net;
using UniGetUI.Core.Data;
using UniGetUI.Core.Tools;

namespace UniGetUI.PackageEngine.Managers.CargoManager;

internal sealed class CargoHttp : IDisposable
{
    internal const int ManifestLimit = 4 * 1024 * 1024;
    internal const long ArchiveLimit = 256L * 1024 * 1024;
    private static readonly SemaphoreSlim ApiGate = new(1);
    private static long _lastApiRequest;
    private readonly HttpClient _client;
    internal static Func<HttpMessageHandler>? TestHandlerFactory { get; set; }

    internal CargoHttp()
    {
        HttpMessageHandler handler;
        if (TestHandlerFactory is { } factory)
            handler = factory();
        else
        {
            var configured = CoreTools.GenericHttpClientParameters;
            configured.AllowAutoRedirect = false;
            handler = configured;
        }
        _client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(CoreData.UserAgentString);
    }

    internal static async Task PaceApiAsync(CancellationToken cancellationToken)
    {
        await ApiGate.WaitAsync(cancellationToken);
        try
        {
            double elapsed = Stopwatch.GetElapsedTime(_lastApiRequest).TotalMilliseconds;
            if (_lastApiRequest != 0 && elapsed < 1000)
                await Task.Delay(TimeSpan.FromMilliseconds(1000 - elapsed), cancellationToken);
            _lastApiRequest = Stopwatch.GetTimestamp();
        }
        finally
        {
            ApiGate.Release();
        }
    }

    internal async Task<byte[]?> GetAsync(Uri url, long limit, bool allowMissing,
        CancellationToken cancellationToken, Action<ulong, ulong>? progress = null)
    {
        for (int redirect = 0; redirect <= 5; redirect++)
        {
            if (url.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(url.UserInfo))
                throw new InvalidDataException($"Refusing a non-HTTPS or credential-bearing Cargo URL: {url}");
            using HttpResponseMessage response = await _client.GetAsync(
                url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                url = response.Headers.Location is { } location
                    ? new Uri(url, location)
                    : throw new InvalidDataException("Cargo download redirect has no location.");
                continue;
            }
            if (allowMissing && response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                return null;
            response.EnsureSuccessStatusCode();
            long? length = response.Content.Headers.ContentLength;
            if (length > limit)
                throw new InvalidDataException("Cargo download exceeds the allowed size.");
            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using MemoryStream output = new();
            byte[] buffer = new byte[64 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken)) != 0)
            {
                if (output.Length + read > limit)
                    throw new InvalidDataException("Cargo download exceeds the allowed size.");
                output.Write(buffer, 0, read);
                progress?.Invoke((ulong)output.Length, (ulong)Math.Max(0, length ?? 0));
            }
            if (length is not null && output.Length != length)
                throw new InvalidDataException("Cargo download was truncated.");
            return output.ToArray();
        }
        throw new InvalidDataException("Too many Cargo download redirects.");
    }

    public void Dispose() => _client.Dispose();
}
