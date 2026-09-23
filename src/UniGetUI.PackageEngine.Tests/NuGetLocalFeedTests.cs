using UniGetUI.Core.IconEngine;
using UniGetUI.PackageEngine.Classes.Manager;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.ManagerClasses.Classes;
using UniGetUI.PackageEngine.ManagerClasses.Manager;
using UniGetUI.PackageEngine.Managers.Generic.NuGet.Internal;
using UniGetUI.PackageEngine.Managers.PowerShellManager;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.Tests.Infrastructure.Builders;

namespace UniGetUI.PackageEngine.Tests;

public sealed class NuGetLocalFeedTests
{
    [Theory]
    [InlineData("https://community.chocolatey.org/api/v2/", false)]
    [InlineData("https://packages.example.test/api/v3/index.json", false)]
    [InlineData("file://Data/nuget/server/", true)]
    [InlineData("file:///C:/packages", true)]
    public void TryGetDirectoryOnlyAcceptsFileSystemSources(string url, bool expected)
    {
        var manager = new PackageManagerBuilder().Build();
        var source = new ManagerSource(manager, "test", new Uri(url));

        Assert.Equal(expected, NuGetLocalFeed.TryGetDirectory(source, out _));
        Assert.Equal(expected, NuGetLocalFeed.IsLocalSource(source));
    }

    [Fact]
    public void TryGetDirectoryReturnsTheFolderBehindTheSourceUrl()
    {
        using var feed = new LocalFeed();
        var manager = feed.CreateManager();

        Assert.True(
            NuGetLocalFeed.TryGetDirectory(manager.Properties.DefaultSource, out string directory)
        );
        Assert.Equal(feed.Directory, directory.TrimEnd(Path.DirectorySeparatorChar));
    }

    [Fact]
    public void FindPackagesKeepsTheLatestStableVersionOfEachPackage()
    {
        using var feed = new LocalFeed();
        feed.WritePackage("Contoso.Tool", "1.0.0");
        feed.WritePackage("Contoso.Tool", "2.0.0");
        feed.WritePackage("Contoso.Tool", "2.1.0-beta.1");
        feed.WritePackage("Fabrikam.Tool", "1.0.0");

        var manager = feed.CreateManager();
        var packages = feed.Find(manager, "contoso", canPrerelease: false);

        Assert.Single(packages);
        Assert.Equal("Contoso.Tool", packages[0].Id);
        Assert.Equal("2.0.0", packages[0].VersionString);
        Assert.Same(manager.Properties.DefaultSource, packages[0].Source);
        Assert.Same(manager, packages[0].Manager);
    }

    [Fact]
    public void FindPackagesOffersPreReleasesOnlyWhenTheyAreAllowed()
    {
        using var feed = new LocalFeed();
        feed.WritePackage("Contoso.Tool", "2.0.0");
        feed.WritePackage("Contoso.Tool", "2.1.0-beta.1");

        var manager = feed.CreateManager();

        Assert.Equal("2.0.0", feed.Find(manager, "contoso", false)[0].VersionString);
        Assert.Equal("2.1.0-beta.1", feed.Find(manager, "contoso", true)[0].VersionString);
    }

    [Fact]
    public void FindPackagesReadsTheVersionFolderLayout()
    {
        using var feed = new LocalFeed();
        feed.WritePackage("Contoso.Tool", "1.5.0", folder: Path.Join("contoso.tool", "1.5.0"));

        var manager = feed.CreateManager();
        var packages = feed.Find(manager, "contoso", canPrerelease: false);

        Assert.Single(packages);
        Assert.Equal("1.5.0", packages[0].VersionString);
    }

    [Fact]
    public void FindPackagesMatchesMetadataUnlessTheManagerSearchesIdsOnly()
    {
        using var feed = new LocalFeed();
        feed.WritePackage("Contoso.Tool", "1.0.0", description: "A deployment helper");

        Assert.Single(feed.Find(feed.CreateManager(), "deployment", false));
        Assert.Empty(feed.Find(feed.CreateManager(idOnlySearch: true), "deployment", false));
        Assert.Single(feed.Find(feed.CreateManager(idOnlySearch: true), "contoso", false));
    }

