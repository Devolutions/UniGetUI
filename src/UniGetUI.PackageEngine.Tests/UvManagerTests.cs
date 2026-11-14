using System.Text.Json.Nodes;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces.ManagerProviders;
using UniGetUI.PackageEngine.ManagerClasses.Manager;
using UniGetUI.PackageEngine.Managers.Generic.PyPI;
using UniGetUI.PackageEngine.Managers.UvManager;
using UniGetUI.PackageEngine.Managers.UvManager.Helpers;
using UniGetUI.PackageEngine.Managers.UvManager.Internal;
using UniGetUI.PackageEngine.Managers.UvManager.Models;
using UniGetUI.PackageEngine.Managers.UvManager.Services;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.Serializable;
using UniGetUI.PackageEngine.Structs;
using UniGetUI.PackageEngine.Tests.Infrastructure.Builders;

namespace UniGetUI.PackageEngine.Tests;

public class UvManagerTests
{
    /// <summary>
    /// Verbatim `uv tool list --show-paths --show-version-specifiers --show-python` output from
    /// uv 0.13.7 with httpie installed unpinned and ruff installed as `ruff==0.5.0`.
    /// </summary>
    private const string FullListing = """
        httpie v3.2.4 [CPython 3.13.2] (C:\temp\uv-verify\tool-dir\httpie)
        - http (C:\temp\uv-verify\bin-dir\http.exe)
        - httpie (C:\temp\uv-verify\bin-dir\httpie.exe)
        - https (C:\temp\uv-verify\bin-dir\https.exe)
        ruff v0.5.0 [required: ==0.5.0] [CPython 3.13.2] (C:\temp\uv-verify\tool-dir\ruff)
        - ruff (C:\temp\uv-verify\bin-dir\ruff.exe)
        """;

    /// <summary>
    /// Verbatim `uv tool list --outdated --show-python --show-version-specifiers` output while
    /// ruff was pinned and outdated. Up-to-date tools are omitted by uv.
    /// </summary>
    private const string OutdatedListing = """
        ruff v0.5.0 [required: ==0.5.0] [CPython 3.13.2] [latest: 0.17.4]
        - ruff
        """;

    [Fact]
    public void ParsesToolHeadersExecutablesAndAnnotations()
    {
        IReadOnlyList<UvTool> tools = UvToolListParser.Parse(FullListing.Split('\n'));

        Assert.Equal(2, tools.Count);

        UvTool httpie = tools[0];
        Assert.Equal("httpie", httpie.PackageId);
        Assert.Equal("3.2.4", httpie.Version);
        Assert.Equal("CPython 3.13.2", httpie.PythonVersion);
        Assert.Equal("C:\\temp\\uv-verify\\tool-dir\\httpie", httpie.ToolEnvironmentPath);
        Assert.Equal(["http", "httpie", "https"], httpie.Executables);
        Assert.Null(httpie.VersionSpecifier);
        Assert.Null(httpie.LatestVersion);

        UvTool ruff = tools[1];
        Assert.Equal("ruff", ruff.PackageId);
        Assert.Equal("0.5.0", ruff.Version);
        Assert.Equal("==0.5.0", ruff.VersionSpecifier);
        Assert.Equal("CPython 3.13.2", ruff.PythonVersion);
        Assert.Equal(["ruff"], ruff.Executables);
    }

    [Fact]
    public void ParsesOutdatedListingWithLatestVersion()
    {
        IReadOnlyList<UvTool> tools = UvToolListParser.Parse(OutdatedListing.Split('\n'));

        UvTool ruff = Assert.Single(tools);
        Assert.Equal("ruff", ruff.PackageId);
        Assert.Equal("0.5.0", ruff.Version);
        Assert.Equal("0.17.4", ruff.LatestVersion);
        Assert.True(ruff.IsPinnedToExactVersion);
    }

    [Fact]
    public void ParsesHumanNoticeAsEmptyListing()
    {
        Assert.Empty(UvToolListParser.Parse(["No tools installed", ""]));
    }

