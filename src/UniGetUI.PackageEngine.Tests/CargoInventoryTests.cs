using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Managers.CargoManager;
using UniGetUI.PackageEngine.Serializable;
using UniGetUI.PackageEngine.Tests.Infrastructure.Helpers;

namespace UniGetUI.PackageEngine.Tests;

[Collection(nameof(OperationOrchestrationTestCollection))]
public sealed class CargoInventoryTests : IDisposable
{
    public void Dispose()
    {
        CratesIOClient.TEST_ApiUrlOverride = null;
        CratesIOClient.InvalidateCache();
    }

    [Theory]
    [InlineData("named-registry")]
    [InlineData("registry-url")]
    [InlineData("registry-path")]
    [InlineData("unix-directory")]
    [InlineData("windows-directory")]
    public void InstalledInventoryPreservesDisplayOnlySourcesWithoutDroppingOtherPackages(string scenario)
    {
        string description = SourceDescription(scenario);
        var entries = Cargo.ParseInstallList(
        [
            "public-tool v1.0.0:",
            "    public-tool",
            $"foreign-tool v2.0.0 ({description}):",
            "    foreign-tool",
            "another-public-tool v3.0.0:",
            "    another-public-tool",
        ]);
        var manager = new Cargo();
        var packages = manager.GetPackages(entries, LoggableTaskType.ListInstalledPackages);

        Assert.Equal(["public-tool", "foreign-tool", "another-public-tool"], packages.Select(package => package.Id));
        Assert.Equal(["1.0.0", "2.0.0", "3.0.0"], packages.Select(package => package.VersionString));
        Assert.Same(manager.DefaultSource, packages[0].Source);
        Assert.Same(manager.DefaultSource, packages[2].Source);
        Assert.Equal(description, packages[1].Source.Name);
        Assert.Equal(new Uri("about:blank"), packages[1].Source.Url);
        Assert.NotEqual(manager.DefaultSource.Name, packages[1].Source.Name);
        Assert.Equal(["uninstall", "foreign-tool"],
            manager.OperationHelper.GetParameters(packages[1], new InstallOptions(), OperationType.Uninstall));
        var error = Assert.Throws<InvalidOperationException>(() =>
            manager.OperationHelper.GetParameters(packages[1], new InstallOptions(), OperationType.Install));
        Assert.Contains("explicit Cargo source arguments", error.Message);
    }

    [Fact]
    public void UpdateDiscoverySkipsDisplayOnlySourcesAndStillReturnsCratesIoUpdates()
    {
        using var server = new TestHttpServer(request =>
            request.Url?.AbsolutePath == "/api/v1/crates/public-tool"
                ? (200, """
                    {
                      "crate": {
                        "name": "public-tool",
                        "max_stable_version": "1.1.0",
                        "max_version": "1.1.0",
                        "newest_version": "1.1.0"
                      },
                      "versions": [
                        {"num":"1.1.0","checksum":"fixture","dl_path":"/source","yanked":false}
                      ]
                    }
                    """, "application/json")
                : (500, "A non-crates.io package must not query this server.", "text/plain"));
        CratesIOClient.InvalidateCache();
        CratesIOClient.TEST_ApiUrlOverride = server.BaseUri.AbsoluteUri.TrimEnd('/') + "/api/v1";
        var entries = Cargo.ParseInstallList(
        [
            "foreign-tool v9.0.0 (registry `company`):",
            "    foreign-tool",
            "public-tool v1.0.0:",
            "    public-tool",
            "vendor-tool v2.0.0 (dir /tmp/vendor):",
            "    vendor-tool",
        ]);

        var manager = new Cargo();
        var installed = manager.GetPackages(entries, LoggableTaskType.ListInstalledPackages);
        Assert.Equal(["foreign-tool", "public-tool", "vendor-tool"], installed.Select(package => package.Id));
        Assert.Empty(server.RequestPaths);

        var update = Assert.Single(manager.GetPackages(entries, LoggableTaskType.ListUpdates));
        Assert.Equal("public-tool", update.Id);
        Assert.Equal("1.0.0", update.VersionString);
        Assert.Equal("1.1.0", update.NewVersionString);
        Assert.Same(manager.DefaultSource, update.Source);
        Assert.Equal(["/api/v1/crates/public-tool"], server.RequestPaths);
    }

    [Theory]
    [InlineData("git")]
    [InlineData("registry-index")]
    [InlineData("local-path")]
    public void InstalledInventoryPreservesKnownSourceUris(string scenario)
    {
        using var sandbox = new CargoTestSandbox();
        string path = sandbox.PathFor("local tool");
        string description = scenario switch
        {
            "git" => "https://github.com/publisher/tool?branch=main#abcdef",
            "registry-index" => "registry+https://private.test/index",
            _ => path,
        };
        Uri expected = scenario switch
        {
            "git" => new Uri(description),
            "registry-index" => new Uri("https://private.test/index"),
            _ => new Uri(path),
        };
        var manager = new Cargo();
        var entry = Assert.Single(Cargo.ParseInstallList([$"tool v1.2.3 ({description}):"]));

        var package = Assert.Single(manager.GetPackages([entry], LoggableTaskType.ListInstalledPackages));

        Assert.Equal("tool", package.Id);
        Assert.Equal("1.2.3", package.VersionString);
        Assert.Equal(description, package.Source.Name);
        Assert.Equal(expected, package.Source.Url);
    }

    private static string SourceDescription(string scenario) => scenario switch
    {
        "named-registry" => "registry `company`",
        "registry-url" => "registry `https://private.test/index`",
        "registry-path" => @"registry `C:\registry index`",
        "unix-directory" => "dir /tmp/vendor",
        "windows-directory" => @"dir C:\vendor packages",
        _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
    };
}
