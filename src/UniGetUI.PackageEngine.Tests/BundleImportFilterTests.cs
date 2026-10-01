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
        bool shellInterpreted = true,
        string sourceName = "",
        BundleSourceStatus sourceStatus = BundleSourceStatus.Default
    )
    {
        var report = new BundleReport { IsEmpty = true };
        var filtered = BundleImportFilter.Apply(
            ref report,
            new BundleReportSubject("Contoso.Test", "Contoso Test", "Winget", "winget"),
            options,
            allowCli,
            allowPrePost,
            shellInterpreted,
            sourceName,
            sourceStatus
        );
        return (filtered, report);
    }

    private static IEnumerable<BundleReportEntry> EntriesFor(BundleReport report) =>
        report.Contents.Values.SelectMany(package => package.Entries);

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

    [Fact]
    public void SkipHashCheckIsReportedAsHighSeverityWithoutBeingStripped()
    {
        var (options, report) = Filter(new InstallOptions { SkipHashCheck = true });

        Assert.True(options.SkipHashCheck);
        var entry = Assert.Single(EntriesFor(report));
        Assert.Equal(nameof(InstallOptions.SkipHashCheck), entry.Field);
        Assert.Equal("true", entry.Value);
        Assert.Equal(entry.Label, entry.Line);
        Assert.False(entry.LineCarriesTheValue);
        Assert.Equal(BundleReportSeverity.High, entry.Severity);
        Assert.True(entry.Allowed);
        Assert.True(report.HasHighSeverityFindings);
    }

    [Fact]
    public void RunAsAdministratorIsReportedButDoesNotGateTheImport()
    {
        var (options, report) = Filter(new InstallOptions { RunAsAdministrator = true });

        Assert.True(options.RunAsAdministrator);
        var entry = Assert.Single(EntriesFor(report));
        Assert.Equal(nameof(InstallOptions.RunAsAdministrator), entry.Field);
        Assert.Equal(BundleReportSeverity.Info, entry.Severity);
        Assert.False(report.HasHighSeverityFindings);
    }

    [Fact]
    public void KillBeforeOperationIsReportedAsHighSeverityWithEveryProcessNamed()
    {
        var (options, report) = Filter(
            new InstallOptions { KillBeforeOperation = ["explorer", "msedge"] }
        );

        Assert.Equal(["explorer", "msedge"], options.KillBeforeOperation);
        var entry = Assert.Single(EntriesFor(report));
        Assert.Equal(nameof(InstallOptions.KillBeforeOperation), entry.Field);
        Assert.Equal("explorer, msedge", entry.Value);
        Assert.Equal(BundleReportSeverity.High, entry.Severity);
    }

    [Fact]
    public void ACustomInstallLocationIsReportedAsInformational()
    {
        var (options, report) = Filter(
            new InstallOptions { CustomInstallLocation = @"C:\Windows\System32" }
        );

        Assert.Equal(@"C:\Windows\System32", options.CustomInstallLocation);
        var entry = Assert.Single(EntriesFor(report));
        Assert.Equal(nameof(InstallOptions.CustomInstallLocation), entry.Field);
        Assert.Equal("Custom install location", entry.Label);
        Assert.Equal(@"C:\Windows\System32", entry.Value);
        Assert.True(entry.LineCarriesTheValue);
        Assert.Equal(BundleReportSeverity.Info, entry.Severity);
        Assert.False(report.HasHighSeverityFindings);
    }

    [Fact]
    public void ADefaultSourceIsNotReported()
    {
        var (_, report) = Filter(new InstallOptions(), sourceName: "winget");

        Assert.True(report.IsEmpty);
    }

    [Fact]
    public void AKnownNonDefaultSourceIsReportedAsInformational()
    {
        var (_, report) = Filter(
            new InstallOptions(),
            sourceName: "msstore",
            sourceStatus: BundleSourceStatus.Known
        );

        var entry = Assert.Single(EntriesFor(report));
        Assert.Equal("Source", entry.Field);
        Assert.Equal("msstore", entry.Value);
        Assert.Equal(BundleReportSeverity.Info, entry.Severity);
        Assert.False(report.HasHighSeverityFindings);
    }

    [Fact]
    public void ASourceTheManagerDoesNotKnowIsReportedButDoesNotGateTheImport()
    {
        var (_, report) = Filter(
            new InstallOptions(),
            sourceName: "http://evil.example/feed",
            sourceStatus: BundleSourceStatus.Unknown
        );

        var entry = Assert.Single(EntriesFor(report));
        Assert.Equal("Source", entry.Field);
        Assert.Equal("Unknown package source", entry.Label);
        Assert.Equal(BundleReportSeverity.Info, entry.Severity);
        Assert.False(report.HasHighSeverityFindings);
    }

    [Fact]
    public void TheAlreadyExistingChecksCarryAHighSeverity()
    {
        var (_, report) = Filter(
            new InstallOptions
            {
                Version = "1.0; calc",
                PreInstallCommand = "calc",
                CustomParameters_Install = ["--evil"],
            },
            allowCli: false,
            allowPrePost: false
        );

        Assert.All(
            EntriesFor(report),
            entry => Assert.Equal(BundleReportSeverity.High, entry.Severity)
        );
        Assert.Equal(
            [
                nameof(InstallOptions.CustomParameters_Install),
                nameof(InstallOptions.PreInstallCommand),
                nameof(InstallOptions.Version),
            ],
            EntriesFor(report).Select(entry => entry.Field).Order()
        );
        Assert.True(report.HasHighSeverityFindings);
    }

    [Fact]
    public void AnInformationalOnlyBundleDoesNotTriggerTheImportGate()
    {
        var (_, report) = Filter(
            new InstallOptions { CustomInstallLocation = "D:\\Apps" },
            sourceName: "msstore",
            sourceStatus: BundleSourceStatus.Known
        );

        Assert.Equal(2, EntriesFor(report).Count());
        Assert.False(report.HasHighSeverityFindings);
    }

    [Fact]
    public void TheReportCarriesThePackageNameAndManagerForDisplay()
    {
        var (_, report) = Filter(new InstallOptions { RunAsAdministrator = true });

        var package = Assert.Single(report.Contents).Value;
        Assert.Equal("Contoso.Test", package.Subject.Id);
        Assert.Equal("Contoso Test", package.Subject.Name);
        Assert.Equal("Winget", package.Subject.ManagerName);
        Assert.Equal("winget", package.Subject.Source);
        Assert.Equal("Contoso Test", package.Subject.DisplayName);
    }

    [Fact]
    public void ASubjectWithoutANameFallsBackToItsIdForDisplay()
    {
        Assert.Equal(
            "Contoso.Test",
            new BundleReportSubject("Contoso.Test", "", "Winget", "winget").DisplayName);
    }

    [Fact]
    public void TheReportCountsFindingsBySeverityAndTracksStrippedOnes()
    {
        var (_, report) = Filter(
            new InstallOptions
            {
                RunAsAdministrator = true,
                PreInstallCommand = "calc",
                CustomInstallLocation = @"D:\Apps",
            },
            allowPrePost: false
        );

        Assert.Equal(1, report.HighSeverityCount);
        Assert.Equal(2, report.InformationalCount);
        Assert.True(report.HasStrippedFindings);
        Assert.Equal(3, report.AllEntries.Count());
    }

    [Fact]
    public void AReportWithNothingStrippedSaysSo()
    {
        var (_, report) = Filter(new InstallOptions { SkipHashCheck = true });

        Assert.False(report.HasStrippedFindings);
    }

    [Fact]
    public void APackageIsFlaggedRiskyOnlyWhenItCarriesAHighSeverityFinding()
    {
        var (_, risky) = Filter(new InstallOptions { SkipHashCheck = true });
        var (_, tame) = Filter(new InstallOptions { RunAsAdministrator = true });

        Assert.True(Assert.Single(risky.Contents).Value.HasHighSeverityFindings);
        Assert.False(Assert.Single(tame.Contents).Value.HasHighSeverityFindings);
    }

    [Fact]
    public void TheSamePackageIdFromTwoManagersIsKeptApart()
    {
        var report = new BundleReport { IsEmpty = true };
        BundleImportFilter.Apply(
            ref report,
            new BundleReportSubject("nodejs", "Node.js", "Scoop", "main"),
            new InstallOptions { RunAsAdministrator = true },
            true, true, false);
        BundleImportFilter.Apply(
            ref report,
            new BundleReportSubject("nodejs", "Node.js", "Chocolatey", "chocolatey"),
            new InstallOptions { SkipHashCheck = true },
            true, true, false);

        Assert.Equal(2, report.Contents.Count);
        Assert.Equal(
            ["Chocolatey", "Scoop"],
            report.Contents.Values.Select(entry => entry.Subject.ManagerName).Order());
    }

    [Fact]
    public void AValueStrippedBySecureSettingsIsMarkedAsReEnableable()
    {
        var (_, report) = Filter(
            new InstallOptions { PreInstallCommand = "calc" },
            allowPrePost: false
        );

        Assert.True(Assert.Single(EntriesFor(report)).StrippedBySetting);
        Assert.True(report.HasSettingControlledStripping);
    }

    [Fact]
    public void AVersionStrippedByPatternIsNotPresentedAsReEnableable()
    {
        var (_, report) = Filter(new InstallOptions { Version = "1.0; calc" });

        var entry = Assert.Single(EntriesFor(report));
        Assert.False(entry.Allowed);
        Assert.False(entry.StrippedBySetting);
        Assert.True(report.HasStrippedFindings);
        Assert.False(report.HasSettingControlledStripping);
    }

    [Fact]
    public void OnlyTheFindingsThatSurviveAsHighSeverityGateTheImport()
    {
        var (_, report) = Filter(
            new InstallOptions
            {
                RunAsAdministrator = true,
                CustomInstallLocation = @"D:\Apps",
            },
            sourceName: "http://evil.example/feed",
            sourceStatus: BundleSourceStatus.Unknown
        );

        Assert.Equal(3, report.InformationalCount);
        Assert.Equal(0, report.HighSeverityCount);
        Assert.False(report.HasHighSeverityFindings);
    }

    [Fact]
    public void SkipHashCheckAndProcessKillsStillGateTheImport()
    {
        var (_, hash) = Filter(new InstallOptions { SkipHashCheck = true });
        var (_, kills) = Filter(new InstallOptions { KillBeforeOperation = ["explorer"] });

        Assert.True(hash.HasHighSeverityFindings);
        Assert.True(kills.HasHighSeverityFindings);
    }

    [Fact]
    public void TheSamePackageIdFromTwoSourcesOfOneManagerIsKeptApart()
    {
        var report = new BundleReport { IsEmpty = true };
        BundleImportFilter.Apply(
            ref report,
            new BundleReportSubject("Contoso.App", "App", "Winget", "winget"),
            new InstallOptions { SkipHashCheck = true },
            true, true, false);
        BundleImportFilter.Apply(
            ref report,
            new BundleReportSubject("Contoso.App", "App", "Winget", "msstore"),
            new InstallOptions { RunAsAdministrator = true },
            true, true, false);

        Assert.Equal(2, report.Contents.Count);
        Assert.Equal(
            ["msstore", "winget"],
            report.Contents.Values.Select(package => package.Subject.Source).Order());
    }
}
