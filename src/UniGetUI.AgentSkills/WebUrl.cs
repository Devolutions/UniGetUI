// A WHATWG URL parser covering what the CLI uses from JavaScript's `URL`:
// protocol/username/password/host/hostname/port/pathname/search/hash,
// searchParams.get, relative resolution (`new URL(rel, base)`) and href.
// System.Uri canonicalizes differently (RFC 3986), which would change parsing
// results for sources, so the relevant WHATWG rules are implemented here.

using System.Globalization;
using System.Text;

namespace Skills;

internal sealed class WebUrl
{
    public string Scheme { get; private set; } = "";
    public string Username { get; private set; } = "";
    public string Password { get; private set; } = "";
    public string Hostname { get; private set; } = "";
    public string Port { get; private set; } = "";
    public string Pathname { get; private set; } = "";
    public string Search { get; private set; } = "";
    public string Hash { get; private set; } = "";
    private bool _hasAuthority;

    public string Protocol => Scheme + ":";
    public string Host => Port.Length > 0 ? $"{Hostname}:{Port}" : Hostname;

    public string Href
    {
        get
        {
            var sb = new StringBuilder(Protocol);
            if (_hasAuthority)
            {
                sb.Append("//");
                if (Username.Length > 0 || Password.Length > 0)
                {
                    sb.Append(Username);
                    if (Password.Length > 0) sb.Append(':').Append(Password);
                    sb.Append('@');
                }
                sb.Append(Host);
            }
            return sb.Append(Pathname).Append(Search).Append(Hash).ToString();
        }
    }

    public override string ToString() => Href;

    private static readonly Dictionary<string, string?> SpecialSchemes = new()
    {
        ["http"] = "80", ["https"] = "443", ["ws"] = "80", ["wss"] = "443", ["ftp"] = "21", ["file"] = null,
    };

    private bool IsSpecial => SpecialSchemes.ContainsKey(Scheme);

    /// `new URL(input)`; null when it would throw.
    public static WebUrl? Parse(string input) => Parse(input, null);

    /// `new URL(input, base)`; null when it would throw.
    public static WebUrl? Parse(string input, string? baseUrl)
    {
        input = Clean(input);
        var scheme = ReadScheme(input);
        if (scheme == null)
        {
            if (baseUrl == null) return null;
            var b = Parse(baseUrl);
            if (b == null) return null;
            return ParseAbsolute(ResolveRelative(b, input));
        }
        return ParseAbsolute(input);
    }

    private static string Clean(string s)
    {
        s = s.Trim(' ', '\0', '\x01', '\x02', '\x03', '\x04', '\x05', '\x06', '\x07', '\x08', '\t', '\n', '\x0b', '\x0c', '\r',
            '\x0e', '\x0f', '\x10', '\x11', '\x12', '\x13', '\x14', '\x15', '\x16', '\x17', '\x18', '\x19', '\x1a', '\x1b', '\x1c', '\x1d', '\x1e', '\x1f');
        return s.Replace("\t", "").Replace("\n", "").Replace("\r", "");
    }