    [Fact]
    public void FindPackagesIgnoresFilesThatAreNotReadableNuGetPackages()
    {
        using var feed = new LocalFeed();
        feed.WritePackage("Contoso.Tool", "1.0.0");
        File.WriteAllText(Path.Join(feed.Directory, "broken.nupkg"), "not a zip archive");
        File.WriteAllText(Path.Join(feed.Directory, "notes.txt"), "ignored");

        var packages = feed.Find(feed.CreateManager(), "", canPrerelease: false);

        Assert.Single(packages);
        Assert.Equal("Contoso.Tool", packages[0].Id);
    }

    [Fact]
    public void FindPackagesPicksUpAPackageFileThatWasReplaced()
    {
        using var feed = new LocalFeed();
        string file = feed.WritePackage("Contoso.Tool", "1.0.0");

        var manager = feed.CreateManager();
        Assert.Equal("1.0.0", feed.Find(manager, "contoso", false)[0].VersionString);

        File.Delete(file);
        feed.WritePackage("Contoso.Tool", "1.0.0", description: "A much longer description");

        Assert.Equal("1.0.0", feed.Find(manager, "much longer", false)[0].VersionString);
    }

    [Fact]
    public void GetAvailableUpdatesOnlyOffersNewerVersions()
    {
        using var feed = new LocalFeed();
        feed.WritePackage("Contoso.Tool", "1.0.0");
        feed.WritePackage("Contoso.Tool", "2.0.0");
        feed.WritePackage("Fabrikam.Tool", "1.0.0");

        var manager = feed.CreateManager();
        var source = manager.Properties.DefaultSource;
        IPackage outdated = new PackageBuilder()
            .WithManager(manager)
            .WithSource(source)
            .WithId("Contoso.Tool")
            .WithVersion("1.0.0")
            .Build();
        IPackage current = new PackageBuilder()
            .WithManager(manager)
            .WithSource(source)
            .WithId("Fabrikam.Tool")
            .WithVersion("1.0.0")
            .Build();

        var updates = manager.GetAvailableUpdatesLocal(
            source,
            feed.Directory,
            [outdated, current],
            canPrerelease: false,
            Logger(manager)
        );

        Assert.Single(updates);
        Assert.Equal("Contoso.Tool", updates[0].Id);
        Assert.Equal("1.0.0", updates[0].VersionString);
        Assert.Equal("2.0.0", updates[0].NewVersionString);
    }

    [Fact]
    public void GetAvailableUpdatesOffersOneUpdatePerIdWhenAPackageIsInstalledTwice()
    {
        using var feed = new LocalFeed();
        feed.WritePackage("Contoso.Tool", "2.0.0");

        var manager = feed.CreateManager();
        var source = manager.Properties.DefaultSource;
        IPackage currentUser = new PackageBuilder()
            .WithManager(manager)
            .WithSource(source)
            .WithId("Contoso.Tool")
            .WithVersion("1.0.0")
            .Build();
        IPackage allUsers = new PackageBuilder()
            .WithManager(manager)
            .WithSource(source)
            .WithId("contoso.tool")
            .WithVersion("1.0.0")
            .Build();

        var updates = manager.GetAvailableUpdatesLocal(
            source,
            feed.Directory,
            [currentUser, allUsers],
            canPrerelease: false,
            Logger(manager)
        );

        Assert.Equal("2.0.0", Assert.Single(updates).NewVersionString);
    }

