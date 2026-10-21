using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Managers.CargoManager;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.Tests.Infrastructure.Builders;
using UniGetUI.PackageEngine.Tests.Infrastructure.Helpers;

namespace UniGetUI.PackageEngine.Tests;

[Collection(nameof(OperationOrchestrationTestCollection))]
public sealed class CargoDetailsProvenanceTests : IDisposable
{
    private const string Id = "collision-tool";
    private const string Checksum = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string ManifestPath = "/api/v1/crates/" + Id;
    private readonly CargoTestSandbox _sandbox = new();
    private readonly Cargo _manager = new FixtureCargo();
    private readonly TestHttpServer _server = new(_ => (200, """
        {
          "crate": {
            "name": "collision-tool",
            "description": "Public crates.io package, unrelated to the same-id foreign package.",
            "homepage": "https://publisher.test/collision-tool",
            "keywords": ["cli", "rust"],
            "max_stable_version": "2.1.0",
            "max_version": "2.1.0",
            "newest_version": "2.1.0"
          },
          "categories": [
            {"category": "Command-line utilities", "description": "CLI tools", "id": "command-line-utilities"}
          ],
          "versions": [
            {
              "num": "2.1.0", "checksum": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "dl_path": "/api/v1/crates/collision-tool/2.1.0/download", "yanked": false
            },
            {
              "num": "1.2.3",
              "checksum": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
              "dl_path": "/api/v1/crates/collision-tool/1.2.3/download",
              "crate_size": 4096, "license": "MIT", "published_by": {"name": "Fixture Publisher"},
              "updated_at": "2026-10-20T12:00:00Z", "yanked": false
            },
            {
              "num": "1.0.0", "checksum": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
              "dl_path": "/api/v1/crates/collision-tool/1.0.0/download", "yanked": false
            }
          ]
        }
        """, "application/json"));

    public CargoDetailsProvenanceTests()
    {
        CratesIOClient.InvalidateCache();
        CratesIOClient.TEST_ApiUrlOverride = _server.BaseUri.AbsoluteUri.TrimEnd('/') + "/api/v1";
        _manager.Initialize();
        // The public wrappers return early for an unready manager. Prove these tests
        // reach Cargo's provider rather than passing because initialization was skipped.
        Assert.True(_manager.IsReady());
    }

    public void Dispose()
    {
        CratesIOClient.TEST_ApiUrlOverride = null;
        CratesIOClient.InvalidateCache();
        _server.Dispose();
        _sandbox.Dispose();
    }

    [Theory]
    [InlineData("git")]
    [InlineData("path")]
    [InlineData("private-registry")]
    public void ForeignSourcesCannotReceiveCratesIoDetailsOrSourceDownloadMetadata(string sourceKind)
    {
        var foreign = new PackageDetails(Package(ForeignSource(sourceKind)));
        _manager.DetailsHelper.GetDetails(foreign);

        AssertNoCratesIoMetadata(foreign);
        Assert.Empty(_server.RequestPaths);

        // The same identifier really exists in the fixture registry. Warming its
        // cache must not allow the foreign package to inherit its download metadata.
        var registry = new PackageDetails(Package());
        _manager.DetailsHelper.GetDetails(registry);
        Assert.Equal("1.2.3", registry.Version);
        Assert.Equal(Checksum, registry.InstallerHash);
        Assert.Equal([ManifestPath], _server.RequestPaths);

        _manager.DetailsHelper.GetDetails(foreign);
        AssertNoCratesIoMetadata(foreign);
        Assert.Equal([ManifestPath], _server.RequestPaths);
    }

    [Theory]
    [InlineData("git")]
    [InlineData("path")]
    [InlineData("private-registry")]
    public void ForeignSourcesCannotReceiveCratesIoVersionCatalogs(string sourceKind)
    {
        var foreign = Package(ForeignSource(sourceKind));
        Assert.Empty(_manager.DetailsHelper.GetVersions(foreign));
        Assert.Empty(_server.RequestPaths);

        Assert.Equal(["2.1.0", "1.2.3", "1.0.0"], _manager.DetailsHelper.GetVersions(Package()));
        Assert.Equal([ManifestPath], _server.RequestPaths);

        Assert.Empty(_manager.DetailsHelper.GetVersions(foreign));
        Assert.Equal([ManifestPath], _server.RequestPaths);
    }

    [Fact]
    public void CratesIoDetailsUseSelectedVersionSourceUrlAndChecksum()
    {
        var details = new PackageDetails(Package());
        _manager.DetailsHelper.GetDetails(details);

        Assert.Equal("1.2.3", details.Version);
        Assert.Equal("Source", details.InstallerType);
        Assert.Equal("https://crates.io/api/v1/crates/collision-tool/1.2.3/download",
            details.InstallerUrl?.AbsoluteUri);
        Assert.Equal(Checksum, details.InstallerHash);
        Assert.Equal(4096, details.InstallerSize);
        Assert.Equal(new Uri(_server.BaseUri, ManifestPath), details.ManifestUrl);
        Assert.Equal("Public crates.io package, unrelated to the same-id foreign package.", details.Description);
        Assert.Equal("Fixture Publisher", details.Author);
        Assert.Equal("Fixture Publisher", details.Publisher);
        Assert.Equal("MIT", details.License);
        Assert.Equal("https://publisher.test/collision-tool", details.HomepageUrl?.AbsoluteUri);
        Assert.Equal("2026-10-20T12:00:00Z", details.UpdateDate);
        Assert.Equal(["cli", "rust", "Command-line utilities"], details.Tags);
        Assert.Equal([ManifestPath], _server.RequestPaths);
    }

    [Fact]
    public void CratesIoVersionCatalogReturnsManifestVersions()
    {
        Assert.Equal(["2.1.0", "1.2.3", "1.0.0"], _manager.DetailsHelper.GetVersions(Package()));
        Assert.Equal([ManifestPath], _server.RequestPaths);
    }

    private Package Package(IManagerSource? source = null) =>
        new PackageBuilder().WithManager(_manager).WithId(Id).WithVersion("1.2.3")
            .WithSource(source ?? _manager.DefaultSource).Build();

    private IManagerSource ForeignSource(string sourceKind)
    {
        string url = sourceKind switch
        {
            "git" => "https://github.com/private-owner/collision-tool#deadbeef",
            "path" => new Uri(_sandbox.PathFor("local-collision-tool")).AbsoluteUri,
            _ => "https://private.test/index",
        };
        string name = sourceKind switch
        {
            "git" => "git+" + url,
            "path" => _sandbox.PathFor("local-collision-tool"),
            _ => "registry+" + url,
        };
        return new SourceBuilder().WithManager(_manager).WithName(name).WithUrl(url).Build();
    }

    private static void AssertNoCratesIoMetadata(PackageDetails details)
    {
        Assert.Null(details.Version);
        Assert.Null(details.Description);
        Assert.Null(details.ManifestUrl);
        Assert.Null(details.InstallerType);
        Assert.Null(details.InstallerUrl);
        Assert.Null(details.InstallerHash);
        Assert.Equal(0, details.InstallerSize);
        Assert.Empty(details.Tags);
    }

    // Keep the actual Cargo details provider and public readiness checks, but never
    // launch a process or depend on a locally installed Cargo executable.
    private sealed class FixtureCargo : Cargo
    {
        protected override void _loadManagerExecutableFile(out bool found, out string path, out string callArguments)
        {
            found = true;
            path = "cargo-not-launched-by-details-tests";
            callArguments = "";
        }

        protected override void _loadManagerVersion(out string version) => version = "1.99.0-fixture";
    }
}
