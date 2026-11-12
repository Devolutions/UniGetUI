using UniGetUI.Extensions.Xcode;

namespace UniGetUI.Extensions.Xcode.Tests;

public sealed class XcodeCatalogMatchingTests
{
    private static readonly Version Current = new(26, 4);

    [Fact]
    public void MatchesInstalledOnlyByVersionBuildAndArchitecture()
    {
        XcodeCatalogRelease release = Metadata("26.4", "17E192", XcodeArchitecture.AppleSilicon, "26.2");
        var installed = new InstalledXcode("26.4", "17E192", XcodeArchitecture.AppleSilicon, false, "/Applications/Xcode.app");

        Assert.NotNull(XcodeCatalogMatching.ForInstalled(installed, [release], Current, fresh: true));
        Assert.Null(XcodeCatalogMatching.ForInstalled(installed with { Version = "26.4 Beta" }, [release], Current, true));
        Assert.Null(XcodeCatalogMatching.ForInstalled(installed with { Build = "17E193" }, [release], Current, true));
        Assert.Null(XcodeCatalogMatching.ForInstalled(installed with { Architecture = XcodeArchitecture.Universal }, [release], Current, true));
        Assert.Null(XcodeCatalogMatching.ForInstalled(installed with { Build = null }, [release], Current, true));
    }

    [Fact]
    public void InstallationCompatibilityRequiresTheExactBuildAndArchitecture()
    {
        XcodeRelease row = Row("26.4", "17E192", XcodeArchitecture.AppleSilicon);
        XcodeCatalogRelease selected = Metadata(row.Version, row.Build!, row.Architecture, "26.5") with
        {
            Download = new Uri("https://download.developer.apple.com/Xcode_26.4.xip"),
        };
        XcodeCatalogRelease otherBuild = selected with { Build = "17E193", RequiredMacOS = new Version(26, 2, 0) };
        XcodeCatalogRelease otherArchitecture = selected with
        {
            Architecture = XcodeArchitecture.Universal,
            RequiredMacOS = new Version(26, 2, 0),
        };
        XcodeCatalogRelease nonDownloadable = selected with { Download = null, RequiredMacOS = new Version(26, 2, 0) };
        XcodeCatalogRelease[] catalog = [selected, otherBuild, otherArchitecture, nonDownloadable];

        XcodeCatalogDetails details = XcodeCatalogMatching.ForRelease(row, catalog, Current, fresh: true)!;

        Assert.Equal(XcodeCompatibility.Unsupported, details.Compatibility.Status);
        Assert.Equal(new Version(26, 5, 0), details.Compatibility.RequiredMacOS);
        Assert.Equal(XcodeCompatibility.Unknown,
            XcodeCatalogMatching.ForRelease(row, catalog, Current, fresh: false)!.Compatibility.Status);
        Assert.Null(XcodeCatalogMatching.ForRelease(row with { Build = null }, catalog, Current, true));
        Assert.Null(XcodeCatalogMatching.ForRelease(row with { Build = "17E192\n" }, catalog, Current, true));
        Assert.Null(XcodeCatalogMatching.ForRelease(row with { IsIdentityUnverified = true }, catalog, Current, true));
        Assert.Null(XcodeCatalogMatching.ForRelease(row with { Architecture = XcodeArchitecture.Unspecified }, catalog, Current, true));
        Assert.Null(XcodeCatalogMatching.ForRelease(row with { Version = "26.3" }, catalog, Current, true));
        Assert.Null(XcodeCatalogMatching.ForRelease(row, [nonDownloadable], Current, true));

        XcodeCatalogRelease conflicting = selected with { RequiredMacOS = new Version(26, 2, 0) };
        Assert.Equal(XcodeCompatibility.Unknown,
            XcodeCatalogMatching.ForRelease(row, [selected, conflicting], Current, true)!.Compatibility.Status);
    }

    [Fact]
    public void AvailableRequiresEveryCliRowToMatchTheCatalog()
    {
        var row = Row("26.4", "17E192", XcodeArchitecture.AppleSilicon);
        XcodeCatalogRelease release = Metadata("26.4", "17E192", XcodeArchitecture.AppleSilicon, "26.2");

        Assert.NotNull(XcodeCatalogMatching.ForAvailable(row.Version, [row], [release], Current, true));
        Assert.Null(XcodeCatalogMatching.ForAvailable(row.Version, [row with { Build = null }], [release], Current, true));
        Assert.Null(XcodeCatalogMatching.ForAvailable(row.Version, [row, row with { Build = "17E193" }], [release], Current, true));
        Assert.Null(XcodeCatalogMatching.ForAvailable(row.Version, [row with { Architecture = XcodeArchitecture.Unspecified }], [release], Current, true));
    }

