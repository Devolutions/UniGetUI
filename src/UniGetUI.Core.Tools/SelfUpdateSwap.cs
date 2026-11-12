namespace UniGetUI.Core.Tools;

public static class SelfUpdateSwap
{
    public const string FailedMarker = "=== Swap failed:";

    public static string ParentDirectory(string path)
    {
        int slash = path.TrimEnd('/').LastIndexOf('/');
        return slash <= 0 ? "/" : path[..slash];
    }

    public static bool CanReplace(string target, Func<string, bool> isWritable) =>
        isWritable(ParentDirectory(target)) && isWritable(target);

    public static bool TryGetFailure(string updateLog, out string target)
    {
        foreach (string rawLine in updateLog.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.StartsWith(FailedMarker, StringComparison.Ordinal))
            {
                target = line[FailedMarker.Length..].Trim();
                return true;
            }
        }

        target = string.Empty;
        return false;
    }
}
