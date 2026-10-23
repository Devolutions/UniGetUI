using System.Net;
using System.Text;
using UniGetUI.PackageEngine.Classes.Manager;
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

        public V2TestManager CreateManager(IReadOnlyList<(string Id, string Version)> installed)
        {
            var manager = new V2TestManager($"{Server.BaseUri.AbsoluteUri}api/v2/", installed);
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

            if (!(request.Url?.AbsolutePath ?? string.Empty).EndsWith("/GetUpdates()"))
                return (404, string.Empty, "application/xml");

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

        public V2TestManager(string sourceUrl, IReadOnlyList<(string Id, string Version)> installed)
        {
            _installed = installed;

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

        private sealed class Helper(BaseNuGet manager) : BaseNuGetDetailsHelper(manager)
        {
            protected override string? GetInstallLocation_UnSafe(IPackage package) => null;
        }
    }
}
