using System.Net;
using System.Text;
using UniGetUI.PackageEngine.Classes.Manager;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.ManagerClasses.Classes;
using UniGetUI.PackageEngine.ManagerClasses.Manager;
using UniGetUI.PackageEngine.Managers.PowerShellManager;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.Tests.Infrastructure.Helpers;

namespace UniGetUI.PackageEngine.Tests;

public sealed class NuGetV2UpdatesTests
{
    private const int FeedMaxUrlLength = 11000;

    [Fact]
    public void GetAvailableUpdatesReportsEveryModuleWhenTheInstalledListExceedsTheFeedUrlLimit()
    {
        using var feed = new V2UpdatesFeed();
        var manager = feed.CreateManager(InstalledModules(450, duplicatedVersions: 60));

        var updates = manager.GetAvailableUpdates();

        Assert.Equal(450, updates.Count);
        Assert.Equal(450, updates.Select(update => update.Id.ToLowerInvariant()).Distinct().Count());
        Assert.All(updates, update => Assert.Equal("99.0.0", update.NewVersionString));
        Assert.True(feed.Server.RequestPaths.Count > 1);
        Assert.All(feed.RequestedUrlLengths, length => Assert.True(length <= FeedMaxUrlLength));
    }

    [Fact]
    public void GetAvailableUpdatesKeepsTheOtherChunksWhenOneRequestFails()
    {
        using var feed = new V2UpdatesFeed { FailingPackageId = "Microsoft.Graph.Beta.Module000" };
        var manager = feed.CreateManager(InstalledModules(450, duplicatedVersions: 0));

        var updates = manager.GetAvailableUpdates();

        Assert.NotEmpty(updates);
        Assert.True(updates.Count < 450);
        Assert.DoesNotContain(updates, update => update.Id == "Microsoft.Graph.Beta.Module000");
        Assert.Contains(updates, update => update.Id == "Microsoft.Graph.Beta.Module449");
    }

    [Theory]
    [InlineData(false, "99.0.0")]
    [InlineData(true, "100.0.0-beta")]
    public void AFeedWhoseGetUpdatesOnlyAnswersForAllVersionsFallsBackToTheLatestVersionById(
        bool preRelease,
        string expected
    )
    {
        using var feed = new V2UpdatesFeed { IgnoresLatestOnlyGetUpdates = true };
        var manager = feed.CreateManager(InstalledModules(3, duplicatedVersions: 0));
        var source = manager.Properties.DefaultSource;

        var updates = manager.GetAvailableUpdatesFromSource(
            source,
            manager.GetInstalledPackages(),
            preRelease,
            manager.TaskLogger.CreateNew(LoggableTaskType.ListUpdates),
            out int errors
        );

        Assert.Equal(0, errors);
        Assert.Equal(3, updates.Count);
        Assert.All(updates, update => Assert.Equal(expected, update.NewVersionString));
        Assert.Equal(3, feed.Server.RequestPaths.Count(path => path.Contains("/FindPackagesById()")));
    }

    [Theory]
    [InlineData(false, false, false, "99.0.0", 3)]
    [InlineData(true, false, false, "100.0.0-beta", 3)]
    [InlineData(false, true, false, "99.0.0", 9)]
    [InlineData(true, true, false, "100.0.0-beta", 9)]
    [InlineData(false, false, true, "99.0.0", 6)]
    [InlineData(true, false, true, "100.0.0-beta", 6)]
    public void AnArtifactoryFeedIsLookedUpByIdBecauseItsGetUpdatesIgnoresBatches(
        bool preRelease,
        bool flagsNoLatestVersion,
        bool ignoresLatestFilter,
        string expected,
        int lookups
    )
    {
        using var feed = new V2UpdatesFeed
        {
            ServedByArtifactory = true,
            FlagsNoLatestVersion = flagsNoLatestVersion,
            IgnoresLatestFilter = ignoresLatestFilter,
            VersionPages = UpdateVersionPages,
            UnlistedVersions = ["101.0.0"],
        };
        var manager = feed.CreateManager(InstalledModules(3, duplicatedVersions: 0));
        var source = manager.Properties.DefaultSource;

        var updates = manager.GetAvailableUpdatesFromSource(
            source,
            manager.GetInstalledPackages(),
            preRelease,
            manager.TaskLogger.CreateNew(LoggableTaskType.ListUpdates),
            out int errors
        );

        Assert.Equal(0, errors);
        Assert.Equal(3, updates.Count);
        Assert.All(updates, update => Assert.Equal(expected, update.NewVersionString));
        Assert.Equal(1, feed.Server.RequestPaths.Count(path => path.Contains("/GetUpdates()")));
        Assert.Equal(lookups, feed.Server.RequestPaths.Count(path => path.Contains("/FindPackagesById()")));
        Assert.All(
            feed.Server.RequestPaths.Where(path => path.Contains("/FindPackagesById()")),
            path => Assert.True(path.Contains("filter=") ^ path.Contains("semVerLevel=2.0.0"), path)
        );
    }

