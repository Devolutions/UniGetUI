using UniGetUI.PackageEngine.Managers.MacPkgManager.Internal;

namespace UniGetUI.PackageEngine.Managers.MacPkgManager;

/// <summary>A package a feed publishes, as the Catalog Editor previews it.</summary>
public sealed record FeedPackage(string Name, string Id, string Version, string File, string Kind,
    IReadOnlyList<string> Architectures);

/// <summary>A file in a feed that UniGetUI cannot offer, and why.</summary>
public sealed record FeedProblem(string File, string Reason);

/// <summary>What a feed offers: its packages, the files it skips, and a warning about where it lives.</summary>
public sealed record FeedInspection(IReadOnlyList<FeedPackage> Packages, IReadOnlyList<FeedProblem> Problems,
    string? Warning);

/// <summary>
/// Previews a feed for the administrator writing a catalog: everything it publishes, every file it
/// cannot read with the reason, and whether the root helper would accept a folder feed.
/// </summary>
public static class PkgFeedInspector
{
    /// <summary>Whether a feed location is usable: an https URL, a file URL or an absolute folder path.</summary>
    public static bool IsValidLocation(string? text) => PkgFeed.TryParseLocation(text, out _);

    /// <summary>Reads the feed fresh. Throws when the feed itself cannot be reached or its index read.</summary>
    public static FeedInspection Inspect(string text)
    {
        if (!PkgFeed.TryParseLocation(text, out Uri location))
            throw new InvalidDataException($"\"{text}\" is not an https URL or an absolute folder path.");
        PkgFeed.ForgetCachedIndexes();
        if (!location.IsFile)
            return new FeedInspection(PkgFeed.List(location).Select(Describe).ToArray(), [], null);

        string folder = location.LocalPath;
        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException($"The folder {folder} does not exist or is not mounted.");
        var packages = new List<FeedPackage>();
        var problems = new List<FeedProblem>();
        foreach (string file in PkgFeed.EnumeratePackages(folder))
        {
            try
            {
                var metadata = PkgFeed.ReadFile(file);
                packages.Add(new FeedPackage(metadata.Title ?? Path.GetFileNameWithoutExtension(file), metadata.Id,
                    metadata.Version, Path.GetRelativePath(folder, file), KindName(file, metadata.WrapsPackage),
                    metadata.Architectures));
            }
            catch (Exception ex) when (PkgFeed.IsUnreadable(ex))
            {
                problems.Add(new FeedProblem(Path.GetRelativePath(folder, file), ex.Message));
            }
        }
        string? warning = OperatingSystem.IsMacOS() && TrustedPath.WhyUntrusted(folder) is { } why
            ? $"Standard users can change this folder ({why}) The package helper will not install from it, "
                + "and unsigned packages in it are refused."
            : null;
        return new FeedInspection(packages, problems, warning);
    }

    private static FeedPackage Describe(PkgFeedItem item) => new(item.Name, item.Id, item.Version,
        Uri.UnescapeDataString(Path.GetFileName(item.Location.AbsolutePath)),
        KindName(item.Location.AbsolutePath, item.WrapsPackage), item.Architectures);

    private static string KindName(string file, bool wrapsPackage) => FeedFile.KindOf(file) switch
    {
        FeedFileKind.DiskImage => wrapsPackage ? ".dmg (package)" : ".dmg (app)",
        FeedFileKind.Archive => ".zip (app)",
        _ => ".pkg",
    };
}