    [Fact]
    public void ParsesPlainListingWithoutFlags()
    {
        string[] lines =
        [
            "ruff v0.17.4",
            "- ruff",
            "httpie v3.2.4",
            "- http",
            "- httpie",
            "- https",
        ];

        IReadOnlyList<UvTool> tools = UvToolListParser.Parse(lines);

        Assert.Equal(2, tools.Count);
        Assert.Equal("0.17.4", tools[0].Version);
        Assert.Null(tools[0].PythonVersion);
        Assert.Null(tools[0].ToolEnvironmentPath);
        Assert.Equal(["ruff"], tools[0].Executables);
        Assert.Equal(3, tools[1].Executables.Count);
    }

    [Fact]
    public void ParsesVersionSpecifiersContainingSpaces()
    {
        string[] lines =
        [
            "black v24.2.0 [required: >=23, <25] [CPython 3.12.8]",
            "- black",
        ];

        UvTool tool = Assert.Single(UvToolListParser.Parse(lines));
        Assert.Equal(">=23, <25", tool.VersionSpecifier);
        Assert.False(tool.IsPinnedToExactVersion);
    }

    [Fact]
    public void DropsExecutableLineBeforeAnyHeader()
    {
        Assert.Empty(UvToolListParser.Parse(["- http"]));
    }

    [Theory]
    [InlineData("==0.5.0", true)]
    [InlineData(">=0.5.0", false)]
    [InlineData(">=23, <25", false)]
    [InlineData(null, false)]
    public void ExactPinDetectionFollowsTheSpecifier(string? specifier, bool expected)
    {
        string[] lines =
        [
            specifier is null
                ? "ruff v0.5.0"
                : $"ruff v0.5.0 [required: {specifier}]",
            "- ruff",
        ];

        UvTool tool = Assert.Single(UvToolListParser.Parse(lines));
        Assert.Equal(expected, tool.IsPinnedToExactVersion);
    }

    [Fact]
    public void ParseInstalledToolsBuildsPackages()
    {
        Uv manager = new();

        IReadOnlyList<Package> packages = Uv.ParseInstalledTools(
            UvToolListParser.Parse(FullListing.Split('\n')),
            manager.DefaultSource,
            manager
        );

        Assert.Equal(2, packages.Count);
        Assert.Equal("httpie", packages[0].Id);
        Assert.Equal("3.2.4", packages[0].VersionString);
        Assert.Equal("ruff", packages[1].Id);
        Assert.Equal("0.5.0", packages[1].VersionString);
    }

    [Fact]
    public void ParseAvailableUpdatesSkipsPinnedAndDowngradeApps()
    {
        Uv manager = new();
        IReadOnlyList<UvTool> tools = UvToolListParser.Parse(
            [
                "ruff v0.5.0 [required: ==0.5.0] [CPython 3.13.2] [latest: 0.17.4]",
                "- ruff",
                "powetra v1.0.0 [latest: 0.5.0]",
                "- powetra",
                "contosa v1.0.0 [latest: 1.1.0]",
                "- contosa",
            ]
        );

        IReadOnlyList<Package> updates = Uv.ParseAvailableUpdates(
            tools,
            manager.DefaultSource,
            manager
        );

        Package update = Assert.Single(updates);
        Assert.Equal("contosa", update.Id);
        Assert.Equal("1.0.0", update.VersionString);
        Assert.Equal("1.1.0", update.NewVersionString);
    }

    [Fact]
    public void ParseSearchMatchesBuildsPackagesWithLatestVersion()
    {
        Uv manager = new();

        IReadOnlyList<Package> packages = Uv.ParseSearchMatches(
            ["ruff", "httpie"],
            new Dictionary<string, string?> { { "ruff", "0.17.4" }, { "httpie", null } },
            manager.DefaultSource,
            manager
        );

        Assert.Equal(2, packages.Count);
        Assert.Equal("0.17.4", packages[0].VersionString);
        Assert.Equal("latest", packages[1].VersionString);
    }