    [Fact]
    public void GetAvailableUpdatesSkipsPreReleasesUnlessTheyAreAllowed()
    {
        using var feed = new LocalFeed();
        feed.WritePackage("Contoso.Tool", "2.0.0-beta.1");

        var manager = feed.CreateManager();
        var source = manager.Properties.DefaultSource;
        IPackage installed = new PackageBuilder()
            .WithManager(manager)
            .WithSource(source)
            .WithId("Contoso.Tool")
            .WithVersion("1.0.0")
            .Build();

        Assert.Empty(
            manager.GetAvailableUpdatesLocal(
                source,
                feed.Directory,
                [installed],
                false,
                Logger(manager)
            )
        );
        Assert.Single(
            manager.GetAvailableUpdatesLocal(
                source,
                feed.Directory,
                [installed],
                true,
                Logger(manager)
            )
        );
    }

    [Fact]
    public void GetDetailsReadsTheEmbeddedNuspec()
    {
        using var feed = new LocalFeed();
        string file = feed.WritePackage(
            "Contoso.Tool",
            "2.0.0",
            description: "A deployment helper",
            authors: "Contoso Ltd",
            tags: "deployment tooling",
            dependencyId: "Fabrikam.Core"
        );

        var manager = feed.CreateManager();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithSource(manager.Properties.DefaultSource)
            .WithId("Contoso.Tool")
            .WithVersion("2.0.0")
            .Build();
        var details = new PackageDetailsBuilder().Build(package);

        manager.ExposedDetailsHelper.LoadDetails(details);

        Assert.Equal("A deployment helper", details.Description);
        Assert.Equal("Contoso Ltd", details.Author);
        Assert.Equal("Contoso Ltd", details.Publisher);
        Assert.Equal("MIT", details.License);
        Assert.Equal("https://example.test/package", details.HomepageUrl?.AbsoluteUri);
        Assert.Equal(new Uri(file), details.InstallerUrl);
        Assert.Equal(new Uri(file), details.ManifestUrl);
        Assert.Equal(new FileInfo(file).Length, details.InstallerSize);
        Assert.Equal(["deployment", "tooling"], details.Tags);
        Assert.Equal("Fabrikam.Core", Assert.Single(details.Dependencies).Name);
        Assert.Equal("1.2.0", details.Dependencies[0].Version);
    }

    [Fact]
    public void GetDetailsReportsAPackageFileThatIsNotOnTheFeedAnymore()
    {
        using var feed = new LocalFeed();
        var manager = feed.CreateManager();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithSource(manager.Properties.DefaultSource)
            .WithId("Contoso.Tool")
            .WithVersion("2.0.0")
            .Build();
        var details = new PackageDetailsBuilder().Build(package);

        manager.ExposedDetailsHelper.LoadDetails(details);

        Assert.Null(details.Description);
        Assert.Null(details.InstallerUrl);
    }

    [Fact]
    public void GetIconUsesTheIconUrlOfTheNuspec()
    {
        using var feed = new LocalFeed();
        feed.WritePackage("Contoso.Tool", "2.0.0", iconUrl: "https://example.test/icon.png");
        feed.WritePackage("Fabrikam.Tool", "2.0.0");

        var manager = feed.CreateManager();
        CacheableIcon? icon = manager.ExposedDetailsHelper.LoadIcon(
            new PackageBuilder()
                .WithManager(manager)
                .WithSource(manager.Properties.DefaultSource)
                .WithId("Contoso.Tool")
                .WithVersion("2.0.0")
                .Build()
        );

        Assert.Equal("https://example.test/icon.png", icon?.Url?.AbsoluteUri);
        Assert.Null(
            manager.ExposedDetailsHelper.LoadIcon(
                new PackageBuilder()
                    .WithManager(manager)
                    .WithSource(manager.Properties.DefaultSource)
                    .WithId("Fabrikam.Tool")
                    .WithVersion("2.0.0")
                    .Build()
            )
        );
    }

