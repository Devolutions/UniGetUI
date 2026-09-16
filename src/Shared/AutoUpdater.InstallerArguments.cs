using Microsoft.Win32;

namespace UniGetUI.Shared;

internal enum WindowsInstallScope
{
    Unknown,
    CurrentUser,
    AllUsers,
}

internal static class AutoUpdaterInstallerArguments
{
    private const string CommonWindowsArguments =
        "/SILENT /SUPPRESSMSGBOXES /NORESTART /SP- /NoVCRedist /NoEdgeWebView /NoWinGet /NoRedirectionGuard /NoDesktopShortcut";

    private const string UninstallSubkeyName =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{889610CC-4337-4BDB-AC3B-4F21806C0BDE}_is1";

    /// <summary>
    /// Arguments for the Windows installer when it is run to update an existing copy.
    /// A portable copy is pinned to its own directory and re-selects the portable task,
    /// because the installer would otherwise fall back to its default directory and
    /// install a second, regular copy elsewhere.
    /// </summary>
    internal static string ForWindows(
        bool isPortable,
        string installationDirectory,
        WindowsInstallScope installScope)
    {
        string arguments = CommonWindowsArguments + installScope switch
        {
            WindowsInstallScope.AllUsers => " /ALLUSERS",
            WindowsInstallScope.CurrentUser => " /CURRENTUSER",
            _ => "",
        };

        if (!isPortable || string.IsNullOrWhiteSpace(installationDirectory))
        {
            return arguments;
        }

        string directory = Path.TrimEndingDirectorySeparator(installationDirectory);
        return $"{arguments} /TASKS=\"portableinstall\" /DIR=\"{directory}\"";
    }

    internal static WindowsInstallScope DetectInstallScope(string installationDirectory)
    {
        if (!OperatingSystem.IsWindows())
        {
            return WindowsInstallScope.Unknown;
        }

        return ResolveInstallScope(
            installationDirectory,
            PreferMatchingPath(
                installationDirectory,
                ReadRecordedInstallPath(RegistryHive.LocalMachine, RegistryView.Registry32),
                ReadRecordedInstallPath(RegistryHive.LocalMachine, RegistryView.Registry64)),
            ReadRecordedInstallPath(RegistryHive.CurrentUser, RegistryView.Default),
            GetAllUsersRoots());
    }

    internal static WindowsInstallScope ResolveInstallScope(
        string installationDirectory,
        string? allUsersInstallPath,
        string? currentUserInstallPath,
        IReadOnlyList<string> allUsersRoots)
    {
        string directory = NormalizeDirectory(installationDirectory);
        if (directory.Length is 0)
        {
            return WindowsInstallScope.Unknown;
        }

        if (DirectoriesMatch(directory, allUsersInstallPath))
        {
            return WindowsInstallScope.AllUsers;
        }

        if (DirectoriesMatch(directory, currentUserInstallPath))
        {
            return WindowsInstallScope.CurrentUser;
        }

        foreach (string root in allUsersRoots)
        {
            if (IsInside(directory, NormalizeDirectory(root)))
            {
                return WindowsInstallScope.AllUsers;
            }
        }

        return WindowsInstallScope.Unknown;
    }

    internal static string? PreferMatchingPath(
        string installationDirectory,
        string? firstPath,
        string? secondPath)
    {
        string directory = NormalizeDirectory(installationDirectory);
        if (directory.Length > 0 && DirectoriesMatch(directory, secondPath))
        {
            return secondPath;
        }

        return firstPath ?? secondPath;
    }

    private static bool DirectoriesMatch(string directory, string? recordedPath)
    {
        string recorded = NormalizeDirectory(recordedPath ?? "");
        return recorded.Length > 0 && directory.Equals(recorded, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInside(string directory, string root)
    {
        return root.Length > 0
            && directory.Length > root.Length
            && directory.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            && (directory[root.Length] is '\\' or '/');
    }

    private static string NormalizeDirectory(string path)
    {
        string trimmed = path.Trim().Trim('"');
        return trimmed.Length is 0 ? "" : trimmed.TrimEnd('\\', '/');
    }

    private static IReadOnlyList<string> GetAllUsersRoots()
    {
        List<string> roots = [];
        string[] candidates =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetEnvironmentVariable("ProgramW6432") ?? "",
        ];

        foreach (string candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                roots.Add(candidate);
            }
        }

        return roots;
    }

    private static string? ReadRecordedInstallPath(RegistryHive hive, RegistryView view)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
            using RegistryKey? key = baseKey.OpenSubKey(UninstallSubkeyName);
            if (key is null)
            {
                return null;
            }

            string? path = key.GetValue("Inno Setup: App Path")?.ToString();
            if (string.IsNullOrWhiteSpace(path))
            {
                path = key.GetValue("InstallLocation")?.ToString();
            }

            return string.IsNullOrWhiteSpace(path) ? null : path.Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
