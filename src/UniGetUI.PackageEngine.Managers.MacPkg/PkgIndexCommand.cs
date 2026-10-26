using System.Text.Json;
using UniGetUI.PackageEngine.Managers.MacPkgManager.Internal;

namespace UniGetUI.PackageEngine.Managers.MacPkgManager;

/// <summary>
/// <c>unigetui pkg-index &lt;folder&gt;</c>: writes the index.json that serves a folder of .pkg and .dmg files
/// as a web feed, with each package's identifier, version, app and SHA-256 read from the files themselves.
/// </summary>
public static class PkgIndexCommand
{
    public const string Command = "pkg-index";

    public static bool IsCommand(string[] args) =>
        args.Length > 0 && string.Equals(args[0], Command, StringComparison.OrdinalIgnoreCase);

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length != 1 || args[0] is "-h" or "--help")
        {
            output.WriteLine($"Usage: unigetui {Command} <folder>");
            output.WriteLine("Writes index.json into <folder>, listing every .pkg and .dmg in it and its subfolders,");
            output.WriteLine("so the folder can be published on a web server as a package feed.");
            return args.Length == 1 ? 0 : 2;
        }

        string folder = Path.GetFullPath(args[0]);
        if (!Directory.Exists(folder))
        {
            error.WriteLine($"The folder {folder} does not exist.");
            return 1;
        }

        var (index, skipped) = Build(folder);
        string destination = Path.Combine(folder, PkgFeed.IndexFileName);
        string temporary = destination + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(index, PkgFeedJsonContext.Default.PkgFeedIndex) + Environment.NewLine);
        File.Move(temporary, destination, overwrite: true);

        foreach (var entry in index.Packages)
            output.WriteLine($"  {entry.File}: {entry.Name} ({entry.Id} {entry.Version})");
        foreach (string file in skipped)
            error.WriteLine($"  Skipped {Path.GetRelativePath(folder, file)}: it is not a readable flat package or a disk image with one app.");
        output.WriteLine($"Wrote {destination} with {index.Packages.Length} package(s).");
        return skipped.Count == 0 ? 0 : 3;
    }

    internal static (PkgFeedIndex Index, IReadOnlyList<string> Skipped) Build(string folder)
    {
        var entries = new List<PkgFeedIndexEntry>();
        var skipped = new List<string>();
        foreach (string file in PkgFeed.EnumeratePackages(folder))
        {
            if (PkgFeed.TryReadFile(file) is not { } metadata)
            {
                skipped.Add(file);
                continue;
            }
            string? icon = PublishIcon(file);
            entries.Add(new PkgFeedIndexEntry
            {
                Id = metadata.Id,
                Name = metadata.Title ?? Path.GetFileNameWithoutExtension(file),
                Version = metadata.Version,
                File = RelativeUrlPath(folder, file),
                Sha256 = PkgFeed.ComputeSha256(file),
                Size = new FileInfo(file).Length,
                Architectures = metadata.Architectures.Count > 0 ? [.. metadata.Architectures] : null,
                Icon = icon is null ? null : RelativeUrlPath(folder, icon),
                App = PkgFeedIndexApp.From(metadata.App),
                WrapsPackage = metadata.WrapsPackage ? true : null,
            });
        }
        return (new PkgFeedIndex { Version = PkgFeedIndex.CurrentVersion, Packages = [.. entries] }, skipped);
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

    private static string RelativeUrlPath(string folder, string file) =>
        Path.GetRelativePath(folder, file).Replace(Path.DirectorySeparatorChar, '/');
}
