using UniGetUI.Core.Data;

namespace UniGetUI.Avalonia.Infrastructure;

internal static class ReleaseNotesLinks
{
    public static Uri PageUri(bool darkTheme) =>
        new($"{CoreData.ReleaseNotesUrl}?theme={Theme(darkTheme)}");

    public static Uri UpdateDialogUri(bool darkTheme) =>
        new($"{CoreData.ReleaseNotesUrl}?theme={Theme(darkTheme)}&utm_source=unigetui&utm_medium=app&utm_campaign=self-update");

    private static string Theme(bool darkTheme) => darkTheme ? "dark-neutral" : "light";

    public static bool IsReleaseNotesPage(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri)
            return false;

        Uri page = new(CoreData.ReleaseNotesUrl);
        return string.Equals(uri.Scheme, page.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(uri.Host, page.Host, StringComparison.OrdinalIgnoreCase)
            && string.Equals(uri.AbsolutePath, page.AbsolutePath, StringComparison.OrdinalIgnoreCase);
    }

    public static string BrowserDownloadUrl(string versionName, string installerDownloadUrl, bool isWindows) =>
        isWindows && !string.IsNullOrWhiteSpace(installerDownloadUrl)
            ? installerDownloadUrl
            : CoreData.GetGitHubReleasePageUrlFromTag(CoreData.GetGitHubReleaseTag(ShortVersion(versionName)));

    public static string ShortVersion(string versionName)
    {
        string trimmed = versionName.Trim();
        return Version.TryParse(trimmed, out Version? parsed) && parsed.Revision == 0
            ? $"{parsed.Major}.{parsed.Minor}.{parsed.Build}"
            : trimmed;
    }

    public static bool CanOpenInBrowser(Uri? uri) =>
        uri is { IsAbsoluteUri: true }
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
}
