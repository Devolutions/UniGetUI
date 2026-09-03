using UniGetUI.Interface.Enums;
using UniGetUI.PackageEngine.Classes.Manager.Classes;
using UniGetUI.PackageEngine.Serializable;

namespace UniGetUI.PackageEngine.Tests;

public sealed class BundleImportFilterTests
{
    private static (InstallOptions Options, BundleReport Report) Filter(
        InstallOptions options,
        bool allowCli = true,
        bool allowPrePost = true,
        bool shellInterpreted = true
    )
    {
        var report = new BundleReport { IsEmpty = true };
        var filtered = BundleImportFilter.Apply(
            ref report,
            "Contoso.Test",
            options,
            allowCli,
            allowPrePost,
            shellInterpreted
        );
        return (filtered, report);
    }

    private static IEnumerable<BundleReportEntry> EntriesFor(BundleReport report) =>
        report.Contents.TryGetValue("Contoso.Test", out var entries) ? entries : [];

    [Fact]
    public void ALegitimateVersionIsKeptAndDoesNotTriggerTheReport()
    {
        var (options, report) = Filter(new InstallOptions { Version = "1.2.3" });

        Assert.Equal("1.2.3", options.Version);
        Assert.True(report.IsEmpty);
    }

    [Fact]
    public void AnEmptyBundleProducesAnEmptyReport()
    {
        var (_, report) = Filter(new InstallOptions());

        Assert.True(report.IsEmpty);
        Assert.Empty(report.Contents);
    }

    [Theory]
    [InlineData("1.2.3.4.5; Start-Process calc")]
    [InlineData("1.0$(calc)")]
    [InlineData("1.0`ncalc")]
    [InlineData("1.0 --index-url http://evil.example")]
    [InlineData("2021 Update")]
    [InlineData("1.0'; calc; '")]
    public void AnOutOfPatternVersionIsStrippedAndReported(string version)
    {
        var (options, report) = Filter(new InstallOptions { Version = version });

        Assert.Equal("", options.Version);
        Assert.False(report.IsEmpty);
        var entry = Assert.Single(EntriesFor(report));
        Assert.False(entry.Allowed);
        Assert.Contains("Requested version", entry.Line);
    }

    // WinGet publishes versions containing spaces and receives them as one quoted argument, so
    // importing such a bundle must not silently clear the pinned version.
    [Fact]
    public void AVersionWithSpacesIsKeptForADirectExecManager()
    {
        var (options, report) = Filter(
            new InstallOptions { Version = "2021 Update" },
            shellInterpreted: false
        );

        Assert.Equal("2021 Update", options.Version);
        Assert.True(report.IsEmpty);
    }

    [Fact]
    public void ASeparatorInAVersionIsLeftAloneForADirectExecManager()
    {
        var (options, report) = Filter(
            new InstallOptions { Version = "1.0; calc" },
            shellInterpreted: false
        );

        Assert.Equal("1.0; calc", options.Version);
        Assert.True(report.IsEmpty);
    }

    [Fact]
    public void PrePostCommandsAreStrippedWhenTheSecureSettingIsOff()
    {
        var (options, report) = Filter(
            new InstallOptions { PreInstallCommand = "calc" },
            allowPrePost: false
        );

        Assert.Equal("", options.PreInstallCommand);
        Assert.False(report.IsEmpty);
        Assert.False(Assert.Single(EntriesFor(report)).Allowed);
    }

    [Fact]
    public void PrePostCommandsAreKeptButStillReportedWhenTheSecureSettingIsOn()
    {
        var (options, report) = Filter(
            new InstallOptions { PreInstallCommand = "calc" },
            allowPrePost: true
        );

        Assert.Equal("calc", options.PreInstallCommand);
        Assert.False(report.IsEmpty);
        Assert.True(Assert.Single(EntriesFor(report)).Allowed);
    }

    [Fact]
    public void CustomArgumentsAreClearedWhenTheSecureSettingIsOff()
    {
        var (options, report) = Filter(
            new InstallOptions { CustomParameters_Install = ["--evil"] },
            allowCli: false
        );

        Assert.Empty(options.CustomParameters_Install);
        Assert.False(report.IsEmpty);
    }

    [Fact]
    public void EveryStrippedFieldIsReportedIndividually()
    {
        var (options, report) = Filter(
            new InstallOptions
            {
                Version = "1.0; calc",
                PreInstallCommand = "calc",
                CustomParameters_Install = ["--evil"],
            },
            allowCli: false,
            allowPrePost: false
        );

        Assert.Equal("", options.Version);
        Assert.Equal("", options.PreInstallCommand);
        Assert.Empty(options.CustomParameters_Install);
        Assert.Equal(3, EntriesFor(report).Count());
        Assert.All(EntriesFor(report), entry => Assert.False(entry.Allowed));
    }
}
