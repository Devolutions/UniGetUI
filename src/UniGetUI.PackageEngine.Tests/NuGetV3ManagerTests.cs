using System.Net;
using UniGetUI.PackageEngine.Classes.Manager;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.ManagerClasses.Classes;
using UniGetUI.PackageEngine.ManagerClasses.Manager;
using UniGetUI.PackageEngine.Managers.Generic.NuGet.Internal;
using UniGetUI.PackageEngine.Managers.PowerShellManager;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.Structs;
using UniGetUI.PackageEngine.Tests.Infrastructure.Helpers;

namespace UniGetUI.PackageEngine.Tests;

public sealed class NuGetV3ManagerTests
{
    [Fact]
    public void FindPackagesV3AssociatesTheSourceAndSeedsTheIconCache()
    {
        using var feed = new UpdateFeed();
        var manager = feed.CreateManager();
        var source = manager.Properties.DefaultSource;

        var packages = manager.FindPackagesV3(source, "contoso", false, feed.Logger(manager));

        Assert.Equal(2, packages.Count);
        Assert.All(packages, package => Assert.Same(source, package.Source));
        Assert.All(packages, package => Assert.Same(manager, package.Manager));

        Assert.Equal("Contoso.Tool", packages[0].Id);
        Assert.Equal("2.0.0", packages[0].VersionString);
        Assert.Equal(
            $"{feed.BaseUri}icons/contoso.png",
            BaseNuGet.V3IconUrls[packages[0].GetVersionedHash()]
        );

        Assert.Equal("Fabrikam.Tool", packages[1].Id);
        Assert.Equal(string.Empty, BaseNuGet.V3IconUrls[packages[1].GetVersionedHash()]);
    }

    [Fact]
    public void FindPackagesV3ReportsAnUnresolvableServiceIndex()
    {
        using var feed = new UpdateFeed { ServiceIndexStatusCode = 500 };
        var manager = feed.CreateManager();

        var packages = manager.FindPackagesV3(
            manager.Properties.DefaultSource,
            "contoso",
            false,
            feed.Logger(manager)
        );

        Assert.Empty(packages);
    }

    [Fact]
    public void GetAvailableUpdatesV3CarriesTheInstalledScopeOntoTheUpdate()
    {
        using var feed = new UpdateFeed { Versions = ["2025.1.0", "2025.2.0"] };
        var manager = feed.CreateManager();
        var source = manager.Properties.DefaultSource;

        var installed = new PackageBuilderFor(manager, source)
            .Build("Devolutions.PowerShell", "2025.1.0", PackageScope.Machine);

        var update = Assert.Single(
            manager.GetAvailableUpdatesV3(source, [installed], false, feed.Logger(manager))!
        );

        Assert.Equal("Devolutions.PowerShell", update.Id);
        Assert.Equal("2025.1.0", update.VersionString);
        Assert.Equal("2025.2.0", update.NewVersionString);
        Assert.Equal(PackageScope.Machine, update.OverridenOptions.Scope);
        Assert.Same(source, update.Source);
    }

    [Fact]
    public void GetAvailableUpdatesV3KeepsACurrentUserModuleOnCurrentUser()
    {
        using var feed = new UpdateFeed { Versions = ["2025.1.0", "2025.2.0"] };
        var manager = feed.CreateManager();
        var source = manager.Properties.DefaultSource;

        var installed = new PackageBuilderFor(manager, source)
            .Build("Devolutions.PowerShell", "2025.1.0", PackageScope.User);

        var update = Assert.Single(
            manager.GetAvailableUpdatesV3(source, [installed], false, feed.Logger(manager))!
        );

        Assert.Equal(PackageScope.User, update.OverridenOptions.Scope);
    }

    [Fact]
    public void GetAvailableUpdatesV3ResolvesAModuleInstalledTwiceToTheLastEnumeratedScope()
    {
        using var feed = new UpdateFeed { Versions = ["2025.1.0", "2025.2.0", "2025.3.0"] };
        var manager = feed.CreateManager();
        var source = manager.Properties.DefaultSource;
        var builder = new PackageBuilderFor(manager, source);

        var update = Assert.Single(
            manager.GetAvailableUpdatesV3(
                source,
                [
                    builder.Build("Devolutions.PowerShell", "2025.1.0", PackageScope.Machine),
                    builder.Build("Devolutions.PowerShell", "2025.2.0", PackageScope.User),
                ],
                false,
                feed.Logger(manager)
            )!
        );

        Assert.Equal("2025.2.0", update.VersionString);
        Assert.Equal("2025.3.0", update.NewVersionString);
        Assert.Equal(PackageScope.User, update.OverridenOptions.Scope);
    }

