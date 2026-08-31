using System.Net;
using UniGetUI.Core.IconEngine;
using UniGetUI.PackageEngine.Classes.Manager;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.ManagerClasses.Manager;
using UniGetUI.PackageEngine.Managers.Generic.NuGet.Internal;
using UniGetUI.PackageEngine.Managers.PowerShellManager;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.Tests.Infrastructure.Builders;
using UniGetUI.PackageEngine.Tests.Infrastructure.Helpers;

namespace UniGetUI.PackageEngine.Tests;

public sealed class NuGetV3ClientTests
{
    private static readonly string[] DefaultVersions =
    [
        "1.0.0",
        "1.2.0",
        "2.0.0-beta.1",
        "2.0.0",
    ];

    private const string SparseCatalogEntry = """
    {"id":"Contoso.Tool","version":"2.0.0"}
    """;

    [Theory]
    [InlineData("https://api.nuget.org/v3/index.json", true)]
    [InlineData("https://pkgs.dev.azure.com/contoso/_packaging/feed/nuget/v3/index.json", true)]
    [InlineData("https://nuget.pkg.github.com/contoso/INDEX.JSON", true)]
    [InlineData("https://www.nuget.org/api/v2", false)]
    [InlineData("https://www.powershellgallery.com/api/v2", false)]
    [InlineData("https://community.chocolatey.org/api/v2/", false)]
    [InlineData("https://packages.example.test/api/v3", true)]
    [InlineData("https://packages.example.test/api/v3/", true)]
    [InlineData("https://packages.example.test/nuget/V3", true)]
    [InlineData("https://packages.example.test/feed", false)]
    [InlineData("https://www.poshtestgallery.com/api/v2", false)]
    public void IsV3SourceOnlyMatchesServiceIndexUrls(string url, bool expected)
    {
        var source = new SourceBuilder().WithUrl(url).Build();

        Assert.Equal(expected, NuGetV3ServiceIndex.IsV3Source(source));
    }

    [Theory]
    [InlineData("https://api.nuget.org/v3/index.json", "https://api.nuget.org/v3/index.json")]
    [InlineData("https://example.test/api/v3", "https://example.test/api/v3/index.json")]
    [InlineData("https://example.test/api/v3/", "https://example.test/api/v3/index.json")]
    [InlineData("https://example.test/v3/index.json?x=1", "https://example.test/v3/index.json")]
    public void ServiceIndexUrlIsDerivedFromTheSourceUrl(string url, string expected)
    {
        var source = new SourceBuilder().WithUrl(url).Build();

        Assert.Equal(expected, NuGetV3ServiceIndex.GetServiceIndexUrl(source)?.AbsoluteUri);
    }

    [Fact]
    public void ResolveSelectsThePreferredResourceForEachType()
    {
        using var feed = new FakeV3Feed();
        var index = feed.Resolve();

        Assert.NotNull(index);
        Assert.Equal($"{feed.BaseUri}query", index.SearchQueryService?.AbsoluteUri);
        Assert.Equal($"{feed.BaseUri}registration-gz-semver2/", index.RegistrationsBaseUrl?.AbsoluteUri);
        Assert.Equal($"{feed.BaseUri}flatcontainer/", index.PackageBaseAddress?.AbsoluteUri);
    }

    [Fact]
    public void ResolveReadsResourcesDeclaringTheirTypeAsAnArray()
    {
        using var feed = new FakeV3Feed { UseArrayResourceTypes = true };
        var index = feed.Resolve();

        Assert.NotNull(index);
        Assert.Equal($"{feed.BaseUri}query", index.SearchQueryService?.AbsoluteUri);
        Assert.Equal($"{feed.BaseUri}flatcontainer/", index.PackageBaseAddress?.AbsoluteUri);
    }

    [Fact]
    public void ResolveIsCachedPerSource()
    {
        using var feed = new FakeV3Feed();

        Assert.NotNull(feed.Resolve(clearCaches: true));
        Assert.NotNull(feed.Resolve(clearCaches: false));

        Assert.Single(feed.Server.RequestPaths, path => path.Contains("index.json"));
    }

    [Fact]
    public void ResolveReturnsNullOnAMalformedServiceIndex()
    {
        using var feed = new FakeV3Feed { ServiceIndexBody = "{ not json" };

        Assert.Null(feed.Resolve());
    }

    [Fact]
    public void ResolveReturnsNullWhenNoUsableResourceIsAdvertised()
    {
        using var feed = new FakeV3Feed
        {
            ServiceIndexBody = """
            {"version":"3.0.0","resources":[{"@id":"https://example.test/publish","@type":"PackagePublish/2.0.0"}]}
            """,
        };

        Assert.Null(feed.Resolve());
    }

    [Fact]
    public void SearchSendsV3ParametersAndReadsJsonResults()
    {
        using var feed = new FakeV3Feed();
        var index = feed.Resolve();
        Assert.NotNull(index);

        var results = NuGetV3Client.Search(index, "contoso tool", false, "DotnetTool", 50);

        Assert.Equal(2, results.Count);
        Assert.Equal("Contoso.Tool", results[0].Id);
        Assert.Equal("2.0.0", results[0].Version);
        Assert.Equal("A tool", results[0].Description);
        Assert.Equal($"{feed.BaseUri}icons/contoso.png", results[0].IconUrl);

        string query = Assert.Single(feed.Server.RequestPaths, path => path.Contains("/query"));
        Assert.Contains("q=contoso+tool", query);
        Assert.Contains("take=50", query);
        Assert.Contains("prerelease=false", query);
        Assert.Contains("semVerLevel=2.0.0", query);
        Assert.Contains("packageType=DotnetTool", query);
    }

    [Fact]
    public void SearchOmitsThePackageTypeFilterWhenNoneIsRequested()
    {
        using var feed = new FakeV3Feed();
        var index = feed.Resolve();
        Assert.NotNull(index);

        NuGetV3Client.Search(index, "contoso", true, null, 20);

        string query = Assert.Single(feed.Server.RequestPaths, path => path.Contains("/query"));
        Assert.DoesNotContain("packageType", query);
        Assert.Contains("prerelease=true", query);
        Assert.Contains("take=20", query);
    }

    [Fact]
    public void SearchFallsBackToAnExactIdLookupWhenTheFeedHasNoSearchService()
    {
        using var feed = new FakeV3Feed { AdvertiseSearchService = false };
        var index = feed.Resolve();
        Assert.NotNull(index);

        var results = NuGetV3Client.Search(index, "Contoso.Tool", false, null, 50);

        var result = Assert.Single(results);
        Assert.Equal("Contoso.Tool", result.Id);
        Assert.Equal("2.0.0", result.Version);
        Assert.DoesNotContain(feed.Server.RequestPaths, path => path.Contains("/query"));
    }

    [Fact]
    public void SearchPagesUntilTheResultLimitIsReached()
    {
        using var feed = new FakeV3Feed { SearchPageSize = 2, SearchTotalPackages = 5 };
        var index = feed.Resolve();
        Assert.NotNull(index);

        var results = NuGetV3Client.Search(index, "contoso", false, null, 5);

        Assert.Equal(5, results.Count);
        Assert.Equal(
            ["Pkg.0", "Pkg.1", "Pkg.2", "Pkg.3", "Pkg.4"],
            results.Select(result => result.Id)
        );

        var queries = feed.Server.RequestPaths.Where(path => path.Contains("/query")).ToList();
        Assert.Equal(3, queries.Count);
        Assert.Contains("skip=0", queries[0]);
        Assert.Contains("skip=2", queries[1]);
        Assert.Contains("skip=4", queries[2]);
    }

