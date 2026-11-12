using UniGetUI.Core.Logging;

namespace UniGetUI.Extensions.Xcode.Local;

/// <summary>
/// Finds Xcode bundles in the configured folder, or in Applications and Spotlight when no folder was configured,
/// and which one <c>xcode-select</c> points at. Links to an app are skipped.
/// </summary>
internal sealed class InstalledXcodeScanner
{
    public const string DefaultDirectory = "/Applications";
    private const int MaximumEntries = 4096;
    private readonly XcodeToolRunner _run;
    private readonly string _defaultDirectory;

    public InstalledXcodeScanner(XcodeToolRunner run, string? defaultDirectory = null)
    {
        _run = run;
        _defaultDirectory = defaultDirectory ?? DefaultDirectory;
    }

    public async Task<IReadOnlyList<XcodeBundleInfo>> ScanBundlesAsync(
        string? installDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool discover = string.IsNullOrEmpty(installDirectory);
        string directory = discover ? _defaultDirectory : installDirectory!;
        List<string> apps = [];
        try
        {
            apps.AddRange(Directory.EnumerateDirectories(directory, "*.app", new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                MatchCasing = MatchCasing.CaseInsensitive,
            }).Take(MaximumEntries));
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new XcodesException($"Could not read {directory}: {ex.Message}", ex);
        }

        if (discover && apps.Count < MaximumEntries)
        {
            try
            {
                XcodesResult spotlight = await _run("/usr/bin/mdfind",
                    ["-0", "kMDItemCFBundleIdentifier == 'com.apple.dt.Xcode'"], cancellationToken).ConfigureAwait(false);
                if (spotlight.Success)
                    apps.AddRange(spotlight.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries)
                        .Take(MaximumEntries - apps.Count));
                else
                    Logger.Warn("Xcode: Spotlight could not find additional installed Xcodes; showing the Applications folder.");
            }
            catch (XcodesException)
            {
                Logger.Warn("Xcode: Spotlight could not find additional installed Xcodes; showing the Applications folder.");
            }
        }

        List<XcodeBundleInfo> found = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string app in apps.Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsAppDirectory(app) || !seen.Add(Resolve(app)))
                continue;
            if (await XcodeBundle.ReadAsync(app, _run, cancellationToken).ConfigureAwait(false) is { } bundle)
                found.Add(bundle);
        }
        return found;
    }

    private static bool IsAppDirectory(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.Any(char.IsControl)
            || !path.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            return false;
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            return (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == FileAttributes.Directory;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The app <c>xcode-select</c> points at, with links resolved, or null when it points at none.</summary>
    public async Task<string?> GetSelectedAppAsync(CancellationToken cancellationToken)
    {
        XcodesResult result = await _run("/usr/bin/xcode-select", ["-p"], cancellationToken).ConfigureAwait(false);
        return result.Success ? AppOfDeveloperDirectory(result.StandardOutput.Trim()) : null;
    }

    internal static string? AppOfDeveloperDirectory(string developerDirectory)
    {
        const string suffix = "/Contents/Developer";
        string trimmed = developerDirectory.TrimEnd('/');
        if (!Path.IsPathFullyQualified(trimmed) || trimmed.Any(char.IsControl)
            || !trimmed.EndsWith(suffix, StringComparison.Ordinal))
            return null;
        return Resolve(trimmed[..^suffix.Length]);
    }

    /// <summary>The path with every link along it followed, so two names for one app compare equal.</summary>
    internal static string Resolve(string path)
    {
        string full = Path.GetFullPath(path).TrimEnd('/');
        string? parent = Path.GetDirectoryName(full);
        string resolvedParent = parent is null || parent == "/" ? parent ?? "" : Resolve(parent);
        string combined = Path.Join(resolvedParent.Length == 0 ? "/" : resolvedParent, Path.GetFileName(full));
        try
        {
            if (new DirectoryInfo(combined).ResolveLinkTarget(returnFinalTarget: true) is { } target)
                return Resolve(target.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return combined;
    }
}