    [Fact]
    public void GetAvailableUpdatesV3ReturnsNothingWhenEveryPackageIsCurrent()
    {
        using var feed = new UpdateFeed { Versions = ["1.0.0", "2.0.0"] };
        var manager = feed.CreateManager();
        var source = manager.Properties.DefaultSource;
        var builder = new PackageBuilderFor(manager, source);

        var updates = manager.GetAvailableUpdatesV3(
            source,
            [builder.Build("Contoso.Tool", "2.0.0"), builder.Build("Fabrikam.Tool", "2.0.0")],
            false,
            feed.Logger(manager)
        );

        Assert.NotNull(updates);
        Assert.Empty(updates);
    }

    [Fact]
    public void GetAvailableUpdatesV3ReturnsNullWhenTheServiceIndexCannotBeResolved()
    {
        using var feed = new UpdateFeed { ServiceIndexStatusCode = 500 };
        var manager = feed.CreateManager();
        var source = manager.Properties.DefaultSource;

        Assert.Null(
            manager.GetAvailableUpdatesV3(
                source,
                [new PackageBuilderFor(manager, source).Build("Contoso.Tool", "1.0.0")],
                false,
                feed.Logger(manager)
            )
        );
    }

    [Fact]
    public void GetAvailableUpdatesV3KeepsOtherPackagesWhenOneFeedLookupFails()
    {
        using var feed = new UpdateFeed
        {
            Versions = ["1.0.0", "2.0.0"],
            UnservedPackageIds = ["Broken.Tool"],
        };
        var manager = feed.CreateManager();
        var source = manager.Properties.DefaultSource;
        var builder = new PackageBuilderFor(manager, source);

        var updates = manager.GetAvailableUpdatesV3(
            source,
            [builder.Build("Broken.Tool", "1.0.0"), builder.Build("Contoso.Tool", "1.0.0")],
            false,
            feed.Logger(manager)
        );

        Assert.NotNull(updates);
        var update = Assert.Single(updates);
        Assert.Equal("Contoso.Tool", update.Id);
        Assert.Equal("2.0.0", update.NewVersionString);
    }

    [Fact]
    public void GetAvailableUpdatesV3HonoursThePreReleasePreference()
    {
        using var feed = new UpdateFeed { Versions = ["1.0.0", "2.0.0-beta.1"] };
        var manager = feed.CreateManager();
        var source = manager.Properties.DefaultSource;
        var installed = new PackageBuilderFor(manager, source).Build("Contoso.Tool", "1.0.0");

        Assert.Empty(
            manager.GetAvailableUpdatesV3(source, [installed], false, feed.Logger(manager))!
        );

        NuGetV3Client.ClearCaches();
        var update = Assert.Single(
            manager.GetAvailableUpdatesV3(source, [installed], true, feed.Logger(manager))!
        );
        Assert.Equal("2.0.0-beta.1", update.NewVersionString);
    }

    // The upper bound is the regression guard: without MaxDegreeOfParallelism a user with many
    // installed tools would fan out unbounded against the feed. The lower bound proves the test
    // is not vacuous, and is reached through a handshake rather than a sleep - the fixture holds
    // each request until two are genuinely in flight - so the assertion does not depend on
    // timing under load.
    [Fact]
    public void GetAvailableUpdatesV3BoundsConcurrentFeedRequests()
    {
        using var feed = new UpdateFeed
        {
            Versions = ["1.0.0", "2.0.0"],
            HandleConcurrently = true,
            HandshakeTarget = 2,
        };
        var manager = feed.CreateManager();
        var source = manager.Properties.DefaultSource;
        var builder = new PackageBuilderFor(manager, source);

        List<IPackage> installed = [];
        for (int i = 0; i < NuGetV3Client.MaxConcurrentRequests * 4; i++)
            installed.Add(builder.Build($"Contoso.Tool.{i}", "1.0.0"));

        var updates = manager.GetAvailableUpdatesV3(
            source,
            installed,
            false,
            feed.Logger(manager)
        );

        Assert.NotNull(updates);
        Assert.Equal(installed.Count, updates.Count);
        Assert.InRange(
            feed.Server.PeakConcurrentRequests,
            feed.HandshakeTarget,
            NuGetV3Client.MaxConcurrentRequests
        );
    }