    [Fact]
    public void CompatibilityThresholdIncludesTheRequiredMacOS()
    {
        var row = Row("26.4", "17E192", XcodeArchitecture.AppleSilicon);
        XcodeCatalogRelease release = Metadata("26.4", "17E192", XcodeArchitecture.AppleSilicon, "26.4");
        XcodeCatalogDetails details = XcodeCatalogMatching.ForAvailable(row.Version, [row], [release], Current, true)!;

        Assert.Equal(XcodeCompatibility.Supported, details.Compatibility.Status);
        Assert.Equal(new Version(26, 4, 0), details.RequiredMacOS);
    }

    [Fact]
    public void AllPossibleVariantsMustBeIncompatibleBeforeWarning()
    {
        var row = Row("26.4", "17E192", XcodeArchitecture.AppleSilicon);
        XcodeCatalogRelease arm = Metadata("26.4", "17E192", XcodeArchitecture.AppleSilicon, "26.5");
        XcodeCatalogRelease universal = Metadata("26.4", "17E193", XcodeArchitecture.Universal, "26.6");

        XcodeCatalogDetails details = XcodeCatalogMatching.ForAvailable(row.Version, [row], [arm, universal], Current, true)!;
        Assert.Null(details.RequiredMacOS);
        Assert.Equal(XcodeCompatibility.Unsupported, details.Compatibility.Status);
        Assert.Equal(new Version(26, 5, 0), details.Compatibility.RequiredMacOS);

        universal = universal with { RequiredMacOS = new Version(26, 2, 0) };
        details = XcodeCatalogMatching.ForAvailable(row.Version, [row], [arm, universal], Current, true)!;
        Assert.Equal(XcodeCompatibility.Unknown, details.Compatibility.Status);
    }

    [Fact]
    public void StaleOrIncompleteRequirementsCannotProveCompatibility()
    {
        var row = Row("26.4", "17E192", XcodeArchitecture.AppleSilicon);
        XcodeCatalogRelease release = Metadata("26.4", "17E192", XcodeArchitecture.AppleSilicon, "26.2");

        Assert.Equal(XcodeCompatibility.Unknown,
            XcodeCatalogMatching.ForAvailable(row.Version, [row], [release], Current, false)!.Compatibility.Status);
        Assert.Equal(XcodeCompatibility.Unknown,
            XcodeCatalogMatching.ForAvailable(row.Version, [row], [release with { RequiredMacOS = null }], Current, true)!.Compatibility.Status);
    }

    [Fact]
    public void ConflictingDetailsAreNotAttributedToAnAmbiguousVersion()
    {
        var row = Row("26.4", "17E192", XcodeArchitecture.AppleSilicon);
        XcodeCatalogRelease arm = Metadata("26.4", "17E192", XcodeArchitecture.AppleSilicon, "26.2") with
        {
            ReleaseDate = new DateOnly(2026, 3, 1),
            SDKs = ["iOS 26.4"],
        };
        XcodeCatalogRelease universal = Metadata("26.4", "17E193", XcodeArchitecture.Universal, "26.2") with
        {
            ReleaseDate = new DateOnly(2026, 3, 2),
            SDKs = ["iOS 26.5"],
        };

        XcodeCatalogDetails details = XcodeCatalogMatching.ForAvailable(row.Version, [row], [arm, universal], Current, true)!;

        Assert.Null(details.ReleaseDate);
        Assert.Empty(details.SDKs);
        Assert.Equal(new Version(26, 2, 0), details.RequiredMacOS);
    }

    [Fact]
    public void AppleSizeAppearsOnlyWhenEveryPossibleVariantMatches()
    {
        XcodeRelease arm = Row("26.4", "17E192", XcodeArchitecture.AppleSilicon) with { AppleSizeMB = 1235 };
        XcodeRelease universal = Row("26.4", "17E193", XcodeArchitecture.Universal) with { AppleSizeMB = 1235 };
        XcodeCatalogRelease armDetails = Metadata(arm.Version, arm.Build!, arm.Architecture, "26.2");
        XcodeCatalogRelease universalDetails = Metadata(universal.Version, universal.Build!, universal.Architecture, "26.2");

        XcodeCatalogDetails one = XcodeCatalogMatching.ForAvailable(arm.Version, [arm], [armDetails], Current, true)!;
        XcodeCatalogDetails shared = XcodeCatalogMatching.ForAvailable(arm.Version, [arm, universal], [armDetails, universalDetails], Current, true)!;
        XcodeCatalogDetails incomplete = XcodeCatalogMatching.ForAvailable(arm.Version, [arm], [armDetails, universalDetails], Current, true)!;
        XcodeCatalogDetails different = XcodeCatalogMatching.ForAvailable(arm.Version,
            [arm, universal with { AppleSizeMB = 1300 }], [armDetails, universalDetails], Current, true)!;

        Assert.Equal(1235, one.SizeMB);
        Assert.Equal(1235, shared.SizeMB);
        Assert.Null(incomplete.SizeMB);
        Assert.Null(different.SizeMB);
    }

