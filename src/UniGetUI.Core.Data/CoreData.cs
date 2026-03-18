using System.Diagnostics;
using System.Text;
using UniGetUI.Core.Logging;

namespace UniGetUI.Core.Data
{
    public static class CoreData
    {
        private const string GitHubReleasePageBaseUrl = "https://github.com/Devolutions/UniGetUI/releases/tag/";
        private const string GitHubReleaseApiBaseUrl = "https://api.github.com/repos/Devolutions/UniGetUI/releases/tags/";

        private static int? __code_page;
        public static int CODE_PAGE
        {
            get => __code_page ??= GetCodePage();
        }
        public const string VersionName = "2026.1.0"; // Do not modify this line, use file scripts/set-version.ps1
        public const int BuildNumber = 106; // Do not modify this line, use file scripts/set-version.ps1

        public const string UserAgentString =
            $"UniGetUI/{VersionName} (https://devolutions.net/unigetui; unigetui@devolutions.net)";

        public const string AppIdentifier = "MartiCliment.UniGetUI";
        public const string MainWindowIdentifier = "MartiCliment.UniGetUI.MainInterface";

        public static string GetGitHubReleaseTag()
        {
            return GetGitHubReleaseTag(VersionName);
        }

        public static string[] GetGitHubReleaseTagCandidates()
        {
            return GetGitHubReleaseTagCandidates(VersionName);
        }

        public static string GetGitHubReleaseTag(string versionName)
        {
            return GetGitHubReleaseTagCandidates(versionName)[0];
        }

