namespace UniGetUI.PackageEngine.Managers.InstallerFeeds;

/// <summary>A package a feed publishes, as the Catalog Editor previews it.</summary>
public sealed record FeedPackage(string Name, string Id, string Version, string File, string Kind,
    IReadOnlyList<string> Architectures);

/// <summary>A file in a feed that UniGetUI cannot offer, and why.</summary>
public sealed record FeedProblem(string File, string Reason);

/// <summary>What a feed offers: its packages, the files it skips, and a warning about where it lives.</summary>
public sealed record FeedInspection(IReadOnlyList<FeedPackage> Packages, IReadOnlyList<FeedProblem> Problems,
    string? Warning);
