using UniGetUI.PackageEngine.Managers.CargoManager;
using UniGetUI.PackageEngine.Tests.Infrastructure.Helpers;

namespace UniGetUI.PackageEngine.Tests;

public sealed class CargoClientTests : IDisposable
{
    public void Dispose()
    {
        CratesIOClient.TEST_ApiUrlOverride = null;
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
}
