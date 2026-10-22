namespace UniGetUI.Core.Tools;

public enum MacUpdateTargetStatus
{
    Ready,
    BundleNotFound,
    TranslocatedUnresolved,
    NotWritable,
}

public sealed record MacUpdateTarget(MacUpdateTargetStatus Status, string BundlePath, string? RunningBundlePath);

public static class MacUpdateTargetResolver
{
    public const string DefaultInstallPath = "/Applications/UniGetUI.app";
    public const string SwapFailedMarker = "=== Swap failed:";

    // Runs as root for a bundle the user cannot replace ($1=target, $2=new app, $3=pid).
    // The old bundle is deleted in the background once that process has exited.
    public const string ElevatedSwapScript = """
        set -e
        target="$1"; newapp="$2"; pid="$3"
        rm -rf "$target.old"
        chown -R "$(/usr/bin/stat -f '%u:%g' "$target")" "$newapp"
        /usr/bin/xattr -dr com.apple.quarantine "$newapp" 2>/dev/null || true
        mv "$target" "$target.old"
        if ! mv "$newapp" "$target"; then
          mv "$target.old" "$target"
          exit 1
        fi
        (i=0
         while kill -0 "$pid" 2>/dev/null && [ "$i" -lt 150 ]; do sleep 0.2; i=$((i+1)); done
         rm -rf "$target.old") >/dev/null 2>&1 &
        """;

    public static string? FindEnclosingAppBundle(string? executablePath)
    {
        if (string.IsNullOrEmpty(executablePath))
        {
            return null;
        }

        string dir = executablePath;
        while (true)
        {
            int slash = dir.LastIndexOf('/');
            if (slash <= 0)
            {
                return null;
            }

            dir = dir[..slash];
            if (dir.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            {
                return dir;
            }
        }
    }

    public static bool IsTranslocatedPath(string path) =>
        path.Contains("/AppTranslocation/", StringComparison.Ordinal);

    public static MacUpdateTarget Resolve(
        string? executablePath,
        Func<string, string?> resolveTranslocatedOriginal,
        Func<string, bool> directoryExists,
        Func<string, bool> isWritable)
    {
        string? running = FindEnclosingAppBundle(executablePath);
        string target = running ?? DefaultInstallPath;

        if (running is not null && IsTranslocatedPath(running))
        {
            string? original = resolveTranslocatedOriginal(running);
            if (original is null || IsTranslocatedPath(original) || !directoryExists(original))
            {
                original = directoryExists(DefaultInstallPath) ? DefaultInstallPath : null;
            }

            if (original is null)
            {
                return new MacUpdateTarget(MacUpdateTargetStatus.TranslocatedUnresolved, running, running);
            }

            target = original;
        }

        if (!directoryExists(target))
        {
            return new MacUpdateTarget(MacUpdateTargetStatus.BundleNotFound, target, running);
        }

        int slash = target.TrimEnd('/').LastIndexOf('/');
        string parent = slash <= 0 ? "/" : target[..slash];
        if (!isWritable(parent) || !isWritable(target))
        {
            return new MacUpdateTarget(MacUpdateTargetStatus.NotWritable, target, running);
        }

        return new MacUpdateTarget(MacUpdateTargetStatus.Ready, target, running);
    }

    public static bool TryGetSwapFailure(string updateLog, out string bundlePath)
    {
        foreach (string rawLine in updateLog.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.StartsWith(SwapFailedMarker, StringComparison.Ordinal))
            {
                bundlePath = line[SwapFailedMarker.Length..].Trim();
                return true;
            }
        }

        bundlePath = string.Empty;
        return false;
    }
}
