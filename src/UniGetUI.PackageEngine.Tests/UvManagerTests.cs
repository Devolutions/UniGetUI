using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Managers.Generic.PyPI;
using UniGetUI.PackageEngine.Managers.UvManager;
using UniGetUI.PackageEngine.Managers.UvManager.Helpers;
using UniGetUI.PackageEngine.Managers.UvManager.Models;
using UniGetUI.PackageEngine.Managers.UvManager.Services;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.Serializable;
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

        IReadOnlyList<string> versions = PyPIProjectClient.ParseVersions(json);

        Assert.Equal(["1.0.0.post1", "1.0.0", "1.0.0rc1", "0.10.0", "0.8.0"], versions);
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
        IReadOnlyList<string> uninstall = helper.GetParameters(
            package,
            new InstallOptions { CustomParameters_Uninstall = ["--verbose"] },
            OperationType.Uninstall
        );

        Assert.Equal(["install", "ruff", "--quiet"], install);
        Assert.Equal(["install", "\"ruff==0.5.0\""], installPinned);
        Assert.Equal(["upgrade", "ruff"], update);
        Assert.Equal(["uninstall", "ruff", "--verbose"], uninstall);
        Assert.Equal(OperationVeredict.Success, helper.GetResult(package, OperationType.Uninstall, [], 0));
        Assert.Equal(OperationVeredict.Failure, helper.GetResult(package, OperationType.Uninstall, [], 1));
    }
}
