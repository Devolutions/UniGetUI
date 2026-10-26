using System.Collections.Concurrent;

namespace UniGetUI.PackageEngine.Managers.MacPkgManager.Internal;

/// <summary>
/// Whether a path is out of reach of standard users: it and every folder above it belong to root or an
/// administrator, and are writable by no one else (group write only for the wheel or admin group).
/// </summary>
internal static class TrustedPath
{
    private const int WheelGroup = 0;
    private const int AdminGroup = 80;

    /// <summary>Replaces the check in tests, which run in folders owned by the user running them.</summary>
    internal static Func<string, string?>? TestOverride { get; set; }

    private static readonly ConcurrentDictionary<int, bool> Administrators = new();

    /// <summary>Why a standard user could change the path, or null when none could.</summary>
    public static string? WhyUntrusted(string path)
    {
        if (TestOverride is { } test)
            return test(path);
        var resolved = Tool.Run("/bin/realpath", [path]);
        if (resolved.ExitCode != 0)
            return $"{path} could not be resolved: {resolved.StandardError.Trim()}";
        for (string? current = resolved.Text.Trim(); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            var stat = Tool.Run("/usr/bin/stat", ["-f", "%u %g %Lp", current]);
            string[] fields = stat.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (stat.ExitCode != 0 || fields.Length != 3 || !int.TryParse(fields[0], out int owner)
                || !int.TryParse(fields[1], out int group))
                return $"{current} could not be inspected: {stat.StandardError.Trim()}";
            int mode = Convert.ToInt32(fields[2], 8);
            if ((mode & 0b000_000_010) != 0)
                return $"{current} can be changed by any user.";
            if ((mode & 0b000_010_000) != 0 && group is not (WheelGroup or AdminGroup))
                return $"{current} can be changed by every member of its group.";
            if (owner != 0 && !IsAdministrator(owner))
                return $"{current} belongs to a user who is not an administrator.";
        }
        return null;
    }

    private static bool IsAdministrator(int uid) => Administrators.GetOrAdd(uid, id =>
        Tool.Run("/usr/bin/dsmemberutil", ["checkmembership", "-u", id.ToString(), "-g", AdminGroup.ToString()])
            .Text.Contains("is a member", StringComparison.Ordinal));
}
