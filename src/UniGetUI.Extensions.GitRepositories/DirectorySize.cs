using System.IO.Enumeration;

namespace UniGetUI.Extensions.GitRepositories;

/// <summary>Adds up the size of the files under a folder, as a clone or worktree takes on disk.</summary>
public static class DirectorySize
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// The total length of the files under <paramref name="path"/>, or null when the folder does not exist.
    /// Symbolic links and junctions are not followed or counted. This reads every directory entry,
    /// so it can take a while on a large tree: call it off the UI thread.
    /// </summary>
    /// <param name="excluded">Folders under <paramref name="path"/> left out, such as worktrees nested in a clone.</param>
    public static long? Measure(string path, IEnumerable<string>? excluded = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(path))
            return null;

        string root = GitRepositoryService.NormalizePath(path);
        string prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        HashSet<string> skipped = (excluded ?? [])
            .Select(GitRepositoryService.NormalizePath)
            .Where(folder => folder.StartsWith(prefix, PathComparison))
            .ToHashSet(PathComparer);

        var entries = new FileSystemEnumerable<long>(root,
            (ref FileSystemEntry entry) => entry.Length,
            new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = false,
                AttributesToSkip = 0,
                ReturnSpecialDirectories = false,
            })
        {
            ShouldIncludePredicate = (ref FileSystemEntry entry) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return !entry.IsDirectory && (entry.Attributes & FileAttributes.ReparsePoint) == 0;
            },
            ShouldRecursePredicate = (ref FileSystemEntry entry) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    return false;
                if (skipped.Count == 0)
                    return true;
                return !skipped.Contains(entry.ToFullPath());
            },
        };

        long total = 0;
        foreach (long length in entries)
            total = checked(total + length);
        return total;
    }
}
