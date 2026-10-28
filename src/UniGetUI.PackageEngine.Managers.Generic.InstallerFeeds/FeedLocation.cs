using System.Security.Cryptography;

namespace UniGetUI.PackageEngine.Managers.InstallerFeeds;

public static class FeedLocation
{
    public const string IndexFileName = "index.json";
    private const int MaxScanDepth = 3;

    /// <summary>
    /// Turns the location an administrator wrote (an https URL, a file URL or an absolute path)
    /// into a feed address. Plain http is refused except on loopback, since the index pins hashes.
    /// </summary>
    public static bool TryParse(string? text, out Uri location)
    {
        location = null!;
        text = text?.Trim();
        if (string.IsNullOrEmpty(text))
            return false;
        if (Path.IsPathRooted(text) && !text.Contains("://", StringComparison.Ordinal))
        {
            if (OperatingSystem.IsWindows() && !Path.IsPathFullyQualified(text))
                return false;
            location = new Uri(Path.GetFullPath(text));
            return true;
        }
        if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? uri))
            return false;
        if (uri.IsFile || uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
        {
            location = uri;
            return true;
        }
        return false;
    }

    public static Uri IndexUrl(Uri location) =>
        location.AbsolutePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? location
            : new Uri(location.AbsoluteUri.TrimEnd('/') + "/" + IndexFileName);

    // Index paths are relative and may not climb out of the feed, so an index cannot point
    // installs at another host or scheme.
    public static Uri? ResolveWithin(Uri indexUrl, string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains('\\') || relative.StartsWith('/')
            || relative.Contains("://", StringComparison.Ordinal)
            || relative.Split('/').Any(segment => segment is ".." or "." or ""))
            return null;
        var resolved = new Uri(indexUrl, string.Join('/', relative.Split('/').Select(Uri.EscapeDataString)));
        string root = indexUrl.AbsoluteUri[..(indexUrl.AbsoluteUri.LastIndexOf('/') + 1)];
        return resolved.AbsoluteUri.StartsWith(root, StringComparison.Ordinal) ? resolved : null;
    }

    public static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    public static string ComputeSha256(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    public static IEnumerable<string> EnumerateFiles(string directory, IReadOnlyCollection<string> extensions)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MaxRecursionDepth = MaxScanDepth,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };
        return Directory.EnumerateFiles(directory, "*", options)
            .Where(f => HasExtension(f, extensions))
            .Order(StringComparer.Ordinal);
    }

    public static bool HasExtension(string path, IReadOnlyCollection<string> extensions) =>
        extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static string RelativeUrlPath(string folder, string file) =>
        Path.GetRelativePath(folder, file).Replace(Path.DirectorySeparatorChar, '/');
}