    [Fact]
    public void SearchStopsPagingOnAShortPage()
    {
        using var feed = new FakeV3Feed { SearchPageSize = 10, SearchTotalPackages = 3 };
        var index = feed.Resolve();
        Assert.NotNull(index);

        var results = NuGetV3Client.Search(index, "contoso", false, null, 50);

        Assert.Equal(3, results.Count);
        Assert.Single(feed.Server.RequestPaths, path => path.Contains("/query"));
    }

    [Fact]
    public void SearchKeepsTheHighestVersionOfADuplicatedId()
    {
        using var feed = new FakeV3Feed { DuplicateIdVersions = ["1.0.0", "10.0.0", "2.0.0"] };
        var index = feed.Resolve();
        Assert.NotNull(index);

        var results = NuGetV3Client.Search(index, "contoso", false, null, 50);

        var result = Assert.Single(results);
        Assert.Equal("Duplicated.Tool", result.Id);
        Assert.Equal("10.0.0", result.Version);
    }

    [Fact]
    public void SearchFallsBackToAnExactIdLookupWhenTheFeedReturnsNoResults()
    {
        using var feed = new FakeV3Feed { SearchTotalPackages = 0 };
        var index = feed.Resolve();
        Assert.NotNull(index);

        var results = NuGetV3Client.Search(index, "Contoso.Tool", false, null, 50);

        var result = Assert.Single(results);
        Assert.Equal("Contoso.Tool", result.Id);
        Assert.Equal("2.0.0", result.Version);
    }

    [Fact]
    public void GetCatalogEntryToleratesMetadataWithEveryOptionalFieldMissing()
    {
        using var feed = new FakeV3Feed { CatalogEntryBody = SparseCatalogEntry };
        var index = feed.Resolve();
        Assert.NotNull(index);

        var entry = NuGetV3Client.GetCatalogEntry(index, "Contoso.Tool", "2.0.0");

        Assert.NotNull(entry);
        Assert.Null(entry.Description);
        Assert.Null(entry.LicenseExpression);
        Assert.Null(entry.PackageHash);
        Assert.Equal(0, entry.PackageSize);
        Assert.Null(entry.DependencyGroups);
        Assert.Empty(NuGetV3Json.AsStringList(entry.Authors));
        Assert.Empty(NuGetV3Json.AsStringList(entry.Tags));
        Assert.Equal("2026-01-02T03:04:05Z", entry.Published);
    }

    [Fact]
    public void GetDetailsToleratesMetadataWithEveryOptionalFieldMissing()
    {
        using var feed = new FakeV3Feed { CatalogEntryBody = SparseCatalogEntry };
        NuGetV3ServiceIndex.ClearCache();
        NuGetV3Client.ClearCaches();
        BaseNuGet.V3Entries.Clear();

        var manager = new TestNuGetManager($"{feed.BaseUri}v3/index.json");
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithSource(manager.Properties.DefaultSource)
            .WithId("Contoso.Tool")
            .WithVersion("2.0.0")
            .Build();
        var details = new PackageDetailsBuilder().Build(package);

        manager.ExposedDetailsHelper.LoadDetails(details);

        Assert.Null(details.Description);
        Assert.Null(details.License);
        Assert.Empty(details.Dependencies);
        Assert.Empty(details.Tags);
        Assert.Equal(
            $"{feed.BaseUri}flatcontainer/contoso.tool/2.0.0/contoso.tool.2.0.0.nupkg",
            details.InstallerUrl?.AbsoluteUri
        );
        Assert.Contains(feed.Server.RequestMethods, method => method == "HEAD");
    }

    [Theory]
    [InlineData("Contoso.Tool", "contoso.tool")]
    [InlineData("CONTOSO_Tool-Extra", "contoso_tool-extra")]
    [InlineData(" Contoso.Tool ", "contoso.tool")]
    [InlineData("Contoso+Tool", "contoso%2Btool")]
    [InlineData("Contoso Tool", "contoso%20tool")]
    public void EscapeIdNormalizesAndEscapesPackageIds(string packageId, string expected)
    {
        Assert.Equal(expected, NuGetV3Client.EscapeId(packageId));
    }

    [Fact]
    public void AV2SourceNeverReachesAV3Endpoint()
    {
        using var feed = new FakeV3Feed();
        NuGetV3ServiceIndex.ClearCache();
        NuGetV3Client.ClearCaches();
        BaseNuGet.Manifests.Clear();
        BaseNuGet.V3Entries.Clear();
        BaseNuGet.V3IconUrls.Clear();

        var manager = new TestNuGetManager($"{feed.BaseUri}api/v2");
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithSource(manager.Properties.DefaultSource)
            .WithId("Contoso.Tool")
            .WithVersion("2.0.0")
            .Build();

        manager.ExposedDetailsHelper.LoadDetails(new PackageDetailsBuilder().Build(package));
        manager.ExposedDetailsHelper.LoadVersions(package);

        Assert.NotEmpty(feed.Server.RequestPaths);
        Assert.All(
            feed.Server.RequestPaths,
            path =>
            {
                Assert.DoesNotContain("index.json", path);
                Assert.DoesNotContain("/query", path);
                Assert.DoesNotContain("registration", path);
            }
        );
        Assert.Contains(feed.Server.RequestPaths, path => path.Contains("Packages(Id="));
        Assert.Contains(feed.Server.RequestPaths, path => path.Contains("FindPackagesById"));
    }

    [Fact]
    public void GetInstallableVersionsUsesTheFlatContainerForV3Sources()
    {
        using var feed = new FakeV3Feed { Versions = ["1.0.0", "10.0.0", "2.1.0"] };
        NuGetV3ServiceIndex.ClearCache();
        NuGetV3Client.ClearCaches();

        var manager = new TestNuGetManager($"{feed.BaseUri}v3/index.json");
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithSource(manager.Properties.DefaultSource)
            .WithId("Contoso.Tool")
            .WithVersion("1.0.0")
            .Build();

        Assert.Equal(
            ["10.0.0", "2.1.0", "1.0.0"],
            manager.ExposedDetailsHelper.LoadVersions(package)
        );
    }

    [Fact]
    public void GetNuPkgUrlUsesTheFlatContainerForV3SourcesAndV2PathsOtherwise()
    {
        using var feed = new FakeV3Feed();
        NuGetV3ServiceIndex.ClearCache();
        NuGetV3Client.ClearCaches();

        var v3Manager = new TestNuGetManager($"{feed.BaseUri}v3/index.json");
        var v3Package = new PackageBuilder()
            .WithManager(v3Manager)
            .WithSource(v3Manager.Properties.DefaultSource)
            .WithId("Contoso.Tool")
            .WithVersion("1.0.0.0")
            .Build();

        Assert.Equal(
            $"{feed.BaseUri}flatcontainer/contoso.tool/1.0.0/contoso.tool.1.0.0.nupkg",
            NuGetManifestLoader.GetNuPkgUrl(v3Package).AbsoluteUri
        );

        var v2Manager = new TestNuGetManager("https://packages.example.test/api/v2");
        var v2Package = new PackageBuilder()
            .WithManager(v2Manager)
            .WithSource(v2Manager.Properties.DefaultSource)
            .WithId("Contoso.Tool")
            .WithVersion("1.0.0")
            .Build();

        Assert.Equal(
            "https://packages.example.test/api/v2/package/Contoso.Tool/1.0.0",
            NuGetManifestLoader.GetNuPkgUrl(v2Package).AbsoluteUri
        );
    }

    [Fact]
    public void SearchDegradesToTheExactIdLookupOnAMalformedSearchResponse()
    {
        using var feed = new FakeV3Feed { SearchBody = "{ \"data\": [ not json" };
        var index = feed.Resolve();
        Assert.NotNull(index);

        var result = Assert.Single(NuGetV3Client.Search(index, "Contoso.Tool", false, null, 50));
        Assert.Equal("Contoso.Tool", result.Id);
        Assert.Equal("2.0.0", result.Version);
        Assert.Null(result.Description);
    }