        public static string[] GetGitHubReleaseTagCandidates(string versionName)
        {
            string normalizedVersion = string.IsNullOrWhiteSpace(versionName)
                ? VersionName
                : versionName.Trim();

            if (normalizedVersion.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            {
                return [normalizedVersion];
            }

            string prefixedTag = $"v{normalizedVersion}";
            return UsesPrefixedCalendarReleaseTags(normalizedVersion)
                ? [prefixedTag, normalizedVersion]
                : [normalizedVersion, prefixedTag];
        }

        public static string GetGitHubReleasePageUrl()
        {
            return GetGitHubReleasePageUrlFromTag(GetGitHubReleaseTag());
        }

        public static string GetGitHubReleasePageUrlFromTag(string releaseTag)
        {
            return GitHubReleasePageBaseUrl + releaseTag;
        }

        public static string GetGitHubReleaseApiUrlFromTag(string releaseTag)
        {
            return GitHubReleaseApiBaseUrl + Uri.EscapeDataString(releaseTag);
        }

        private static bool UsesPrefixedCalendarReleaseTags(string versionName)
        {
            int dotIndex = versionName.IndexOf('.');
            if (dotIndex != 4 || dotIndex == versionName.Length - 1)
            {
                return false;
            }

            ReadOnlySpan<char> year = versionName.AsSpan(0, dotIndex);
            return int.TryParse(year, out int parsedYear) && parsedYear >= 2000;
        }

        private static bool? IS_PORTABLE;
        private static string? PORTABLE_PATH;
        public static bool IsPortable
        {
            get => IS_PORTABLE ?? false;
        }

        public static string? TEST_DataDirectoryOverride { private get; set; }

        /// <summary>
        /// The directory where all the user data is stored. The directory is automatically created if it does not exist.
        /// </summary>
        public static string UniGetUIDataDirectory
        {
            get
            {
                if (TEST_DataDirectoryOverride is not null)
                {
                    return TEST_DataDirectoryOverride;
                }

                if (IS_PORTABLE is null)
                {
                    IS_PORTABLE = File.Exists(
                        Path.Join(UniGetUIExecutableDirectory, "ForceUniGetUIPortable")
                    );

                    if (IS_PORTABLE is true)
                    {
                        string path = Path.Join(UniGetUIExecutableDirectory, "Settings");
                        try
                        {
                            if (!Directory.Exists(path))
                                Directory.CreateDirectory(path);
                            var testfilepath = Path.Join(path, "PermissionTestFile");
                            File.WriteAllText(
                                testfilepath,
                                "https://www.youtube.com/watch?v=dQw4w9WgXcQ"
                            );
                            PORTABLE_PATH = path;
                            return path;
                        }
                        catch (Exception ex)
                        {
                            IS_PORTABLE = false;
                            Logger.Error(
                                $"Could not acces/write path {path}. UniGetUI will NOT be run in portable mode, and User settings will be used instead"
                            );
                            Logger.Error(ex);
                        }
                    }
                }
                else if (IS_PORTABLE is true)
                {
                    return PORTABLE_PATH
                        ?? throw new InvalidOperationException("This shouldn't be possible");
                }

                string old_path = Path.Join(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".wingetui"
                );
                string new_path = Path.Join(GetLocalDataRoot(), "UniGetUI");
                return GetNewDataDirectoryOrMoveOld(old_path, new_path);
            }
        }

        /// <summary>
        /// The directory where the user configurations are stored. The directory is automatically created if it does not exist.
        /// </summary>
        public static string UniGetUIUserConfigurationDirectory
        {
            get
            {
                string oldConfigPath = UniGetUIDataDirectory; // Old config path was the data directory itself
                string newConfigPath = Path.Join(UniGetUIDataDirectory, "Configuration");

                if (Directory.Exists(oldConfigPath) && !Directory.Exists(newConfigPath))
                {
                    //Migration case
                    try
                    {
                        Logger.Info(
                            $"Moving configuration files from '{oldConfigPath}' to '{newConfigPath}'"
                        );
                        Directory.CreateDirectory(newConfigPath);

                        foreach (
                            string file in Directory.GetFiles(
                                oldConfigPath,
                                "*.*",
                                SearchOption.TopDirectoryOnly
                            )
                        )
                        {
                            string fileName = Path.GetFileName(file);
                            string fileExtension = Path.GetExtension(file);
                            bool isConfigFile =
                                string.IsNullOrEmpty(fileExtension)
                                || fileExtension.ToLowerInvariant() == ".json";

                            if (isConfigFile)
                            {
                                string newFile = Path.Join(newConfigPath, fileName);
                                // Avoid overwriting if somehow file already exists
                                if (!File.Exists(newFile))
                                {
                                    File.Move(file, newFile);
                                    Logger.Debug(
                                        $"Moved configuration file '{file}' to '{newFile}'"
                                    );
                                }
                                // Clean up old file to avoid duplicates and confusion
                                else
                                {
                                    Logger.Warn(
                                        $"Configuration file '{newFile}' already exists, skipping move from '{file}'."
                                    );
                                    File.Delete(file);
                                }
                            }
                            else
                            {
                                Logger.Debug(
                                    $"Skipping non-configuration file '{file}' during migration."
                                );
                            }
                        }
                        Logger.Info($"Configuration files moved successfully to '{newConfigPath}'");
                    }
                    catch (Exception ex)
                    {
                        // Fallback to old path if migration fails to not break functionality
                        Logger.Error(
                            $"Error moving configuration files from '{oldConfigPath}' to '{newConfigPath}'. Using old path for now. Manual migration might be needed."
                        );
                        Logger.Error(ex);
                        return oldConfigPath;
                    }
                }
                else if (!Directory.Exists(newConfigPath))
                {
                    //New install case, migration not needed
                    Directory.CreateDirectory(newConfigPath);
                }
                return newConfigPath;
            }
        }

        /// <summary>
        /// The directory where the installation options are stored. The directory is automatically created if it does not exist.
        /// </summary>
        public static string UniGetUIInstallationOptionsDirectory
        {
            get
            {
                string path = Path.Join(UniGetUIDataDirectory, "InstallationOptions");
                if (!Directory.Exists(path))
                    Directory.CreateDirectory(path);
                return path;
            }
        }

        /// <summary>
        /// The directory where the metadata cache is stored. The directory is automatically created if it does not exist.
        /// </summary>
        public static string UniGetUICacheDirectory_Data
        {
            get
            {
                string path = Path.Join(UniGetUIDataDirectory, "CachedMetadata");
                if (!Directory.Exists(path))
                    Directory.CreateDirectory(path);
                return path;
            }
        }

        /// <summary>
        /// The directory where the cached icons and screenshots are saved. The directory is automatically created if it does not exist.
        /// </summary>
        public static string UniGetUICacheDirectory_Icons
        {
            get
            {
                string path = Path.Join(UniGetUIDataDirectory, "CachedMedia");
                if (!Directory.Exists(path))
                    Directory.CreateDirectory(path);
                return path;
            }
        }

        /// <summary>
        /// The directory where the cached language files are stored. The directory is automatically created if it does not exist.
        /// </summary>
        public static string UniGetUICacheDirectory_Lang
        {
            get
            {
                string path = Path.Join(UniGetUIDataDirectory, "CachedLanguageFiles");
                if (!Directory.Exists(path))
                    Directory.CreateDirectory(path);
                return path;
            }
        }

        /// <summary>
        /// The directory where package backups will be saved by default.
        /// </summary>
        public static string UniGetUI_DefaultBackupDirectory
        {
            get
            {
                string documentsDirectory = GetDocumentsRoot();
                string old_dir = Path.Join(documentsDirectory, "WingetUI");
                string new_dir = Path.Join(documentsDirectory, "UniGetUI");
                return GetNewDataDirectoryOrMoveOld(old_dir, new_dir);
            }
        }

        public static bool IsDaemon;
        public static bool WasDaemon;

        /// <summary>
        /// The ID of the notification that is used to inform the user that updates are available
        /// </summary>
        public const int UpdatesAvailableNotificationTag = 1234;

        /// <summary>
        /// The ID of the notification that is used to inform the user that UniGetUI can be updated
        /// </summary>
        public const int UniGetUICanBeUpdated = 1235;

        /// <summary>
        /// The ID of the notification that is used to inform the user that shortcuts are available for deletion
        /// </summary>
        public const int NewShortcutsNotificationTag = 1236;

        /// <summary>
        /// A path pointing to the location where the app is installed
        /// </summary>
        public static string UniGetUIExecutableDirectory
        {
            get
            {
                string? dir = Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location
                );
                if (dir is not null)
                {
                    return dir;
                }

                Logger.Error(
                    "System.Reflection.Assembly.GetExecutingAssembly().Location returned an empty path"
                );

                return AppContext.BaseDirectory.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar
                );
            }
        }

