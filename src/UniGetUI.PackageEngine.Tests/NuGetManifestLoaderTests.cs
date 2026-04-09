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
    public void GetManifestContent_UsesCachedManifestWhenAvailable()
    {
        BaseNuGet.Manifests.Clear();
        var manager = new PackageManagerBuilder().Build();
        var package = new PackageBuilder().WithManager(manager).WithId("Contoso.Tool").Build();
        BaseNuGet.Manifests[package.GetHash()] = "<cached />";

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