    [Fact]
    public void FindPackagesSkipsAManifestThatExpandsPastTheSizeLimit()
    {
        using var feed = new LocalFeed();
        feed.WritePackage("Contoso.Tool", "1.0.0");
        feed.WriteRawPackage(
            "bomb.1.0.0.nupkg",
            $"""
            <?xml version="1.0" encoding="utf-8"?>
            <package>
              <metadata>
                <id>Contoso.Bomb</id>
                <version>1.0.0</version>
                <description>{new string('A', 5 * 1024 * 1024)}</description>
              </metadata>
            </package>
            """
        );

        var packages = feed.Find(feed.CreateManager(), "contoso", canPrerelease: false);

        Assert.Equal("Contoso.Tool", Assert.Single(packages).Id);
    }

    [Fact]
    public void FindPackagesRejectsAManifestThatDeclaresADocumentTypeDefinition()
    {
        using var feed = new LocalFeed();
        feed.WriteRawPackage(
            "entities.1.0.0.nupkg",
            """
            <?xml version="1.0" encoding="utf-8"?>
            <!DOCTYPE package [
              <!ENTITY lol "lol">
              <!ENTITY lol2 "&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;">
            ]>
            <package>
              <metadata>
                <id>Contoso.Entities</id>
                <version>1.0.0</version>
                <description>&lol2;</description>
              </metadata>
            </package>
            """
        );

        Assert.Empty(feed.Find(feed.CreateManager(), "contoso", canPrerelease: false));
    }

    [Fact]
    public void GetIconExtractsAnIconEmbeddedInThePackage()
    {
        byte[] iconBytes = [137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3, 4];

        using var feed = new LocalFeed();
        feed.WritePackage(
            "Contoso.Tool",
            "2.0.0",
            iconFile: "images/icon.png",
            iconBytes: iconBytes
        );

        var manager = feed.CreateManager();
        CacheableIcon? icon = manager.ExposedDetailsHelper.LoadIcon(
            new PackageBuilder()
                .WithManager(manager)
                .WithSource(manager.Properties.DefaultSource)
                .WithId("Contoso.Tool")
                .WithVersion("2.0.0")
                .Build()
        );

        Assert.NotNull(icon);
        Assert.True(icon.Value.IsLocalPath);
        Assert.Equal(iconBytes, File.ReadAllBytes(icon.Value.LocalPath));
        Assert.Equal(".png", Path.GetExtension(icon.Value.LocalPath));
    }

    [Fact]
    public void GetIconPrefersTheIconUrlOverAnEmbeddedIcon()
    {
        using var feed = new LocalFeed();
        feed.WritePackage(
            "Contoso.Tool",
            "2.0.0",
            iconUrl: "https://example.test/icon.png",
            iconFile: "images/icon.png",
            iconBytes: [1, 2, 3]
        );

        var manager = feed.CreateManager();
        CacheableIcon? icon = manager.ExposedDetailsHelper.LoadIcon(
            new PackageBuilder()
                .WithManager(manager)
                .WithSource(manager.Properties.DefaultSource)
                .WithId("Contoso.Tool")
                .WithVersion("2.0.0")
                .Build()
        );

        Assert.False(icon?.IsLocalPath);
        Assert.Equal("https://example.test/icon.png", icon?.Url.AbsoluteUri);
    }

    [Fact]
    public void GetIconIgnoresAnEmbeddedIconThatIsNotInTheArchive()
    {
        using var feed = new LocalFeed();
        feed.WritePackage("Contoso.Tool", "2.0.0", iconFile: "images/missing.png");

        var manager = feed.CreateManager();

        Assert.Null(
            manager.ExposedDetailsHelper.LoadIcon(
                new PackageBuilder()
                    .WithManager(manager)
                    .WithSource(manager.Properties.DefaultSource)
                    .WithId("Contoso.Tool")
                    .WithVersion("2.0.0")
                    .Build()
            )
        );
    }