    [Fact]
    public void SearchReturnsNothingWhenBothTheSearchAndTheExactIdLookupFail()
    {
        using var feed = new FakeV3Feed
        {
            SearchBody = "{ \"data\": [ not json",
            FlatContainerBody = "{ \"versions\": ",
        };
        var index = feed.Resolve();
        Assert.NotNull(index);

        Assert.Empty(NuGetV3Client.Search(index, "Contoso.Tool", false, null, 50));
    }

    [Fact]
    public void SearchIgnoresResultsMissingAnIdOrVersion()
    {
        using var feed = new FakeV3Feed
        {
            SearchBody = """
            {
              "totalHits": 3,
              "data": [
                { "version": "1.0.0" },
                { "id": "No.Version" },
                { "id": "Good.Tool", "version": "1.0.0" }
              ]
            }
            """,
        };
        var index = feed.Resolve();
        Assert.NotNull(index);

        var result = Assert.Single(NuGetV3Client.Search(index, "contoso", false, null, 50));
        Assert.Equal("Good.Tool", result.Id);
    }

    [Fact]
    public void GetVersionsReturnsNothingOnAMalformedVersionIndex()
    {
        using var feed = new FakeV3Feed { FlatContainerBody = "{ \"versions\": " };
        var index = feed.Resolve();
        Assert.NotNull(index);

        Assert.Empty(NuGetV3Client.GetVersions(index, "Contoso.Tool"));
        Assert.Null(NuGetV3Client.GetUpdateCandidate(index, "Contoso.Tool", "1.0.0", false));
    }

    [Fact]
    public void GetCatalogEntryFallsBackToTheNuspecOnAMalformedRegistrationLeaf()
    {
        using var feed = new FakeV3Feed { RegistrationLeafBody = "{ oops" };
        var index = feed.Resolve();
        Assert.NotNull(index);

        var entry = NuGetV3Client.GetCatalogEntry(index, "Contoso.Tool", "2.0.0");

        Assert.NotNull(entry);
        Assert.Equal("A tool from the nuspec", entry.Description);
        Assert.Contains(feed.Server.RequestPaths, path => path.EndsWith(".nuspec"));
    }

    [Fact]
    public void GetCatalogEntryReturnsNothingWhenBothMetadataSourcesAreMalformed()
    {
        using var feed = new FakeV3Feed
        {
            RegistrationLeafBody = "{ oops",
            NuspecBody = "<package><metadata><id>Contoso",
        };
        var index = feed.Resolve();
        Assert.NotNull(index);

        Assert.Null(NuGetV3Client.GetCatalogEntry(index, "Contoso.Tool", "2.0.0"));
    }

    // A registration lookup that cannot be completed must not be reported as "listed": the flat
    // container includes unlisted versions, so treating an unknown result as listed would let a
    // transient failure surface a withdrawn release as an update.
    [Fact]
    public void AMalformedRegistrationLeafYieldsAnUnknownListedStatus()
    {
        using var feed = new FakeV3Feed { RegistrationLeafBody = "{ oops" };
        var index = feed.Resolve();
        Assert.NotNull(index);

        Assert.Equal(
            ListedStatus.Unknown,
            NuGetV3Client.GetListedStatus(index, "Contoso.Tool", "2.0.0")
        );
    }

    [Fact]
    public void AFeedWithoutRegistrationsYieldsAnUnknownListedStatus()
    {
        using var feed = new FakeV3Feed { AdvertiseRegistrations = false };
        var index = feed.Resolve();
        Assert.NotNull(index);

        Assert.Equal(
            ListedStatus.Unknown,
            NuGetV3Client.GetListedStatus(index, "Contoso.Tool", "2.0.0")
        );
    }

    [Theory]
    [InlineData(true, ListedStatus.Listed)]
    [InlineData(false, ListedStatus.Unlisted)]
    internal void AnExplicitListedFlagIsReported(bool listed, ListedStatus expected)
    {
        using var feed = new FakeV3Feed
        {
            RegistrationLeafBody = $"{{\"listed\":{(listed ? "true" : "false")}}}",
        };
        var index = feed.Resolve();
        Assert.NotNull(index);

        Assert.Equal(expected, NuGetV3Client.GetListedStatus(index, "Contoso.Tool", "2.0.0"));
    }

    // Registration predates the listed field, so a feed that omits it is treated as listed
    // rather than blocking every update on that feed.
    [Fact]
    public void AnOmittedListedFlagIsTreatedAsListed()
    {
        using var feed = new FakeV3Feed { RegistrationLeafBody = "{\"published\":\"2026-01-02T03:04:05Z\"}" };
        var index = feed.Resolve();
        Assert.NotNull(index);

        Assert.Equal(
            ListedStatus.Listed,
            NuGetV3Client.GetListedStatus(index, "Contoso.Tool", "2.0.0")
        );
    }

    // Previously capped at five probes, which hid a valid update when the newest few versions
    // had been unlisted.
    [Fact]
    public void GetUpdateCandidateWalksPastMoreThanFiveUnlistedVersions()
    {
        using var feed = new FakeV3Feed
        {
            Versions = ["1.0.0", "2.0.1", "2.0.2", "2.0.3", "2.0.4", "2.0.5", "2.0.6", "2.0.7"],
            UnlistedVersions = ["2.0.7", "2.0.6", "2.0.5", "2.0.4", "2.0.3", "2.0.2"],
        };
        var index = feed.Resolve();
        Assert.NotNull(index);

        Assert.Equal(
            "2.0.1",
            NuGetV3Client.GetUpdateCandidate(index, "Contoso.Tool", "1.0.0", false, out bool failed)
        );
        Assert.False(failed);
    }

    // A feed outage must be reported, not silently converted into "no update available".
    [Fact]
    public void GetUpdateCandidateReportsAFailedVersionRequest()
    {
        using var feed = new FakeV3Feed { FlatContainerStatusCode = 500 };
        var index = feed.Resolve();
        Assert.NotNull(index);

        Assert.Null(
            NuGetV3Client.GetUpdateCandidate(index, "Contoso.Tool", "1.0.0", false, out bool failed)
        );
        Assert.True(failed);
    }

    [Fact]
    public void GetUpdateCandidateReportsAnUnresolvableListedStatus()
    {
        using var feed = new FakeV3Feed
        {
            Versions = ["1.0.0", "2.0.0"],
            RegistrationLeafStatusCode = 500,
        };
        var index = feed.Resolve();
        Assert.NotNull(index);

        Assert.Null(
            NuGetV3Client.GetUpdateCandidate(index, "Contoso.Tool", "1.0.0", false, out bool failed)
        );
        Assert.True(failed);
    }

    // A registration-less feed cannot answer listed status at all, so the newest matching
    // version is accepted rather than reporting every check as failed.
    [Fact]
    public void GetUpdateCandidateAcceptsTheNewestVersionOnARegistrationLessFeed()
    {
        using var feed = new FakeV3Feed
        {
            AdvertiseRegistrations = false,
            Versions = ["1.0.0", "2.0.0"],
        };
        var index = feed.Resolve();
        Assert.NotNull(index);

        Assert.Equal(
            "2.0.0",
            NuGetV3Client.GetUpdateCandidate(index, "Contoso.Tool", "1.0.0", false, out bool failed)
        );
        Assert.False(failed);
    }