    [Fact]
    public void AnArtifactoryFeedOffersNoUpdateWhenNothingNewerIsListed()
    {
        using var feed = new V2UpdatesFeed
        {
            ServedByArtifactory = true,
            FlagsNoLatestVersion = true,
            VersionPages = [["1.0.0", "2.25.0"], ["101.0.0"]],
            UnlistedVersions = ["101.0.0"],
        };
        var manager = feed.CreateManager(InstalledModules(3, duplicatedVersions: 0));

        Assert.Empty(manager.GetAvailableUpdates());
    }

    [Theory]
    [InlineData(V2EntryStyle.Decorated, false, "99.0.0")]
    [InlineData(V2EntryStyle.Decorated, true, "100.0.0-beta")]
    [InlineData(V2EntryStyle.LinkOnly, false, "99.0.0")]
    [InlineData(V2EntryStyle.LinkOnly, true, "100.0.0-beta")]
    [InlineData(V2EntryStyle.EmptyElements, false, "99.0.0")]
    public void EntriesWithAttributesOtherPrefixesOrOnlyAnIdLinkAreRead(
        V2EntryStyle style,
        bool preRelease,
        string expected
    )
    {
        using var feed = new V2UpdatesFeed
        {
            IgnoresLatestOnlyGetUpdates = true,
            EntryStyle = style,
            VersionPages = UpdateVersionPages,
            UnlistedVersions = ["101.0.0"],
        };
        var manager = feed.CreateManager(InstalledModules(3, duplicatedVersions: 0));
        var source = manager.Properties.DefaultSource;

        var updates = manager.GetAvailableUpdatesFromSource(
            source,
            manager.GetInstalledPackages(),
            preRelease,
            manager.TaskLogger.CreateNew(LoggableTaskType.ListUpdates),
            out int errors
        );

        Assert.Equal(0, errors);
        Assert.Equal(3, updates.Count);
        Assert.All(updates, update => Assert.Equal(expected, update.NewVersionString));
    }

    [Theory]
    [InlineData(false, "99.0.0")]
    [InlineData(true, "100.0.0-beta")]
    public void AFeedThatFlagsNoLatestVersionIsReadThroughItsFullVersionList(bool preRelease, string expected)
    {
        using var feed = new V2UpdatesFeed
        {
            IgnoresLatestOnlyGetUpdates = true,
            FlagsNoLatestVersion = true,
            VersionPages = UpdateVersionPages,
            UnlistedVersions = ["101.0.0"],
        };
        var manager = feed.CreateManager(InstalledModules(3, duplicatedVersions: 0));
        var source = manager.Properties.DefaultSource;

        var updates = manager.GetAvailableUpdatesFromSource(
            source,
            manager.GetInstalledPackages(),
            preRelease,
            manager.TaskLogger.CreateNew(LoggableTaskType.ListUpdates),
            out int errors
        );

        Assert.Equal(0, errors);
        Assert.Equal(3, updates.Count);
        Assert.All(updates, update => Assert.Equal(expected, update.NewVersionString));
    }

