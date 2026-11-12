namespace UniGetUI.Extensions.Xcode;

public enum XcodeCompatibility
{
    Unknown,
    Supported,
    Unsupported,
}

public sealed record XcodeCompatibilityAssessment(
    XcodeCompatibility Status, Version? RequiredMacOS, Version CurrentMacOS);

/// <summary>Fields shared by every matching candidate, not an assertion about which archive will be downloaded.</summary>
public sealed record XcodeCatalogDetails(
    DateOnly? ReleaseDate,
    Version? RequiredMacOS,
    Uri? ReleaseNotes,
    IReadOnlyList<string> SDKs,
    IReadOnlyList<string> Compilers,
    int? SizeMB,
    IReadOnlyList<string> SameBuildVersions,
    IReadOnlyList<XcodeSdkIdentity> SDKIdentities,
    XcodeCompatibilityAssessment Compatibility);

public static class XcodeCatalogMatching
{
    /// <summary>Details for the exact archive an installation will request, when its identity is known.</summary>
    public static XcodeCatalogDetails? ForRelease(
        XcodeRelease release,
        IReadOnlyList<XcodeCatalogRelease> catalog,
        Version currentMacOS,
        bool fresh)
    {
        if (release.Build is not { Length: > 0 } build || XcodesService.HasUnusableIdentity(release)
            || release.Architecture == XcodeArchitecture.Unspecified)
            return null;

        XcodeCatalogRelease[] candidates = [.. catalog.Where(candidate => candidate.Download is not null
            && SameIdentity(release.Version, build, release.Architecture, candidate))];
        return candidates.Length == 0 ? null : Summarize(candidates, catalog, currentMacOS, fresh);
    }

    public static XcodeCatalogDetails? ForAvailable(
        string version,
        IReadOnlyList<XcodeRelease> listed,
        IReadOnlyList<XcodeCatalogRelease> catalog,
        Version currentMacOS,
        bool fresh)
    {
        XcodeRelease[] rows = [.. listed.Where(item => SameVersion(item.Version, version))];
        XcodeCatalogRelease[] candidates = [.. catalog.Where(item => SameVersion(item.Version, version))];
        if (rows.Length == 0 || candidates.Length == 0
            || rows.Any(row => row.Build is null
                || !candidates.Any(candidate => SameIdentity(row.Version, row.Build, row.Architecture, candidate))))
            return null;

        // A version may have several variants; compatibility is certain only when all candidates agree.
        XcodeCatalogDetails details = Summarize(candidates, catalog, currentMacOS, fresh);
        int? appleSize = rows[0].AppleSizeMB;
        if (details.SizeMB is null && appleSize is not null && rows.All(row => row.AppleSizeMB == appleSize)
            && candidates.All(candidate => rows.Any(row => row.Build is not null
                && SameIdentity(row.Version, row.Build, row.Architecture, candidate))))
            details = details with { SizeMB = appleSize };
        return details;
    }

    public static XcodeCatalogDetails? ForInstalled(
        InstalledXcode installed,
        IReadOnlyList<XcodeCatalogRelease> catalog,
        Version currentMacOS,
        bool fresh)
    {
        if (installed.Build is null) return null;
        XcodeCatalogRelease[] candidates = [.. catalog.Where(candidate =>
            SameIdentity(installed.Version, installed.Build, installed.Architecture, candidate))];
        return candidates.Length == 0 ? null : Summarize(candidates, catalog, currentMacOS, fresh);
    }

    private static bool SameVersion(string left, string right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool SameIdentity(string version, string build, XcodeArchitecture architecture, XcodeCatalogRelease candidate)
        => SameVersion(version, candidate.Version)
            && string.Equals(build, candidate.Build, StringComparison.Ordinal)
            && architecture == candidate.Architecture;

    private static XcodeCatalogDetails Summarize(
        XcodeCatalogRelease[] candidates, IReadOnlyList<XcodeCatalogRelease> catalog, Version currentMacOS, bool fresh)
    {
        XcodeCatalogRelease first = candidates[0];
        Version current = new(currentMacOS.Major, Math.Max(currentMacOS.Minor, 0), Math.Max(currentMacOS.Build, 0));
        Version? required = candidates.All(item => item.RequiredMacOS is not null && item.RequiredMacOS == first.RequiredMacOS)
            ? first.RequiredMacOS : null;
        XcodeCompatibility status = XcodeCompatibility.Unknown;
        Version? incompatibleMinimum = null;
        if (fresh && candidates.All(item => item.RequiredMacOS is not null))
        {
            if (candidates.All(item => item.RequiredMacOS! <= current))
                status = XcodeCompatibility.Supported;
            else if (candidates.All(item => item.RequiredMacOS! > current))
            {
                status = XcodeCompatibility.Unsupported;
                incompatibleMinimum = candidates.Min(item => item.RequiredMacOS!);
            }
        }

        return new XcodeCatalogDetails(
            candidates.All(item => item.ReleaseDate == first.ReleaseDate) ? first.ReleaseDate : null,
            required,
            candidates.All(item => item.ReleaseNotes == first.ReleaseNotes) ? first.ReleaseNotes : null,
            candidates.All(item => item.SDKs.SequenceEqual(first.SDKs)) ? first.SDKs : [],
            candidates.All(item => item.Compilers.SequenceEqual(first.Compilers)) ? first.Compilers : [],
            candidates.All(item => item.SizeMB == first.SizeMB) ? first.SizeMB : null,
            SameBuildVersions(candidates, catalog),
            CommonSDKIdentities(candidates),
            new XcodeCompatibilityAssessment(status, incompatibleMinimum ?? required, current));
    }

    private static IReadOnlyList<XcodeSdkIdentity> CommonSDKIdentities(IReadOnlyList<XcodeCatalogRelease> candidates)
    {
        IEnumerable<XcodeSdkIdentity> common = candidates[0].SDKIdentities.Distinct();
        foreach (XcodeCatalogRelease candidate in candidates.Skip(1))
            common = common.Intersect(candidate.SDKIdentities);
        return [.. common.OrderBy(identity => identity.Platform, StringComparer.Ordinal)
            .ThenBy(identity => identity.Version, StringComparer.Ordinal)
            .ThenBy(identity => identity.Build, StringComparer.Ordinal)];
    }

    private static IReadOnlyList<string> SameBuildVersions(
        XcodeCatalogRelease[] candidates, IReadOnlyList<XcodeCatalogRelease> catalog)
    {
        HashSet<string>? common = null;
        foreach (XcodeCatalogRelease candidate in candidates)
        {
            if (candidate.Architecture == XcodeArchitecture.Unspecified
                || !XcodeVersion.TryParse(candidate.Version, out XcodeVersion current))
                return [];

            var matches = catalog.Where(item =>
                    item.Architecture == candidate.Architecture
                    && string.Equals(item.Build, candidate.Build, StringComparison.Ordinal)
                    && !SameVersion(item.Version, candidate.Version)
                    && XcodeVersion.TryParse(item.Version, out XcodeVersion other)
                    && current.IsPrerelease != other.IsPrerelease)
                .Select(item => item.Version)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (common is null)
                common = matches;
            else
                common.IntersectWith(matches);
            if (common.Count == 0)
                return [];
        }
        return common is null ? [] : [.. common.Order(StringComparer.OrdinalIgnoreCase)];
    }
}
