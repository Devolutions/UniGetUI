using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.PackageEngine.Classes.Manager;
using UniGetUI.PackageEngine.PackageClasses;

namespace UniGetUI.Tests;

public sealed class PackageLinkTests
{
    [Fact]
    public void Parse_ReadsEveryParameter()
    {
        PackageLink? link = PackageLink.TryParse(
            "unigetui://package?manager=winget&source=msstore&id=TechSmith.Snagit.2025&version=25.1.0&name=Snagit%202025");

        Assert.Equal(
            new PackageLink("TechSmith.Snagit.2025", "winget", "msstore", "25.1.0", "Snagit 2025"),
            link);
    }

    [Fact]
    public void Parse_OnlyNeedsAnId()
    {
        Assert.Equal(new PackageLink("Git.Git", null, null, null, null), PackageLink.TryParse("unigetui://package?id=Git.Git"));
    }

    [Theory]
    [InlineData("unigetui://package/?id=Git.Git")]
    [InlineData("unigetui:package?id=Git.Git")]
    [InlineData("UNIGETUI://Package?ID=Git.Git")]
    [InlineData("unigetui://package?id=Git.Git#fragment")]
    [InlineData("unigetui://package?id=Git.Git&")]
    [InlineData("unigetui://package?id=%20Git.Git%20")]
    public void Parse_AcceptsTheShapesBrowsersAndShellsProduce(string uri)
    {
        Assert.Equal("Git.Git", PackageLink.TryParse(uri)?.Id);
    }

    [Fact]
    public void Parse_DecodesPlusAsASpace()
    {
        Assert.Equal("Visual Studio Code", PackageLink.TryParse("unigetui://package?id=x&name=Visual+Studio+Code")?.Name);
    }

    [Fact]
    public void Parse_IgnoresUnknownParameters()
    {
        Assert.Equal("Git.Git", PackageLink.TryParse("unigetui://package?id=Git.Git&future=1")?.Id);
    }

    [Fact]
    public void Parse_DoesNotTakeInstallOptionsFromTheLink()
    {
        PackageLink? link = PackageLink.TryParse(
            "unigetui://package?id=Git.Git&args=--silent&installer=https%3A%2F%2Fexample.test%2Fevil.exe");

        Assert.Equal(new PackageLink("Git.Git", null, null, null, null), link);
    }

    [Theory]
    [InlineData("unigetui://package")]
    [InlineData("unigetui://package?")]
    [InlineData("unigetui://package?id=")]
    [InlineData("unigetui://package?id=%20")]
    [InlineData("unigetui://package?manager=winget")]
    [InlineData("unigetui://package?id=Git.Git&id=Other.Package")]
    [InlineData("unigetui://package?id=Git.Git&ID=Other.Package")]
    [InlineData("unigetui://package?id=Git.Git%0A--import-settings")]
    [InlineData("unigetui://package?id=Git.Git&name=a%00b")]
    [InlineData("unigetui://openUniGetUI")]
    [InlineData("unigetui://packages?id=Git.Git")]
    [InlineData("https://package?id=Git.Git")]
    [InlineData("package?id=Git.Git")]
    [InlineData("")]
    [InlineData(null)]
    public void Parse_RefusesMalformedOrOtherLinks(string? uri)
    {
        Assert.Null(PackageLink.TryParse(uri));
    }

    [Fact]
    public void Parse_RefusesOversizedValuesAndLinks()
    {
        string longValue = new('a', PackageLink.MaxValueLength + 1);
        Assert.Null(PackageLink.TryParse($"unigetui://package?id={longValue}"));
        Assert.NotNull(PackageLink.TryParse($"unigetui://package?id={longValue[1..]}"));

        string longLink = "unigetui://package?id=Git.Git&padding=" + new string('a', PackageLink.MaxLinkLength);
        Assert.Null(PackageLink.TryParse(longLink));
    }

