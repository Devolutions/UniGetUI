using UniGetUI.PackageEngine.Classes.Manager;
using UniGetUI.PackageEngine.Classes.Manager.Providers;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Managers.DotNetManager;
using UniGetUI.PackageEngine.Managers.Generic.NuGet;
using UniGetUI.PackageEngine.Managers.Generic.NuGet.Internal;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.Serializable;
using UniGetUI.PackageEngine.Structs;
using UniGetUI.PackageEngine.Tests.Infrastructure.Builders;

namespace UniGetUI.PackageEngine.Tests;

public sealed class DotNetManagerTests
{
    // A `dotnet tool install` with neither --global nor --tool-path is a manifest-scoped LOCAL
    // install: it writes into a dotnet-tools.json tied to the working directory. UniGetUI runs
    // its own `dotnet tool list` from its install directory, so it can never enumerate a
    // manifest created anywhere else - the tool appears installed once and then vanishes, and
    // is never considered for updates. Global is therefore the default, and local must be asked
    // for explicitly.
    [Fact]
    public void InstallDefaultsToGlobalWhenNoScopeIsRequested()
    {
        var manager = new DotNet();
        var package = new PackageBuilder().WithManager(manager).WithId("dotnetsay").Build();

        var parameters = manager.OperationHelper.GetParameters(
            package,
            new InstallOptions(),
            OperationType.Install
        );

        Assert.Contains("--global", parameters);
    }

    [Theory]
    [InlineData(PackageScope.User, false)]
    [InlineData(PackageScope.Machine, true)]
    public void InstallHonoursAnExplicitlyRequestedScope(string scope, bool expectGlobal)
    {
        var manager = new DotNet();
        var package = new PackageBuilder().WithManager(manager).WithId("dotnetsay").Build();

        var parameters = manager.OperationHelper.GetParameters(
            package,
            new InstallOptions { InstallationScope = scope },
            OperationType.Install
        );

        Assert.Equal(expectGlobal, parameters.Contains("--global"));
    }

    [Theory]
    [InlineData(PackageScope.Local, false)]
    [InlineData(PackageScope.Global, true)]
    public void OperationsPreserveTheScopeAnInstalledToolWasFoundIn(
        string scope,
        bool expectGlobal
    )
    {
        var manager = new DotNet();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("dotnetsay")
            .WithOptions(new OverridenInstallationOptions(scope))
            .Build();

        foreach (var operation in new[] { OperationType.Update, OperationType.Uninstall })
        {
            var parameters = manager.OperationHelper.GetParameters(
                package,
                new InstallOptions(),
                operation
            );

            Assert.Equal(expectGlobal, parameters.Contains("--global"));
        }
    }

    // dotnet rejects --global and --tool-path together, so a custom location must suppress the
    // global default rather than produce a command line that cannot run.
    [Fact]
    public void ACustomInstallLocationSuppressesTheGlobalFlag()
    {
        var manager = new DotNet();
        var package = new PackageBuilder().WithManager(manager).WithId("dotnetsay").Build();

        var parameters = manager.OperationHelper.GetParameters(
            package,
            new InstallOptions { CustomInstallLocation = "C:\\tools" },
            OperationType.Install
        );

        Assert.Contains("--tool-path", parameters);
        Assert.DoesNotContain("--global", parameters);
    }

    [Fact]
    public void ACustomInstallLocationSuppressesTheGlobalFlagEvenWhenGlobalIsRequested()
    {
        var manager = new DotNet();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("dotnetsay")
            .WithOptions(new OverridenInstallationOptions(PackageScope.Global))
            .Build();

        var parameters = manager.OperationHelper.GetParameters(
            package,
            new InstallOptions { CustomInstallLocation = "C:\\tools" },
            OperationType.Install
        );

        Assert.Contains("--tool-path", parameters);
        Assert.DoesNotContain("--global", parameters);
    }