    [Fact]
    public void GetVersionsDistinguishesAFailedRequestFromAnEmptyFeedAnswer()
    {
        using var feed = new FakeV3Feed { FlatContainerStatusCode = 500 };
        var index = feed.Resolve();
        Assert.NotNull(index);

        Assert.Empty(NuGetV3Client.GetVersions(index, "Contoso.Tool", out bool failed));
        Assert.True(failed);

        using var empty = new FakeV3Feed { FlatContainerBody = "{\"versions\":[]}" };
        var emptyIndex = empty.Resolve();
        Assert.NotNull(emptyIndex);

        Assert.Empty(NuGetV3Client.GetVersions(emptyIndex, "Contoso.Tool", out bool emptyFailed));
        Assert.False(emptyFailed);
    }

    [Fact]
    public void GetNuspecMetadataReturnsNothingOnMalformedXml()
    {
        using var feed = new FakeV3Feed
        {
            AdvertiseRegistrations = false,
            NuspecBody = "<package><metadata><id>Contoso",
        };
        var index = feed.Resolve();
        Assert.NotNull(index);

        Assert.Null(NuGetV3Client.GetCatalogEntry(index, "Contoso.Tool", "2.0.0"));
    }

    [Fact]
    public void TheExactIdFallbackRejectsAPackageWithoutTheRequiredPackageType()
    {
        using var feed = new FakeV3Feed { SearchTotalPackages = 0 };
        var index = feed.Resolve();
        Assert.NotNull(index);

        Assert.Empty(NuGetV3Client.Search(index, "Contoso.Tool", false, "DotnetTool", 50));
        Assert.Contains(feed.Server.RequestPaths, path => path.EndsWith(".nuspec"));
    }

    [Fact]
    public void TheExactIdFallbackAcceptsAPackageDeclaringTheRequiredPackageType()
    {
        using var feed = new FakeV3Feed
        {
            SearchTotalPackages = 0,
            NuspecPackageType = "DotnetTool",
        };
        var index = feed.Resolve();
        Assert.NotNull(index);

        var result = Assert.Single(
            NuGetV3Client.Search(index, "Contoso.Tool", false, "DotnetTool", 50)
        );
        Assert.Equal("Contoso.Tool", result.Id);
        Assert.True(result.IsExactIdFallback);
    }

    [Fact]
    public void TheExactIdFallbackSkipsPackageTypeVerificationWhenNoneIsRequired()
    {
        using var feed = new FakeV3Feed { SearchTotalPackages = 0 };
        var index = feed.Resolve();
        Assert.NotNull(index);

        Assert.Single(NuGetV3Client.Search(index, "Contoso.Tool", false, null, 50));
        Assert.DoesNotContain(feed.Server.RequestPaths, path => path.EndsWith(".nuspec"));
    }

    [Fact]
    public void ParseNuspecReadsPackageTypes()
    {
        var entry = NuGetV3Client.ParseNuspec(
            """
            <package><metadata><id>Contoso.Tool</id>
              <packageTypes><packageType name="DotnetTool" /></packageTypes>
            </metadata></package>
            """,
            "Contoso.Tool",
            "1.0.0",
            null
        );

        Assert.NotNull(entry);
        Assert.True(entry.HasPackageType("dotnettool"));
        Assert.False(entry.HasPackageType("Dependency"));
    }

    [Fact]
    public void HasPackageTypeIsFalseWhenTheMetadataDeclaresNone()
    {
        var entry = NuGetV3Client.ParseNuspec(
            """<package><metadata><id>Contoso.Tool</id></metadata></package>""",
            "Contoso.Tool",
            "1.0.0",
            null
        );

        Assert.NotNull(entry);
        Assert.Null(entry.PackageTypes);
        Assert.False(entry.HasPackageType("DotnetTool"));
    }

    // A search service returns nothing for a package precisely when it has been withdrawn, so
    // the exact-id fallback must not resurrect an unlisted version from the flat container -
    // which does list withdrawn versions.
    [Fact]
    public void TheExactIdFallbackSkipsUnlistedVersions()
    {
        using var feed = new FakeV3Feed
        {
            SearchTotalPackages = 0,
            Versions = ["1.0.0", "2.0.0", "3.0.0"],
            UnlistedVersions = ["3.0.0", "2.0.0"],
        };
        var index = feed.Resolve();
        Assert.NotNull(index);

        var result = Assert.Single(NuGetV3Client.Search(index, "Contoso.Tool", false, null, 50));
        Assert.Equal("1.0.0", result.Version);
    }

    [Fact]
    public void TheExactIdFallbackReturnsNothingWhenEveryVersionIsUnlisted()
    {
        using var feed = new FakeV3Feed
        {
            SearchTotalPackages = 0,
            Versions = ["1.0.0", "2.0.0"],
            UnlistedVersions = ["1.0.0", "2.0.0"],
        };
        var index = feed.Resolve();
        Assert.NotNull(index);

        Assert.Empty(NuGetV3Client.Search(index, "Contoso.Tool", false, null, 50));
    }

    // Unlike update selection, an exact-id search has no failure channel and the user named the
    // package explicitly, so an unresolvable listed status shows the package rather than hiding
    // it. Update selection reports the same condition as a failed check instead.
    [Fact]
    public void TheExactIdFallbackShowsThePackageWhenListedStatusCannotBeEstablished()
    {
        using var feed = new FakeV3Feed
        {
            SearchTotalPackages = 0,
            Versions = ["1.0.0", "2.0.0"],
            RegistrationLeafStatusCode = 500,
        };
        var index = feed.Resolve();
        Assert.NotNull(index);

        var result = Assert.Single(NuGetV3Client.Search(index, "Contoso.Tool", false, null, 50));
        Assert.Equal("2.0.0", result.Version);
    }

    [Fact]
    public void TheExactIdFallbackTakesTheNewestVersionOnARegistrationLessFeed()
    {
        using var feed = new FakeV3Feed
        {
            AdvertiseSearchService = false,
            AdvertiseRegistrations = false,
            Versions = ["1.0.0", "2.0.0"],
        };
        var index = feed.Resolve();
        Assert.NotNull(index);

        var result = Assert.Single(NuGetV3Client.Search(index, "Contoso.Tool", false, null, 50));
        Assert.Equal("2.0.0", result.Version);
    }

    // A registration-only service index is accepted (search and details still work through it),
    // but versions cannot be enumerated without a PackageBaseAddress. Converting that into an
    // empty version list would report "no update available" on every check forever, so it is
    // surfaced as a failure instead.
    [Fact]
    public void AFeedWithoutAPackageBaseAddressReportsAVersionEnumerationFailure()
    {
        using var feed = new FakeV3Feed { AdvertisePackageBaseAddress = false };
        var index = feed.Resolve();
        Assert.NotNull(index);
        Assert.Null(index.PackageBaseAddress);
        Assert.NotNull(index.RegistrationsBaseUrl);

        Assert.Empty(NuGetV3Client.GetVersions(index, "Contoso.Tool", out bool failed));
        Assert.True(failed);

        Assert.Null(
            NuGetV3Client.GetUpdateCandidate(
                index,
                "Contoso.Tool",
                "1.0.0",
                false,
                out bool candidateFailed
            )
        );
        Assert.True(candidateFailed);
    }

    [Fact]
    public void GetVersionsDescendingOrdersBySemanticVersion()
    {
        using var feed = new FakeV3Feed
        {
            Versions = ["1.0.0", "10.0.0", "2.1.0", "2.0.0-beta.1", "9.9.9"],
        };
        var index = feed.Resolve();
        Assert.NotNull(index);

        var versions = NuGetV3Client.GetVersionsDescending(index, "Contoso.Tool");

        Assert.Equal(["10.0.0", "9.9.9", "2.1.0", "2.0.0-beta.1", "1.0.0"], versions);
    }

    [Fact]
    public void GetVersionsRequestsTheFlatContainerIndexOnlyOncePerPackage()
    {
        using var feed = new FakeV3Feed();
        var index = feed.Resolve();
        Assert.NotNull(index);

        NuGetV3Client.GetVersions(index, "Contoso.Tool");
        NuGetV3Client.GetVersions(index, "Contoso.Tool");

        Assert.Single(feed.Server.RequestPaths, path => path.Contains("/flatcontainer/"));
    }

