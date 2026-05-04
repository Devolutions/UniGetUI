namespace UniGetUI.Interface;

internal static class IpcHttpRoutes
{
    public const string Prefix = "/uniget";
    public const string Version = "/v1";
    public const string ApiRoot = Prefix + Version;

    public static string Path(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        return relativePath.StartsWith("/", StringComparison.Ordinal)
            ? ApiRoot + relativePath
            : throw new ArgumentException(
                "IPC route fragments must start with '/'.",
                nameof(relativePath)
            );
    }

    public static bool Matches(string path, string relativePath)
    {
        return path.Equals(Path(relativePath), StringComparison.OrdinalIgnoreCase);
    }

    public static bool StartsWith(string path, string relativePathPrefix)
    {
        return path.StartsWith(Path(relativePathPrefix), StringComparison.OrdinalIgnoreCase);
    }
}