    [Fact]
    public void PackagesNoFeedVersionListsAreNamedInOneSummaryLine()
    {
        using var feed = new V2UpdatesFeed
        {
            ServedByArtifactory = true,
            FlagsNoLatestVersion = true,
            VersionPages = [[]],
        };
        var manager = feed.CreateManager(InstalledModules(1, duplicatedVersions: 0));
        var logger = manager.TaskLogger.CreateNew(LoggableTaskType.ListUpdates);

        Assert.Empty(
            manager.GetAvailableUpdatesFromSource(
                manager.Properties.DefaultSource,
                manager.GetInstalledPackages(),
                false,
                logger,
                out _
            )
        );
        logger.Close(0);
        Assert.Contains(
            logger.AsColoredString(true),
            line => line.Contains("lists no version of Microsoft.Graph.Beta.Module000")
                && line.Contains("credentials sent: no")
        );
    }

    [Fact]
    public void AVersionListWhoseNextLinkRepeatsAPageStopsThere()
    {
        using var feed = new V2UpdatesFeed
        {
            ServedByArtifactory = true,
            FlagsNoLatestVersion = true,
            NextLinkRepeatsPage = true,
            VersionPages = UpdateVersionPages,
        };
        var manager = feed.CreateManager(InstalledModules(1, duplicatedVersions: 0));

        var updates = manager.GetAvailableUpdatesFromSource(
            manager.Properties.DefaultSource,
            manager.GetInstalledPackages(),
            false,
            manager.TaskLogger.CreateNew(LoggableTaskType.ListUpdates),
            out int errors
        );

        Assert.Equal(0, errors);
        Assert.Equal("99.0.0", Assert.Single(updates).NewVersionString);
        Assert.Equal(3, feed.Server.RequestPaths.Count(path => path.Contains("/FindPackagesById()")));
    }

    [Fact]
    public void EntriesWhoseIdOrVersionCannotBeReadAreLoggedWithTheResponse()
    {
        using var feed = new V2UpdatesFeed
        {
            ServedByArtifactory = true,
            EntryStyle = V2EntryStyle.Unreadable,
            VersionPages = UpdateVersionPages,
        };
        var manager = feed.CreateManager(InstalledModules(1, duplicatedVersions: 0));
        var logger = manager.TaskLogger.CreateNew(LoggableTaskType.ListUpdates);

        Assert.Empty(
            manager.GetAvailableUpdatesFromSource(
                manager.Properties.DefaultSource,
                manager.GetInstalledPackages(),
                false,
                logger,
                out _
            )
        );
        logger.Close(0);
        Assert.Contains(
            logger.AsColoredString(true),
            line => line.Contains("whose id or version could not be read")
                && line.Contains("Microsoft.Graph.Beta.Module000")
        );
    }

    [Fact]
    public void ASourceKnownToBeArtifactorySkipsGetUpdatesOnLaterChecks()
    {
        using var feed = new V2UpdatesFeed { ServedByArtifactory = true, VersionPages = UpdateVersionPages };
        var manager = feed.CreateManager(InstalledModules(2, duplicatedVersions: 0));

        for (int check = 0; check < 2; check++)
        {
            var updates = manager.GetAvailableUpdatesFromSource(
                manager.Properties.DefaultSource,
                manager.GetInstalledPackages(),
                false,
                manager.TaskLogger.CreateNew(LoggableTaskType.ListUpdates),
                out _
            );
            Assert.Equal(2, updates.Count);
        }

        Assert.Equal(1, feed.Server.RequestPaths.Count(path => path.Contains("/GetUpdates()")));
    }

    [Theory]
    [InlineData(V2EntryStyle.Plain)]
    [InlineData(V2EntryStyle.Decorated)]
    [InlineData(V2EntryStyle.LinkOnly)]
    [InlineData(V2EntryStyle.EmptyElements)]
    public void V2SearchReadsEntriesInEveryShape(V2EntryStyle style)
    {
        using var feed = new V2UpdatesFeed { EntryStyle = style };
        var manager = feed.CreateManager(InstalledModules(1, duplicatedVersions: 0));

        var result = Assert.Single(manager.FindPackages("contoso"));

        Assert.Equal("Contoso.Tool", result.Id);
        Assert.Equal("1.2.0", result.VersionString);
    }