    [Theory]
    [InlineData("unigetui://package?id=Git.Git", true)]
    [InlineData("unigetui://package?id=", true)]
    [InlineData("unigetui://package/", true)]
    [InlineData("unigetui://openUniGetUI", false)]
    [InlineData("unigetui://updateAll", false)]
    [InlineData("C:\\bundle.ubundle", false)]
    public void IsPackageLink_RecognisesTheActionWhetherOrNotTheLinkIsValid(string arg, bool expected)
    {
        Assert.Equal(expected, PackageLink.IsPackageLink(arg));
    }

    [Fact]
    public void ToUri_RoundTripsValuesThatNeedEncoding()
    {
        PackageLink link = new("@scope/pkg&x=1", "npm", "my source", "1.0.0-beta+2", "Name: with & symbols / ?");

        string uri = link.ToUri();

        Assert.StartsWith("unigetui://package?manager=npm&source=my%20source&id=%40scope%2Fpkg%26x%3D1&", uri);
        Assert.Equal(link, PackageLink.TryParse(uri));
    }

    [Fact]
    public void ToUri_LeavesOutMissingParameters()
    {
        Assert.Equal("unigetui://package?id=Git.Git", new PackageLink("Git.Git", null, null, null, null).ToUri());
    }

    [Fact]
    public void FromPackage_NamesTheManagerAndPackage()
    {
        var manager = new NullPackageManager();
        var package = new Package("Snagit 2025", "TechSmith.Snagit.2025", "25.1.0",
            new ManagerSource(manager, "main", new Uri("https://example.test/feed")), manager);

        PackageLink link = PackageLink.FromPackage(package);

        // The test manager has no custom sources, so the source is left out.
        Assert.Equal(new PackageLink("TechSmith.Snagit.2025", manager.Id, null, null, "Snagit 2025"), link);
        Assert.True(PackageLink.CanLinkTo(package));
    }

    [Fact]
    public void FromPackage_DropsANameTooLongToParse()
    {
        var manager = new NullPackageManager();
        var package = new Package(new string('n', PackageLink.MaxValueLength + 1), "Some.Package", "1.0",
            new ManagerSource(manager, "main", new Uri("https://example.test/feed")), manager);

        PackageLink link = PackageLink.FromPackage(package);

        Assert.Null(link.Name);
        Assert.Equal(link, PackageLink.TryParse(link.ToUri()));
    }

    [Fact]
    public void CanLinkTo_RefusesPackagesOnlyKnownToThisComputer()
    {
        var package = new Package("Local", "Local.App", "1.0", NullSource.Instance, NullPackageManager.Instance);

        Assert.False(PackageLink.CanLinkTo(package));
    }

    [Theory]
    [InlineData("Git", "Git", "Git")]
    [InlineData("git", "Git", "Git")]
    [InlineData("Git", "Git.Git", "Git (Git.Git)")]
    [InlineData(null, "Git.Git", "Git.Git")]
    public void DisplayName_ShowsTheIdOnlyWhenItAddsSomething(string? name, string id, string expected)
    {
        Assert.Equal(expected, new PackageLink(id, null, null, null, name).DisplayName);
    }

    [Fact]
    public void ToastParser_LeavesPackageLinksToTheStartupProcessor()
    {
        Assert.Null(WindowsAppNotificationBridge.TryParseToastLaunchArgument("unigetui://package?id=Git.Git"));
        Assert.Equal("openUniGetUI", WindowsAppNotificationBridge.TryParseToastLaunchArgument("unigetui://openUniGetUI"));
    }

    [Fact]
    public void StartupProcessor_FindsTheFirstValidPackageLink()
    {
        PackageLink? link = StartupArgumentProcessor.FindPackageLink(
            ["--daemon", "unigetui://package?id=", "unigetui://package?id=Git.Git", "unigetui://package?id=Other"]);

        Assert.Equal("Git.Git", link?.Id);
        Assert.Null(StartupArgumentProcessor.FindPackageLink(["unigetui://openUniGetUI", "bundle.ubundle"]));
    }
}