    [Fact]
    public void ParsesPyPIMetadataFields()
    {
        const string json = """
            {
              "info": {
                "version": "0.5.0",
                "summary": "An extremely fast Python package manager",
                "author": "Astral",
                "maintainer": "Stargazers",
                "license": "MIT",
                "home_page": "https://astral.sh/uv",
                "package_url": "https://pypi.org/project/uv/",
                "classifiers": [
                  "License :: OSI Approved :: Apache Software License",
                  "Topic :: Software Development :: Build Tools"
                ],
                "requires_python": ">=3.8",
                "requires_dist": ["ruff>=0.4", "another; extra == 'all'"]
              },
              "url": {
                "url": "https://files.pythonhosted.org/uv-0.5.0-py3-none-any.whl",
                "digests": { "sha256": "deadbeef" },
                "upload_time": "2024-01-01T00:00:00"
              }
            }
            """;

        PyPIPackageMetadata? metadata = PyPIProjectClient.ParseMetadata(json);

        Assert.NotNull(metadata);
        Assert.Equal("0.5.0", metadata.Version);
        Assert.Equal("An extremely fast Python package manager", metadata.Summary);
        Assert.Equal("Astral", metadata.Author);
        Assert.Equal("Stargazers", metadata.Maintainer);
        Assert.Contains("License :: OSI Approved :: Apache Software License", metadata.Classifiers);
        Assert.Equal("https://astral.sh/uv", metadata.Homepage!.ToString());
        Assert.Equal(">=3.8", metadata.RequiresPython);
        Assert.Equal(2, metadata.RequiresDist.Count);

        PyPIInstallerInfo? installer = PyPIProjectClient.ParseInstaller(json);

        Assert.NotNull(installer);
        Assert.Equal("https://files.pythonhosted.org/uv-0.5.0-py3-none-any.whl", installer.InstallerUrl.ToString());
        Assert.Equal("deadbeef", installer.InstallerHash);
        Assert.Equal("Wheel", installer.InstallerType);
        Assert.Equal("2024-01-01T00:00:00", installer.UploadTime);
    }

    [Fact]
    public void OrdersPyPIVersionsNewestFirst()
    {
        const string json = """
            {
              "releases": {
                "0.8.0": [],
                "0.10.0": [],
                "1.0.0rc1": [],
                "1.0.0": [],
                "1.0.0.post1": [],
                "mangled": []
              }
            }
            """;

        IReadOnlyList<string> versions = PyPIProjectClient.ParseVersions(json, includePreReleases: true);

        Assert.Equal(["1.0.0.post1", "1.0.0", "1.0.0rc1", "0.10.0", "0.8.0"], versions);
    }

    [Fact]
    public void ParseVersionsStableOnlyWhenPreReleasesAreExcluded()
    {
        const string json = """
            {
              "releases": {
                "1.0.0": [
                  {
                    "filename": "sample-1.0.0-py3-none-any.whl",
                    "url": "https://files.pythonhosted.org/packages/ab/cd/sample-1.0.0-py3-none-any.whl",
                    "yanked": false,
                    "yanked_reason": null
                  }
                ],
                "1.0.0rc1": [
                  {
                    "filename": "sample-1.0.0rc1-py3-none-any.whl",
                    "url": "https://files.pythonhosted.org/packages/ef/01/sample-1.0.0rc1-py3-none-any.whl",
                    "yanked": false,
                    "yanked_reason": null
                  }
                ],
                "1.0.0.post1": [
                  {
                    "filename": "sample-1.0.0.post1-py3-none-any.whl",
                    "url": "https://files.pythonhosted.org/packages/23/45/sample-1.0.0.post1-py3-none-any.whl",
                    "yanked": false,
                    "yanked_reason": null
                  }
                ],
                "2.0.0b1": [
                  {
                    "filename": "sample-2.0.0b1-py3-none-any.whl",
                    "url": "https://files.pythonhosted.org/packages/67/89/sample-2.0.0b1-py3-none-any.whl",
                    "yanked": true,
                    "yanked_reason": "Broken release"
                  }
                ]
              }
            }
            """;

        IReadOnlyList<string> stable = PyPIProjectClient.ParseVersions(json, includePreReleases: false);

        Assert.Equal(["1.0.0.post1", "1.0.0"], stable);

        IReadOnlyList<string> withPreReleases = PyPIProjectClient.ParseVersions(json, includePreReleases: true);

        Assert.Equal(["1.0.0.post1", "1.0.0", "1.0.0rc1"], withPreReleases);
    }