    [Fact]
    public void AnArtifactoryWhoseHeadersAreStrippedIsRecognisedByItsResponses()
    {
        using var feed = new V2UpdatesFeed
        {
            ServedByArtifactory = true,
            SendsArtifactoryHeaders = false,
            VersionPages = UpdateVersionPages,
        };
        var manager = feed.CreateManager(InstalledModules(3, duplicatedVersions: 0));

        var updates = manager.GetAvailableUpdatesFromSource(
            manager.Properties.DefaultSource,
            manager.GetInstalledPackages(),
            false,
            manager.TaskLogger.CreateNew(LoggableTaskType.ListUpdates),
            out int errors
        );

        Assert.Equal(0, errors);
        Assert.Equal(3, updates.Count);
        Assert.All(updates, update => Assert.Equal("99.0.0", update.NewVersionString));
    }

    [Fact]
    public void V2SearchKeepsTheVersionFromTheEntryLink()
    {
        using var feed = new V2UpdatesFeed { EntryStyle = V2EntryStyle.ElementDiffersFromLink };
        var manager = feed.CreateManager(InstalledModules(1, duplicatedVersions: 0));

        Assert.Equal("1.2.0", Assert.Single(manager.FindPackages("contoso")).VersionString);
    }

    [Fact]
    public void APackageWhoseLookupFailsDoesNotCostTheOthersTheirUpdates()
    {
        using var feed = new V2UpdatesFeed { ServedByArtifactory = true, VersionPages = UpdateVersionPages };
        var manager = feed.CreateManager([("Good.Module", "1.0.0"), ("Bad.Module", "6.6.6"), ("Other.Module", "2.0.0")]);
        manager.ThrowsOnVersion = "6.6.6";
        var logger = manager.TaskLogger.CreateNew(LoggableTaskType.ListUpdates);

        var updates = manager.GetAvailableUpdatesFromSource(
            manager.Properties.DefaultSource,
            manager.GetInstalledPackages(),
            false,
            logger,
            out int errors
        );

        Assert.Equal(1, errors);
        Assert.Equal(["Good.Module", "Other.Module"], updates.Select(update => update.Id).Order());
        logger.Close(errors);
        Assert.Contains(logger.AsColoredString(true), line => line.Contains("Failed to look up package Bad.Module"));
    }

    [Fact]
    public void AnUnreachableFeedStopsTheRemainingLookups()
    {
        var feed = new V2UpdatesFeed { ServedByArtifactory = true, VersionPages = UpdateVersionPages };
        var manager = feed.CreateManager(InstalledModules(60, duplicatedVersions: 0));
        Assert.Equal(
            60,
            manager
                .GetAvailableUpdatesFromSource(
                    manager.Properties.DefaultSource,
                    manager.GetInstalledPackages(),
                    false,
                    manager.TaskLogger.CreateNew(LoggableTaskType.ListUpdates),
                    out _
                )
                .Count
        );
        feed.Dispose();
        var logger = manager.TaskLogger.CreateNew(LoggableTaskType.ListUpdates);

        var updates = manager.GetAvailableUpdatesFromSource(
            manager.Properties.DefaultSource,
            manager.GetInstalledPackages(),
            false,
            logger,
            out int errors
        );

        Assert.Empty(updates);
        Assert.InRange(errors, 1, 60 - 40);
        logger.Close(errors);
        Assert.Contains(logger.AsColoredString(true), line => line.Contains("could not be reached"));
    }

    public enum V2EntryStyle
    {
        Plain,
        Decorated,
        LinkOnly,
        EmptyElements,
        ElementDiffersFromLink,
        Unreadable,
    }

    private static readonly string[][] UpdateVersionPages =
    [
        ["1.0.0", "99.0.0", "100.0.0-beta"],
        ["98.0.0", "101.0.0"],
    ];

    [Fact]
    public void AnUpToDateFeedCostsOneExtraRequestAndNoLookupById()
    {
        using var feed = new V2UpdatesFeed { UpToDate = true };
        var manager = feed.CreateManager(InstalledModules(3, duplicatedVersions: 0));

        var updates = manager.GetAvailableUpdates();

        Assert.Empty(updates);
        Assert.Equal(2, feed.Server.RequestPaths.Count(path => path.Contains("/GetUpdates()")));
        Assert.DoesNotContain(feed.Server.RequestPaths, path => path.Contains("/FindPackagesById()"));
    }