    [Fact]
    public void GetUpdateCandidateIgnoresPreReleasesUnlessTheyAreEnabled()
    {
        using var feed = new FakeV3Feed { Versions = ["1.0.0", "1.2.0", "2.0.0-beta.1"] };
        var index = feed.Resolve();
        Assert.NotNull(index);

        Assert.Equal("1.2.0", NuGetV3Client.GetUpdateCandidate(index, "Contoso.Tool", "1.0.0", false));
        Assert.Equal(
            "2.0.0-beta.1",
            NuGetV3Client.GetUpdateCandidate(index, "Contoso.Tool", "1.0.0", true)
        );
    }

    [Fact]
    public void GetUpdateCandidateReturnsNullWhenTheInstalledVersionIsTheHighest()
    {
        using var feed = new FakeV3Feed();
        var index = feed.Resolve();
        Assert.NotNull(index);

        Assert.Null(NuGetV3Client.GetUpdateCandidate(index, "Contoso.Tool", "2.0.0", false));
        Assert.Null(NuGetV3Client.GetUpdateCandidate(index, "Contoso.Tool", "9.0.0", false));
    }

    [Fact]
    public void GetUpdateCandidateSkipsUnlistedVersions()
    {
        using var feed = new FakeV3Feed
        {
            Versions = ["1.0.0", "1.2.0", "1.3.0"],
            UnlistedVersions = ["1.3.0"],
        };
        var index = feed.Resolve();
        Assert.NotNull(index);

        Assert.Equal("1.2.0", NuGetV3Client.GetUpdateCandidate(index, "Contoso.Tool", "1.0.0", false));
    }

    [Fact]
    public void GetUpdateCandidateReturnsNullWhenEveryNewerVersionIsUnlisted()
    {
        using var feed = new FakeV3Feed
        {
            Versions = ["1.0.0", "1.2.0"],
            UnlistedVersions = ["1.2.0"],
        };
        var index = feed.Resolve();
        Assert.NotNull(index);

        Assert.Null(NuGetV3Client.GetUpdateCandidate(index, "Contoso.Tool", "1.0.0", false));
    }

    [Fact]
    public void GetUpdateCandidateDoesNotProbeListingWhenNoNewerVersionExists()
    {
        using var feed = new FakeV3Feed();
        var index = feed.Resolve();
        Assert.NotNull(index);

        NuGetV3Client.GetUpdateCandidate(index, "Contoso.Tool", "2.0.0", false);

        Assert.DoesNotContain(feed.Server.RequestPaths, path => path.Contains("registration"));
    }

    [Fact]
    public void GetCatalogEntryFollowsACatalogEntryReference()
    {
        using var feed = new FakeV3Feed();
        var index = feed.Resolve();
        Assert.NotNull(index);

        var entry = NuGetV3Client.GetCatalogEntry(index, "Contoso.Tool", "2.0.0");

        Assert.NotNull(entry);
        Assert.Equal("Contoso.Tool", entry.Id);
        Assert.Equal("A tool", entry.Description);
        Assert.Equal("Contoso, Fabrikam", NuGetV3Json.AsJoinedString(entry.Authors));
        Assert.Equal("MIT", entry.LicenseExpression);
        Assert.Equal(4096, entry.PackageSize);
        Assert.Equal("hash-value", entry.PackageHash);
        Assert.Equal("2026-01-02T03:04:05Z", entry.Published);
        Assert.Equal(["cli", "tool"], NuGetV3Json.AsStringList(entry.Tags));

        var dependency = Assert.Single(entry.DependencyGroups?[0].Dependencies ?? []);
        Assert.Equal("Contoso.Core", dependency.Id);
        Assert.Equal("[1.5.0, )", dependency.Range);
    }

    [Fact]
    public void GetCatalogEntryReadsAnInlinedCatalogEntry()
    {
        using var feed = new FakeV3Feed { InlineCatalogEntry = true };
        var index = feed.Resolve();
        Assert.NotNull(index);

        var entry = NuGetV3Client.GetCatalogEntry(index, "Contoso.Tool", "2.0.0");

        Assert.NotNull(entry);
        Assert.Equal("A tool", entry.Description);
        Assert.DoesNotContain(feed.Server.RequestPaths, path => path.Contains("/catalog/"));
    }

    [Fact]
    public void GetCatalogEntryFallsBackToTheNuspecWhenTheFeedHasNoRegistration()
    {
        using var feed = new FakeV3Feed { AdvertiseRegistrations = false };
        var index = feed.Resolve();
        Assert.NotNull(index);

        var entry = NuGetV3Client.GetCatalogEntry(index, "Contoso.Tool", "2.0.0");

        Assert.NotNull(entry);
        Assert.Equal("Contoso.Tool", entry.Id);
        Assert.Equal("A tool from the nuspec", entry.Description);
        Assert.Equal("Contoso, Fabrikam", entry.GetAuthors());
        Assert.Equal(["cli", "tool"], entry.GetTags());
        Assert.Equal("MIT", entry.LicenseExpression);
        Assert.Equal("https://licenses.test/MIT", entry.LicenseUrl);
        Assert.Equal("https://contoso.test/", entry.ProjectUrl);
        Assert.Equal("Notes", entry.ReleaseNotes);
        Assert.Equal("icon.png", entry.IconFile);
        Assert.Equal(
            $"{feed.BaseUri}flatcontainer/contoso.tool/2.0.0/contoso.tool.2.0.0.nupkg",
            entry.PackageContent
        );

        var dependencies = entry.DependencyGroups?[0].Dependencies;
        Assert.NotNull(dependencies);
        Assert.Equal(2, dependencies.Count);
        Assert.Equal("Contoso.Core", dependencies[0].Id);
        Assert.Equal("[1.5.0, )", dependencies[0].Range);
        Assert.Equal("Fabrikam.Core", dependencies[1].Id);

        Assert.Contains(feed.Server.RequestPaths, path => path.EndsWith(".nuspec"));
    }

    [Fact]
    public void GetDetailsFallsBackToTheNuspecWhenTheFeedHasNoRegistration()
    {
        using var feed = new FakeV3Feed { AdvertiseRegistrations = false };
        NuGetV3ServiceIndex.ClearCache();
        NuGetV3Client.ClearCaches();
        BaseNuGet.V3Entries.Clear();

        var manager = new TestNuGetManager($"{feed.BaseUri}v3/index.json");
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithSource(manager.Properties.DefaultSource)
            .WithId("Contoso.Tool")
            .WithVersion("2.0.0")
            .Build();
        var details = new PackageDetailsBuilder().Build(package);

        manager.ExposedDetailsHelper.LoadDetails(details);

        Assert.Equal("A tool from the nuspec", details.Description);
        Assert.Equal("Contoso, Fabrikam", details.Author);
        Assert.Equal("MIT", details.License);
        Assert.Equal(["cli", "tool"], details.Tags);
        Assert.Equal(2, details.Dependencies.Count);
        Assert.Equal(
            $"{feed.BaseUri}flatcontainer/contoso.tool/2.0.0/contoso.tool.nuspec",
            details.ManifestUrl?.AbsoluteUri
        );
    }

    [Fact]
    public void ParseNuspecToleratesADocumentWithOnlyAnId()
    {
        var entry = NuGetV3Client.ParseNuspec(
            """<?xml version="1.0"?><package><metadata><id>Contoso.Tool</id></metadata></package>""",
            "Fallback.Id",
            "1.0.0",
            null
        );

        Assert.NotNull(entry);
        Assert.Equal("Contoso.Tool", entry.Id);
        Assert.Equal("1.0.0", entry.Version);
        Assert.Null(entry.Description);
        Assert.Null(entry.DependencyGroups);
        Assert.Empty(entry.GetTags());
        Assert.Null(entry.GetAuthors());
    }