        /// <summary>
        /// A path pointing to the executable file of the app
        /// </summary>
        public static string UniGetUIExecutableFile
        {
            get
            {
                string? filename = Process.GetCurrentProcess().MainModule?.FileName;
                if (filename is not null)
                {
                    return NormalizeExecutablePath(filename);
                }

                Logger.Error(
                    "System.Reflection.Assembly.GetExecutingAssembly().Location returned an empty path"
                );

                return OperatingSystem.IsWindows()
                    ? Path.Join(UniGetUIExecutableDirectory, "UniGetUI.exe")
                    : NormalizeExecutablePath(Path.Join(UniGetUIExecutableDirectory, "UniGetUI"));
            }
        }

        public static string ElevatorPath = "";

        /// <summary>
        /// This method will return the most appropriate data directory.
        /// If the new directory exists, it will be used.
        /// If the new directory does not exist, but the old directory does, it will be moved to the new location, and the new location will be used.
        /// If none exist, the new directory will be created.
        /// </summary>
        /// <param name="old_path">The old/legacy directory</param>
        /// <param name="new_path">The new directory</param>
        /// <returns>The path to an existing, valid directory</returns>
        private static string GetNewDataDirectoryOrMoveOld(string old_path, string new_path)
        {
            if (Directory.Exists(new_path) && !Directory.Exists(old_path))
            {
                return new_path;
            }

            if (Directory.Exists(new_path) && Directory.Exists(old_path))
            {
                try
                {
                    foreach (
                        string old_subdir in Directory.GetDirectories(
                            old_path,
                            "*",
                            SearchOption.AllDirectories
                        )
                    )
                    {
                        string new_subdir = old_subdir.Replace(old_path, new_path);
                        if (!Directory.Exists(new_subdir))
                        {
                            Logger.Debug("New directory: " + new_subdir);
                            Directory.CreateDirectory(new_subdir);
                        }
                        else
                        {
                            Logger.Debug("Directory " + new_subdir + " already exists");
                        }
                    }

                    foreach (
                        string old_file in Directory.GetFiles(
                            old_path,
                            "*",
                            SearchOption.AllDirectories
                        )
                    )
                    {
                        string new_file = old_file.Replace(old_path, new_path);
                        if (!File.Exists(new_file))
                        {
                            Logger.Info("Copying " + old_file);
                            File.Move(old_file, new_file);
                        }
                        else
                        {
                            Logger.Debug("File " + new_file + " already exists.");
                            File.Delete(old_file);
                        }
                    }

                    foreach (
                        string old_subdir in Directory.GetDirectories(
                            old_path,
                            "*",
                            SearchOption.AllDirectories
                        )
                    )
                    {
                        if (
                            !Directory.EnumerateFiles(old_subdir).Any()
                            && !Directory.EnumerateDirectories(old_subdir).Any()
                        )
                        {
                            Logger.Debug("Deleting old empty subdirectory " + old_subdir);
                            Directory.Delete(old_subdir);
                        }
                    }

                    if (
                        !Directory.EnumerateFiles(old_path).Any()
                        && !Directory.EnumerateDirectories(old_path).Any()
                    )
                    {
                        Logger.Info("Deleting old Chocolatey directory " + old_path);
                        Directory.Delete(old_path);
                    }

                    return new_path;
                }
                catch (Exception e)
                {
                    Logger.Error(e);
                    return new_path;
                }
            }

            if ( /*Directory.Exists(new_path)*/
                Directory.Exists(old_path)
            )
            {
                try
                {
                    Directory.Move(old_path, new_path);
                    Task.Delay(100).Wait();
                    return new_path;
                }
                catch (Exception e)
                {
                    Logger.Error(
                        "Cannot move old data directory to new location. Directory to move: "
                            + old_path
                            + ". Destination: "
                            + new_path
                    );
                    Logger.Error(e);
                    return old_path;
                }
            }

            try
            {
                Logger.Debug("Creating non-existing data directory at: " + new_path);
                Directory.CreateDirectory(new_path);
                return new_path;
            }
            catch (Exception e)
            {
                Logger.Error(
                    "Could not create new directory. You may perhaps need to disable Controlled Folder Access from Windows Settings or make an exception for UniGetUI."
                );
                Logger.Error(e);
                return new_path;
            }
        }