    [Fact]
    public void V2VersionListsFollowTheFeedsNextPageLinks()
    {
        using var feed = new V2UpdatesFeed();
        var manager = feed.CreateManager(InstalledModules(1, duplicatedVersions: 0));
        var package = manager.GetInstalledPackages()[0];

        Assert.Equal(["1.0.0", "1.1.0", "1.2.0", "2.0.0"], manager.DetailsHelper.GetVersions(package).Order());
    }

    [Fact]
    public void APackageTheMappingKeepsOffAFeedIsNeverSentToIt()
    {
        using var feed = new V2UpdatesFeed();
        var manager = feed.CreateManager(
            InstalledModules(3, duplicatedVersions: 0),
            canResolve: id => id != "Microsoft.Graph.Beta.Module001"
        );

        var updates = manager.GetAvailableUpdates();

        Assert.Equal(2, updates.Count);
        Assert.DoesNotContain(feed.Server.RequestPaths, path => path.Contains("Module001"));
    }

    [Fact]
    public void AFeedNoInstalledPackageMayUseIsNotQueried()
    {
        using var feed = new V2UpdatesFeed();
        var manager = feed.CreateManager(InstalledModules(3, duplicatedVersions: 0), canResolve: _ => false);

        Assert.Empty(manager.GetAvailableUpdates());
        Assert.Empty(feed.Server.RequestPaths);
    }

    [Theory]
    [InlineData("<link rel=\"next\" href=\"https://feed.test/api/v2/Search()?$skip=100&amp;q=a\" />", "https://feed.test/api/v2/Search()?$skip=100&q=a")]
    [InlineData("<link href=\"https://feed.test/api/v2/Search()?$skip=100\" rel=\"next\"/>", "https://feed.test/api/v2/Search()?$skip=100")]
    [InlineData("<link rel='next' href='Search()?$skip=100'></link>", "https://feed.test/api/v2/Search()?$skip=100")]
    [InlineData("<link rel=\"self\" href=\"https://feed.test/self\" /><link type=\"application/atom+xml\" rel=\"next\" href=\"/api/v2/Search()?$skip=100\" />", "https://feed.test/api/v2/Search()?$skip=100")]
    public void NextPageLinksAreReadWhateverTheirAttributeOrderOrForm(string feedXml, string expected)
    {
        var next = BaseNuGet.FindNextPageUrl(
            $"<feed>{feedXml}</feed>",
            new Uri("https://feed.test/api/v2/Search()?q=a")
        );

        Assert.Equal(expected, next?.AbsoluteUri);
    }

    [Fact]
    public void AFeedWithoutANextLinkHasNoNextPage()
    {
        Assert.Null(
            BaseNuGet.FindNextPageUrl(
                "<feed><link rel=\"self\" href=\"https://feed.test/self\" /></feed>",
                new Uri("https://feed.test/api/v2/Search()")
            )
        );
    }

    private static List<(string Id, string Version)> InstalledModules(int count, int duplicatedVersions)
    {
        var modules = new List<(string Id, string Version)>();
        for (int i = 0; i < count; i++)
        {
            string id = $"Microsoft.Graph.Beta.Module{i:000}";
            modules.Add((id, "2.25.0"));
            if (i < duplicatedVersions)
                modules.Add((id, "2.24.0"));
        }

        return modules;
    }

    private sealed class V2UpdatesFeed : IDisposable
    {
        private TestHttpServer? _server;
        private readonly List<int> _requestedUrlLengths = [];

        public TestHttpServer Server =>
            _server ??= new TestHttpServer(
                Handle,
                responseHeaders: ServedByArtifactory && SendsArtifactoryHeaders
                    ? new Dictionary<string, string> { ["X-JFrog-Version"] = "Artifactory/7.178.1 717801900" }
                    : null
            );

        public string? FailingPackageId { get; init; }

        public bool IgnoresLatestOnlyGetUpdates { get; init; }

        public bool UpToDate { get; init; }

        public bool ServedByArtifactory { get; init; }

        public bool FlagsNoLatestVersion { get; init; }

