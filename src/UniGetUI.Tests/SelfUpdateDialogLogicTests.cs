using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Core.Data;
using UniGetUI.Shared;
using static UniGetUI.Shared.AutoUpdaterHelpers;

namespace UniGetUI.Tests;

public sealed class SelfUpdateDialogLogicTests
{
    [Fact]
    public void Consent_WaitsWhileTheUserHasNotChosenAndTheAppKeepsRunning()
    {
        Assert.Equal(
            SelfUpdateConsent.Waiting,
            ResolveSelfUpdateConsent(installRequested: false, installOnQuitRequested: false, appIsQuitting: false, skipRequested: false));
    }

    [Fact]
    public void Consent_QuittingWithoutAskingDoesNotInstall()
    {
        Assert.Equal(
            SelfUpdateConsent.Decline,
            ResolveSelfUpdateConsent(installRequested: false, installOnQuitRequested: false, appIsQuitting: true, skipRequested: false));
    }

    [Fact]
    public void Consent_UpdateWhenIQuitWaitsUntilTheAppQuits()
    {
        Assert.Equal(
            SelfUpdateConsent.Waiting,
            ResolveSelfUpdateConsent(installRequested: false, installOnQuitRequested: true, appIsQuitting: false, skipRequested: false));
        Assert.Equal(
            SelfUpdateConsent.Install,
            ResolveSelfUpdateConsent(installRequested: false, installOnQuitRequested: true, appIsQuitting: true, skipRequested: false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Consent_UpdateNowInstallsImmediately(bool appIsQuitting)
    {
        Assert.Equal(
            SelfUpdateConsent.Install,
            ResolveSelfUpdateConsent(installRequested: true, installOnQuitRequested: false, appIsQuitting, skipRequested: false));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, true)]
    public void Consent_SkippingEndsTheWaitWithoutInstalling(bool installRequested, bool installOnQuitRequested, bool appIsQuitting)
    {
        Assert.Equal(
            SelfUpdateConsent.Skip,
            ResolveSelfUpdateConsent(installRequested, installOnQuitRequested, appIsQuitting, skipRequested: true));
    }

    [Theory]
    [InlineData("2026.3.4", "2026.3.4", true)]
    [InlineData("v2026.3.4", "2026.3.4", true)]
    [InlineData("2026.3.4", "2026.3.5", false)]
    [InlineData("", "2026.3.4", false)]
    [InlineData(null, "2026.3.4", false)]
    public void SkippedVersion_HidesOnlyThatVersion(string? skipped, string candidate, bool expected)
    {
        Assert.Equal(expected, IsSkippedVersion(skipped, candidate));
    }

    [Theory]
    [InlineData(true, "theme=dark-neutral")]
    [InlineData(false, "theme=light")]
    public void ReleaseNotesUri_LoadsTheChangelogPageInTheAppTheme(bool dark, string expectedTheme)
    {
        Uri dialog = ReleaseNotesLinks.UpdateDialogUri(dark);
        Uri page = ReleaseNotesLinks.PageUri(dark);

        Assert.StartsWith(CoreData.ReleaseNotesUrl + "?", dialog.ToString());
        Assert.Contains(expectedTheme, dialog.Query);
        Assert.Contains("utm_source=unigetui", dialog.Query);
        Assert.True(ReleaseNotesLinks.IsReleaseNotesPage(dialog));

        Assert.StartsWith(CoreData.ReleaseNotesUrl + "?", page.ToString());
        Assert.Contains(expectedTheme, page.Query);
        Assert.DoesNotContain("utm_", page.Query);
        Assert.True(ReleaseNotesLinks.IsReleaseNotesPage(page));
    }

    [Theory]
    [InlineData("https://devolutions.net/data/UniGetUIChangeHistoryUpdate.htm#v2026.3.3.0", true)]
    [InlineData("https://DEVOLUTIONS.net/data/UniGetUIChangeHistoryUpdate.htm?theme=dark", true)]
    [InlineData("https://devolutions.net/unigetui", false)]
    [InlineData("https://github.com/Devolutions/UniGetUI/pull/1", false)]
    [InlineData("http://devolutions.net/data/UniGetUIChangeHistoryUpdate.htm", false)]
    public void ReleaseNotesPage_OnlyTheChangelogStaysInsideTheDialog(string url, bool expected)
    {
        Assert.Equal(expected, ReleaseNotesLinks.IsReleaseNotesPage(new Uri(url)));
    }

    [Theory]
    [InlineData("2026.3.3.0", "2026.3.3")]
    [InlineData("2026.3.3", "2026.3.3")]
    [InlineData("2026.3.3.1", "2026.3.3.1")]
    [InlineData("2026.4.0-beta1", "2026.4.0-beta1")]
    public void ShortVersion_DropsOnlyAZeroFourthSegment(string version, string expected)
    {
        Assert.Equal(expected, ReleaseNotesLinks.ShortVersion(version));
    }

    [Theory]
    [InlineData("https://github.com/Devolutions/UniGetUI", true)]
    [InlineData("http://example.com", true)]
    [InlineData("file:///C:/Windows/System32/cmd.exe", false)]
    [InlineData("ms-settings:privacy", false)]
    public void OpenInBrowser_OnlyHandsWebLinksToTheBrowser(string url, bool expected)
    {
        Assert.Equal(expected, ReleaseNotesLinks.CanOpenInBrowser(new Uri(url)));
    }

    [Fact]
    public void BrowserDownload_UsesTheInstallerOnWindowsAndTheReleasePageElsewhere()
    {
        const string installer = "https://github.com/Devolutions/UniGetUI/releases/download/v2026.3.4/UniGetUI.Installer.exe";

        Assert.Equal(installer, ReleaseNotesLinks.BrowserDownloadUrl("2026.3.4", installer, isWindows: true));
        Assert.Equal(
            CoreData.GetGitHubReleasePageUrlFromTag(CoreData.GetGitHubReleaseTag("2026.3.4")),
            ReleaseNotesLinks.BrowserDownloadUrl("2026.3.4", installer, isWindows: false));
        Assert.Equal(
            CoreData.GetGitHubReleasePageUrlFromTag(CoreData.GetGitHubReleaseTag("2026.3.4")),
            ReleaseNotesLinks.BrowserDownloadUrl("2026.3.4", "", isWindows: true));
    }

    [Theory]
    [InlineData("2026.3.3.0", "https://github.com/Devolutions/UniGetUI/releases/tag/v2026.3.3")]
    [InlineData("2026.3.3", "https://github.com/Devolutions/UniGetUI/releases/tag/v2026.3.3")]
    [InlineData("2026.3.3.1", "https://github.com/Devolutions/UniGetUI/releases/tag/v2026.3.3.1")]
    public void BrowserDownload_ReleasePageUsesThePublishedTag(string feedVersion, string expected)
    {
        Assert.Equal(expected, ReleaseNotesLinks.BrowserDownloadUrl(feedVersion, "", isWindows: false));
    }
}
