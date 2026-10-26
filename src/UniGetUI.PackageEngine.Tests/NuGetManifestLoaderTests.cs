using UniGetUI.PackageEngine.Managers.Generic.NuGet;
using UniGetUI.PackageEngine.Managers.Generic.NuGet.Internal;
using UniGetUI.PackageEngine.Managers.PowerShellManager;
using UniGetUI.PackageEngine.Tests.Infrastructure.Builders;
using UniGetUI.PackageEngine.Tests.Infrastructure.Helpers;

namespace UniGetUI.PackageEngine.Tests;

public sealed class NuGetManifestLoaderTests
{
    [Fact]
    public void GetManifestUrlAndNuPkgUrl_UsePackageSourceAndVersion()
    {
        var manager = new PackageManagerBuilder().Build();
        var source = new SourceBuilder()
            .WithManager(manager)
            .WithUrl("https://packages.example.test/feed")
            .Build();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithSource(source)
            .WithId("Contoso.Tool")
            .WithVersion("1.2.3")
            .Build();

        Assert.Equal(
            "https://packages.example.test/feed/Packages(Id='Contoso.Tool',Version='1.2.3')",
            NuGetManifestLoader.GetManifestUrl(package).ToString()
        );
        Assert.Equal(
            "https://packages.example.test/feed/package/Contoso.Tool/1.2.3",
            NuGetManifestLoader.GetNuPkgUrl(package).ToString()
        );
    }

    [Fact]
    public void GetManifestUrl_WithoutAVersionAsksForTheLatestOne()
    {
        var manager = new PackageManagerBuilder().Build();
        var source = new SourceBuilder()
            .WithManager(manager)
            .WithUrl("https://www.powershellgallery.com/api/v2")
            .Build();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithSource(source)
            .WithId("Devolutions.PowerShell")
            .WithVersion("")
            .Build();

        Assert.Equal(
            "https://www.powershellgallery.com/api/v2/Packages()?$filter=Id eq 'Devolutions.PowerShell' and IsLatestVersion",
            NuGetManifestLoader.GetManifestUrl(package).ToString()
        );
        Assert.EndsWith("Packages(Id='Devolutions.PowerShell',Version='2026.3.2')",
            NuGetManifestLoader.GetManifestUrl(package, "2026.3.2").ToString());
    }

    [Fact]
    public void TheLatestManifestIsKeptOnlyForAWhile()
    {
        var manager = new PackageManagerBuilder().Build();
        // Nothing listens on the discard port, so a fetch fails and only the cache can answer.
        var source = new SourceBuilder().WithManager(manager).WithUrl("http://127.0.0.1:9/api/v2").Build();
        var package = new PackageBuilder().WithManager(manager).WithSource(source).WithId("Contoso.Latest").WithVersion("").Build();
        long key = NuGetManifestLoader.GetCacheKey(package);
        try
        {
            NuGetManifestLoader.LatestManifests[key] = (DateTime.UtcNow, "<entry>fresh</entry>");
            Assert.Equal("<entry>fresh</entry>", NuGetManifestLoader.GetManifestContent(package));

            NuGetManifestLoader.LatestManifests[key] =
                (DateTime.UtcNow - NuGetManifestLoader.LatestManifestLifetime - TimeSpan.FromMinutes(1), "<entry>stale</entry>");
            Assert.Null(NuGetManifestLoader.GetManifestContent(package));
        }
        finally
        {
            NuGetManifestLoader.LatestManifests.TryRemove(key, out _);
        }
    }

    [Fact]
    public void GetCacheKey_MatchesTheVersionedHashForThePackagesOwnVersion()
    {
        var manager = new PackageManagerBuilder().Build();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Tool")
            .WithVersion("1.2.3")
            .Build();

        Assert.Equal(package.GetVersionedHash(), NuGetManifestLoader.GetCacheKey(package));
        Assert.Equal(
            package.GetVersionedHash(),
            NuGetManifestLoader.GetCacheKey(package, "1.2.3")
        );
        Assert.NotEqual(
            package.GetVersionedHash(),
            NuGetManifestLoader.GetCacheKey(package, "2.0.0")
        );
    }

    [Fact]
    public void GetManifestContent_UsesCachedManifestWhenAvailable()
    {
        BaseNuGet.Manifests.Clear();
        var manager = new PackageManagerBuilder().Build();
        var package = new PackageBuilder().WithManager(manager).WithId("Contoso.Tool").Build();
        BaseNuGet.Manifests[package.GetVersionedHash()] = "<cached />";

        Assert.Equal("<cached />", NuGetManifestLoader.GetManifestContent(package));
    }

    [Fact]
    public void GetManifestContent_FallsBackWhenTrailingZeroVersionReturnsNotFound()
    {
        BaseNuGet.Manifests.Clear();
        using var server = new TestHttpServer(request =>
        {
            string rawUrl = request.RawUrl ?? string.Empty;
            return rawUrl switch
            {
                string value when value.Contains("Version='1.0.0'") || value.Contains("Version=%271.0.0%27")
                    => (404, string.Empty, "application/xml"),
                string value when value.Contains("Version='1.0'") || value.Contains("Version=%271.0%27")
                    => (200, "<entry>manifest</entry>", "application/xml"),
                _ => (500, string.Empty, "text/plain"),
            };
        });
        var manager = new PackageManagerBuilder().Build();
        var source = new SourceBuilder()
            .WithManager(manager)
            .WithUrl(server.BaseUri.AbsoluteUri.TrimEnd('/'))
            .Build();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithSource(source)
            .WithId("Contoso.Tool")
            .WithVersion("1.0.0")
            .Build();

        var manifest = NuGetManifestLoader.GetManifestContent(package);

        Assert.Equal("<entry>manifest</entry>", manifest);
        Assert.Equal(2, server.RequestPaths.Count);
        Assert.Contains("1.0.0", server.RequestPaths[0]);
        Assert.True(
            server.RequestPaths[1].Contains("Version='1.0'") || server.RequestPaths[1].Contains("Version=%271.0%27")
        );
    }
}
