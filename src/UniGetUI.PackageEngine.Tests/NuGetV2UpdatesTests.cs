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

        public TestHttpServer Server => _server ??= new TestHttpServer(Handle);

        public string? FailingPackageId { get; init; }

        public bool IgnoresLatestOnlyGetUpdates { get; init; }

        public bool UpToDate { get; init; }

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
                if (filter is null)
                {
                    int page = int.TryParse(request.QueryString["$skip"], out int skip) ? skip / 2 : 0;
                    string[][] pages = [["1.0.0", "1.1.0"], ["1.2.0", "2.0.0"]];
                    var body = new StringBuilder("<feed>");
                    foreach (string version in pages[page])
                        body.Append($"<entry><id>{Server.BaseUri}api/v2/Packages(Id='{id}',Version='{version}')</id></entry>");
                    if (page + 1 < pages.Length)
                        body.Append($"<link rel=\"next\" href=\"{Server.BaseUri}api/v2/FindPackagesById()?id='{id}'&amp;$skip={(page + 1) * 2}\" />");
                    body.Append("</feed>");
                    return (200, body.ToString(), "application/atom+xml");
                }

                string latest = filter == "IsAbsoluteLatestVersion" ? "100.0.0-beta" : "99.0.0";
                return (200, $"<feed><entry><d:Id>{id}</d:Id><d:Version>{latest}</d:Version></entry></feed>", "application/atom+xml");
            }

            if (!path.EndsWith("/GetUpdates()"))
                return (404, string.Empty, "application/xml");

            bool allVersions = request.QueryString["includeAllVersions"] == "true";
            if (UpToDate || (IgnoresLatestOnlyGetUpdates && !allVersions))
                return (200, "<feed></feed>", "application/atom+xml");

            if (rawUrl.Length > FeedMaxUrlLength)
                return (404, string.Empty, "application/xml");

            string[] ids = (request.QueryString["packageIds"] ?? string.Empty)
                .Trim('\'')
                .Split('|', StringSplitOptions.RemoveEmptyEntries);

            if (FailingPackageId is not null && ids.Contains(FailingPackageId))
                return (500, string.Empty, "application/xml");

            var feed = new StringBuilder("<feed>");
            foreach (string id in ids)
                feed.Append($"<entry><d:Id>{id}</d:Id><d:Version>99.0.0</d:Version></entry>");
            feed.Append("</feed>");

            return (200, feed.ToString(), "application/atom+xml");
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
