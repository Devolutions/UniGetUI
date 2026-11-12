using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using UniGetUI.Core.Logging;

namespace UniGetUI.Extensions.Xcode.AppleAuth;

/// <summary>
/// Runs the system HTTPS client in a clean environment. Requests and cookies travel over its private pipes,
/// rather than through .NET's HTTP diagnostics or the child's arguments and environment.
/// </summary>
internal sealed class AppleCurlTransport : HttpMessageHandler
{
    private const int MaximumRequestBytes = 1024 * 1024;
    private const int MaximumHeaderBytes = 64 * 1024;
    private const int MaximumRedirects = 5;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly HashSet<string> AppleHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "appstoreconnect.apple.com",
        "idmsa.apple.com",
        "developer.apple.com",
        "developerservices2.apple.com",
        "download.developer.apple.com",
    };

    private readonly CookieContainer _cookies;
    private readonly AppleCurlTransportOptions _options;
    private readonly CancellationTokenSource _lifetime = new();
    private int _disposed;

    public AppleCurlTransport(CookieContainer cookies, AppleCurlTransportOptions? options = null)
    {
        _cookies = cookies;
        _options = options ?? new AppleCurlTransportOptions();
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsMacOS())
            throw new HttpRequestException("Apple sign-in is available only on macOS.");
        if (request.RequestUri is not { IsAbsoluteUri: true } uri || !Allowed(uri))
            throw new HttpRequestException("The Apple request has an unapproved address.");
        if (request.Method != HttpMethod.Get && request.Method != HttpMethod.Post && request.Method != HttpMethod.Put)
            throw new HttpRequestException("The Apple request uses an unsupported method.");

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        byte[]? body = await ReadRequestBodyAsync(request.Content, lifetime.Token).ConfigureAwait(false);
        try
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            for (int redirects = 0; ; redirects++)
            {
                if (!visited.Add(uri.AbsoluteUri))
                    throw new HttpRequestException("Apple returned a redirect loop.");
                request.RequestUri = uri;
                HttpResponseMessage response = await SendOnceAsync(request, body, lifetime.Token).ConfigureAwait(false);
                if (!IsRedirect(response.StatusCode) || !_options.FollowRedirects)
                    return response;

                Uri? location = response.Headers.Location;
                if (location is null)
                    return response;
                if (!Uri.TryCreate(uri, location, out Uri? target))
                {
                    response.Dispose();
                    throw new HttpRequestException("Apple returned an invalid redirect.");
                }
                if (request.Method == HttpMethod.Get && IsUnauthorizedRedirect(uri, target))
                {
                    request.RequestUri = target;
                    return response;
                }

                bool sameOrigin = string.Equals(uri.Scheme, target.Scheme, StringComparison.OrdinalIgnoreCase)
                                  && string.Equals(uri.IdnHost, target.IdnHost, StringComparison.OrdinalIgnoreCase)
                                  && uri.Port == target.Port;
                response.Dispose();
                if (request.Method != HttpMethod.Get || body is not null || !Allowed(target) || !sameOrigin)
                    throw new HttpRequestException("Apple redirected the request to an unapproved destination.");
                if (redirects >= MaximumRedirects)
                    throw new HttpRequestException("Apple redirected the request too many times.");
                uri = target;
            }
        }
        finally
        {
            if (body is not null)
                CryptographicOperations.ZeroMemory(body);
        }
    }

    private async Task<HttpResponseMessage> SendOnceAsync(
        HttpRequestMessage request, byte[]? body, CancellationToken cancellationToken)
    {
        Uri uri = request.RequestUri!;
        ProcessStartInfo start = CreateProcess();
        _options.BeforeStart?.Invoke(start);
        Process process;
        try
        {
            process = Process.Start(start) ?? throw new HttpRequestException("Could not start the Apple network client.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new HttpRequestException("Could not start the Apple network client.");
        }

        Task stderr = DrainErrorAsync(process.StandardError.BaseStream);
        var output = new BufferedStream(process.StandardOutput.BaseStream, 16 * 1024);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.HeaderTimeout);
            await WriteRequestAsync(process.StandardInput, request, body, timeout.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            ParsedResponse parsed = await ReadResponseAsync(output, timeout.Token).ConfigureAwait(false);
            var response = new HttpResponseMessage((HttpStatusCode)parsed.Status)
            {
                RequestMessage = request,
                Version = HttpVersion.Version11,
                Content = new StreamContent(new CurlResponseStream(process, output, stderr, _lifetime.Token)),
            };
            foreach ((string name, string value) in parsed.Headers)
            {
                if (_options.UseCookies && name.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        StoreSetCookie(_cookies, uri, value);
                    }
                    catch (Exception ex) when (ex is CookieException or ArgumentException)
                    {
                        response.Dispose();
                        throw new HttpRequestException("Apple returned an invalid session cookie.");
                    }
                }
                if (!response.Headers.TryAddWithoutValidation(name, value)
                    && !response.Content.Headers.TryAddWithoutValidation(name, value))
                {
                    response.Dispose();
                    throw new HttpRequestException("Apple returned an invalid response header.");
                }
            }
            if (response.Content.Headers.ContentEncoding.Any(value =>
                    !value.Equals("identity", StringComparison.OrdinalIgnoreCase)))
            {
                response.Dispose();
                throw new HttpRequestException("Apple returned an unsupported response encoding.");
            }
            return response;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Stop(process, output, stderr);
            throw new HttpRequestException("Apple did not respond in time.");
        }
        catch (OperationCanceledException)
        {
            Stop(process, output, stderr);
            throw;
        }
        catch (HttpRequestException)
        {
            Stop(process, output, stderr);
            throw;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or DecoderFallbackException)
        {
            Stop(process, output, stderr);
            throw new HttpRequestException("The Apple network connection failed.");
        }
    }

    private ProcessStartInfo CreateProcess()
    {
        var start = new ProcessStartInfo("/usr/bin/curl")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            CreateNoWindow = true,
        };
        start.Environment.Clear();
        foreach (string argument in new[]
                 {
                     "-q", "--config", "-", "--silent", "--http1.1", "--no-keepalive",
                     "--noproxy", "*", "--proto", "=https", "--retry", "0", "--connect-timeout", "30",
                     "--disallow-username-in-url", "--dump-header", "-",
                 })
            start.ArgumentList.Add(argument);
        if (_options.CertificateAuthority is { } authority)
        {
            start.ArgumentList.Add("--cacert");
            start.ArgumentList.Add(authority);
        }
        return start;
    }

    private async Task WriteRequestAsync(
        StreamWriter input, HttpRequestMessage request, byte[]? body, CancellationToken cancellationToken)
    {
        Uri uri = request.RequestUri!;
        await WriteOptionAsync(input, "url", uri.AbsoluteUri, cancellationToken).ConfigureAwait(false);
        await WriteOptionAsync(input, "request", request.Method.Method, cancellationToken).ConfigureAwait(false);
        await WriteOptionAsync(input, "header", "Connection: close", cancellationToken).ConfigureAwait(false);
        await WriteOptionAsync(input, "header", "Accept-Encoding: identity", cancellationToken).ConfigureAwait(false);
        await WriteOptionAsync(input, "header", "Expect:", cancellationToken).ConfigureAwait(false);
        foreach ((string name, IEnumerable<string> values) in request.Headers)
            await WriteHeadersAsync(input, name, values, cancellationToken).ConfigureAwait(false);
        if (request.Content is not null)
        {
            foreach ((string name, IEnumerable<string> values) in request.Content.Headers)
                await WriteHeadersAsync(input, name, values, cancellationToken).ConfigureAwait(false);
        }

        string cookie = _options.UseCookies ? FormatCookieHeader(_cookies, uri) : "";
        if (cookie.Length > MaximumHeaderBytes)
            throw new HttpRequestException("The Apple session has too many cookies.");
        if (cookie.Length > 0)
            await WriteOptionAsync(input, "header", "Cookie: " + cookie, cancellationToken).ConfigureAwait(false);
        if (body is not null)
            await WriteOptionAsync(input, "data-raw", StrictUtf8.GetString(body), cancellationToken).ConfigureAwait(false);
        else if (request.Method != HttpMethod.Get)
            await WriteOptionAsync(input, "header", "Content-Length: 0", cancellationToken).ConfigureAwait(false);
        await input.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteHeadersAsync(
        StreamWriter input, string name, IEnumerable<string> values, CancellationToken cancellationToken)
    {
        if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            return;
        if (name.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Host", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Connection", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase))
            throw new HttpRequestException("The Apple request contains a disallowed header.");
        foreach (string value in values)
            await WriteOptionAsync(input, "header", name + ": " + value, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteOptionAsync(StreamWriter writer, string name, string value, CancellationToken cancellationToken)
    {
        if (value.Length > MaximumRequestBytes || value.Any(char.IsControl))
            throw new HttpRequestException("The Apple request contains invalid data.");
        string quoted = value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
        await writer.WriteAsync($"{name} = \"{quoted}\"\n".AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]?> ReadRequestBodyAsync(HttpContent? content, CancellationToken cancellationToken)
    {
        if (content is null)
            return null;
        if (content.Headers.ContentLength > MaximumRequestBytes)
            throw new HttpRequestException("The Apple request is too large.");
        await using Stream input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[16 * 1024];
        try
        {
            int read;
            while ((read = await input.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaximumRequestBytes)
                    throw new HttpRequestException("The Apple request is too large.");
                buffer.Write(chunk, 0, read);
            }
            return buffer.ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(chunk);
            if (buffer.TryGetBuffer(out ArraySegment<byte> segment))
                CryptographicOperations.ZeroMemory(segment.AsSpan());
        }
    }

    private static async Task<ParsedResponse> ReadResponseAsync(Stream input, CancellationToken cancellationToken)
    {
        int total = 0;
        for (int informational = 0; informational <= 4; informational++)
        {
            using var block = new MemoryStream();
            byte[] one = new byte[1];
            int suffix = 0;
            while (suffix != 0x0d0a0d0a)
            {
                int read = await input.ReadAsync(one, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    throw new HttpRequestException("Apple closed the connection before sending a response.");
                if (++total > MaximumHeaderBytes)
                    throw new HttpRequestException("Apple returned too many response headers.");
                block.WriteByte(one[0]);
                suffix = (suffix << 8) | one[0];
            }
            string[] lines = Encoding.Latin1.GetString(block.ToArray()).Split("\r\n", StringSplitOptions.None);
            string[] status = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (status.Length < 2 || !status[0].StartsWith("HTTP/1.", StringComparison.Ordinal)
                || !int.TryParse(status[1], NumberStyles.None, CultureInfo.InvariantCulture, out int code)
                || code is < 100 or > 599)
                throw new HttpRequestException("Apple returned an invalid HTTP response.");
            if (code is >= 100 and < 200)
            {
                if (code == 101)
                    throw new HttpRequestException("Apple returned an unsupported HTTP response.");
                continue;
            }

            List<(string Name, string Value)> headers = [];
            foreach (string line in lines.Skip(1))
            {
                if (line.Length == 0)
                    break;
                int colon = line.IndexOf(':');
                if (colon <= 0 || !line[..colon].All(IsHeaderCharacter)
                    || line[(colon + 1)..].Any(character => char.IsControl(character) && character != '\t'))
                    throw new HttpRequestException("Apple returned an invalid response header.");
                headers.Add((line[..colon], line[(colon + 1)..].Trim(' ', '\t')));
            }
            return new ParsedResponse(code, headers);
        }
        throw new HttpRequestException("Apple returned too many intermediate responses.");
    }

    private static bool IsHeaderCharacter(char value)
        => char.IsAsciiLetterOrDigit(value) || "!#$%&'*+-.^_`|~".Contains(value, StringComparison.Ordinal);

    internal static void StoreSetCookie(CookieContainer cookies, Uri uri, string header)
    {
        int semicolon = header.IndexOf(';');
        ReadOnlySpan<char> pair = (semicolon >= 0 ? header.AsSpan(0, semicolon) : header.AsSpan()).Trim();
        int equals = pair.IndexOf('=');
        if (equals <= 0)
            throw new CookieException("The Set-Cookie header has no cookie name.");
        string name = pair[..equals].Trim().ToString();
        if (name.Length == 0 || !name.All(IsHeaderCharacter))
            throw new CookieException("The Set-Cookie header has an invalid cookie name.");
        string rawValue = pair[(equals + 1)..].Trim().ToString();
        string storedValue = rawValue.Contains(',') && !(rawValue.Length >= 2 && rawValue[0] == '"' && rawValue[^1] == '"')
            ? $"\"{rawValue}\""
            : rawValue;

        string? domain = null;
        string? path = null;
        DateTime? expires = null;
        TimeSpan? maxAge = null;
        bool secure = false;
        bool httpOnly = false;

        if (semicolon >= 0)
        {
            foreach (string rawAttribute in header[(semicolon + 1)..].Split(';'))
            {
                ReadOnlySpan<char> attribute = rawAttribute.AsSpan().Trim();
                if (attribute.IsEmpty)
                    continue;
                int attrEquals = attribute.IndexOf('=');
                ReadOnlySpan<char> attrName = (attrEquals >= 0 ? attribute[..attrEquals] : attribute).Trim();
                ReadOnlySpan<char> attrValue = attrEquals >= 0 ? attribute[(attrEquals + 1)..].Trim() : default;

                if (attrName.Equals("Domain", StringComparison.OrdinalIgnoreCase))
                {
                    ReadOnlySpan<char> trimmedDomain = attrValue.TrimStart('.');
                    if (trimmedDomain.IsEmpty)
                        throw new CookieException("The Set-Cookie header has an empty Domain attribute.");
                    string host = trimmedDomain.ToString();
                    domain = uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6 ? host : "." + host;
                }
                else if (attrName.Equals("Path", StringComparison.OrdinalIgnoreCase) && !attrValue.IsEmpty && attrValue[0] == '/')
                {
                    path = attrValue.ToString();
                }
                else if (attrName.Equals("Max-Age", StringComparison.OrdinalIgnoreCase)
                         && long.TryParse(attrValue, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long seconds))
                {
                    maxAge = seconds <= 0
                        ? TimeSpan.Zero
                        : TimeSpan.FromSeconds(Math.Min(seconds, 3650L * 86_400));
                }
                else if (attrName.Equals("Expires", StringComparison.OrdinalIgnoreCase)
                         && DateTimeOffset.TryParse(attrValue, CultureInfo.InvariantCulture,
                             DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset parsedExpires))
                {
                    expires = parsedExpires.UtcDateTime;
                }
                else if (attrName.Equals("Secure", StringComparison.OrdinalIgnoreCase) && attrEquals < 0)
                {
                    secure = true;
                }
                else if (attrName.Equals("HttpOnly", StringComparison.OrdinalIgnoreCase) && attrEquals < 0)
                {
                    httpOnly = true;
                }
            }
        }

        DateTime? effectiveExpires = maxAge is { } max
            ? (max <= TimeSpan.Zero ? DateTime.UnixEpoch : DateTime.UtcNow.Add(max))
            : expires;

        if (domain is not null)
        {
            var cookie = new Cookie(name, storedValue, path ?? "/", domain)
            {
                Secure = secure,
                HttpOnly = httpOnly,
            };
            if (effectiveExpires is { } expiry)
                cookie.Expires = expiry;
            cookies.Add(uri, cookie);
            return;
        }

        var builder = new StringBuilder();
        builder.Append(name).Append('=').Append(storedValue);
        if (path is not null)
            builder.Append("; Path=").Append(path);
        if (effectiveExpires is { } hostExpiry)
            builder.Append("; Expires=").Append(hostExpiry.ToUniversalTime().ToString("R", CultureInfo.InvariantCulture));
        if (secure)
            builder.Append("; Secure");
        if (httpOnly)
            builder.Append("; HttpOnly");
        cookies.SetCookies(uri, builder.ToString());
    }

    internal static string FormatCookieHeader(CookieContainer cookies, Uri uri)
        => string.Join("; ", cookies.GetCookies(uri).Cast<Cookie>()
            .Where(cookie => !cookie.Expired && cookie.Name.Length > 0)
            .Select(cookie => $"{cookie.Name}={UnwrapStoredValue(cookie.Value)}"));

    private static string UnwrapStoredValue(string value)
        => value.Length >= 2 && value[0] == '"' && value[^1] == '"' && value.Contains(',')
            ? value[1..^1]
            : value;

    private bool Allowed(Uri uri)
        => uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo)
           && string.IsNullOrEmpty(uri.Fragment) && uri.AbsoluteUri.Length <= MaximumHeaderBytes
           && (_options.AllowAddress?.Invoke(uri) ?? (uri.IsDefaultPort && AppleHosts.Contains(uri.IdnHost)));

    private static bool IsRedirect(HttpStatusCode status)
        => status is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static bool IsUnauthorizedRedirect(Uri from, Uri target)
        => from.IdnHost.Equals("download.developer.apple.com", StringComparison.OrdinalIgnoreCase)
           && target.Scheme == Uri.UriSchemeHttps && target.IsDefaultPort && string.IsNullOrEmpty(target.UserInfo)
           && target.IdnHost.Equals("developer.apple.com", StringComparison.OrdinalIgnoreCase)
           && (target.AbsolutePath.Equals("/unauthorized", StringComparison.OrdinalIgnoreCase)
               || target.AbsolutePath.StartsWith("/unauthorized/", StringComparison.OrdinalIgnoreCase));

    private static async Task DrainErrorAsync(Stream error)
    {
        byte[] buffer = new byte[4096];
        try
        {
            while (await error.ReadAsync(buffer).ConfigureAwait(false) > 0) { }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static void Stop(Process process, Stream output, Task stderr)
    {
        try
        {
            if (!process.HasExited)
                process.Kill();
            if (!process.WaitForExit(5000))
                Logger.Warn("The Apple network client did not stop in time.");
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception)
        {
            Logger.Warn("Could not stop the Apple network client.");
        }
        output.Dispose();
        try
        {
            if (!stderr.Wait(TimeSpan.FromSeconds(1)))
                Logger.Warn("The Apple network client's error pipe did not close in time.");
        }
        catch (AggregateException)
        {
            Logger.Warn("Could not drain the Apple network client's error pipe.");
        }
        process.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _lifetime.Cancel();
            _lifetime.Dispose();
        }
        base.Dispose(disposing);
    }

    private sealed record ParsedResponse(int Status, IReadOnlyList<(string Name, string Value)> Headers);

    private sealed class CurlResponseStream : Stream
    {
        private readonly Process _process;
        private readonly Stream _output;
        private readonly Task _stderr;
        private readonly CancellationTokenRegistration _lifetime;
        private int _stopped;
        private bool _finished;

        public CurlResponseStream(Process process, Stream output, Task stderr, CancellationToken lifetime)
        {
            _process = process;
            _output = output;
            _stderr = stderr;
            _lifetime = lifetime.Register(static state => ((CurlResponseStream)state!).Stop(), this);
        }

        public override bool CanRead => Volatile.Read(ref _stopped) == 0;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_finished)
                return 0;
            try
            {
                int read = await _output.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read != 0 || buffer.Length == 0)
                    return read;
                await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                await _stderr.ConfigureAwait(false);
                if (_process.ExitCode != 0)
                    throw new IOException($"The Apple network connection ended unexpectedly (error {_process.ExitCode}).");
                _finished = true;
                return 0;
            }
            catch (OperationCanceledException)
            {
                Stop();
                throw;
            }
            catch (IOException)
            {
                Stop();
                throw;
            }
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _lifetime.Dispose();
                Stop();
            }
            base.Dispose(disposing);
        }

        private void Stop()
        {
            if (Interlocked.Exchange(ref _stopped, 1) == 0)
                AppleCurlTransport.Stop(_process, _output, _stderr);
        }
    }
}

internal sealed record AppleCurlTransportOptions
{
    public Func<Uri, bool>? AllowAddress { get; init; }
    public string? CertificateAuthority { get; init; }
    public Action<ProcessStartInfo>? BeforeStart { get; init; }
    public TimeSpan HeaderTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public bool UseCookies { get; init; } = true;
    public bool FollowRedirects { get; init; } = true;
}