    [Fact]
    public void ParseVersionsDropsFullyYankedReleases()
    {
        const string json = """
            {
              "releases": {
                "20.5": [
                  {
                    "filename": "packaging-20.5-py2.py3-none-any.whl",
                    "url": "https://files.pythonhosted.org/packages/0e/75/dc57e8d75c9f4e7ada4d30230875b66633e250504566bdb4d98615808cd1/packaging-20.5-py2.py3-none-any.whl",
                    "yanked": false,
                    "yanked_reason": null
                  },
                  {
                    "filename": "packaging-20.5.tar.gz",
                    "url": "https://files.pythonhosted.org/packages/61/b8/5a96ce56fcdf1bf7494f642ec7fad3e8c192b3a5b2116b59955ff32f70de/packaging-20.5.tar.gz",
                    "yanked": false,
                    "yanked_reason": null
                  }
                ],
                "20.6": [
                  {
                    "filename": "packaging-20.6-py2.py3-none-any.whl",
                    "url": "https://files.pythonhosted.org/packages/06/ef/89d5ce843d4ccaf93d78c93c3c1973897552afafe69df20fb0ed6a1ed40f/packaging-20.6-py2.py3-none-any.whl",
                    "yanked": true,
                    "yanked_reason": "Incorrect git commit was used for cutting release"
                  },
                  {
                    "filename": "packaging-20.6.tar.gz",
                    "url": "https://files.pythonhosted.org/packages/60/fa/4f3c98e84c945958d0633b16d3a8b1a739f649af1090929309b0e9e0c91b/packaging-20.6.tar.gz",
                    "yanked": true,
                    "yanked_reason": "Incorrect git commit was used for cutting release"
                  }
                ],
                "20.7": [
                  {
                    "filename": "packaging-20.7-py2.py3-none-any.whl",
                    "url": "https://files.pythonhosted.org/packages/28/87/8edcf555adaf60d053ead881bc056079e29319b643ca710339ce84413136/packaging-20.7-py2.py3-none-any.whl",
                    "yanked": false,
                    "yanked_reason": null
                  },
                  {
                    "filename": "packaging-20.7.tar.gz",
                    "url": "https://files.pythonhosted.org/packages/16/7c/33ae3aa02eb10ca726b21aa88d338e3f619c674e4fb8544eb352330d880a/packaging-20.7.tar.gz",
                    "yanked": true,
                    "yanked_reason": null
                  }
                ],
                "21.0": []
              }
            }
            """;

        IReadOnlyList<string> versions = PyPIProjectClient.ParseVersions(json, includePreReleases: true);

        Assert.Equal(["21.0", "20.7", "20.5"], versions);
        Assert.DoesNotContain("20.6", versions);
    }

    [Fact]
    public void ManagerDeclaresTheUvIdentity()
    {
        Uv manager = new();

        Assert.Equal("uv", manager.Id);
        Assert.Equal("Uv", manager.Name);
        Assert.Equal("PACKAGE_MANAGERS.md#uv", manager.Properties.SetupGuide);
        Assert.True(manager.Capabilities.CanUninstall);
        Assert.True(manager.Capabilities.SupportsCustomVersions);
        Assert.True(manager.Capabilities.SupportsPreRelease);
    }