    // Issue #5331: the max-installed filter read the map with the original-cased id but wrote
    // it lowercased, so the lookup always missed and the map degraded to last-wins instead of
    // max-wins. Any id that is not already lowercase - i.e. most NuGet and PowerShell ids -
    // could therefore be offered an update already satisfied by a copy in another scope.
    [Fact]
    public void KeepUpdatesNewerThanInstalledUsesTheHighestInstalledCopyForAMixedCaseId()
    {
        using var feed = new UpdateFeed();
        var manager = feed.CreateManager();
        var source = manager.Properties.DefaultSource;
        var builder = new PackageBuilderFor(manager, source);

        IReadOnlyList<IPackage> installed =
        [
            builder.Build("Devolutions.PowerShell", "2025.2.0", PackageScope.Machine),
            builder.Build("Devolutions.PowerShell", "2025.1.0", PackageScope.User),
        ];

        var alreadySatisfied = builder.BuildUpdate(
            "Devolutions.PowerShell",
            "2025.1.0",
            "2025.1.5"
        );
        Assert.Empty(manager.KeepUpdatesNewerThanInstalled([alreadySatisfied], installed));

        var genuine = builder.BuildUpdate("Devolutions.PowerShell", "2025.1.0", "2025.3.0");
        Assert.Single(manager.KeepUpdatesNewerThanInstalled([genuine], installed));
    }

    [Fact]
    public void KeepUpdatesNewerThanInstalledStillWorksForALowercaseId()
    {
        using var feed = new UpdateFeed();
        var manager = feed.CreateManager();
        var source = manager.Properties.DefaultSource;
        var builder = new PackageBuilderFor(manager, source);

        IReadOnlyList<IPackage> installed =
        [
            builder.Build("dotnet-ef", "10.0.0"),
            builder.Build("dotnet-ef", "8.0.0"),
        ];

        Assert.Empty(
            manager.KeepUpdatesNewerThanInstalled(
                [builder.BuildUpdate("dotnet-ef", "8.0.0", "9.0.0")],
                installed
            )
        );
        Assert.Single(
            manager.KeepUpdatesNewerThanInstalled(
                [builder.BuildUpdate("dotnet-ef", "8.0.0", "11.0.0")],
                installed
            )
        );
    }

    [Fact]
    public void KeepUpdatesNewerThanInstalledOffersTheStableReleaseOverAnInstalledPreRelease()
    {
        using var feed = new UpdateFeed();
        var manager = feed.CreateManager();
        var source = manager.Properties.DefaultSource;
        var builder = new PackageBuilderFor(manager, source);

        IReadOnlyList<IPackage> installed = [builder.Build("Contoso.Tool", "2.0.0-rc1")];

        Assert.Single(
            manager.KeepUpdatesNewerThanInstalled(
                [builder.BuildUpdate("Contoso.Tool", "2.0.0-rc1", "2.0.0")],
                installed
            )
        );
        Assert.Empty(
            manager.KeepUpdatesNewerThanInstalled(
                [builder.BuildUpdate("Contoso.Tool", "2.0.0-rc1", "2.0.0-beta.9")],
                installed
            )
        );
    }

    [Fact]
    public void KeepUpdatesNewerThanInstalledKeepsUpdatesForUnknownIds()
    {
        using var feed = new UpdateFeed();
        var manager = feed.CreateManager();
        var source = manager.Properties.DefaultSource;
        var builder = new PackageBuilderFor(manager, source);

        Assert.Single(
            manager.KeepUpdatesNewerThanInstalled(
                [builder.BuildUpdate("Not.Installed", "1.0.0", "2.0.0")],
                []
            )
        );
    }

    private sealed class PackageBuilderFor(BaseNuGet manager, IManagerSource source)
    {
        public Package Build(string id, string version, string? scope = null)
        {
            return new Package(
                id,
                id,
                version,
                source,
                manager,
                scope is null ? null : new OverridenInstallationOptions(scope)
            );
        }

        public Package BuildUpdate(string id, string installedVersion, string newVersion)
        {
            return new Package(id, id, installedVersion, newVersion, source, manager);
        }
    }

    private sealed class UpdateFeed : IDisposable
    {
        private TestHttpServer? _server;

