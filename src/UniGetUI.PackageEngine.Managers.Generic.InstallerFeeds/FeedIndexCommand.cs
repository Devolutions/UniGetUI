using System.Text.Json;
using UniGetUI.Core.Logging;

namespace UniGetUI.PackageEngine.Managers.InstallerFeeds;

public interface IFeedIndexer
{
    FeedPlatform Platform { get; }

    IReadOnlyCollection<string> Extensions { get; }

    (IReadOnlyList<FeedIndexEntry> Entries, IReadOnlyList<FeedProblem> Skipped) Build(string folder);
}

/// <summary>
/// <c>unigetui pkg-index &lt;folder&gt;</c>: writes the index.json that serves a folder of packages as a
/// web feed, with each package's identifier, version and SHA-256 read from the files themselves.
/// </summary>
public static class FeedIndexCommand
{
    public const string Command = "pkg-index";

    public static bool IsCommand(string[] args) =>
        args.Length > 0 && string.Equals(args[0], Command, StringComparison.OrdinalIgnoreCase);

    public static int Run(string[] args, TextWriter output, TextWriter error, IFeedIndexer indexer)
    {
        string kinds = string.Join(", ", indexer.Extensions);
        if (args.Length != 1 || args[0] is "-h" or "--help")
        {
            output.WriteLine($"Usage: unigetui {Command} <folder>");
            output.WriteLine($"Writes index.json into <folder>, listing every {kinds} file in it and its subfolders,");
            output.WriteLine("so the folder can be published on a web server as a package feed.");
            return args.Length == 1 ? 0 : 2;
        }

        string folder = Path.GetFullPath(args[0]);
        if (!Directory.Exists(folder))
        {
            error.WriteLine($"The folder {folder} does not exist.");
            return 1;
        }

        var (index, skipped) = Build(folder, indexer);
        string destination = Path.Combine(folder, FeedLocation.IndexFileName);
        string temporary = destination + ".tmp";
        File.WriteAllText(temporary, FeedJson.Serialize(index, FeedIndexJsonContext.Default.FeedIndex) + Environment.NewLine);
        File.Move(temporary, destination, overwrite: true);

        var own = index.EntriesFor(indexer.Platform);
        var kept = index.EntriesFor(Other(indexer.Platform));
        foreach (var entry in own)
            output.WriteLine($"  {entry.File}: {entry.Name} ({entry.Id} {entry.Version})");
        foreach (var entry in kept)
            output.WriteLine($"  {entry.File}: {entry.Name} ({entry.Id} {entry.Version}) (kept)");
        foreach (var problem in skipped)
            error.WriteLine($"  Skipped {problem.File}: {problem.Reason}");
        output.WriteLine($"Wrote {destination} with {own.Length + kept.Length} package(s).");
        return skipped.Count == 0 ? 0 : 3;
    }

    private static FeedPlatform Other(FeedPlatform platform) =>
        platform == FeedPlatform.Windows ? FeedPlatform.MacOS : FeedPlatform.Windows;

    /// <summary>
    /// Indexes this platform's packages and keeps the entries the other platform wrote, each under its
    /// own key, as long as their files are still there unchanged.
    /// </summary>
    internal static (FeedIndex Index, IReadOnlyList<FeedProblem> Skipped) Build(string folder, IFeedIndexer indexer)
    {
        var (entries, skipped) = indexer.Build(folder);
        var problems = skipped.ToList();
        var kept = new List<FeedIndexEntry>();
        var indexUrl = new Uri(Path.Combine(folder, FeedLocation.IndexFileName));
        foreach (var entry in ReadExisting(folder)?.EntriesFor(Other(indexer.Platform)) ?? [])
        {
            if (entry is null || FeedLocation.HasExtension(entry.File ?? "", indexer.Extensions)
                || FeedLocation.ResolveWithin(indexUrl, entry.File) is not { } file || !File.Exists(file.LocalPath))
                continue;
            if (!string.Equals(FeedLocation.ComputeSha256(file.LocalPath), entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add(new FeedProblem(entry.File!, "it changed since another platform's UniGetUI indexed it; run pkg-index there again."));
                continue;
            }
            kept.Add(entry);
        }

        static FeedIndexEntry[] Sorted(IEnumerable<FeedIndexEntry> list) => [.. list.OrderBy(e => e.File, StringComparer.Ordinal)];
        FeedIndexEntry[] own = Sorted(entries), other = Sorted(kept);
        var (mac, windows) = indexer.Platform == FeedPlatform.Windows ? (other, own) : (own, other);
        return (new FeedIndex
        {
            Version = FeedIndex.CurrentVersion,
            Packages = mac,
            WindowsPackages = windows.Length > 0 ? windows : null,
        }, problems);
    }

    private static FeedIndex? ReadExisting(string folder)
    {
        string path = Path.Combine(folder, FeedLocation.IndexFileName);
        if (!File.Exists(path))
            return null;
        try
        {
            var index = FeedJson.Deserialize(File.ReadAllBytes(path), FeedIndexJsonContext.Default.FeedIndex);
            return index?.Version == FeedIndex.CurrentVersion ? index : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Logger.Warn($"Could not read the existing index {path}, so it is written anew: {ex.Message}");
            return null;
        }
    }
}