    private static string? ReadScheme(string s)
    {
        if (s.Length == 0 || !char.IsAsciiLetter(s[0])) return null;
        for (var i = 1; i < s.Length; i++)
        {
            var c = s[i];
            if (c == ':') return s[..i].ToLowerInvariant();
            if (!(char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.')) return null;
        }
        return null;
    }

    private static string ResolveRelative(WebUrl b, string input)
    {
        bool IsSlash(char c) => c == '/' || (b.IsSpecial && c == '\\');
        var origin = b.Protocol + (b._hasAuthority ? "//" + (b.Username.Length > 0 || b.Password.Length > 0 ? b.Username + (b.Password.Length > 0 ? ":" + b.Password : "") + "@" : "") + b.Host : "");
        if (input.Length >= 2 && IsSlash(input[0]) && IsSlash(input[1])) return b.Protocol + input;
        if (input.Length >= 1 && IsSlash(input[0])) return origin + input;
        if (input.StartsWith('?')) return origin + b.Pathname + input;
        if (input.StartsWith('#')) return origin + b.Pathname + b.Search + input;
        if (input.Length == 0) return origin + b.Pathname + b.Search;
        var dir = b.Pathname;
        var slash = dir.LastIndexOf('/');
        dir = slash >= 0 ? dir[..(slash + 1)] : "/";
        return origin + dir + input;
    }

    private static WebUrl? ParseAbsolute(string input)
    {
        var scheme = ReadScheme(input);
        if (scheme == null) return null;
        var u = new WebUrl { Scheme = scheme };
        var rest = input[(scheme.Length + 1)..];

        // Split off fragment and query first (they never contain each other's markers).
        var hashIdx = rest.IndexOf('#');
        string? fragment = null;
        if (hashIdx >= 0)
        {
            fragment = rest[(hashIdx + 1)..];
            rest = rest[..hashIdx];
        }
        var qIdx = rest.IndexOf('?');
        string? query = null;
        if (qIdx >= 0)
        {
            query = rest[(qIdx + 1)..];
            rest = rest[..qIdx];
        }

        string path;
        if (u.IsSpecial)
        {
            var i = 0;
            while (i < rest.Length && rest[i] is '/' or '\\') i++;
            if (scheme == "file")
            {
                u._hasAuthority = true;
                var authEnd = rest.IndexOfAny(['/', '\\'], i);
                if (i >= 2)
                {
                    var authority = authEnd >= 0 ? rest[i..authEnd] : rest[i..];
                    u.Hostname = authority.ToLowerInvariant();
                    path = authEnd >= 0 ? rest[authEnd..] : "";
                }
                else
                {
                    path = rest;
                }
            }
            else
            {
                var end = rest.IndexOfAny(['/', '\\'], i);
                var authority = end >= 0 ? rest[i..end] : rest[i..];
                path = end >= 0 ? rest[end..] : "";
                if (!u.ParseAuthority(authority, special: true)) return null;
                if (u.Hostname.Length == 0) return null;
            }
            path = path.Replace('\\', '/');
            u.Pathname = NormalizePath(path, special: true);
        }
        else if (rest.StartsWith("//"))
        {
            var end = rest.IndexOf('/', 2);
            var authority = end >= 0 ? rest[2..end] : rest[2..];
            path = end >= 0 ? rest[end..] : "";
            if (!u.ParseAuthority(authority, special: false)) return null;
            u.Pathname = path.Length == 0 ? "" : NormalizePath(path, special: false);
        }
        else if (rest.StartsWith('/'))
        {
            u.Pathname = NormalizePath(rest, special: false);
        }
        else
        {
            // Opaque path (e.g. "github:owner/repo", "mailto:x")
            u.Pathname = PercentEncode(rest, c => c < 0x20 || c > 0x7e);
        }

        if (query != null) u.Search = query.Length == 0 ? "" : "?" + PercentEncode(query, c => c < 0x21 || c > 0x7e || c is '"' or '#' or '<' or '>' || (u.IsSpecial && c == '\''));
        if (fragment != null) u.Hash = fragment.Length == 0 ? "" : "#" + PercentEncode(fragment, c => c < 0x21 || c > 0x7e || c is '"' or '<' or '>' or '`');
        return u;
    }

    private bool ParseAuthority(string authority, bool special)
    {
        _hasAuthority = true;
        var at = authority.LastIndexOf('@');
        if (at >= 0)
        {
            var userinfo = authority[..at];
            authority = authority[(at + 1)..];
            var colon = userinfo.IndexOf(':');
            static bool UserinfoSet(int c) => c < 0x21 || c > 0x7e || c is '"' or '#' or '<' or '>' or '?' or '`' or '{' or '}' or '/' or ':' or ';' or '=' or '@' or '[' or '\\' or ']' or '^' or '|';
            Username = PercentEncode(colon >= 0 ? userinfo[..colon] : userinfo, UserinfoSet);
            Password = colon >= 0 ? PercentEncode(userinfo[(colon + 1)..], UserinfoSet) : "";
        }
        string host;
        string port = "";
        if (authority.StartsWith('['))
        {
            var close = authority.IndexOf(']');
            if (close < 0) return false;
            host = authority[..(close + 1)].ToLowerInvariant();
            var after = authority[(close + 1)..];
            if (after.Length > 0)
            {
                if (!after.StartsWith(':')) return false;
                port = after[1..];
            }
        }
        else
        {
            var colon = authority.LastIndexOf(':');
            if (colon >= 0)
            {
                port = authority[(colon + 1)..];
                host = authority[..colon];
            }
            else
            {
                host = authority;
            }
            if (special)
            {
                host = PercentDecode(host);
                if (host.Any(c => c is ' ' or '#' or '%' or '/' or ':' or '<' or '>' or '?' or '@' or '[' or '\\' or ']' or '^' or '|' || c < 0x20 || c == 0x7f)) return false;
                host = host.ToLowerInvariant();
            }
            else
            {
                if (host.Any(c => c is ' ' or '#' or '/' or ':' or '<' or '>' or '?' or '@' or '[' or '\\' or ']' or '^' or '|')) return false;
                host = PercentEncode(host, c => c < 0x20 || c > 0x7e);
            }
        }
        if (port.Length > 0)
        {
            if (!port.All(char.IsAsciiDigit)) return false;
            if (!int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n > 65535) return false;
            port = n.ToString(CultureInfo.InvariantCulture);
            if (SpecialSchemes.TryGetValue(Scheme, out var def) && def == port) port = "";
        }
        Hostname = host;
        Port = port;
        return true;
    }

    private static string NormalizePath(string path, bool special)
    {
        if (path.Length == 0) return special ? "/" : "";
        var segments = path.Split('/').Skip(1).ToList();
        var output = new List<string>();
        for (var i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];
            var lower = seg.ToLowerInvariant();
            var isLast = i == segments.Count - 1;
            if (lower is ".." or ".%2e" or "%2e." or "%2e%2e")
            {
                if (output.Count > 0) output.RemoveAt(output.Count - 1);
                if (isLast) output.Add("");
                continue;
            }
            if (lower is "." or "%2e")
            {
                if (isLast) output.Add("");
                continue;
            }
            output.Add(PercentEncode(seg, c => c < 0x21 || c > 0x7e || c is '"' or '#' or '<' or '>' or '?' or '`' or '{' or '}'));
        }
        return "/" + string.Join("/", output);
    }

    private static string PercentEncode(string s, Func<int, bool> shouldEncode)
    {
        if (!s.Any(c => shouldEncode(c))) return s;
        var sb = new StringBuilder();
        Span<byte> buf = stackalloc byte[4];
        foreach (var rune in s.EnumerateRunes())
        {
            if (rune.Value < 0x80 && !shouldEncode(rune.Value))
            {
                sb.Append((char)rune.Value);
                continue;
            }
            if (rune.Value < 0x80 || shouldEncode(rune.Value))
            {
                var n = rune.EncodeToUtf8(buf);
                for (var i = 0; i < n; i++) sb.Append('%').Append(buf[i].ToString("X2", CultureInfo.InvariantCulture));
            }
            else
            {
                sb.Append(rune.ToString());
            }
        }
        return sb.ToString();
    }

    /// Lenient percent-decoding (invalid escapes are kept literally).
    private static string PercentDecode(string s) => UrlUtil.PercentDecodeBytes(s, strict: false) is { } b ? Encoding.UTF8.GetString(b) : s;

    /// `url.searchParams.get(name)`
    public string? SearchParam(string name)
    {
        var q = Search.StartsWith('?') ? Search[1..] : Search;
        foreach (var pair in q.Split('&'))
        {
            if (pair.Length == 0) continue;
            var eq = pair.IndexOf('=');
            var k = FormDecode(eq >= 0 ? pair[..eq] : pair);
            if (k == name) return FormDecode(eq >= 0 ? pair[(eq + 1)..] : "");
        }
        return null;
    }

    private static string FormDecode(string s) => PercentDecode(s.Replace('+', ' '));
}

/// JavaScript URI helpers.
internal static class UrlUtil
{
    /// `encodeURIComponent`
    public static string EncodeUriComponent(string s)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(s))
        {
            var c = (char)b;
            if (char.IsAsciiLetterOrDigit(c) || "-_.!~*'()".Contains(c)) sb.Append(c);
            else sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    /// Percent-decode to bytes. Strict mode (decodeURIComponent) returns null on
    /// a malformed escape; lenient mode keeps it literally.
    internal static byte[]? PercentDecodeBytes(string s, bool strict)
    {
        var bytes = new List<byte>();
        var textStart = 0;
        void FlushText(int end)
        {
            if (end > textStart) bytes.AddRange(Encoding.UTF8.GetBytes(s[textStart..end]));
        }
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] != '%') continue;
            var valid = i + 2 < s.Length && Uri.IsHexDigit(s[i + 1]) && Uri.IsHexDigit(s[i + 2]);
            if (!valid)
            {
                if (strict) return null;
                continue;
            }
            FlushText(i);
            bytes.Add(Convert.ToByte(s.Substring(i + 1, 2), 16));
            i += 2;
            textStart = i + 1;
        }
        FlushText(s.Length);
        return bytes.ToArray();
    }

    /// `decodeURIComponent`; null for malformed sequences (JS URIError).
    public static string? DecodeUriComponent(string s)
    {
        var bytes = PercentDecodeBytes(s, strict: true);
        if (bytes == null) return null;
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    /// `new URLSearchParams(pairs).toString()`
    public static string SearchParams(params (string Key, string Value)[] pairs)
    {
        static string Enc(string s)
        {
            var sb = new StringBuilder();
            foreach (var b in Encoding.UTF8.GetBytes(s))
            {
                var c = (char)b;
                if (char.IsAsciiLetterOrDigit(c) || c is '*' or '-' or '.' or '_') sb.Append(c);
                else if (c == ' ') sb.Append('+');
                else sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }
        return string.Join("&", pairs.Select(p => Enc(p.Key) + "=" + Enc(p.Value)));
    }
}