    [Fact]
    public void OperationHelperEmitsUvToolVerbs()
    {
        Uv manager = new();
        Package package = new PackageBuilder().WithManager(manager).WithId("ruff").Build();
        UvPkgOperationHelper helper = (UvPkgOperationHelper)manager.OperationHelper;

        IReadOnlyList<string> install = helper.GetParameters(
            package,
            new InstallOptions { CustomParameters_Install = ["--quiet"] },
            OperationType.Install
        );
        IReadOnlyList<string> installPinned = helper.GetParameters(
            package,
            new InstallOptions { Version = "0.5.0" },
            OperationType.Install
        );
        IReadOnlyList<string> update = helper.GetParameters(
            package,
            new InstallOptions(),
            OperationType.Update
        );
        IReadOnlyList<string> updatePinned = helper.GetParameters(
            package,
            new InstallOptions { Version = "0.5.0", VersionIsExplicit = true },
            OperationType.Update
        );
        IReadOnlyList<string> uninstall = helper.GetParameters(
            package,
            new InstallOptions { CustomParameters_Uninstall = ["--verbose"] },
            OperationType.Uninstall
        );
        IReadOnlyList<string> installPreRelease = helper.GetParameters(
            package,
            new InstallOptions { PreRelease = true },
            OperationType.Install
        );
        IReadOnlyList<string> updatePreRelease = helper.GetParameters(
            package,
            new InstallOptions { PreRelease = true },
            OperationType.Update
        );
        IReadOnlyList<string> uninstallPreRelease = helper.GetParameters(
            package,
            new InstallOptions { PreRelease = true },
            OperationType.Uninstall
        );

        Assert.Equal(["install", "ruff", "--quiet"], install);
        Assert.Equal(["install", "\"ruff==0.5.0\""], installPinned);
        Assert.Equal(["upgrade", "ruff"], update);
        Assert.Equal(["upgrade", "\"ruff==0.5.0\""], updatePinned);
        Assert.Equal(["uninstall", "ruff", "--verbose"], uninstall);
        Assert.Equal(["install", "ruff", "--prerelease", "allow"], installPreRelease);
        Assert.Equal(["upgrade", "ruff", "--prerelease", "allow"], updatePreRelease);
        Assert.Equal(["uninstall", "ruff"], uninstallPreRelease);
        Assert.Equal(OperationVeredict.Success, helper.GetResult(package, OperationType.Uninstall, [], 0));
        Assert.Equal(OperationVeredict.Failure, helper.GetResult(package, OperationType.Uninstall, [], 1));
    }
}

public sealed class UvInstallOptionsTests : IDisposable
{
    private readonly string _recordsFile = Path.Combine(
        Path.GetTempPath(),
        $"uv-records-test-{Guid.NewGuid():N}.json"
    );

    /// <summary>
    /// Verbatim `uv python list --output-format json` rows from uv 0.13.7 on Windows: two
    /// uv-managed CPython builds on disk (default and freethreaded variants are separate rows
    /// in the text listing, but this fixture keeps one of each), plus three bare download rows.
    /// </summary>
    private const string PythonJson = """
        [
          {"key":"cpython-3.13.2-windows-x86_64-none","version":"3.13.2","version_parts":{"major":3,"minor":13,"patch":2},"path":"C:\\Users\\me\\AppData\\Roaming\\uv\\python\\cpython-3.13.2-windows-x86_64-none\\python.exe","symlink":null,"url":null,"os":"windows","variant":"default","implementation":"cpython","arch":"x86_64","libc":"none"},
          {"key":"cpython-3.12.8+freethreaded-windows-x86_64-none","version":"3.12.8","version_parts":{"major":3,"minor":12,"patch":8},"path":"C:\\Users\\me\\AppData\\Roaming\\uv\\python\\cpython-3.12.8-freethreaded-windows-x86_64-none\\python.exe","symlink":null,"url":null,"os":"windows","variant":"freethreaded","implementation":"cpython","arch":"x86_64","libc":"none"},
          {"key":"cpython-3.11.18-windows-x86_64-none","version":"3.11.18","version_parts":{"major":3,"minor":11,"patch":18},"path":null,"symlink":null,"url":"https://releases.astral.sh/python-build-standalone/20261026/cpython-3.11.18.tar.gz","os":"windows","variant":"default","implementation":"cpython","arch":"x86_64","libc":"none"},
          {"key":"pypy-3.11.17-windows-x86_64-none","version":"3.11.17","version_parts":{"major":3,"minor":11,"patch":17},"path":null,"symlink":null,"url":"https://downloads.python.org/pypy/pypy3.11-v8.0.1-win64.zip","os":"windows","variant":"default","implementation":"pypy","arch":"x86_64","libc":"none"},
          {"key":"graalpy-3.12.0-windows-x86_64-none","version":"3.12.0","version_parts":{"major":3,"minor":12,"patch":0},"path":null,"symlink":null,"url":"https://github.com/oracle/graalpython/releases/download/graal-25.2.4/graalpy-3.12-25.2.4-windows-amd64.zip","os":"windows","variant":"default","implementation":"graalpy","arch":"x86_64","libc":"none"}
        ]
        """;

