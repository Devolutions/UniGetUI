// Minimal blocking HTTP client standing in for the global `fetch`.
//
// Like Node's fetch: no system proxy, redirects followed, gzip/deflate/br
// decoded, User-Agent `Sys.UserAgent` unless a request sets its own.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Skills;

internal sealed class HttpResponse(int status, Dictionary<string, string> headers, byte[] body)
{
    public int Status { get; } = status;
    public byte[] Body { get; } = body;
    public bool Ok => Status is >= 200 and < 300;

    public string? Header(string name) => headers.TryGetValue(name.ToLowerInvariant(), out var v) ? v : null;

    public string Text() => DecodeUtf8(Body);

    /// `response.json()`: UTF-8 decode (dropping a BOM) then JSON.parse.
    public JsonNode? Json() => Skills.Json.TryParse(Text(), out var n) ? n : null;

    public bool TryJson(out JsonNode? node) => Skills.Json.TryParse(Text(), out node);

    /// TextDecoder('utf-8'): replacement characters, leading BOM removed.
    public static string DecodeUtf8(byte[] bytes)
    {
        var s = Encoding.UTF8.GetString(bytes);
        return s.StartsWith('﻿') ? s[1..] : s;
    }
}

internal sealed class HttpException(string message) : Exception(message);

internal sealed class HttpTooLargeException(long max) : Exception($"too-large:{max}")
{
    public long Max { get; } = max;
}

internal sealed class HttpRequest(string url)
{
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 20,
        AutomaticDecompression = DecompressionMethods.All,
        UseProxy = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    {
        Timeout = System.Threading.Timeout.InfiniteTimeSpan,
    };

    private readonly List<(string Key, string Value)> _headers = new();
    private TimeSpan _timeout = TimeSpan.FromSeconds(300);
    private long? _maxBytes;

    public static HttpRequest Get(string url) => new(url);

    public HttpRequest Header(string k, string v)
    {
        _headers.Add((k, v));
        return this;
    }

    public HttpRequest Timeout(TimeSpan t)
    {
        _timeout = t;
        return this;
    }

    public HttpRequest MaxBytes(long n)
    {
        _maxBytes = n;
        return this;
    }

    /// Perform the request. Transport failures throw <see cref="HttpException"/>;
    /// any HTTP status is a response. Oversized successful bodies throw
    /// <see cref="HttpTooLargeException"/>.
    public HttpResponse Send()
    {
        try
        {
            return SendAsync().GetAwaiter().GetResult();
        }
        catch (HttpTooLargeException)
        {
            throw;
        }
        catch (OperationCanceledException) when (Sys.Context?.Cancel.IsCancellationRequested == true)
        {
            throw;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException or IOException or UriFormatException or InvalidOperationException or NotSupportedException)
        {
            throw new HttpException(e is TaskCanceledException or OperationCanceledException ? "The operation was aborted due to timeout" : e.Message);
        }
    }

    private async Task<HttpResponse> SendAsync()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Sys.Context?.Cancel ?? CancellationToken.None);
        cts.CancelAfter(_timeout);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        var hasUa = false;
        foreach (var (k, v) in _headers)
        {
            if (k.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)) hasUa = true;
            req.Headers.TryAddWithoutValidation(k, v);
        }
        if (!hasUa) req.Headers.TryAddWithoutValidation("User-Agent", Sys.UserAgent);
        using var resp = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        var status = (int)resp.StatusCode;
        var headers = new Dictionary<string, string>();
        foreach (var h in resp.Headers) headers[h.Key.ToLowerInvariant()] = string.Join(", ", h.Value);
        foreach (var h in resp.Content.Headers) headers[h.Key.ToLowerInvariant()] = string.Join(", ", h.Value);

        // Error statuses are reported before size limits (the TS download checks
        // `response.ok` first); for size-limited requests their bodies are skipped.
        var ok = status is >= 200 and < 300;
        var max = ok ? _maxBytes : null;
        if (!ok && _maxBytes != null) return new HttpResponse(status, headers, []);
        if (max != null && resp.Content.Headers.ContentLength is { } len && len > max) throw new HttpTooLargeException(max.Value);

        await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, cts.Token).ConfigureAwait(false)) > 0)
        {
            ms.Write(buffer, 0, read);
            if (max != null && ms.Length > max) throw new HttpTooLargeException(max.Value);
        }
        return new HttpResponse(status, headers, ms.ToArray());
    }

    /// Send, returning null on transport failure (the common `try { fetch } catch {}`).
    public HttpResponse? TrySend()
    {
        try
        {
            return Send();
        }
        catch (HttpException)
        {
            return null;
        }
        catch (HttpTooLargeException)
        {
            return null;
        }
    }
}
