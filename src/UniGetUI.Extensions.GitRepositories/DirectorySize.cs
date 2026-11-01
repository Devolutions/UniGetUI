using System.IO.Enumeration;

namespace UniGetUI.Extensions.GitRepositories;

/// <summary>Adds up the size of the files under a folder, as a clone or worktree takes on disk.</summary>
public static class DirectorySize
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>
    /// The total length of every file under <paramref name="path"/>, or null when the folder does not exist.
    /// Links and junctions are not followed, so nothing outside the folder or counted twice gets in, and
    /// folders that cannot be read are skipped. This reads every directory entry, so it can take a while
    /// on a large tree: call it off the UI thread.
    /// </summary>
    /// <param name="excluded">Folders under <paramref name="path"/> left out, such as worktrees nested in a clone.</param>
    public static long? Measure(string path, IEnumerable<string>? excluded = null,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(path))
            return null;

        string root = GitRepositoryService.NormalizePath(path);
        string[] skipped = [.. (excluded ?? [])
            .Select(GitRepositoryService.NormalizePath)
            .Where(folder => !string.Equals(folder, root, PathComparison))];

        var entries = new FileSystemEnumerable<long>(root,
            (ref FileSystemEntry entry) => entry.Length,
            new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = 0,
                ReturnSpecialDirectories = false,
            })
        {
            ShouldIncludePredicate = (ref FileSystemEntry entry) => !entry.IsDirectory,
            ShouldRecursePredicate = (ref FileSystemEntry entry) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    return false;
                if (skipped.Length == 0)
                    return true;
                string folder = GitRepositoryService.NormalizePath(entry.ToFullPath());
                return !skipped.Any(excludedFolder => string.Equals(folder, excludedFolder, PathComparison));
            },
        };

        long total = 0;
        try
        {
            foreach (long length in entries)
                total += length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The tree changed while it was read, such as a build deleting its output: keep what was counted.
        }
        return total;
    }
}