    public void Dispose()
    {
        File.Delete(_recordsFile);
    }

    [Fact]
    public void ParsesPythonListingIntoInstalledAndDownloadableEntries()
    {
        IReadOnlyList<UvPythonInstall> interpreters = UvPythonService.ParseJson(PythonJson);

        Assert.Equal(5, interpreters.Count);

        UvPythonInstall installed = interpreters[0];
        Assert.Equal("cpython-3.13.2-windows-x86_64-none", installed.Key);
        Assert.Equal("3.13.2", installed.Version);
        Assert.Equal("cpython", installed.Implementation);
        Assert.Equal("default", installed.Variant);
        Assert.True(installed.IsInstalled);

        UvPythonInstall freethreaded = interpreters[1];
        Assert.Equal("freethreaded", freethreaded.Variant);
        Assert.True(freethreaded.IsInstalled);

        UvPythonInstall download = interpreters[2];
        Assert.Null(download.Path);
        Assert.Equal("https://releases.astral.sh/python-build-standalone/20261026/cpython-3.11.18.tar.gz", download.DownloadUrl);
        Assert.False(download.IsInstalled);

        Assert.Equal("pypy", interpreters[3].Implementation);
        Assert.Equal("graalpy", interpreters[4].Implementation);
    }

    [Fact]
    public void PythonListingRequiresAnArrayAndPopulatedRows()
    {
        Assert.Throws<InvalidDataException>(() => UvPythonService.ParseJson("{}"));
        Assert.Throws<InvalidDataException>(() => UvPythonService.ParseJson("[]"));
        Assert.Throws<InvalidDataException>(() => UvPythonService.ParseJson("""[{"key":"cpython-3.13"}]"""));
    }

    [Fact]
    public void InstallFlagsFollowTheUserRequest()
    {
        Uv manager = new();
        Package package = new PackageBuilder()
            .WithManager(manager)
            .WithId("ruff")
            .WithOptions(new OverridenInstallationOptions
            {
                Uv_PythonRequest = "3.12",
                Uv_ManagedPython = true,
            })
            .Build();
        UvPkgOperationHelper helper = (UvPkgOperationHelper)manager.OperationHelper;

        IReadOnlyList<string> install = helper.GetParameters(
            package,
            new InstallOptions(),
            OperationType.Install
        );
        IReadOnlyList<string> uninstall = helper.GetParameters(
            package,
            new InstallOptions(),
            OperationType.Uninstall
        );

        Assert.Equal(["install", "ruff", "--python", "\"3.12\"", "--managed-python"], install);
        Assert.Equal(["uninstall", "ruff"], uninstall);

        OverridenInstallationOptions systemOnly = new()
        {
            Uv_PythonRequest = "3.13.2",
            Uv_ManagedPython = false,
            Uv_NoPythonDownloads = true,
        };
        IReadOnlyList<string> systemOnlyInstall = helper.GetParameters(
            new PackageBuilder()
                .WithManager(manager)
                .WithId("httpie")
                .WithOptions(systemOnly)
                .Build(),
            new InstallOptions(),
            OperationType.Install
        );

        Assert.Equal(
            ["install", "httpie", "--python", "\"3.13.2\"", "--no-managed-python", "--no-python-downloads"],
            systemOnlyInstall
        );
    }

