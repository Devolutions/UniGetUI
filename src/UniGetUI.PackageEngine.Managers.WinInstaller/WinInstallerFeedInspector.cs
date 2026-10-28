using UniGetUI.PackageEngine.Managers.InstallerFeeds;
using UniGetUI.PackageEngine.Managers.WinInstallerManager.Internal;

namespace UniGetUI.PackageEngine.Managers.WinInstallerManager;

public static class WinInstallerFeedInspector
{
    public static bool IsValidLocation(string? text) => FeedLocation.TryParse(text, out _);

    public static FeedInspection Inspect(string text)
    {
        if (!FeedLocation.TryParse(text, out Uri location))
            throw new InvalidDataException($"\"{text}\" is not an https URL or an absolute folder path.");
        WinFeed.ForgetCachedIndexes();
        if (!location.IsFile)
            return new FeedInspection(WinFeed.List(location, currentOnly: true).Select(Describe).ToArray(), [], null);

        string folder = location.LocalPath;
        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException($"The folder {folder} does not exist or is not reachable.");
        var packages = new List<FeedPackage>();
        var problems = new List<FeedProblem>();
        foreach (string file in WinFeed.EnumeratePackages(folder))
        {
            try
            {
                var metadata = WinFeed.ReadFile(file);
                packages.Add(new FeedPackage(metadata.Name, metadata.Id, metadata.Version, Path.GetRelativePath(folder, file),
                    InstallerKinds.Describe(metadata.Details.Kind), metadata.Architectures));
            }
            catch (Exception ex) when (WinFeed.IsUnreadable(ex))
            {
                problems.Add(new FeedProblem(Path.GetRelativePath(folder, file), ex.Message));
            }
        }
        return new FeedInspection(packages, problems, null);
    }

    private static FeedPackage Describe(WinFeedItem item) => new(item.Name, item.Id, item.Version,
        Uri.UnescapeDataString(Path.GetFileName(item.Location.AbsolutePath)), InstallerKinds.Describe(item.Details.Kind),
        item.Architectures);
}

public sealed class WinInstallerIndexer : IFeedIndexer
{
    public FeedPlatform Platform => FeedPlatform.Windows;

    public IReadOnlyCollection<string> Extensions => InstallerFile.Extensions;

    public (IReadOnlyList<FeedIndexEntry> Entries, IReadOnlyList<FeedProblem> Skipped) Build(string folder)
    {
        var entries = new List<FeedIndexEntry>();
        var skipped = new List<FeedProblem>();
        foreach (string file in WinFeed.EnumeratePackages(folder))
        {
            InstallerMetadata metadata;
            try
            {
                metadata = WinFeed.ReadFile(file);
            }
            catch (Exception ex) when (WinFeed.IsUnreadable(ex))
            {
                skipped.Add(new FeedProblem(Path.GetRelativePath(folder, file), ex.Message));
                continue;
            }
            string? icon = PublishIcon(file);
            entries.Add(new FeedIndexEntry
            {
                Id = metadata.Id,
                Name = metadata.Name,
                Version = metadata.Version,
                File = FeedLocation.RelativeUrlPath(folder, file),
                Sha256 = FeedLocation.ComputeSha256(file),
                Size = new FileInfo(file).Length,
                Architectures = metadata.Architectures.Count > 0 ? [.. metadata.Architectures] : null,
                Icon = icon is null ? null : FeedLocation.RelativeUrlPath(folder, icon),
                Windows = metadata.Details.ToIndex(),
            });
        }
        return (entries, skipped);
    }

    private static string? PublishIcon(string file)
    {
        string? icon = WinFeed.FindIcon(file);
        if (icon is null || Path.GetDirectoryName(icon) == Path.GetDirectoryName(file))
            return icon;
        string sibling = Path.ChangeExtension(file, Path.GetExtension(icon));
        File.Copy(icon, sibling, overwrite: false);
        return sibling;
    }
}
