using System.Diagnostics;
using System.Text;
using UniGetUI.Core.Logging;

namespace UniGetUI.Core.Data
{
    public static class CoreData
    {
        private const string GitHubReleasePageBaseUrl = "https://github.com/Devolutions/UniGetUI/releases/tag/";
        private const string GitHubReleaseApiBaseUrl = "https://api.github.com/repos/Devolutions/UniGetUI/releases/tags/";
        public const string ReleaseNotesUrl = "https://devolutions.net/unigetui/release-notes/";

        private static int? __code_page;
        public static int CODE_PAGE
        {
            get => __code_page ??= GetCodePage();
        }

        private static Encoding? __console_encoding;
        // The encoding CLIs that don't force UTF-8 (e.g. Chocolatey) actually emit their console output in.
        public static Encoding ConsoleEncoding
        {
            get
            {
                if (__console_encoding is not null)
                    return __console_encoding;
                try
                {
                    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                    __console_encoding = Encoding.GetEncoding(CODE_PAGE);
                }
                catch (Exception e)
                {
                    Logger.Warn($"Could not resolve console code page {CODE_PAGE}, falling back to UTF-8");
                    Logger.Warn(e);
                    __console_encoding = Encoding.UTF8;
                }
                return __console_encoding;
            }
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
            return ReleaseNotesUrl;
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

        public static bool IsPortable => AppPaths.IsPortable;

        /// <summary>
        /// Where the per-user data directory lives, regardless of whether portable mode is
        /// active. Unlike <see cref="UniGetUIDataDirectory"/> this creates and migrates nothing.
        /// </summary>
        public static string? TEST_PerUserDataDirectoryOverride { private get; set; }

        public static string PerUserDataDirectoryPath =>
            TEST_PerUserDataDirectoryOverride ?? Path.Join(GetLocalDataRoot(), "UniGetUI");

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

                if (AppPaths.PortableDataDirectory is { } portableDirectory)
                {
                    return portableDirectory;
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

        private static string[]? _sanitizedProcessArguments;

        /// <summary>
        /// Records the process arguments after startup has removed anything that could only
        /// have been injected, so later consumers do not have to re-read the raw OS command
        /// line. The executable path is preserved so the shape matches
        /// Environment.GetCommandLineArgs().
        /// </summary>
        public static void SetSanitizedProcessArguments(string[] args)
        {
            string[] raw = Environment.GetCommandLineArgs();
            _sanitizedProcessArguments = raw.Length > 0 ? [raw[0], .. args] : [.. args];
        }

        /// <summary>
        /// The sanitized process arguments when startup has published them, and the raw OS
        /// command line otherwise.
        /// </summary>
        public static string[] GetProcessArguments()
        {
            return _sanitizedProcessArguments ?? Environment.GetCommandLineArgs();
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
                if (AppPaths.PortableDataDirectory is { } portableDirectory)
                {
                    string portableBackups = Path.Join(portableDirectory, "Backups");
                    if (!Directory.Exists(portableBackups))
                        Directory.CreateDirectory(portableBackups);
                    return portableBackups;
                }

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
        public static string UniGetUIExecutableDirectory => AppPaths.InstallationDirectory;

        public static string ResolveInstallationDirectory(
            string executableDirectory,
            Func<string, bool>? fileExists = null,
            Func<string, bool>? directoryExists = null
        ) => AppPaths.ResolveInstallationDirectory(executableDirectory, fileExists, directoryExists);

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
        /// Extra arguments to insert between the elevator binary and the elevated command.
        /// For example, "-A" when using sudo with an askpass helper on Linux.
        /// </summary>
        public static string ElevatorArgs = "";

        /// <summary>
        /// Set by the UI layer to bring the main window to the foreground and wait until it is.
        /// Called right before a UAC prompt is triggered so the app owns the foreground and can
        /// delegate it to the consent UI (fixes prompts hiding behind the window with secure desktop off).
        /// Null in headless mode, where no window exists to activate.
        /// </summary>
        public static Func<Task>? BringMainWindowToForegroundAsync;

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

        /// <summary>
        /// The controlled launcher script that runs PowerShell package operations. It lives next to
        /// the application binary so it carries the same integrity as the executable itself, and it
        /// is invoked with -File so the operation parameters bind as data instead of being
        /// reassembled into a script body the way -Command does.
        /// </summary>
        public static string PowerShellOperationLauncher =>
            Path.Join(
                UniGetUIExecutableDirectory,
                "Assets",
                "Utilities",
                "unigetui_ps_operation.ps1"
            );

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