        public TestHttpServer Server => _server ??= new TestHttpServer(Handle, HandleConcurrently);

        public string BaseUri => Server.BaseUri.AbsoluteUri;

        public IReadOnlyList<string> Versions { get; init; } = ["1.0.0", "2.0.0"];

        public IReadOnlyList<string> UnservedPackageIds { get; init; } = [];

        public int ServiceIndexStatusCode { get; init; } = 200;

        public bool HandleConcurrently { get; init; }

        public int HandshakeTarget { get; init; }

        private readonly ManualResetEventSlim _handshakeRelease = new(false);
        private int _handshakeInFlight;

        public TestNuGetManager CreateManager()
        {
            NuGetV3ServiceIndex.ClearCache();
            NuGetV3Client.ClearCaches();
            BaseNuGet.V3Entries.Clear();
            BaseNuGet.V3IconUrls.Clear();
            return new TestNuGetManager($"{BaseUri}v3/index.json");
        }

        public INativeTaskLogger Logger(BaseNuGet manager) =>
            manager.TaskLogger.CreateNew(LoggableTaskType.ListUpdates);

        public void Dispose()
        {
            _server?.Dispose();
            _handshakeRelease.Dispose();
        }

        // Blocks until HandshakeTarget requests are simultaneously in flight, so overlap is
        // established by agreement instead of by racing a timer. The timeout releases everyone
        // if the client turns out to be serialised, keeping a failing run fast.
        private void AwaitHandshake()
        {
            if (HandshakeTarget <= 0)
                return;

            if (Interlocked.Increment(ref _handshakeInFlight) >= HandshakeTarget)
                _handshakeRelease.Set();

            if (!_handshakeRelease.Wait(TimeSpan.FromSeconds(10)))
                _handshakeRelease.Set();

            Interlocked.Decrement(ref _handshakeInFlight);
        }

        private (int StatusCode, string Content, string ContentType) Handle(
            HttpListenerRequest request
        )
        {
            string path = request.Url?.AbsolutePath ?? string.Empty;

            if (path.EndsWith("/v3/index.json", StringComparison.OrdinalIgnoreCase))
            {
                return ServiceIndexStatusCode is 200
                    ? (200, ServiceIndex, "application/json")
                    : (ServiceIndexStatusCode, string.Empty, "application/json");
            }

            if (path.EndsWith("/query", StringComparison.OrdinalIgnoreCase))
                return (200, SearchResponse, "application/json");

            if (path.Contains("/flatcontainer/", StringComparison.OrdinalIgnoreCase))
            {
                AwaitHandshake();

                if (
                    UnservedPackageIds.Any(id =>
                        path.Contains(
                            $"/{id.ToLowerInvariant()}/",
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                )
                    return (500, string.Empty, "application/json");

                return (200, FlatContainerIndex, "application/json");
            }

            if (path.Contains("/registration/", StringComparison.OrdinalIgnoreCase))
                return (200, RegistrationLeaf, "application/json");

            return (404, string.Empty, "application/json");
        }

        private string ServiceIndex =>
            $$"""
            {
              "version": "3.0.0",
              "resources": [
                { "@id": "{{BaseUri}}query", "@type": "SearchQueryService/3.5.0" },
                { "@id": "{{BaseUri}}registration/", "@type": "RegistrationsBaseUrl/3.6.0" },
                { "@id": "{{BaseUri}}flatcontainer/", "@type": "PackageBaseAddress/3.0.0" }
              ]
            }
            """;

        private string SearchResponse =>
            $$"""
            {
              "totalHits": 2,
              "data": [
                {
                  "id": "Contoso.Tool",
                  "version": "2.0.0",
                  "iconUrl": "{{BaseUri}}icons/contoso.png"
                },
                { "id": "Fabrikam.Tool", "version": "1.0.0" }
              ]
            }
            """;

        private string FlatContainerIndex =>
            $"{{\"versions\":[{string.Join(",", Versions.Select(version => $"\"{version}\""))}]}}";

        private const string RegistrationLeaf =
            """
            {"listed":true,"published":"2026-01-02T03:04:05Z"}
            """;
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

            DetailsHelper = new Helper(this);
        }

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

        private sealed class Helper(BaseNuGet manager) : BaseNuGetDetailsHelper(manager)
        {
            protected override string? GetInstallLocation_UnSafe(IPackage package) => null;
        }
    }
}