    [Fact]
    public void SuccessfulInstallsAreRecordedAndUpgradesRepeatTheRequest()
    {
        UvToolInstallRecords.TestPathOverride = _recordsFile;
        try
        {
            Uv manager = new();
            UvPkgOperationHelper helper = (UvPkgOperationHelper)manager.OperationHelper;
            Package package = new PackageBuilder()
                .WithManager(manager)
                .WithId("ruff")
                .WithVersion("0.5.0")
                .WithOptions(new OverridenInstallationOptions { Uv_PythonRequest = "3.12" })
                .Build();

            Assert.Null(UvToolInstallRecords.Get("ruff"));
            Assert.Equal(
                OperationVeredict.Success,
                helper.GetResult(package, OperationType.Install, [], 0)
            );

            UvToolInstallRecord? record = UvToolInstallRecords.Get("ruff");
            Assert.NotNull(record);
            Assert.Equal("0.5.0", record.Version);
            Assert.Equal("3.12", record.PythonRequest);
            Assert.Null(record.ManagedPython);
            Assert.Equal("uv", record.Source);
            Assert.Equal(1, record.SchemaVersion);

            Package upgrade = new PackageBuilder()
                .WithManager(manager)
                .WithId("ruff")
                .WithVersion("0.5.0")
                .WithNewVersion("0.17.4")
                .Build();
            IReadOnlyList<string> upgradeParameters = helper.GetParameters(
                upgrade,
                new InstallOptions(),
                OperationType.Update
            );
            Assert.Contains("--python", upgradeParameters);
            Assert.Contains("\"3.12\"", upgradeParameters);

            Assert.Equal(
                OperationVeredict.Success,
                helper.GetResult(upgrade, OperationType.Update, [], 0)
            );
            Assert.Equal("0.17.4", UvToolInstallRecords.Get("ruff")!.Version);

            Assert.Equal(
                OperationVeredict.Success,
                helper.GetResult(package, OperationType.Uninstall, [], 0)
            );
            Assert.Null(UvToolInstallRecords.Get("ruff"));
        }
        finally
        {
            UvToolInstallRecords.TestPathOverride = null;
        }
    }

    [Fact]
    public void FreshInstallsWithoutOptionsRecordDefaultBehaviour()
    {
        UvToolInstallRecords.TestPathOverride = _recordsFile;
        try
        {
            Uv manager = new();
            UvPkgOperationHelper helper = (UvPkgOperationHelper)manager.OperationHelper;
            Package package = new PackageBuilder()
                .WithManager(manager)
                .WithId("httpie")
                .WithVersion("3.2.4")
                .Build();

            Assert.Equal(
                OperationVeredict.Success,
                helper.GetResult(package, OperationType.Install, [], 0)
            );

            UvToolInstallRecord? record = UvToolInstallRecords.Get("httpie");
            Assert.NotNull(record);
            Assert.Null(record.PythonRequest);
            Assert.Null(record.ManagedPython);
            Assert.False(record.NoPythonDownloads);

            IReadOnlyList<string> parameters = helper.GetParameters(
                package,
                new InstallOptions(),
                OperationType.Update
            );
            Assert.Equal(["upgrade", "httpie"], parameters);
        }
        finally
        {
            UvToolInstallRecords.TestPathOverride = null;
        }
    }

    [Fact]
    public void UvOptionsSurviveAsJsonNodeRoundTrip()
    {
        InstallOptions options = new()
        {
            Uv_PythonRequest = "3.13.2",
            Uv_ManagedPython = "false",
            Uv_NoPythonDownloads = true,
        };

        InstallOptions reloaded = new(options.AsJsonNode());

        Assert.Equal("3.13.2", reloaded.Uv_PythonRequest);
        Assert.Equal("false", reloaded.Uv_ManagedPython);
        Assert.True(reloaded.Uv_NoPythonDownloads);
    }