        public bool IgnoresLatestFilter { get; init; }

        public V2EntryStyle EntryStyle { get; init; }

        public bool NextLinkRepeatsPage { get; init; }

        public bool SendsArtifactoryHeaders { get; init; } = true;

        private string FeedOpen =>
            ServedByArtifactory ? "<feed xmlns:ns6=\"http://jfrog.com/xmlns/nuget/v2/cache\">" : "<feed>";

        public string[][] VersionPages { get; init; } = [["1.0.0", "1.1.0"], ["1.2.0", "2.0.0"]];

        public string[] UnlistedVersions { get; init; } = [];

        public IReadOnlyList<int> RequestedUrlLengths
        {
            get
            {
                lock (_requestedUrlLengths)
                {
                    return _requestedUrlLengths.ToArray();
                }
            }
        }

        public V2TestManager CreateManager(
            IReadOnlyList<(string Id, string Version)> installed,
            Func<string, bool>? canResolve = null
        )
        {
            var manager = new V2TestManager($"{Server.BaseUri.AbsoluteUri}api/v2/", installed, canResolve);
            manager.Initialize();
            Assert.True(manager.IsReady());
            return manager;
        }

        public void Dispose() => _server?.Dispose();

        private (int StatusCode, string Content, string ContentType) Handle(
            HttpListenerRequest request
        )
        {
            string rawUrl = request.Url?.AbsoluteUri ?? string.Empty;
            lock (_requestedUrlLengths)
            {
                _requestedUrlLengths.Add(rawUrl.Length);
            }

            string path = request.Url?.AbsolutePath ?? string.Empty;
            if (path.EndsWith("/FindPackagesById()"))
            {
                string id = (request.QueryString["id"] ?? string.Empty).Trim('\'');
                string? filter = request.QueryString["$filter"];
                if (filter is not null && !IgnoresLatestFilter)
                {
                    if (FlagsNoLatestVersion)
                        return (200, $"{FeedOpen}</feed>", "application/atom+xml");

                    string latest = filter == "IsAbsoluteLatestVersion" ? "100.0.0-beta" : "99.0.0";
                    return (200, $"{FeedOpen}{Entry(id, latest)}</feed>", "application/atom+xml");
                }

                int page = !NextLinkRepeatsPage && int.TryParse(request.QueryString["page"], out int requested)
                    ? requested
                    : 0;
                var body = new StringBuilder(FeedOpen);
                foreach (string version in VersionPages[page])
                    body.Append(Entry(id, version));
                if (page + 1 < VersionPages.Length)
                    body.Append($"<link rel=\"next\" href=\"{Server.BaseUri}api/v2/FindPackagesById()?id='{id}'&amp;semVerLevel=2.0.0&amp;page={page + 1}\" />");
                body.Append("</feed>");
                return (200, body.ToString(), "application/atom+xml");
            }

            if (path.EndsWith("/Search()"))
                return (200, $"{FeedOpen}{Entry("Contoso.Tool", "1.0.0")}{Entry("Contoso.Tool", "1.2.0")}</feed>", "application/atom+xml");

            if (!path.EndsWith("/GetUpdates()"))
                return (404, string.Empty, "application/xml");

            string[] ids = (request.QueryString["packageIds"] ?? string.Empty)
                .Trim('\'')
                .Split('|', StringSplitOptions.RemoveEmptyEntries);

            bool allVersions = request.QueryString["includeAllVersions"] == "true";
            if (
                UpToDate
                || (IgnoresLatestOnlyGetUpdates && !allVersions)
                || (ServedByArtifactory && ids.Length > 1)
            )
                return (200, $"{FeedOpen}</feed>", "application/atom+xml");

            if (rawUrl.Length > FeedMaxUrlLength)
                return (404, string.Empty, "application/xml");

            if (FailingPackageId is not null && ids.Contains(FailingPackageId))
                return (500, string.Empty, "application/xml");

            var feed = new StringBuilder(FeedOpen);
            foreach (string id in ids)
                feed.Append(Entry(id, "99.0.0"));
            feed.Append("</feed>");

            return (200, feed.ToString(), "application/atom+xml");
        }