    [Fact]
    public void DefaultAndKnownSourcesPointAtTheNuGetV3ServiceIndex()
    {
        var manager = new DotNet();

        Assert.Equal(
            "https://api.nuget.org/v3/index.json",
            manager.Properties.DefaultSource.Url.AbsoluteUri
        );
        Assert.All(
            manager.Properties.KnownSources,
            source =>
                Assert.Equal("https://api.nuget.org/v3/index.json", source.Url.AbsoluteUri)
        );
        Assert.True(NuGetV3ServiceIndex.IsV3Source(manager.Properties.DefaultSource));
    }

    [Fact]
    public void ParseInstalledPackages_SkipsHeadersAndFalseRows()
    {
        var manager = new DotNet();
        var packages = DotNet.ParseInstalledPackages(
            [
                "Package Id      Version      Commands",
                "--------------------------------------",
                "dotnetsay       2.1.7        dotnetsay",
                "               1.0.0",
                "try-convert     0.9.232202",
            ],
            manager.DefaultSource,
            manager,
            new OverridenInstallationOptions(PackageScope.Local)
        );

        Assert.Collection(
            packages,
            package =>
            {
                Assert.Equal("dotnetsay", package.Id);
                Assert.Equal("2.1.7", package.VersionString);
                Assert.Equal(PackageScope.Local, package.OverridenOptions.Scope);
            },
            package =>
            {
                Assert.Equal("try-convert", package.Id);
                Assert.Equal("0.9.232202", package.VersionString);
            }
        );
    }

    [Fact]
    public void ParseInstalledPackages_PreservesRequestedScope()
    {
        var manager = new DotNet();
        var package = Assert.Single(
            DotNet.ParseInstalledPackages(
                [
                    "Package Id      Version      Commands",
                    "--------------------------------------",
                    "dotnetsay       2.1.7        dotnetsay",
                ],
                manager.DefaultSource,
                manager,
                new OverridenInstallationOptions(PackageScope.Global)
            )
        );

        Assert.Equal(PackageScope.Global, package.OverridenOptions.Scope);
    }

    [Fact]
    public void ParseSourcesKeepsOnlyEnabledSourcesFromTheNuGetConfiguration()
    {
        var sources = DotNetSourceHelper.ParseSources(
            [
                "Registered Sources:",
                "  1.  nuget.org [Disabled]",
                "      https://api.nuget.org/v3/index.json",
                "  2.  Artifactory [Enabled]",
                "      https://artifactory.example.test/artifactory/api/nuget/nuget",
                "  3.  My [Team] Feed [Enabled]",
                @"      C:\Packages\Team Feed",
            ]
        );

        Assert.NotNull(sources);
        Assert.Collection(
            sources,
            source =>
            {
                Assert.Equal("Artifactory", source.Key);
                Assert.Equal(
                    new Uri("https://artifactory.example.test/artifactory/api/nuget/nuget"),
                    source.Url
                );
            },
            source =>
            {
                Assert.Equal("My [Team] Feed", source.Key);
                Assert.True(source.Url.IsFile);
            }
        );
    }

    [Fact]
    public void ParseSourcesReturnsNothingWhenNoSourceIsConfigured()
    {
        var sources = DotNetSourceHelper.ParseSources(["No sources found."]);

        Assert.NotNull(sources);
        Assert.Empty(sources);
    }

    [Fact]
    public void ParseSourcesRejectsOutputItCannotRead()
    {
        Assert.Null(
            DotNetSourceHelper.ParseSources(
                [
                    "Sources inscrites :",
                    "  1.  nuget.org [Activ\u00e9]",
                    "      https://api.nuget.org/v3/index.json",
                ]
            )
        );
    }

    [Fact]
    public void TheNuGetOrgFeedIsTheDefaultSourceWhateverItsKey()
    {
        var manager = new DotNet();
        var helper = (DotNetSourceHelper)manager.SourcesHelper;

        var sources = helper.ToManagerSources(
            [
                new("NuGet official package source", new Uri("https://api.nuget.org/v3/index.json")),
                new("Private", new Uri("https://packages.example.test/v3/index.json")),
            ]
        );

        Assert.Same(manager.DefaultSource, sources[0]);
        Assert.Equal("Private", sources[1].Name);
    }

