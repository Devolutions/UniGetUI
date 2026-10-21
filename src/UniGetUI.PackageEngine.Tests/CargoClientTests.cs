using UniGetUI.PackageEngine.Managers.CargoManager;
using UniGetUI.PackageEngine.Tests.Infrastructure.Helpers;

namespace UniGetUI.PackageEngine.Tests;

[Collection(nameof(OperationOrchestrationTestCollection))]
public sealed class CargoClientTests : IDisposable
{
    public void Dispose()
    {
        CratesIOClient.TEST_ApiUrlOverride = null;
        CratesIOClient.InvalidateCache();
    }

    [Fact]
    public void GetManifest_ReturnsManifestAndResolvedUri()
    {
        using var server = new TestHttpServer(request =>
        {
            return request.Url?.AbsolutePath switch
            {
                "/api/v1/crates/ripgrep" => (
                    200,
                    """
                    {
                      "crate": {
                        "max_stable_version": "14.1.0",
                        "max_version": "14.1.0",
                        "name": "ripgrep",
                        "newest_version": "14.1.0"
                      },
                      "versions": [
                        {
                          "checksum": "abc",
                          "dl_path": "/api/v1/crates/ripgrep/14.1.0/download",
                          "num": "14.1.0",
                          "yanked": false
                        }
                      ]
                    }
                    """,
                    "application/json"
                ),
                _ => (404, string.Empty, "application/json"),
            };
        });
        CratesIOClient.TEST_ApiUrlOverride = $"{server.BaseUri.AbsoluteUri.TrimEnd('/')}/api/v1";

        var (uri, manifest) = CratesIOClient.GetManifest("ripgrep");

        Assert.Equal($"{server.BaseUri.AbsoluteUri.TrimEnd('/')}/api/v1/crates/ripgrep", uri.ToString());
        Assert.Equal("ripgrep", manifest.crate.name);
        Assert.Equal("14.1.0", Assert.Single(manifest.versions).num);
    }

    [Fact]
    public void GetManifestVersion_ReturnsWrappedVersion()
    {
        using var server = new TestHttpServer(request =>
        {
            return request.Url?.AbsolutePath switch
            {
                "/api/v1/crates/ripgrep/14.1.0" => (
                    200,
                    """
                    {
                      "version": {
                        "checksum": "abc",
                        "dl_path": "/api/v1/crates/ripgrep/14.1.0/download",
                        "num": "14.1.0",
                        "yanked": false
                      }
                    }
                    """,
                    "application/json"
                ),
                _ => (404, string.Empty, "application/json"),
            };
        });
        CratesIOClient.TEST_ApiUrlOverride = $"{server.BaseUri.AbsoluteUri.TrimEnd('/')}/api/v1";

        var version = CratesIOClient.GetManifestVersion("ripgrep", "14.1.0");

        Assert.Equal("14.1.0", version.num);
    }

    [Fact]
    public void GetManifest_ThrowsWhenResponseContainsNullCrate()
    {
        using var server = new TestHttpServer(request =>
        {
            return request.Url?.AbsolutePath switch
            {
                "/api/v1/crates/ripgrep" => (200, """{"crate":null,"versions":[]}""", "application/json"),
                _ => (404, string.Empty, "application/json"),
            };
        });
        CratesIOClient.TEST_ApiUrlOverride = $"{server.BaseUri.AbsoluteUri.TrimEnd('/')}/api/v1";

        Assert.Throws<NullResponseException>(() => CratesIOClient.GetManifest("ripgrep"));
    }

    [Fact]
    public void SelectUpdateChoosesHighestStableNonYankedVersionRegardlessOfOrder()
    {
        var manifest = CargoTestData.Manifest(
            CargoTestData.VersionInfo("9.0.0", yanked: true),
            CargoTestData.VersionInfo("2.0.0-beta.1"),
            CargoTestData.VersionInfo("1.4.0"),
            CargoTestData.VersionInfo("1.2.4"),
            CargoTestData.VersionInfo("invalid"),
            CargoTestData.VersionInfo("1.3.0"));
        Assert.Equal("1.4.0", CratesIOClient.SelectUpdate(manifest, "1.2.3")?.num);
    }

    [Theory]
    [InlineData("1.2.3")]
    [InlineData("1.2.3+build.5")]
    [InlineData("2.0.0")]
    public void SelectUpdateNeverOffersEqualBuildMetadataOrDowngrades(string installed)
    {
        var manifest = CargoTestData.Manifest(
            CargoTestData.VersionInfo("1.2.2"),
            CargoTestData.VersionInfo("1.2.3+build.99"),
            CargoTestData.VersionInfo("3.0.0-alpha.1"),
            CargoTestData.VersionInfo("9.0.0", yanked: true));
        Assert.Null(CratesIOClient.SelectUpdate(manifest, installed));
        Assert.Null(CratesIOClient.SelectUpdate(CargoTestData.Manifest(), installed));
    }

    [Fact]
    public void SelectUpdatePromotesPrereleaseToStableAndRejectsInvalidInstalledVersion()
    {
        var manifest = CargoTestData.Manifest(CargoTestData.VersionInfo("1.2.3"));
        Assert.Equal("1.2.3", CratesIOClient.SelectUpdate(manifest, "1.2.3-rc.1")?.num);
        Assert.Contains("Invalid installed Cargo version",
            Assert.Throws<InvalidDataException>(() => CratesIOClient.SelectUpdate(manifest, "invalid")).Message);
    }

    [Fact]
    public void ManifestCacheAvoidsDuplicateRequestsAndManagerSuccessInvalidatesIt()
    {
        using var server = new TestHttpServer(_ => (200, """
            {"crate":{"name":"tool","max_stable_version":"1.2.3","max_version":"1.2.3","newest_version":"1.2.3"},
             "versions":[{"num":"1.2.3","checksum":"abc","dl_path":"/download","yanked":false}]}
            """, "application/json"));
        CratesIOClient.TEST_ApiUrlOverride = server.BaseUri.AbsoluteUri.TrimEnd('/') + "/api/v1";
        Assert.Equal("1.2.3", CratesIOClient.GetManifest("tool").Item2.versions[0].num);
        Assert.Equal("1.2.3", CratesIOClient.GetManifest("tool").Item2.versions[0].num);
        Assert.Equal(["/api/v1/crates/tool"], server.RequestPaths);
        var manager = new Cargo();
        var package = new Infrastructure.Builders.PackageBuilder().WithManager(manager).WithId("tool").Build();
        Assert.Equal(Enums.OperationVeredict.Success,
            manager.OperationHelper.GetResult(package, Enums.OperationType.Install, [], 0));
        Assert.Equal("1.2.3", CratesIOClient.GetManifest("tool").Item2.versions[0].num);
        Assert.Equal(["/api/v1/crates/tool", "/api/v1/crates/tool"], server.RequestPaths);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(503)]
    public async Task ApiErrorsPropagateAndAreNotCachedAsSuccessfulNoUpdateResults(int status)
    {
        using var server = new TestHttpServer(_ => (status, "unavailable", "text/plain"));
        CratesIOClient.TEST_ApiUrlOverride = server.BaseUri.AbsoluteUri.TrimEnd('/') + "/api/v1";
        for (int request = 0; request < 2; request++)
        {
            var error = await Assert.ThrowsAsync<HttpRequestException>(() => CratesIOClient.GetManifestAsync("tool", default));
            Assert.Equal((System.Net.HttpStatusCode)status, error.StatusCode);
        }
        Assert.Equal(["/api/v1/crates/tool", "/api/v1/crates/tool"], server.RequestPaths);
    }
}
