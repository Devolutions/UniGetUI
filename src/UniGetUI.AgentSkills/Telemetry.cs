// Anonymous usage tracking and security audit lookups (port of telemetry.ts).
// Events are fired in the background and awaited (up to 5s) by Flush at normal
// CLI exit, like the TS pending-promise list.

using System.Text.Json.Nodes;

namespace Skills;

internal static class Telemetry
{
    private const string TelemetryUrl = "https://add-skill.vercel.sh/t";
    private const string AuditUrl = "https://add-skill.vercel.sh/audit";

    private static string? _version;
    private static string? _detectedAgent;
    private static readonly List<Task> Pending = new();

    public static void SetVersion(string v) => _version = v;

    public static void SetDetectedAgent(string? name) => _detectedAgent = name;

    private static bool IsCi() =>
        new[] { "CI", "GITHUB_ACTIONS", "GITLAB_CI", "CIRCLECI", "TRAVIS", "BUILDKITE", "JENKINS_URL", "TEAMCITY_VERSION" }.Any(Sys.EnvTruthy);

    /// The CLI reports by default; a public API call only when its host opted in.
    public static bool Enabled => (Sys.Context?.Telemetry ?? true) && !Sys.EnvTruthy("DISABLE_TELEMETRY") && !Sys.EnvTruthy("DO_NOT_TRACK");

    /// Security audit results; null on any error or timeout.
    public static JsonObject? FetchAuditData(string source, IReadOnlyList<string> skillSlugs, int timeoutMs = 3000)
    {
        if (!Enabled || skillSlugs.Count == 0) return null;
        var qs = UrlUtil.SearchParams(("source", source), ("skills", string.Join(",", skillSlugs)));
        var resp = HttpRequest.Get($"{AuditUrl}?{qs}").Timeout(TimeSpan.FromMilliseconds(timeoutMs)).TrySend();
        if (resp == null || !resp.Ok) return null;
        return resp.TryJson(out var n) ? n as JsonObject : null;
    }

    /// Fire-and-forget event. Null values are omitted (as `undefined` is in TS).
    public static void Track(params (string Key, string? Value)[] data)
    {
        if (!Enabled) return;
        var pairs = new List<(string Key, string Value)>();
        void Set(string k, string v)
        {
            var i = pairs.FindIndex(p => p.Key == k);
            if (i >= 0) pairs[i] = (k, v);
            else pairs.Add((k, v));
        }
        if (_version != null) Set("v", _version);
        if (IsCi()) Set("ci", "1");
        if (_detectedAgent != null) Set("agent", _detectedAgent);
        foreach (var (k, v) in data)
            if (v != null) Set(k, v);
        var url = $"{TelemetryUrl}?{UrlUtil.SearchParams(pairs.ToArray())}";
        var task = Task.Run(() => HttpRequest.Get(url).Timeout(TimeSpan.FromSeconds(10)).TrySend());
        lock (Pending)
        {
            // A long-running host never calls Flush: drop finished requests.
            Pending.RemoveAll(t => t.IsCompleted);
            Pending.Add(task);
        }
    }

    /// Wait (bounded) for in-flight telemetry requests.
    public static void Flush(TimeSpan timeout)
    {
        Task[] tasks;
        lock (Pending)
        {
            tasks = Pending.ToArray();
            Pending.Clear();
        }
        if (tasks.Length > 0) Task.WaitAll(tasks, timeout);
    }
}
