using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;

namespace UniGetUI.Core.Tools;

public static class InstallerDownloadLocation
{
    public static bool IsCustomDirectorySet => GetCustomDirectory() is not null;

    public static string DefaultDirectory => CoreData.UniGetUI_DefaultInstallerDownloadDirectory;

    public static string? GetCustomDirectory()
    {
        string directory = Settings.GetValue(Settings.K.DefaultInstallerDownloadDirectory);
        return string.IsNullOrWhiteSpace(directory) ? null : directory;
    }

    public static void SetCustomDirectory(string? directory)
    {
        Settings.SetValue(
            Settings.K.DefaultInstallerDownloadDirectory,
            string.IsNullOrWhiteSpace(directory) ? "" : directory
        );
    }

    public static string? ResolveStartDirectory()
    {
        if (GetCustomDirectory() is { } customDirectory)
        {
            if (Directory.Exists(customDirectory))
                return customDirectory;

            Logger.Warn(
                $"The configured installer download directory {customDirectory} does not exist, "
                    + "the file picker will open wherever the system decides"
            );
        }

        return null;
    }

    public static string ResolveExistingDirectory()
    {
        if (GetCustomDirectory() is { } customDirectory && TryCreateDirectory(customDirectory))
            return customDirectory;

        string defaultDirectory = DefaultDirectory;
        TryCreateDirectory(defaultDirectory);
        return defaultDirectory;
    }

    private static bool TryCreateDirectory(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"Could not create the installer download directory {directory}:");
            Logger.Error(ex);
            return false;
        }
    }
}
