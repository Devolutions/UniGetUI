using System.Runtime.InteropServices;

namespace UniGetUI.Extensions.Xcode;

/// <summary>Finds the xcodes command line tool, which Homebrew installs as <c>xcodes</c>.</summary>
public static class XcodesLocator
{
    public const string ExecutableName = "xcodes";

    /// <summary>
    /// Where Homebrew (either prefix) and Mint put it, for when the app was not started from a shell that
    /// has them on the PATH.
    /// </summary>
    public static IReadOnlyList<string> FallbackFolders { get; } =
    [
        "/opt/homebrew/bin",
        "/usr/local/bin",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mint", "bin"),
    ];

    public static string? Find() => Find(Environment.GetEnvironmentVariable("PATH"), FallbackFolders);

    public static string? Find(string? path, IEnumerable<string> fallbackFolders)
    {
        IEnumerable<string> folders = (path ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Concat(fallbackFolders);
        foreach (string folder in folders)
        {
            string candidate;
            try { candidate = Path.Combine(folder, ExecutableName); }
            catch (ArgumentException) { continue; } // A PATH entry with characters a path cannot have.
            if (IsExecutable(candidate))
                return candidate;
        }
        return null;
    }

    private static bool IsExecutable(string path)
    {
        try
        {
            if (!File.Exists(path))
                return false;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return true;
            const UnixFileMode execute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            return (File.GetUnixFileMode(path) & execute) != 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