    [Fact]
    public void RelatedReleasesRequireTheSameBuildAndArchitecture()
    {
        XcodeCatalogRelease release = Metadata("26.4", "17E192", XcodeArchitecture.AppleSilicon, "26.2");
        XcodeCatalogRelease candidate = release with { Version = "26.4 Release Candidate" };
        XcodeCatalogRelease otherBuild = candidate with { Version = "26.4 Beta", Build = "17E191" };
        XcodeCatalogRelease otherArchitecture = candidate with
        {
            Version = "26.4 Beta 2",
            Architecture = XcodeArchitecture.Universal,
        };
        XcodeCatalogRelease anotherStable = release with { Version = "26.3" };
        XcodeCatalogRelease[] catalog = [release, candidate, candidate, otherBuild, otherArchitecture, anotherStable];
        var installed = new InstalledXcode("26.4", "17E192", XcodeArchitecture.AppleSilicon, false, "/Applications/Xcode.app");

        XcodeCatalogDetails details = XcodeCatalogMatching.ForInstalled(installed, catalog, Current, true)!;

        Assert.Equal(["26.4 Release Candidate"], details.SameBuildVersions);
        XcodeCatalogDetails prerelease = XcodeCatalogMatching.ForAvailable(candidate.Version,
            [Row(candidate.Version, candidate.Build, candidate.Architecture)], catalog, Current, true)!;
        Assert.Equal(["26.3", "26.4"], prerelease.SameBuildVersions);
    }

    [Fact]
    public void RelatedReleasesForAnAmbiguousInstallMustMatchEveryPossibleVariant()
    {
        var row = Row("26.4", "17E192", XcodeArchitecture.AppleSilicon);
        XcodeCatalogRelease arm = Metadata("26.4", "17E192", XcodeArchitecture.AppleSilicon, "26.2");
        XcodeCatalogRelease universal = Metadata("26.4", "17E193", XcodeArchitecture.Universal, "26.2");
        XcodeCatalogRelease armCandidate = arm with { Version = "26.4 Release Candidate" };
        XcodeCatalogRelease universalCandidate = universal with { Version = "26.4 Release Candidate" };

        XcodeCatalogDetails unknown = XcodeCatalogMatching.ForAvailable(row.Version, [row],
            [arm, universal, armCandidate], Current, true)!;
        XcodeCatalogDetails shared = XcodeCatalogMatching.ForAvailable(row.Version, [row],
            [arm, universal, armCandidate, universalCandidate], Current, false)!;

        Assert.Empty(unknown.SameBuildVersions);
        Assert.Equal(["26.4 Release Candidate"], shared.SameBuildVersions);
        Assert.Equal(XcodeCompatibility.Unknown, shared.Compatibility.Status);
    }

    [Fact]
    public void UnknownArchitectureDoesNotEstablishARelatedBuild()
    {
        XcodeCatalogRelease release = Metadata("26.4", "17E192", XcodeArchitecture.Unspecified, "26.2");
        XcodeCatalogRelease candidate = release with { Version = "26.4 Release Candidate" };
        var row = Row(release.Version, release.Build, release.Architecture);

        XcodeCatalogDetails details = XcodeCatalogMatching.ForAvailable(row.Version, [row],
            [release, candidate], Current, true)!;

        Assert.Empty(details.SameBuildVersions);
    }

    [Fact]
    public void AmbiguousVariantsExposeOnlySDKBuildsCommonToEveryCandidate()
    {
        var shared = new XcodeSdkIdentity("iOS", "26.4", "23E100");
        var armOnly = new XcodeSdkIdentity("watchOS", "26.4", "23S100");
        var universalOnly = new XcodeSdkIdentity("tvOS", "26.4", "23K100");
        XcodeCatalogRelease arm = Metadata("26.4", "17E192", XcodeArchitecture.AppleSilicon, "26.2") with
        {
            SDKIdentities = [shared, armOnly],
        };
        XcodeCatalogRelease universal = Metadata("26.4", "17E193", XcodeArchitecture.Universal, "26.2") with
        {
            SDKIdentities = [shared, universalOnly],
        };
        XcodeRelease row = Row("26.4", "17E192", XcodeArchitecture.AppleSilicon);

        XcodeCatalogDetails available = XcodeCatalogMatching.ForAvailable(row.Version, [row],
            [arm, universal], Current, true)!;
        var installed = new InstalledXcode(row.Version, row.Build, row.Architecture, false, "/Applications/Xcode.app");
        XcodeCatalogDetails exact = XcodeCatalogMatching.ForInstalled(installed, [arm, universal], Current, true)!;
        XcodeCatalogDetails incomplete = XcodeCatalogMatching.ForAvailable(row.Version, [row],
            [arm, universal with { SDKIdentities = [] }], Current, true)!;

        Assert.Equal([shared], available.SDKIdentities);
        Assert.Equal([shared, armOnly], exact.SDKIdentities);
        Assert.Empty(incomplete.SDKIdentities);
    }

    private static XcodeRelease Row(string version, string build, XcodeArchitecture architecture)
        => new(version, build, architecture, false, false);

    private static XcodeCatalogRelease Metadata(string version, string build, XcodeArchitecture architecture, string required)
        => new(version, build, architecture, null, Version.Parse(required + ".0"), null, [], [], null);
}