    [Fact]
    public void ClearedManagedPythonStaysOutOfSerializedOutput()
    {
        InstallOptions options = new() { Uv_ManagedPython = "true" };
        options.Uv_ManagedPython = "";

        JsonObject serialized = options.AsJsonNode();

        Assert.Null(serialized["UvManagedPython"]);
    }

    [Fact]
    public void SavedUvOptionsLandInThePackageOverrides()
    {
        Uv manager = new();
        Package package = new PackageBuilder()
            .WithManager(manager)
            .WithId("ruff")
            .Build();
        UvPkgOperationHelper helper = (UvPkgOperationHelper)manager.OperationHelper;

        helper.ApplyOperationOverrides(
            package,
            new InstallOptions
            {
                Uv_PythonRequest = "3.13.2",
                Uv_ManagedPython = "false",
                Uv_NoPythonDownloads = true,
            },
            OperationType.Install
        );

        Assert.Equal("3.13.2", package.OverridenOptions.Uv_PythonRequest);
        Assert.False(package.OverridenOptions.Uv_ManagedPython);
        Assert.True(package.OverridenOptions.Uv_NoPythonDownloads);
    }

    [Fact]
    public void EmptySavedUvOptionsLeaveFreshOverridesUntouched()
    {
        Uv manager = new();
        Package package = new PackageBuilder()
            .WithManager(manager)
            .WithId("httpie")
            .Build();
        UvPkgOperationHelper helper = (UvPkgOperationHelper)manager.OperationHelper;

        helper.ApplyOperationOverrides(package, new InstallOptions(), OperationType.Install);

        Assert.Null(package.OverridenOptions.Uv_PythonRequest);
        Assert.Null(package.OverridenOptions.Uv_ManagedPython);
        Assert.False(package.OverridenOptions.Uv_NoPythonDownloads);
    }

    [Fact]
    public void TheInterpreterCatalogueComesFromUvPythonList()
    {
        Uv manager = new();
        manager.Initialize();
        if (!manager.Status.Found)
            return;

        IReadOnlyList<PythonInterpreterChoice> choices =
            manager.OperationHelper.ListPythonInterpreters();

        Assert.NotEmpty(choices);
        Assert.All(choices, choice => Assert.False(string.IsNullOrWhiteSpace(choice.Request)));
    }

    [Fact]
    public void ThePreviewHonorsTheUvOptionsItWasHandedWithoutTouchingThePackage()
    {
        Uv manager = new();
        Package package = new PackageBuilder().WithManager(manager).WithId("ruff").Build();
        InstallOptions options = new()
        {
            Uv_PythonRequest = "3.13.2",
            Uv_ManagedPython = "true",
            Uv_NoPythonDownloads = true,
        };

        IReadOnlyList<string> parameters = manager.OperationHelper.GetStandaloneParameters(
            package,
            options,
            OperationType.Install
        );

        Assert.Contains("--python", parameters);
        Assert.Contains("\"3.13.2\"", parameters);
        Assert.Contains("--managed-python", parameters);
        Assert.Contains("--no-python-downloads", parameters);
        Assert.Null(package.OverridenOptions.Uv_PythonRequest);
    }

    [Fact]
    public void UvRanksVersionsWithPep440AndFallsBackWhenItsParserRejects()
    {
        Uv manager = new();

        // PEP 440 ranks a pre-release below its release; the numeric fallback would return
        // null here because "0rc1" trips its digit-letter-digit ambiguity check.
        Assert.True(manager.CompareVersions("1.0.0rc1", "1.0.0") < 0);
        Assert.True(manager.CompareVersions("1.0.0", "1.0.0rc1") > 0);

        // An implicit post-release ranks above its release (the PyPI column of the table in
        // AGENTS.md's version-comparison section).
        Assert.True(manager.CompareVersions("1.0.0-1", "1.0.0") > 0);

        // A version the PEP 440 parser rejects reaches the base numeric comparison, which
        // reads letter-only strings as zero.
        Assert.Equal(0, manager.CompareVersions("nonsense", "0"));
    }
}
