namespace UniGetUI.Shared;

internal static class AutoUpdaterInstallerArguments
{
    private const string CommonWindowsArguments =
        "/SILENT /SUPPRESSMSGBOXES /NORESTART /SP- /NoVCRedist /NoEdgeWebView /NoWinGet /NoRedirectionGuard /NoDesktopShortcut";

    /// <summary>
    /// Arguments for the Windows installer when it is run to update an existing copy.
    /// A portable copy is pinned to its own directory and re-selects the portable task,
    /// because the installer would otherwise fall back to its default directory and
    /// install a second, regular copy elsewhere.
    /// </summary>
    internal static string ForWindows(bool isPortable, string installationDirectory)
    {
        if (!isPortable || string.IsNullOrWhiteSpace(installationDirectory))
        {
            return CommonWindowsArguments;
        }

        string directory = Path.TrimEndingDirectorySeparator(installationDirectory);
        return $"{CommonWindowsArguments} /TASKS=\"portableinstall\" /DIR=\"{directory}\"";
    }
}