    [Fact]
    public void GetInstallableVersionsListsTheFolderContentsNewestFirst()
    {
        using var feed = new LocalFeed();
        feed.WritePackage("Contoso.Tool", "1.0.0");
        feed.WritePackage("Contoso.Tool", "2.0.0-beta.1");
        feed.WritePackage("Contoso.Tool", "2.0.0");
        feed.WritePackage("Fabrikam.Tool", "3.0.0");

        var manager = feed.CreateManager();
        var versions = manager.ExposedDetailsHelper.LoadVersions(
            new PackageBuilder()
                .WithManager(manager)
                .WithSource(manager.Properties.DefaultSource)
                .WithId("Contoso.Tool")
                .WithVersion("1.0.0")
                .Build()
        );

        Assert.Equal(["2.0.0", "2.0.0-beta.1", "1.0.0"], versions);
    }

    [Fact]
    public void GetInstallableVersionsReturnsNothingWhenTheFolderIsGone()
    {
        var manager = new TestNuGetManager(
            Path.Join(Path.GetTempPath(), Path.GetRandomFileName()),
            idOnlySearch: false
        );

        Assert.Empty(
            manager.ExposedDetailsHelper.LoadVersions(
                new PackageBuilder()
                    .WithManager(manager)
                    .WithSource(manager.Properties.DefaultSource)
                    .WithId("Contoso.Tool")
                    .WithVersion("1.0.0")
                    .Build()
            )
        );
    }

    private static INativeTaskLogger Logger(BaseNuGet manager) =>
        manager.TaskLogger.CreateNew(LoggableTaskType.FindPackages);

    private sealed class LocalFeed : IDisposable
    {
        private readonly LocalNuGetFeedBuilder _feed = new();

        public string Directory => _feed.Directory;

        public TestNuGetManager CreateManager(bool idOnlySearch = false) =>
            new(Directory, idOnlySearch);

        public IReadOnlyList<Package> Find(
            TestNuGetManager manager,
            string query,
            bool canPrerelease
        ) =>
            manager.FindPackagesLocal(
                manager.Properties.DefaultSource,
                Directory,
                query,
                canPrerelease,
                Logger(manager)
            );

        public string WritePackage(
            string id,
            string version,
            string? folder = null,
            string description = "A package",
            string authors = "Example Ltd",
            string tags = "tooling",
            string? iconUrl = null,
            string? dependencyId = null,
            string? iconFile = null,
            byte[]? iconBytes = null
        ) =>
            _feed.WritePackage(
                id,
                version,
                folder,
                description,
                authors,
                tags,
                iconUrl,
                dependencyId,
                iconFile,
                iconBytes
            );

        public string WriteRawPackage(string fileName, string nuspec) =>
            _feed.WriteRawPackage(fileName, nuspec);

        public void Dispose() => _feed.Dispose();
    }

    private sealed class TestNuGetManager : BaseNuGet
    {
        private readonly bool _idOnlySearch;

        public TestNuGetManager(string directory, bool idOnlySearch)
        {
            _idOnlySearch = idOnlySearch;

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
                DefaultSource = new ManagerSource(this, "local", new Uri(directory)),
            };

            ExposedDetailsHelper = new TestNuGetDetailsHelper(this);
            DetailsHelper = ExposedDetailsHelper;
        }

        public TestNuGetDetailsHelper ExposedDetailsHelper { get; }

        protected override bool UseSubstringSearch => _idOnlySearch;

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
    }

    private sealed class TestNuGetDetailsHelper : BaseNuGetDetailsHelper
    {
        public TestNuGetDetailsHelper(BaseNuGet manager)
            : base(manager) { }

        protected override string? GetInstallLocation_UnSafe(IPackage package) => null;

        public void LoadDetails(IPackageDetails details) => GetDetails_UnSafe(details);

        public CacheableIcon? LoadIcon(IPackage package) => GetIcon_UnSafe(package);

        public IReadOnlyList<string> LoadVersions(IPackage package) =>
            GetInstallableVersions_UnSafe(package);
    }
}