    [Fact]
    public void ParseNuspecReturnsNullWhenThereIsNoMetadataElement()
    {
        Assert.Null(
            NuGetV3Client.ParseNuspec("<package></package>", "Contoso.Tool", "1.0.0", null)
        );
    }

    [Fact]
    public void GetNuspecUrlUsesNormalizedLowercasedSegments()
    {
        using var feed = new FakeV3Feed();
        var index = feed.Resolve();
        Assert.NotNull(index);

        Assert.Equal(
            $"{feed.BaseUri}flatcontainer/contoso.tool/1.0.0/contoso.tool.nuspec",
            NuGetV3Client.GetNuspecUrl(index, "Contoso.Tool", "1.0.0.0")?.AbsoluteUri
        );
    }

    [Fact]
    public void GetPackageContentUrlUsesNormalizedLowercasedSegments()
    {
        using var feed = new FakeV3Feed();
        var index = feed.Resolve();
        Assert.NotNull(index);

        Assert.Equal(
            $"{feed.BaseUri}flatcontainer/contoso.tool/1.0.0/contoso.tool.1.0.0.nupkg",
            NuGetV3Client.GetPackageContentUrl(index, "Contoso.Tool", "1.0.0.0")?.AbsoluteUri
        );
    }

    [Fact]
    public void GetRegistrationLeafUrlUsesNormalizedLowercasedSegments()
    {
        using var feed = new FakeV3Feed();
        var index = feed.Resolve();
        Assert.NotNull(index);

        Assert.Equal(
            $"{feed.BaseUri}registration-gz-semver2/contoso.tool/2.0.0-beta.1.json",
            NuGetV3Client.GetRegistrationLeafUrl(index, "Contoso.Tool", "2.0.0-Beta.1")?.AbsoluteUri
        );
    }

    [Theory]
    [InlineData("1.0", "1.0.0")]
    [InlineData("1.0.0.0", "1.0.0")]
    [InlineData("1.0.0.4", "1.0.0.4")]
    [InlineData("1.02.3", "1.2.3")]
    [InlineData("1.0.0+build.9", "1.0.0")]
    [InlineData("2.0.0-Beta.1", "2.0.0-beta.1")]
    [InlineData("2.0.0-Beta.1+meta", "2.0.0-beta.1")]
    [InlineData("not-a-version", "not-a-version")]
    public void NormalizeVersionFollowsNuGetRules(string version, string expected)
    {
        Assert.Equal(expected, NuGetV3Client.NormalizeVersion(version));
    }