    [Fact]
    public void SourceMappingPrefersAnExactIdThenTheLongestPrefix()
    {
        var mapping = new NuGetSourceMapping(
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["nuget.org"] = ["*"],
                ["Private"] = ["Contoso.*"],
                ["Tools"] = ["Contoso.Tools.*", "Contoso.Legacy"],
            }
        );

        Assert.Equal(new[] { "nuget.org" }, mapping.SourcesFor("Newtonsoft.Json"));
        Assert.Equal(new[] { "Private" }, mapping.SourcesFor("contoso.cli"));
        Assert.Equal(new[] { "Tools" }, mapping.SourcesFor("Contoso.Tools.Format"));
        Assert.Equal(new[] { "Tools" }, mapping.SourcesFor("Contoso.Legacy"));
    }

    [Fact]
    public void ConfigurationAllowsAPackageOnlyFromItsMappedSourceUrl()
    {
        var nugetOrg = new Uri("https://api.nuget.org/v3/index.json");
        var privateFeed = new Uri("https://packages.example.test/v3/index.json");
        var configuration = new DotNetNuGetConfiguration(
            [],
            [new("NuGet official package source", nugetOrg), new("Private", privateFeed)],
            new NuGetSourceMapping(
                new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
                {
                    ["NuGet official package source"] = ["*"],
                    ["Private"] = ["Contoso.*"],
                }
            ),
            [],
            canBeReused: true
        );

        Assert.True(configuration.Allows("Contoso.Tool", privateFeed));
        Assert.False(configuration.Allows("Contoso.Tool", nugetOrg));
        Assert.True(configuration.Allows("dotnetsay", nugetOrg));
    }

    [Fact]
    public void ConfigurationFilesStackFromTheClosestOneAndStopAtClear()
    {
        using var folder = new TempFolder();
        string closest = folder.Write(
            "closest.config",
            """
            <configuration>
              <packageSourceMapping>
                <packageSource key="Private"><package pattern="Contoso.*" /></packageSource>
              </packageSourceMapping>
              <packageSourceCredentials>
                <Team_x0020_Feed>
                  <add key="Username" value="%UNIGETUI_TEST_NUGET_USER%" />
                  <add key="ClearTextPassword" value="secret" />
                </Team_x0020_Feed>
                <Kerberos>
                  <add key="Username" value="someone" />
                  <add key="ClearTextPassword" value="ignored" />
                  <add key="ValidAuthenticationTypes" value="negotiate" />
                </Kerberos>
              </packageSourceCredentials>
            </configuration>
            """
        );
        string middle = folder.Write(
            "middle.config",
            """
            <configuration>
              <packageSourceMapping>
                <packageSource key="Private"><package pattern="Ignored.*" /></packageSource>
                <clear />
                <packageSource key="nuget.org"><package pattern="*" /></packageSource>
              </packageSourceMapping>
            </configuration>
            """
        );
        string farthest = folder.Write(
            "farthest.config",
            """
            <configuration>
              <packageSourceMapping>
                <packageSource key="Machine"><package pattern="*" /></packageSource>
              </packageSourceMapping>
            </configuration>
            """
        );

        Environment.SetEnvironmentVariable("UNIGETUI_TEST_NUGET_USER", "builder");
        try
        {
            var settings = NuGetConfigFiles.Read([closest, middle, farthest]);

            Assert.Equal(new[] { "Private" }, settings.Mapping.SourcesFor("Contoso.Tool"));
            Assert.Equal(new[] { "nuget.org" }, settings.Mapping.SourcesFor("Ignored.Tool"));
            var credential = Assert.Single(settings.Credentials);
            Assert.Equal("Team Feed", credential.Key);
            Assert.Equal(new NuGetCredential("builder", "secret"), credential.Value);
        }
        finally
        {
            Environment.SetEnvironmentVariable("UNIGETUI_TEST_NUGET_USER", null);
        }
    }

    [Fact]
    public void CredentialsAreOnlySentToTheOriginOfTheirSource()
    {
        var settings = new NuGetConfigSettings(
            NuGetSourceMapping.None,
            new Dictionary<string, NuGetCredential>(StringComparer.OrdinalIgnoreCase)
            {
                ["Private"] = new("user", "secret"),
                ["Folder"] = new("user", "secret"),
            }
        );
        NuGetFeedCredentials.Replace(
            DotNetSourceHelper.BuildCredentials(
                [
                    new("Private", new Uri("https://packages.example.test/feed/v3/index.json")),
                    new("Folder", new Uri(@"C:\Packages")),
                ],
                settings
            )
        );

        try
        {
            var feed = new Uri("https://packages.example.test/feed/v3/index.json");
            using var sameOrigin = new HttpRequestMessage(
                HttpMethod.Get,
                "https://PACKAGES.example.test/other/path"
            );
            using var otherOrigin = new HttpRequestMessage(
                HttpMethod.Get,
                "https://api.nuget.org/v3/index.json"
            );
            using var noFeed = new HttpRequestMessage(
                HttpMethod.Get,
                "https://packages.example.test/feed/v3/index.json"
            );
            NuGetFeedCredentials.ApplyTo(sameOrigin, feed);
            NuGetFeedCredentials.ApplyTo(otherOrigin, feed);
            NuGetFeedCredentials.ApplyTo(noFeed, null);

            Assert.Equal("Basic", sameOrigin.Headers.Authorization?.Scheme);
            Assert.Equal(
                Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("user:secret")),
                sameOrigin.Headers.Authorization?.Parameter
            );
            Assert.Null(otherOrigin.Headers.Authorization);
            Assert.Null(noFeed.Headers.Authorization);
        }
        finally
        {
            NuGetFeedCredentials.Replace([]);
        }
    }

    [Fact]
    public void FeedsWhosePathsDifferOnlyByCaseKeepTheirOwnCredentials()
    {
        var settings = new NuGetConfigSettings(
            NuGetSourceMapping.None,
            new Dictionary<string, NuGetCredential>(StringComparer.OrdinalIgnoreCase)
            {
                ["Upper"] = new("upper", "u-secret"),
                ["Lower"] = new("lower", "l-secret"),
            }
        );
        NuGetFeedCredentials.Replace(
            DotNetSourceHelper.BuildCredentials(
                [
                    new("Upper", new Uri("https://packages.example.test/Team/v3/index.json")),
                    new("Lower", new Uri("https://packages.example.test/team/v3/index.json")),
                ],
                settings
            )
        );

        try
        {
            foreach (
                var (feed, expected) in new[]
                {
                    ("https://PACKAGES.example.test/Team/v3/INDEX.JSON", "upper:u-secret"),
                    ("https://packages.example.test/team/v3", "lower:l-secret"),
                }
            )
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    "https://packages.example.test/flat/contoso.tool/index.json"
                );
                NuGetFeedCredentials.ApplyTo(request, new Uri(feed));

                Assert.Equal(
                    Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(expected)),
                    request.Headers.Authorization?.Parameter
                );
            }
        }
        finally
        {
            NuGetFeedCredentials.Replace([]);
        }
    }

    [Fact]
    public void FeedsSharingAServerKeepTheirOwnCredentials()
    {
        var settings = new NuGetConfigSettings(
            NuGetSourceMapping.None,
            new Dictionary<string, NuGetCredential>(StringComparer.OrdinalIgnoreCase)
            {
                ["Feed A"] = new("alice", "a-secret"),
                ["Feed B"] = new("bob", "b-secret"),
            }
        );
        NuGetFeedCredentials.Replace(
            DotNetSourceHelper.BuildCredentials(
                [
                    new("Feed A", new Uri("https://pkgs.example.test/org/_packaging/feed-a/nuget/v3/index.json")),
                    new("Feed B", new Uri("https://pkgs.example.test/org/_packaging/feed-b/nuget/v3")),
                ],
                settings
            )
        );

        try
        {
            foreach (
                var (feed, expected) in new[]
                {
                    ("https://pkgs.example.test/org/_packaging/feed-a/nuget/v3", "alice:a-secret"),
                    ("https://pkgs.example.test/org/_packaging/feed-b/nuget/v3/index.json", "bob:b-secret"),
                }
            )
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    "https://pkgs.example.test/org/_packaging/0f3a/nuget/v3/flat2/contoso.tool/index.json"
                );
                NuGetFeedCredentials.ApplyTo(request, new Uri(feed));

                Assert.Equal(
                    Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(expected)),
                    request.Headers.Authorization?.Parameter
                );
            }
        }
        finally
        {
            NuGetFeedCredentials.Replace([]);
        }
    }

    [Fact]
    public void ACloserCredentialThatCannotBeUsedHidesAFartherOne()
    {
        using var folder = new TempFolder();
        string closest = folder.Write(
            "closest.config",
            """
            <configuration>
              <packageSourceCredentials>
                <Private>
                  <add key="Username" value="someone" />
                  <add key="ClearTextPassword" value="kerberos-only" />
                  <add key="ValidAuthenticationTypes" value="negotiate" />
                </Private>
              </packageSourceCredentials>
            </configuration>
            """
        );
        string farthest = folder.Write(
            "farthest.config",
            """
            <configuration>
              <packageSourceCredentials>
                <Private>
                  <add key="Username" value="old" />
                  <add key="ClearTextPassword" value="basic" />
                </Private>
                <Other>
                  <add key="Username" value="other" />
                  <add key="ClearTextPassword" value="kept" />
                </Other>
              </packageSourceCredentials>
            </configuration>
            """
        );

        var settings = NuGetConfigFiles.Read([closest, farthest]);

        Assert.False(settings.Credentials.ContainsKey("Private"));
        Assert.Equal(new NuGetCredential("other", "kept"), settings.Credentials["Other"]);
    }

    [Fact]
    public void PasswordsEncryptedByTheDotNetCliAreDecrypted()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var folder = new TempFolder();
        string config = folder.Write("nuget.config", "<configuration />");
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = new DotNet().FindCandidateExecutableFiles()[0],
            WorkingDirectory = folder.Path,
            ArgumentList =
            {
                "nuget",
                "add",
                "source",
                "https://packages.example.test/v3/index.json",
                "--name",
                "Team Feed",
                "--username",
                "builder",
                "--password",
                "s3cr\u00e9t",
                "--configfile",
                config,
            },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (
            string variable in startInfo
                .Environment.Keys.Where(key =>
                    key.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase)
                    || key.StartsWith("MSBuild", StringComparison.OrdinalIgnoreCase)
                )
                .ToList()
        )
            startInfo.Environment.Remove(variable);

        using var process = System.Diagnostics.Process.Start(startInfo)!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode is 0, output);
        Assert.DoesNotContain("s3cr", File.ReadAllText(config));

        var credential = Assert.Single(NuGetConfigFiles.Read([config]).Credentials);
        Assert.Equal("Team Feed", credential.Key);
        Assert.Equal(new NuGetCredential("builder", "s3cr\u00e9t"), credential.Value);
    }

    [Fact]
    public void LocalFeedSearchOnlyListsDotNetTools()
    {
        using var feed = new LocalNuGetFeedBuilder();
        feed.WriteRawPackage("contoso.tool.1.0.0.nupkg", Nuspec("Contoso.Tool", "DotnetTool"));
        feed.WriteRawPackage("contoso.library.1.0.0.nupkg", Nuspec("Contoso.Library", null));

        var manager = new DotNet();
        var source = new ManagerSource(manager, "local", new Uri(feed.Directory));

        var found = manager.FindPackagesLocal(
            source,
            feed.Directory,
            "Contoso",
            canPrerelease: false,
            manager.TaskLogger.CreateNew(LoggableTaskType.FindPackages)
        );

        Assert.Equal("Contoso.Tool", Assert.Single(found).Id);
    }

    [Fact]
    public void UpdatesAreCheckedOnEveryConfiguredFeedAndMatchTheInstalledTool()
    {
        using var older = new LocalNuGetFeedBuilder();
        using var newer = new LocalNuGetFeedBuilder();
        older.WritePackage("Contoso.Tool", "2.0.0");
        newer.WritePackage("Contoso.Tool", "3.0.0");

        var manager = new FeedBackedDotNet(
            manager =>
                [
                    new ManagerSource(manager, "older", new Uri(older.Directory)),
                    new ManagerSource(manager, "newer", new Uri(newer.Directory)),
                ],
            "Contoso.Tool",
            "1.0.0"
        );
        manager.Initialize();

        var installed = Assert.Single(manager.GetInstalledPackages());
        var update = Assert.Single(manager.GetAvailableUpdates());

        Assert.Equal("older", installed.Source.Name);
        Assert.Equal("newer", update.Source.Name);
        Assert.Equal("1.0.0", update.VersionString);
        Assert.Equal("3.0.0", update.NewVersionString);
    }

    private static string Nuspec(string id, string? packageType) =>
        $"""
        <?xml version="1.0" encoding="utf-8"?>
        <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
          <metadata>
            <id>{id}</id>
            <version>1.0.0</version>
            <authors>Contoso</authors>
            <description>A package</description>
            {(packageType is null ? "" : $"<packageTypes><packageType name=\"{packageType}\" /></packageTypes>")}
          </metadata>
        </package>
        """;

    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Join(System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName());

        public TempFolder() => Directory.CreateDirectory(Path);

        public string Write(string name, string content)
        {
            string file = System.IO.Path.Join(Path, name);
            File.WriteAllText(file, content);
            return file;
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed class FixedSourceHelper : ReadOnlySourceHelper
    {
        private readonly IReadOnlyList<IManagerSource> _sources;

        public FixedSourceHelper(IPackageManager manager, IReadOnlyList<IManagerSource> sources)
            : base(manager, "Fixed sources")
        {
            _sources = sources;
        }

        protected override IReadOnlyList<IManagerSource> GetSources_UnSafe() => _sources;
    }

    private sealed class FeedBackedDotNet : DotNet
    {
        private readonly string _installedId;
        private readonly string _installedVersion;

        public FeedBackedDotNet(
            Func<DotNet, IReadOnlyList<IManagerSource>> sources,
            string installedId,
            string installedVersion
        )
        {
            SourcesHelper = new FixedSourceHelper(this, sources(this));
            _installedId = installedId;
            _installedVersion = installedVersion;
        }

        protected override IReadOnlyList<Package> _getInstalledPackages_UnSafe() =>
            [
                new Package(
                    _installedId,
                    _installedId,
                    _installedVersion,
                    GetInstalledPackagesSource(),
                    this,
                    new OverridenInstallationOptions(PackageScope.Global)
                ),
            ];

        protected override void _loadManagerExecutableFile(
            out bool found,
            out string path,
            out string callArguments
        )
        {
            found = true;
            path = "dotnet";
            callArguments = "tool ";
        }

        protected override void _loadManagerVersion(out string version) => version = "10.0.0";
    }

    [Fact]
    public void UpdatesFoundOnSeveralSourcesKeepTheNewestVersion()
    {
        using var older = new LocalNuGetFeedBuilder();
        using var newer = new LocalNuGetFeedBuilder();
        older.WritePackage("Contoso.Tool", "2.0.0");
        newer.WritePackage("Contoso.Tool", "3.0.0");

        var manager = new DotNet();
        var installed = new PackageBuilder()
            .WithManager(manager)
            .WithSource(manager.DefaultSource)
            .WithId("Contoso.Tool")
            .WithVersion("1.0.0")
            .Build();
        var logger = manager.TaskLogger.CreateNew(LoggableTaskType.ListUpdates);

        List<Package> found = [];
        foreach (
            var source in new[]
            {
                new ManagerSource(manager, "newer", new Uri(newer.Directory)),
                new ManagerSource(manager, "older", new Uri(older.Directory)),
            }
        )
        {
            found.AddRange(
                manager.GetAvailableUpdatesFromSource(
                    source,
                    [installed],
                    canPrerelease: false,
                    logger,
                    out int errors
                )
            );
            Assert.Equal(0, errors);
        }

        var update = Assert.Single(manager.KeepNewestUpdatePerId(found));
        Assert.Equal("3.0.0", update.NewVersionString);
        Assert.Equal("newer", update.Source.Name);
    }
}