        private string Entry(string id, string version)
        {
            string published = UnlistedVersions.Contains(version) ? "1900-01-01T00:00:00" : "2026-01-01T00:00:00";
            string link = $"<id>{Server.BaseUri}api/v2/Packages(Id='{id}',Version='{version}')</id>";
            return EntryStyle switch
            {
                V2EntryStyle.Decorated =>
                    $"<entry xml:base=\"{Server.BaseUri}api/v2/\" m:etag=\"W/&quot;1&quot;\">{link}"
                        + "<m:properties xmlns:ds=\"http://schemas.microsoft.com/ado/2007/08/dataservices\">"
                        + $"<ds:Id>{id}</ds:Id><ds:Version>{version}</ds:Version>"
                        + $"<ds:Published m:type=\"Edm.DateTime\">{published}</ds:Published></m:properties></entry>",
                V2EntryStyle.ElementDiffersFromLink =>
                    $"<entry>{link}<m:properties><d:Id>{id}</d:Id>"
                        + $"<d:Version>{(version.EndsWith(".0") ? version[..^2] : version)}</d:Version>"
                        + $"<d:Published m:type=\"Edm.DateTime\">{published}</d:Published></m:properties></entry>",
                V2EntryStyle.Unreadable => $"<entry><title type=\"text\">{id}</title></entry>",
                V2EntryStyle.EmptyElements =>
                    $"<entry>{link}<m:properties><d:Id></d:Id><d:Version m:null=\"true\"/>"
                        + $"<d:Published m:type=\"Edm.DateTime\">{published}</d:Published></m:properties></entry>",
                V2EntryStyle.LinkOnly =>
                    $"<entry>{link}<m:properties><d:Published m:type=\"Edm.DateTime\">{published}</d:Published></m:properties></entry>",
                _ =>
                    $"<entry>{link}<m:properties><d:Id>{id}</d:Id><d:Version>{version}</d:Version>"
                        + $"<d:Published m:type=\"Edm.DateTime\">{published}</d:Published></m:properties></entry>",
            };
        }
    }

    private sealed class V2TestManager : BaseNuGet
    {
        private readonly IReadOnlyList<(string Id, string Version)> _installed;

        private readonly Func<string, bool>? _canResolve;

        public V2TestManager(
            string sourceUrl,
            IReadOnlyList<(string Id, string Version)> installed,
            Func<string, bool>? canResolve = null
        )
        {
            _installed = installed;
            _canResolve = canResolve;

            Capabilities = new ManagerCapabilities
            {
                SupportsCustomVersions = true,
                SupportsCustomPackageIcons = true,
                CanListDependencies = true,
            };

            Properties = new ManagerProperties
            {
                Id = "test-nuget-v2",
                Name = "TestNuGetV2",
                DefaultSource = new ManagerSource(this, "test", new Uri(sourceUrl)),
            };

            DetailsHelper = new Helper(this);
        }

        public string? ThrowsOnVersion { get; set; }

        public override int? CompareVersions(string versionA, string versionB) =>
            ThrowsOnVersion is not null && (versionA == ThrowsOnVersion || versionB == ThrowsOnVersion)
                ? throw new InvalidOperationException("comparator failure")
                : base.CompareVersions(versionA, versionB);

        protected override IReadOnlyList<Package> _getInstalledPackages_UnSafe() =>
            _installed
                .Select(module => new Package(
                    module.Id,
                    module.Id,
                    module.Version,
                    Properties.DefaultSource,
                    this
                ))
                .ToList();

        public override IReadOnlyList<string> FindCandidateExecutableFiles() => [];

        protected override void _loadManagerExecutableFile(
            out bool found,
            out string executablePath,
            out string callArgs
        )
        {
            found = true;
            executablePath = "test-nuget-v2";
            callArgs = string.Empty;
        }

        protected override void _loadManagerVersion(out string version) => version = "0.0.0";

        protected override bool CanResolveFromSource(string packageId, IManagerSource source) =>
            _canResolve?.Invoke(packageId) ?? true;

        private sealed class Helper(BaseNuGet manager) : BaseNuGetDetailsHelper(manager)
        {
            protected override string? GetInstallLocation_UnSafe(IPackage package) => null;
        }
    }
}
