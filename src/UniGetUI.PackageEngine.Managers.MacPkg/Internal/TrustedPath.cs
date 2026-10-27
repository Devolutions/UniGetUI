namespace UniGetUI.PackageEngine.Managers.MacPkgManager.Internal;

/// <summary>
/// Whether a path is out of reach of standard users: it and every folder above it belong to root or an
/// administrator, and are writable by no one else (group write only for the wheel or admin group).
/// </summary>
internal static class TrustedPath
{
    private const int WheelGroup = 0;
    private const int AdminGroup = 80;
    private const int MaxLinks = 32;

    // ACL rights that let their holder change an item, or what a folder holds.
    private static readonly string[] ModifyingRights =
    [
        "write", "append", "writeattr", "writeextattr", "writesecurity", "chown", "delete",
        "add_file", "add_subdirectory", "delete_child",
    ];
    private static readonly string[] TrustedPrincipals = ["user:root", "group:wheel", "group:admin"];

    /// <summary>Replaces the check in tests, which run in folders owned by the user running them.</summary>
    internal static Func<string, string?>? TestOverride { get; set; }

    /// <summary>Why a standard user could change the path, or null when none could.</summary>
    public static string? WhyUntrusted(string path) => TestOverride is { } test ? test(path) : Check(path, 0);

    // Each component of the path as written is checked, then each link it passes through, hop by hop:
    // a link is only as safe as the folder that holds it, since whoever writes there can repoint it.
    private static string? Check(string path, int links)
    {
        if (!path.StartsWith('/'))
            return $"{path} is not an absolute path.";
        if (WhyWritable("/", out _) is { } rootReason)
            return rootReason;
        string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string current = "/";
        for (int i = 0; i < segments.Length; i++)
        {
            // current never holds a link, so ".." is its parent, as the kernel resolves it.
            if (segments[i] == ".")
                continue;
            if (segments[i] == "..")
            {
                current = Path.GetDirectoryName(current) ?? "/";
                continue;
            }
            string next = current.TrimEnd('/') + "/" + segments[i];
            if (WhyWritable(next, out string? target) is { } reason)
                return reason;
            if (target is not null)
            {
                if (++links > MaxLinks)
                    return $"{path} goes through too many links.";
                string resolved = target.StartsWith('/') ? target : current.TrimEnd('/') + "/" + target;
                return Check(string.Join('/', [resolved, .. segments[(i + 1)..]]), links);
            }
            current = next;
        }
        return null;
    }

    /// <summary>Why a standard user could change this one item; gives its target when it is a link.</summary>
    private static string? WhyWritable(string path, out string? linkTarget)
    {
        linkTarget = null;
        // Without -L, stat describes a link itself rather than what it points to.
        var stat = Tool.Run("/usr/bin/stat", ["-f", "%u %g %Lp %Sp %Y", path]);
        string[] fields = stat.Text.TrimEnd('\n').Split(' ', 5);
        if (stat.ExitCode != 0 || fields.Length < 4 || !int.TryParse(fields[0], out int owner)
            || !int.TryParse(fields[1], out int group))
            return $"{path} could not be inspected: {stat.StandardError.Trim()}";
        if (fields[3].StartsWith('l'))
        {
            linkTarget = fields.Length == 5 && fields[4].Length > 0 ? fields[4] : null;
            return linkTarget is null ? $"{path} is a link whose target could not be read." : null;
        }
        int mode = Convert.ToInt32(fields[2], 8);
        if ((mode & 0b000_000_010) != 0)
            return $"{path} can be changed by any user.";
        if ((mode & 0b000_010_000) != 0 && group is not (WheelGroup or AdminGroup))
            return $"{path} can be changed by every member of its group.";
        if (owner != 0 && !IsAdministrator(owner))
            return $"{path} belongs to a user who is not an administrator.";

        var listing = Tool.Run("/bin/ls", ["-led", path]);
        if (listing.ExitCode != 0)
            return $"{path} could not be inspected: {listing.StandardError.Trim()}";
        return WhyAccessListAllowsChanges(listing.Text) is { } entry
            ? $"{path} has an access control entry that lets others change it: {entry}"
            : null;
    }

    /// <summary>
    /// The first entry of an <c>ls -le</c> listing that allows someone other than root or an administrator
    /// to change the item, or null. Deny entries and read-only grants are harmless.
    /// </summary>
    internal static string? WhyAccessListAllowsChanges(string listing)
    {
        foreach (string line in listing.Split('\n').Skip(1))
        {
            // " 0: group:everyone [inherited] allow|deny right,right,..."
            string[] parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int verdict = Array.FindIndex(parts, p => p is "allow" or "deny");
            if (parts.Length < 4 || !parts[0].EndsWith(':') || verdict < 2 || verdict == parts.Length - 1)
                continue;
            if (parts[verdict] != "allow" || TrustedPrincipals.Contains(parts[1], StringComparer.Ordinal))
                continue;
            if (parts[verdict + 1].Split(',').Any(r => ModifyingRights.Contains(r, StringComparer.Ordinal)))
                return line.Trim();
        }
        return null;
    }

    // Asked each time, never cached: an account removed from the admin group loses its trust at once.
    private static bool IsAdministrator(int uid) =>
        Tool.Run("/usr/bin/dsmemberutil", ["checkmembership", "-u", uid.ToString(), "-g", AdminGroup.ToString()])
            .Text.Contains("is a member", StringComparison.Ordinal);
}