        private static int GetCodePage()
        {
            if (!OperatingSystem.IsWindows())
            {
                return Encoding.UTF8.CodePage;
            }

            try
            {
                using Process p = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "chcp.com",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    },
                };
                p.Start();
                string contents = p.StandardOutput.ReadToEnd();
                string purifiedString = "";

                foreach (var c in contents.Split(':')[^1].Trim())
                {
                    if (c >= '0' && c <= '9')
                    {
                        purifiedString += c;
                    }
                }

                return int.Parse(purifiedString);
            }
            catch (Exception e)
            {
                Logger.Error(e);
                return 0;
            }
        }

        public static readonly string PowerShell5 = OperatingSystem.IsWindows()
            ? Path.Join(Environment.SystemDirectory, "windowspowershell\\v1.0\\powershell.exe")
            : "pwsh";

        private static string GetLocalDataRoot()
        {
            string localApplicationData = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData
            );
            if (!string.IsNullOrWhiteSpace(localApplicationData))
            {
                return localApplicationData;
            }

            string? xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (!string.IsNullOrWhiteSpace(xdgDataHome))
            {
                return xdgDataHome;
            }

            return Path.Join(GetUserHomeDirectory(), ".local", "share");
        }

        private static string GetDocumentsRoot()
        {
            string documentsDirectory = Environment.GetFolderPath(
                Environment.SpecialFolder.MyDocuments
            );
            if (!string.IsNullOrWhiteSpace(documentsDirectory))
            {
                return documentsDirectory;
            }

            return Path.Join(GetUserHomeDirectory(), "Documents");
        }

        private static string GetUserHomeDirectory()
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(userProfile))
            {
                return userProfile;
            }

            return Environment.GetEnvironmentVariable("HOME") ?? AppContext.BaseDirectory;
        }

        private static string NormalizeExecutablePath(string path)
        {
            if (
                OperatingSystem.IsWindows()
                && path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            )
            {
                return Path.ChangeExtension(path, ".exe");
            }

            return path;
        }
    }
}
