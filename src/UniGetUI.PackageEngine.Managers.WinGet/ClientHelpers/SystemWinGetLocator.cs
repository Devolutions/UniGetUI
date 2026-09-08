using Microsoft.Win32;
using UniGetUI.Core.Logging;

namespace UniGetUI.PackageEngine.Managers.WingetManager;

internal static class SystemWinGetLocator
{
    private const string WinGetExecutableName = "winget.exe";
    private const string AppInstallerPackageName = "Microsoft.DesktopAppInstaller";
    private const string AppInstallerPublisherId = "8wekyb3d8bbwe";

    private const string AppxRepositoryKey =
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    public static IEnumerable<string> EnumerateOffPathExecutables(Func<string, bool> fileExists)
    {
        return EnumerateOffPathExecutables(
            fileExists,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ReadAppInstallerInstallDirectories
        );
    }

    internal static IEnumerable<string> EnumerateOffPathExecutables(
        Func<string, bool> fileExists,
        string localAppDataDirectory,
        Func<IReadOnlyList<string>> readAppInstallerInstallDirectories
    )
    {
        foreach (
            string directory in EnumerateCandidateDirectories(
                localAppDataDirectory,
                readAppInstallerInstallDirectories
            )
        )
        {
            string candidate = Path.Join(directory, WinGetExecutableName);
            if (fileExists(candidate))
            {
                yield return candidate;
            }
        }
    }

    private static IEnumerable<string> EnumerateCandidateDirectories(
        string localAppDataDirectory,
        Func<IReadOnlyList<string>> readAppInstallerInstallDirectories
    )
    {
        IReadOnlyList<string> installDirectories = readAppInstallerInstallDirectories();
        if (installDirectories.Count is 0)
        {
            yield break;
        }

        if (!string.IsNullOrWhiteSpace(localAppDataDirectory))
        {
            yield return Path.Join(localAppDataDirectory, "Microsoft", "WindowsApps");
        }

        foreach (string directory in installDirectories)
        {
            yield return directory;
        }
    }

    internal static IReadOnlyList<string> ReadAppInstallerInstallDirectories()
    {
        List<(Version Version, string Directory)> matches = [];

        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(AppxRepositoryKey);
            if (root is null)
            {
                return [];
            }

            foreach (string packageFullName in root.GetSubKeyNames())
            {
                if (!IsAppInstallerPackageFullName(packageFullName))
                {
                    continue;
                }

                try
                {
                    using var entry = root.OpenSubKey(packageFullName);
                    if (
                        entry?.GetValue("PackageRootFolder") is not string directory
                        || string.IsNullOrWhiteSpace(directory)
                    )
                    {
                        continue;
                    }

                    matches.Add((ParsePackageVersion(packageFullName), directory));
                }
                catch
                {
                    continue;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Debug(
                $"Could not read the App Installer install location from the registry: {ex.Message}"
            );
            return [];
        }

        return matches
            .OrderByDescending(match => match.Version)
            .Select(match => match.Directory)
            .ToArray();
    }

    internal static bool IsAppInstallerPackageFullName(string packageFullName)
    {
        string[] pieces = packageFullName.Split('_');
        return pieces.Length >= 4
            && pieces[0].Equals(AppInstallerPackageName, StringComparison.OrdinalIgnoreCase)
            && pieces[^1].Equals(AppInstallerPublisherId, StringComparison.OrdinalIgnoreCase);
    }

    internal static Version ParsePackageVersion(string packageFullName)
    {
        string[] pieces = packageFullName.Split('_');
        return pieces.Length >= 2 && Version.TryParse(pieces[1], out Version? version)
            ? version
            : new Version(0, 0);
    }
}
