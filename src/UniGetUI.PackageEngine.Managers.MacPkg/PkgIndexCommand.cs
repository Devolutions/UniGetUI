using UniGetUI.PackageEngine.Managers.InstallerFeeds;
using UniGetUI.PackageEngine.Managers.MacPkgManager.Internal;

namespace UniGetUI.PackageEngine.Managers.MacPkgManager;

/// <summary>
/// <c>unigetui pkg-index &lt;folder&gt;</c> on a Mac: indexes the .pkg, .dmg and .zip files of a folder, with
/// each package's identifier, version, app and SHA-256 read from the files themselves.
/// </summary>
public sealed class PkgIndexCommand : IFeedIndexer
{
    public const string Command = FeedIndexCommand.Command;

    public static bool IsCommand(string[] args) => FeedIndexCommand.IsCommand(args);

    public static int Run(string[] args, TextWriter output, TextWriter error) =>
        FeedIndexCommand.Run(args, output, error, new PkgIndexCommand());

    public FeedPlatform Platform => FeedPlatform.MacOS;

    public IReadOnlyCollection<string> Extensions => FeedFile.Extensions;

    public (IReadOnlyList<FeedIndexEntry> Entries, IReadOnlyList<FeedProblem> Skipped) Build(string folder)
    {
        var entries = new List<FeedIndexEntry>();
        var skipped = new List<FeedProblem>();
        foreach (string file in PkgFeed.EnumeratePackages(folder))
        {
            if (PkgFeed.TryReadFile(file) is not { } metadata)
            {
                skipped.Add(new FeedProblem(Path.GetRelativePath(folder, file),
                    "it is not a readable flat package or a disk image with one app."));
                continue;
            }
            string? icon = PublishIcon(file);
            entries.Add(new FeedIndexEntry
            {
                Id = metadata.Id,
                Name = metadata.Title ?? Path.GetFileNameWithoutExtension(file),
                Version = metadata.Version,
                File = FeedLocation.RelativeUrlPath(folder, file),
                Sha256 = FeedLocation.ComputeSha256(file),
                Size = new FileInfo(file).Length,
                Architectures = metadata.Architectures.Count > 0 ? [.. metadata.Architectures] : null,
                Icon = icon is null ? null : FeedLocation.RelativeUrlPath(folder, icon),
                App = PkgIndexApp.From(metadata.App),
                WrapsPackage = metadata.WrapsPackage ? true : null,
            });
        }
        return (entries, skipped);
    }

    // A web feed serves only what is in the folder, so an icon taken from a disk image is saved
    // next to it, where a sibling image would go.
    private static string? PublishIcon(string file)
    {
        string? icon = PkgFeed.FindOrExtractIcon(file);
        if (icon is null || Path.GetDirectoryName(icon) == Path.GetDirectoryName(file))
            return icon;
        string sibling = Path.ChangeExtension(file, ".png");
        File.Copy(icon, sibling, overwrite: false);
        return sibling;
    }
}
