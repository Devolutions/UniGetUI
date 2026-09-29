// Node.js `path` module semantics (posix and win32) operating on strings.
//
// Normalize path segments before string prefix checks for path-traversal
// protection. System.IO.Path does not normalize `..` the same way here.

namespace Skills;

internal static class NodePath
{
    public static readonly bool Win = OperatingSystem.IsWindows();
    public static readonly string Sep = Win ? "\\" : "/";

    private static bool IsSep(char c) => Win ? c == '/' || c == '\\' : c == '/';

    private static string[] SplitSeps(string s) => Win ? s.Split('/', '\\') : s.Split('/');

    /// Resolve `.` and `..` segments (Node's internal normalizeString).
    private static string NormalizeString(string path, bool allowAboveRoot, string separator)
    {
        var output = new List<string>();
        foreach (var segment in SplitSeps(path))
        {
            if (segment.Length == 0 || segment == ".") continue;
            if (segment == "..")
            {
                if (output.Count > 0 && output[^1] != "..")
                {
                    output.RemoveAt(output.Count - 1);
                    continue;
                }
                if (allowAboveRoot) output.Add("..");
                continue;
            }
            output.Add(segment);
        }
        return string.Join(separator, output);
    }

    /// Split a win32 path into (device, isAbsolute, rest).
    private static (string? Device, bool Abs, string Tail) Win32Root(string path)
    {
        if (path.Length == 0) return (null, false, path);
        if (IsSep(path[0]))
        {
            if (path.Length > 1 && IsSep(path[1]))
            {
                // Possible UNC root: \\server\share
                var rest = path[2..];
                var parts = rest.Split(['/', '\\'], 3);
                if (parts.Length >= 2 && parts[0].Length > 0 && parts[1].Length > 0)
                {
                    var device = $"\\\\{parts[0]}\\{parts[1]}";
                    var consumed = Math.Min(path.Length, 2 + parts[0].Length + 1 + parts[1].Length);
                    return (device, true, path[consumed..]);
                }
            }
            return (null, true, path[1..]);
        }
        if (path.Length >= 2 && path[1] == ':' && char.IsAsciiLetter(path[0]))
        {
            var device = path[..2];
            if (path.Length >= 3 && IsSep(path[2])) return (device, true, path[3..]);
            return (device, false, path[2..]);
        }
        return (null, false, path);
    }

    public static bool IsAbsolute(string path)
    {
        if (Win)
        {
            var (device, abs, _) = Win32Root(path);
            return abs && (device != null || (path.Length > 0 && IsSep(path[0])));
        }
        return path.StartsWith('/');
    }

    public static string Normalize(string path)
    {
        if (path.Length == 0) return ".";
        if (Win)
        {
            var (device, abs, rest) = Win32Root(path);
            var tail = NormalizeString(rest, !abs, "\\");
            if (tail.Length == 0 && !abs) tail = ".";
            if (tail.Length > 0 && IsSep(path[^1]) && !(device != null && rest.Length == 0)) tail += "\\";
            if (device == null) return abs ? "\\" + tail : tail;
            return abs ? device + "\\" + tail : device + tail;
        }
        var isAbs = path.StartsWith('/');
        var trailing = path.EndsWith('/');
        var p = NormalizeString(path, !isAbs, "/");
        if (p.Length == 0)
        {
            if (isAbs) return "/";
            return trailing ? "./" : ".";
        }
        if (trailing) p += "/";
        return isAbs ? "/" + p : p;
    }

    /// `path.join(...parts)`
    public static string Join(params string[] parts)
    {
        var nonEmpty = parts.Where(p => !string.IsNullOrEmpty(p)).ToArray();
        if (nonEmpty.Length == 0) return ".";
        return Normalize(string.Join(Win ? "\\" : "/", nonEmpty));
    }