    [Theory]
    [InlineData("[1.5.0, )", "1.5.0")]
    [InlineData("[1.5.0]", "1.5.0")]
    [InlineData("1.5.0", "1.5.0")]
    [InlineData("(, 2.0.0]", "(, 2.0.0]")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void FormatDependencyRangeShowsTheLowerBound(string? range, string expected)
    {
        Assert.Equal(expected, BaseNuGetDetailsHelper.FormatDependencyRange(range));
    }

    [Fact]
    public void GetDetailsMapsEveryV3MetadataFieldOntoPackageDetails()
    {
        using var feed = new FakeV3Feed();
        NuGetV3ServiceIndex.ClearCache();
        NuGetV3Client.ClearCaches();
        BaseNuGet.V3Entries.Clear();

        var manager = new TestNuGetManager($"{feed.BaseUri}v3/index.json");
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithSource(manager.Properties.DefaultSource)
            .WithId("Contoso.Tool")
            .WithVersion("2.0.0")
            .Build();
        var details = new PackageDetailsBuilder().Build(package);

        manager.ExposedDetailsHelper.LoadDetails(details);

        Assert.Equal("A tool", details.Description);
        Assert.Equal("Contoso, Fabrikam", details.Author);
        Assert.Equal("Contoso, Fabrikam", details.Publisher);
        Assert.Equal("MIT", details.License);
        Assert.Equal("https://licenses.test/MIT", details.LicenseUrl?.AbsoluteUri);
        Assert.Equal("https://contoso.test/", details.HomepageUrl?.AbsoluteUri);
        Assert.Equal("Notes", details.ReleaseNotes);
        Assert.Equal("2026-01-02T03:04:05Z", details.UpdateDate);
        Assert.Equal("hash-value", details.InstallerHash);
        Assert.Equal(4096, details.InstallerSize);
        Assert.Equal(["cli", "tool"], details.Tags);
        Assert.Equal(
            $"{feed.BaseUri}flatcontainer/contoso.tool/2.0.0/contoso.tool.2.0.0.nupkg",
            details.InstallerUrl?.AbsoluteUri
        );
        Assert.Equal(
            $"{feed.BaseUri}registration-gz-semver2/contoso.tool/2.0.0.json",
            details.ManifestUrl?.AbsoluteUri
        );

        var dependency = Assert.Single(details.Dependencies);
        Assert.Equal("Contoso.Core", dependency.Name);
        Assert.Equal("1.5.0", dependency.Version);
        Assert.True(dependency.Mandatory);
    }

    // The metadata cache must be keyed by version. Package.GetHash() covers only
    // manager + source + id, so keying on it made details for one version answer requests for
    // every other version of the same package - serving the wrong installer URL, dependencies
    // and publication date.
    [Fact]
    public void DetailsForOneVersionDoNotLeakIntoAnotherVersionOfTheSamePackage()
    {
        using var feed = new FakeV3Feed();
        NuGetV3ServiceIndex.ClearCache();
        NuGetV3Client.ClearCaches();
        BaseNuGet.V3Entries.Clear();

        var manager = new TestNuGetManager($"{feed.BaseUri}v3/index.json");

        PackageDetails LoadFor(string version)
        {
            var package = new PackageBuilder()
                .WithManager(manager)
                .WithSource(manager.Properties.DefaultSource)
                .WithId("Contoso.Tool")
                .WithVersion(version)
                .Build();
            var details = new PackageDetailsBuilder().Build(package);
            manager.ExposedDetailsHelper.LoadDetails(details);
            return details;
        }

        var first = LoadFor("2.0.0");
        var second = LoadFor("1.0.0");

        Assert.Equal(
            $"{feed.BaseUri}flatcontainer/contoso.tool/2.0.0/contoso.tool.2.0.0.nupkg",
            first.InstallerUrl?.AbsoluteUri
        );
        Assert.Equal(
            $"{feed.BaseUri}flatcontainer/contoso.tool/1.0.0/contoso.tool.1.0.0.nupkg",
            second.InstallerUrl?.AbsoluteUri
        );
        Assert.Equal(
            $"{feed.BaseUri}registration-gz-semver2/contoso.tool/1.0.0.json",
            second.ManifestUrl?.AbsoluteUri
        );
    }

    [Fact]
    public void GetDetailsDoesNotTouchTheNetworkTwiceForDetailsAndIcon()
    {
        using var feed = new FakeV3Feed();
        NuGetV3ServiceIndex.ClearCache();
        NuGetV3Client.ClearCaches();
        BaseNuGet.V3Entries.Clear();
        BaseNuGet.V3IconUrls.Clear();

        var manager = new TestNuGetManager($"{feed.BaseUri}v3/index.json");
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithSource(manager.Properties.DefaultSource)
            .WithId("Contoso.Tool")
            .WithVersion("2.0.0")
            .Build();

        manager.ExposedDetailsHelper.LoadDetails(new PackageDetailsBuilder().Build(package));
        int requestsAfterDetails = feed.Server.RequestPaths.Count;

        manager.ExposedDetailsHelper.LoadIcon(package);

        Assert.Equal(requestsAfterDetails, feed.Server.RequestPaths.Count);
    }

    // The flat container defines no /{id}/{version}/icon route - it is a nuget.org extension - so
    // an embedded icon must not be guessed at on a conforming third-party feed, where the request
    // would simply 404.
    [Fact]
    public void GetIconDoesNotGuessAnEmbeddedIconRouteOnANonNuGetOrgFeed()
    {
        using var feed = new FakeV3Feed();
        NuGetV3ServiceIndex.ClearCache();
        NuGetV3Client.ClearCaches();
        BaseNuGet.V3Entries.Clear();
        BaseNuGet.V3IconUrls.Clear();

        var manager = new TestNuGetManager($"{feed.BaseUri}v3/index.json");
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithSource(manager.Properties.DefaultSource)
            .WithId("Contoso.Tool")
            .WithVersion("2.0.0")
            .Build();

        Assert.Null(manager.ExposedDetailsHelper.LoadIcon(package));
    }

    [Theory]
    [InlineData("https://api.nuget.org/v3-flatcontainer/", true)]
    [InlineData("https://nuget.org/v3-flatcontainer/", true)]
    [InlineData("https://packages.example.test/v3-flatcontainer/", false)]
    [InlineData("https://nuget.org.example.test/flat/", false)]
    [InlineData("https://pkgs.dev.azure.com/contoso/_packaging/feed/nuget/v3/flat2/", false)]
    public void TheEmbeddedIconRouteIsOnlyUsedWhereItExists(string packageBase, bool expected)
    {
        Assert.Equal(
            expected,
            NuGetV3Client.SupportsEmbeddedIconRoute(new Uri(packageBase))
        );
    }

    // Comment 16: this client implements NuGet semantics, whose pre-release labels are compared
    // case-insensitively. Under strict SemVer's ASCII ordering "1.0.0-Z" would sort below
    // "1.0.0-a", which would pick a different latest version than NuGet does.
    [Fact]
    public void VersionSelectionUsesNuGetsCaseInsensitiveLabelOrdering()
    {
        using var feed = new FakeV3Feed { Versions = ["1.0.0-a", "1.0.0-Z"] };
        var index = feed.Resolve();
        Assert.NotNull(index);

        Assert.Equal(["1.0.0-Z", "1.0.0-a"], NuGetV3Client.GetVersionsDescending(index, "Contoso.Tool"));
        Assert.Equal("1.0.0-Z", NuGetV3Client.SelectHighestVersion(["1.0.0-a", "1.0.0-Z"], true));
    }

    [Fact]
    public void TheClientAndTheManagerAgreeOnLabelOrdering()
    {
        using var feed = new FakeV3Feed();
        var manager = new TestNuGetManager($"{feed.BaseUri}v3/index.json");

        Assert.True(NuGetV3Client.TryParseNuGetVersion("1.0.0-Z", out var upper));
        Assert.True(NuGetV3Client.TryParseNuGetVersion("1.0.0-a", out var lower));

        Assert.Equal(
            Math.Sign(upper.CompareTo(lower)),
            Math.Sign(manager.CompareVersions("1.0.0-Z", "1.0.0-a")!.Value)
        );
        Assert.True(upper > lower);
    }

    [Fact]
    public void GetIconReturnsNothingWhenSearchAlreadyReportedNoIcon()
    {
        using var feed = new FakeV3Feed();
        NuGetV3ServiceIndex.ClearCache();
        NuGetV3Client.ClearCaches();
        BaseNuGet.V3Entries.Clear();
        BaseNuGet.V3IconUrls.Clear();

        var manager = new TestNuGetManager($"{feed.BaseUri}v3/index.json");
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithSource(manager.Properties.DefaultSource)
            .WithId("Contoso.Tool")
            .WithVersion("2.0.0")
            .Build();
        BaseNuGet.V3IconUrls[package.GetVersionedHash()] = string.Empty;

        Assert.Null(manager.ExposedDetailsHelper.LoadIcon(package));
        Assert.Empty(feed.Server.RequestPaths);
    }

    private sealed class TestNuGetManager : BaseNuGet
    {
        public TestNuGetManager(string sourceUrl)
        {
            Capabilities = new ManagerCapabilities
            {
                SupportsCustomVersions = true,
                SupportsCustomPackageIcons = true,
                CanListDependencies = true,
            };

            Properties = new ManagerProperties
            {
                Id = "test-nuget",
                Name = "TestNuGet",
                DefaultSource = new ManagerSource(this, "test", new Uri(sourceUrl)),
            };

            ExposedDetailsHelper = new TestNuGetDetailsHelper(this);
            DetailsHelper = ExposedDetailsHelper;
        }

        public TestNuGetDetailsHelper ExposedDetailsHelper { get; }

        protected override IReadOnlyList<Package> _getInstalledPackages_UnSafe() => [];

        public override IReadOnlyList<string> FindCandidateExecutableFiles() => [];

        protected override void _loadManagerExecutableFile(
            out bool found,
            out string executablePath,
            out string callArgs
        )
        {
            found = false;
            executablePath = string.Empty;
            callArgs = string.Empty;
        }

        protected override void _loadManagerVersion(out string version) => version = "0.0.0";
    }

    private sealed class TestNuGetDetailsHelper : BaseNuGetDetailsHelper
    {
        public TestNuGetDetailsHelper(BaseNuGet manager)
            : base(manager) { }

        protected override string? GetInstallLocation_UnSafe(IPackage package) => null;

        public void LoadDetails(IPackageDetails details) => GetDetails_UnSafe(details);

        public CacheableIcon? LoadIcon(IPackage package) => GetIcon_UnSafe(package);

        public IReadOnlyList<string> LoadVersions(IPackage package) =>
            GetInstallableVersions_UnSafe(package);
    }

    private sealed class FakeV3Feed : IDisposable
    {
        public FakeV3Feed()
        {
            Server = new TestHttpServer(Handle);
        }

        public TestHttpServer Server { get; }

        public string BaseUri => Server.BaseUri.AbsoluteUri;

        public IReadOnlyList<string> Versions { get; init; } = DefaultVersions;

        public IReadOnlyList<string> UnlistedVersions { get; init; } = [];

        public int? SearchPageSize { get; init; }

        public int? SearchTotalPackages { get; init; }

        public IReadOnlyList<string>? DuplicateIdVersions { get; init; }

        public string? CatalogEntryBody { get; init; }

        public string? SearchBody { get; init; }

        public string? FlatContainerBody { get; init; }

        public string? RegistrationLeafBody { get; init; }

        public string? NuspecBody { get; init; }

        public string? NuspecPackageType { get; init; }

        public int FlatContainerStatusCode { get; init; } = 200;

        public int RegistrationLeafStatusCode { get; init; } = 200;

        public bool AdvertiseSearchService { get; init; } = true;

        public bool AdvertiseRegistrations { get; init; } = true;

        public bool AdvertisePackageBaseAddress { get; init; } = true;

        public bool UseArrayResourceTypes { get; init; }

        public bool InlineCatalogEntry { get; init; }

        public string? ServiceIndexBody { get; init; }

        public NuGetV3ServiceIndex? Resolve(bool clearCaches = true)
        {
            if (clearCaches)
            {
                NuGetV3ServiceIndex.ClearCache();
                NuGetV3Client.ClearCaches();
            }

            var source = new SourceBuilder().WithUrl($"{BaseUri}v3/index.json").Build();
            return NuGetV3ServiceIndex.Resolve(source);
        }

        public void Dispose() => Server.Dispose();

        private (int StatusCode, string Content, string ContentType) Handle(
            HttpListenerRequest request
        )
        {
            string path = request.Url?.AbsolutePath ?? string.Empty;

            if (string.Equals(request.HttpMethod, "HEAD", StringComparison.OrdinalIgnoreCase))
                return (200, string.Empty, "application/octet-stream");

            if (path.EndsWith("/v3/index.json", StringComparison.OrdinalIgnoreCase))
                return Json(ServiceIndexBody ?? BuildServiceIndex());

            if (path.EndsWith("/query", StringComparison.OrdinalIgnoreCase))
                return Json(SearchBody ?? BuildSearchResponse(request));

            if (path.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
                return (200, NuspecBody ?? RenderedNuspec, "text/xml");

            if (path.Contains("/flatcontainer/", StringComparison.OrdinalIgnoreCase))
            {
                if (FlatContainerStatusCode is not 200)
                    return (FlatContainerStatusCode, string.Empty, "application/json");

                return Json(FlatContainerBody ?? BuildFlatContainerIndex());
            }

            if (path.Contains("/registration-gz-semver2/", StringComparison.OrdinalIgnoreCase))
            {
                if (RegistrationLeafStatusCode is not 200)
                    return (RegistrationLeafStatusCode, string.Empty, "application/json");

                return Json(RegistrationLeafBody ?? BuildRegistrationLeaf(path));
            }

            if (path.Contains("/catalog/", StringComparison.OrdinalIgnoreCase))
                return Json(CatalogEntry);

            if (path.Contains("/api/v2", StringComparison.OrdinalIgnoreCase))
                return (200, "<entry><d:Id>Contoso.Tool</d:Id></entry>", "application/xml");

            return (404, string.Empty, "application/json");
        }

        private static (int StatusCode, string Content, string ContentType) Json(string content) =>
            (200, content, "application/json");

        private string BuildServiceIndex()
        {
            List<string> resources = [];

            if (AdvertisePackageBaseAddress)
                resources.Add(Resource($"{BaseUri}flatcontainer/", "PackageBaseAddress/3.0.0"));

            if (AdvertiseRegistrations)
            {
                resources.Add(
                    Resource($"{BaseUri}registration-gz-semver2/", "RegistrationsBaseUrl/3.6.0")
                );
                resources.Add(
                    Resource($"{BaseUri}registration-legacy/", "RegistrationsBaseUrl")
                );
            }

            if (AdvertiseSearchService)
            {
                resources.Insert(0, Resource($"{BaseUri}query", "SearchQueryService/3.5.0"));
                resources.Insert(
                    1,
                    Resource($"{BaseUri}legacy-query", "SearchQueryService/3.0.0-beta")
                );
            }

            return $"{{\"version\":\"3.0.0\",\"resources\":[{string.Join(",", resources)}]}}";
        }

        private string Resource(string id, string type)
        {
            string typeValue = UseArrayResourceTypes ? $"[\"{type}\"]" : $"\"{type}\"";
            return $"{{\"@id\":\"{id}\",\"@type\":{typeValue}}}";
        }

        private string BuildSearchResponse(HttpListenerRequest request)
        {
            if (DuplicateIdVersions is { Count: > 0 })
            {
                IEnumerable<string> duplicates = DuplicateIdVersions.Select(version =>
                    $$"""{"id":"Duplicated.Tool","version":"{{version}}","description":"A tool"}"""
                );
                return $$"""{"totalHits":{{DuplicateIdVersions.Count}},"data":[{{string.Join(",", duplicates)}}]}""";
            }

            if (SearchPageSize is { } pageSize || SearchTotalPackages is not null)
            {
                int total = SearchTotalPackages ?? 0;
                int size = SearchPageSize ?? total;
                int skip = int.TryParse(request.QueryString["skip"], out int parsedSkip)
                    ? parsedSkip
                    : 0;
                int take = int.TryParse(request.QueryString["take"], out int parsedTake)
                    ? parsedTake
                    : size;

                List<string> entries = [];
                for (int i = skip; i < Math.Min(total, skip + Math.Min(size, take)); i++)
                    entries.Add($$"""{"id":"Pkg.{{i}}","version":"1.0.0","description":"A tool"}""");

                return $$"""{"totalHits":{{total}},"data":[{{string.Join(",", entries)}}]}""";
            }

            return $$"""
            {
              "totalHits": 2,
              "data": [
                {
                  "id": "Contoso.Tool",
                  "version": "2.0.0",
                  "description": "A tool",
                  "authors": ["Contoso"],
                  "tags": ["cli", "tool"],
                  "iconUrl": "{{BaseUri}}icons/contoso.png",
                  "licenseUrl": "{{BaseUri}}licenses/contoso",
                  "projectUrl": "https://contoso.test/"
                },
                {
                  "id": "Fabrikam.Tool",
                  "version": "1.0.0",
                  "description": "Another tool",
                  "authors": "Fabrikam"
                }
              ]
            }
            """;
        }

        private string BuildFlatContainerIndex() =>
            $"{{\"versions\":[{string.Join(",", Versions.Select(version => $"\"{version}\""))}]}}";

        private string BuildRegistrationLeaf(string path)
        {
            string version = Path.GetFileNameWithoutExtension(path);
            bool listed = !UnlistedVersions.Any(unlisted =>
                NuGetV3Client.NormalizeVersion(unlisted).Equals(
                    version,
                    StringComparison.OrdinalIgnoreCase
                )
            );

            string catalogEntry = InlineCatalogEntry
                ? CatalogEntry
                : $"\"{BaseUri}catalog/contoso.tool.{version}.json\"";

            return $$"""
            {
              "catalogEntry": {{catalogEntry}},
              "packageContent": "{{BaseUri}}flatcontainer/contoso.tool/{{version}}/contoso.tool.{{version}}.nupkg",
              "published": "2026-01-02T03:04:05Z",
              "listed": {{(listed ? "true" : "false")}}
            }
            """;
        }

        private string CatalogEntry => CatalogEntryBody ?? DefaultCatalogEntry;

        private string RenderedNuspec =>
            Nuspec.Replace(
                "{{PACKAGE_TYPES}}",
                NuspecPackageType is null
                    ? string.Empty
                    : $"<packageTypes><packageType name=\"{NuspecPackageType}\" /></packageTypes>"
            );

        private const string Nuspec =
            """
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2012/06/nuspec.xsd">
              <metadata>
                <id>Contoso.Tool</id>
                <version>2.0.0</version>
                <authors>Contoso, Fabrikam</authors>
                <license type="expression">MIT</license>
                <licenseUrl>https://licenses.test/MIT</licenseUrl>
                <icon>icon.png</icon>
                <projectUrl>https://contoso.test/</projectUrl>
                <description>A tool from the nuspec</description>
                <releaseNotes>Notes</releaseNotes>
                <tags>cli tool</tags>
                {{PACKAGE_TYPES}}
                <dependencies>
                  <group targetFramework="net10.0">
                    <dependency id="Contoso.Core" version="[1.5.0, )" />
                  </group>
                  <group targetFramework="net9.0">
                    <dependency id="Fabrikam.Core" version="[2.0.0, )" />
                  </group>
                </dependencies>
              </metadata>
            </package>
            """;

        private const string DefaultCatalogEntry =
            """
            {
              "id": "Contoso.Tool",
              "version": "2.0.0",
              "description": "A tool",
              "authors": "Contoso, Fabrikam",
              "iconFile": "icon.png",
              "tags": ["cli", "tool"],
              "licenseExpression": "MIT",
              "licenseUrl": "https://licenses.test/MIT",
              "projectUrl": "https://contoso.test/",
              "releaseNotes": "Notes",
              "packageHash": "hash-value",
              "packageHashAlgorithm": "SHA512",
              "packageSize": 4096,
              "listed": true,
              "dependencyGroups": [
                {
                  "targetFramework": "net10.0",
                  "dependencies": [{ "id": "Contoso.Core", "range": "[1.5.0, )" }]
                }
              ]
            }
            """;
    }
}
