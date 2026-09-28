// Process-level helpers: environment and home/temp directories that mirror
// Node's `process` / `os` behavior, plus the per-call context the public API
// uses to run the core without touching process-wide state.
//
// The CLI never enters a context, so every member falls back to the real
// process environment, working directory and home directory.

namespace Skills;

/// Ambient settings for one public API call (see <see cref="Sys.Enter"/>).
internal sealed class SysContext
{
    /// Project directory used wherever the CLI would use the working directory.
    public string? Cwd { get; init; }

    /// Home directory override (tests and sandboxes).
    public string? Home { get; init; }

    /// Environment overrides; a null value hides a variable.
    public IReadOnlyDictionary<string, string?>? Env { get; init; }

    /// Receives non-fatal warnings (skipped SKILL.md files, broken symlinks).
    public Action<string>? Warn { get; init; }

    /// Whether anonymous telemetry may be sent from this call.
    public bool Telemetry { get; init; }

    public CancellationToken Cancel { get; init; }

    /// The Notion CLI to run for Notion sources; `ntn` on PATH when null.
    public string? NotionCli { get; init; }

    /// Tests: answers Notion CLI calls instead of starting `ntn`.
    public Func<IReadOnlyList<string>, ProcOutput>? NotionRunner { get; init; }

    /// Tests: fetches a Notion skill archive instead of downloading it.
    public Func<string, DownloadedSource>? NotionDownloader { get; init; }

    /// Per-call cache of the agent table (paths depend on home and environment).
    internal object? AgentsCache;

    /// Per-call record of directories populated by the installer.
    internal object? PopulatedCache;
}

internal static class LibraryInfo
{
    /// The package version (informational version without the commit suffix).
    public static readonly string Version = GetVersion();

    private static string GetVersion()
    {
        var v = typeof(LibraryInfo).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "0.0.0";
        var plus = v.IndexOf('+');
        return plus >= 0 ? v[..plus] : v;
    }
}

internal static class Sys
{
    private static readonly AsyncLocal<SysContext?> Current = new();

    /// The context of the running public API call, or null in the CLI.
    public static SysContext? Context => Current.Value;

    /// Run the rest of the current (synchronous) flow under `context`.
    public static IDisposable Enter(SysContext context)
    {
        var previous = Current.Value;
        Current.Value = context;
        return new Restore(previous);
    }

    private sealed class Restore(SysContext? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }

    /// Run the rest of the current flow with one more environment override
    /// (the CLI passes such variables to a child `skills add`).
    public static IDisposable EnterWithEnv(string name, string? value)
    {
        var c = Current.Value;
        var env = new Dictionary<string, string?>(c?.Env ?? new Dictionary<string, string?>()) { [name] = value };
        return Enter(new SysContext
        {
            Cwd = c?.Cwd,
            Home = c?.Home,
            Env = env,
            Warn = c?.Warn,
            Telemetry = c?.Telemetry ?? false,
            Cancel = c?.Cancel ?? CancellationToken.None,
        });
    }

    public static void ThrowIfCancelled() => Current.Value?.Cancel.ThrowIfCancellationRequested();

    /// Process-wide warning sink used outside a context (the CLI writes to stderr).
    public static Action<string>? WarningSink { get; set; }

    public static void Warn(string message) => (Current.Value is { } c ? c.Warn : WarningSink)?.Invoke(message);

    /// User-Agent for HTTP requests (the CLI sets `skills-cli/&lt;version&gt;`).
    public static string UserAgent { get; set; } = $"Devolutions.AgentSkills/{LibraryInfo.Version}";

    // ─── Environment ───

    /// `process.env.NAME`, even if empty.
    public static string? EnvRaw(string name)
    {
        if (Current.Value?.Env is { } env && env.TryGetValue(name, out var v)) return v;
        return Environment.GetEnvironmentVariable(name);
    }

    /// `process.env.NAME` when set and non-empty (JS truthiness).
    public static string? Env(string name)
    {
        var v = EnvRaw(name);
        return string.IsNullOrEmpty(v) ? null : v;
    }

    public static bool EnvTruthy(string name) => Env(name) != null;

    /// `process.env.NAME?.trim() || fallback`
    public static string? EnvTrimmed(string name)
    {
        var v = EnvRaw(name)?.Trim();
        return string.IsNullOrEmpty(v) ? null : v;
    }

    public static string Cwd() => Current.Value?.Cwd ?? Directory.GetCurrentDirectory();

    /// `os.homedir()` (libuv `uv_os_homedir`).
    public static string HomeDir()
    {
        if (Current.Value?.Home is { } home) return home;
        if (OperatingSystem.IsWindows())
        {
            var p = Env("USERPROFILE");
            if (p != null) return p;
            var d = Env("HOMEDRIVE");
            var hp = Env("HOMEPATH");
            if (d != null && hp != null) return d + hp;
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
        return Env("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    /// `os.tmpdir()`
    public static string TmpDir()
    {
        if (OperatingSystem.IsWindows())
        {
            var p = Env("TEMP") ?? Env("TMP") ?? ((Env("SystemRoot") ?? Env("windir") ?? "C:\\Windows") + "\\temp");
            if (p.Length > 1 && p.EndsWith('\\') && !p.EndsWith(":\\")) p = p[..^1];
            return p;
        }
        var t = Env("TMPDIR") ?? Env("TMP") ?? Env("TEMP") ?? "/tmp";
        if (t.Length > 1 && t.EndsWith('/')) t = t[..^1];
        return t;
    }

    private const string TempChars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    /// `fs.mkdtemp(join(tmpdir(), prefix))`
    public static string MkdTemp(string prefix)
    {
        var baseDir = TmpDir();
        Directory.CreateDirectory(baseDir);
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var suffix = new char[6];
            for (var i = 0; i < 6; i++) suffix[i] = TempChars[Random.Shared.Next(TempChars.Length)];
            var dir = NodePath.Join(baseDir, prefix + new string(suffix));
            if (Directory.Exists(dir) || File.Exists(dir)) continue;
            Directory.CreateDirectory(dir);
            return dir;
        }
        throw new IOException("failed to create temp dir");
    }

    /// `new Date().toISOString()`
    public static string NowIso() => DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
}