    /// `path.resolve(...parts)` relative to the process working directory.
    public static string Resolve(params string[] parts)
    {
        var cwd = Sys.Cwd();
        if (Win)
        {
            string? resolvedDevice = null;
            var resolvedTail = "";
            var resolvedAbs = false;
            var candidates = new List<string> { cwd };
            candidates.AddRange(parts);
            for (var i = candidates.Count - 1; i >= 0; i--)
            {
                var path = candidates[i];
                if (string.IsNullOrEmpty(path)) continue;
                var (device, abs, rest) = Win32Root(path);
                if (device != null && resolvedDevice != null && !device.Equals(resolvedDevice, StringComparison.OrdinalIgnoreCase)) continue;
                if (resolvedDevice == null && device != null) resolvedDevice = device;
                if (!resolvedAbs)
                {
                    resolvedTail = resolvedTail.Length == 0 ? rest : rest + "\\" + resolvedTail;
                    resolvedAbs = abs;
                }
                if (resolvedAbs && resolvedDevice != null) break;
            }
            resolvedDevice ??= Win32Root(cwd).Device;
            var tail = NormalizeString(resolvedTail, !resolvedAbs, "\\");
            var dev = resolvedDevice ?? "";
            if (resolvedAbs) return dev + "\\" + tail;
            var s = dev + tail;
            return s.Length == 0 ? "." : s;
        }
        var resolved = "";
        var isAbs = false;
        for (var i = parts.Length - 1; i >= 0; i--)
        {
            var p = parts[i];
            if (string.IsNullOrEmpty(p)) continue;
            resolved = resolved.Length == 0 ? p : p + "/" + resolved;
            if (p.StartsWith('/'))
            {
                isAbs = true;
                break;
            }
        }
        if (!isAbs) resolved = resolved.Length == 0 ? cwd : cwd + "/" + resolved;
        return "/" + NormalizeString(resolved, false, "/");
    }

    /// `path.relative(from, to)`
    public static string Relative(string from, string to)
    {
        var fromR = Resolve(from);
        var toR = Resolve(to);
        var cmpFrom = Win ? fromR.ToLowerInvariant() : fromR;
        var cmpTo = Win ? toR.ToLowerInvariant() : toR;
        if (cmpFrom == cmpTo) return "";
        if (Win)
        {
            var fd = Win32Root(fromR).Device;
            var td = Win32Root(toR).Device;
            var same = (fd, td) switch
            {
                (null, null) => true,
                ({ } a, { } b) => a.Equals(b, StringComparison.OrdinalIgnoreCase),
                _ => false,
            };
            if (!same) return toR;
        }
        static string[] Split(string s) => SplitSeps(s).Where(x => x.Length > 0).ToArray();
        var fromParts = Split(fromR);
        var toParts = Split(toR);
        var fromCmp = Split(cmpFrom);
        var toCmp = Split(cmpTo);
        var common = 0;
        while (common < fromCmp.Length && common < toCmp.Length && fromCmp[common] == toCmp[common]) common++;
        var output = new List<string>();
        for (var i = common; i < fromParts.Length; i++) output.Add("..");
        for (var i = common; i < toParts.Length; i++) output.Add(toParts[i]);
        return string.Join(Sep, output);
    }

    /// `path.dirname(p)`
    public static string Dirname(string path)
    {
        if (path.Length == 0) return ".";
        if (Win)
        {
            var (_, _, rest) = Win32Root(path);
            var root = path[..(path.Length - rest.Length)];
            var trimmed = rest;
            while (trimmed.Length > 0 && IsSep(trimmed[^1])) trimmed = trimmed[..^1];
            var idx = LastSep(trimmed);
            if (idx >= 0) return root + trimmed[..idx];
            return root.Length == 0 ? "." : root;
        }
        var t = path;
        while (t.Length > 1 && t.EndsWith('/')) t = t[..^1];
        if (t == "/") return "/";
        var i = t.LastIndexOf('/');
        if (i == 0) return "/";
        if (i < 0) return ".";
        var d = t[..i];
        while (d.Length > 1 && d.EndsWith('/')) d = d[..^1];
        return d;
    }

    private static int LastSep(string s)
    {
        for (var i = s.Length - 1; i >= 0; i--)
            if (IsSep(s[i])) return i;
        return -1;
    }

    /// `path.basename(p)`
    public static string Basename(string path)
    {
        var rest = Win ? Win32Root(path).Tail : path;
        while (rest.Length > 0 && IsSep(rest[^1])) rest = rest[..^1];
        var idx = LastSep(rest);
        return idx >= 0 ? rest[(idx + 1)..] : rest;
    }

    /// `path.extname(p)`
    public static string Extname(string path)
    {
        var b = Basename(path);
        var idx = b.LastIndexOf('.');
        return idx <= 0 ? "" : b[idx..];
    }

    /// Platform separators → forward slashes.
    public static string ToPosix(string path) => Win ? path.Replace('\\', '/') : path;

    /// normalize(resolve(target)) starts with normalize(resolve(base)) + sep, or
    /// equals it. Used when validating skill paths.
    public static bool IsPathSafe(string basePath, string target)
    {
        var nb = Normalize(Resolve(basePath));
        var nt = Normalize(Resolve(target));
        return nt.StartsWith(nb + Sep, StringComparison.Ordinal) || nt == nb;
    }
}
