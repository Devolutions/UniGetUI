namespace UniGetUI.Core.Logging
{
    public static class AppPaths
    {
        private const string PortableMarkerFileName = "ForceUniGetUIPortable";
        private const string PortableDataDirectoryName = "Settings";
        private const string PortablePermissionTestFileName = "PermissionTestFile";
        private const string PortableScratchDirectoryName = "Temp";
        private const string FirstRunMarkerFileName = "FirstRun.pending";
        private const string ScratchDirectoryName = "UniGetUI";
        private const string BundledModernAppDirectoryName = "Avalonia";
        private const string WindowsExecutableName = "UniGetUI.exe";
        private const string BundledPingetExecutableName = "pinget.exe";

        private static readonly Lock PortableModeLock = new();
        private static string? __installation_directory;
        private static volatile bool __portable_mode_resolved;
        private static string? __portable_data_directory;

        [ThreadStatic]
        private static bool __resolving_portable_mode;

        /// <summary>
        /// A path pointing to the location where the app is installed
        /// </summary>
        public static string InstallationDirectory =>
            __installation_directory ??= ResolveInstallationDirectory(
                NormalizeDirectoryPath(AppContext.BaseDirectory)
            );

        public static string? TEST_PortableDataDirectoryOverride { private get; set; }

        /// <summary>
        /// Whether UniGetUI stores its data next to the executable. False when the marker file is
        /// absent, and also when it is present but the installation directory is not writable.
        /// </summary>
        public static bool IsPortable => PortableDataDirectory is not null;

        /// <summary>
        /// The portable data directory, or null when not running in portable mode.
        /// </summary>
        public static string? PortableDataDirectory =>
            TEST_PortableDataDirectoryOverride ?? ResolvedPortableDataDirectory;

        private static string? ResolvedPortableDataDirectory
        {
            get
            {
                if (__portable_mode_resolved)
                    return __portable_data_directory;

                if (__resolving_portable_mode)
                    return null;

                lock (PortableModeLock)
                {
                    if (__portable_mode_resolved)
                        return __portable_data_directory;

                    __resolving_portable_mode = true;
                    try
                    {
                        __portable_data_directory = ResolvePortableDataDirectory(InstallationDirectory);
                    }
                    finally
                    {
                        __resolving_portable_mode = false;
                        __portable_mode_resolved = true;
                    }
                }

                return __portable_data_directory;
            }
        }

        /// <summary>
        /// Whether the portable folder has yet to complete a first run. Recorded in the folder
        /// itself rather than in memory, so it survives a first launch that never reaches the UI
        /// - a headless run, a pre-UI CLI command, or a crash - and travels with the folder.
        /// </summary>
        public static bool IsFirstPortableRun =>
            PortableDataDirectory is { } directory
            && File.Exists(Path.Join(directory, FirstRunMarkerFileName));

        /// <summary>
        /// Records that the portable folder has completed its first run.
        /// </summary>
        public static void ClearFirstPortableRun()
        {
            try
            {
                if (PortableDataDirectory is { } directory)
                    File.Delete(Path.Join(directory, FirstRunMarkerFileName));
            }
            catch (Exception ex)
            {
                Logger.Warn("Could not clear the portable first-run marker");
                Logger.Warn(ex);
            }
        }

        /// <summary>
        /// The directory for files that must not outlive an uninstall: the session log, the
        /// WebView2 profile, per-attempt update logs, and the %TEMP% handed to elevated
        /// subprocesses. Not created automatically; callers that write must ensure it exists.
        /// </summary>
        public static string ScratchDirectory =>
            PortableDataDirectory is { } portableDirectory
                ? Path.Join(portableDirectory, PortableScratchDirectoryName)
                : Path.Join(Path.GetTempPath(), ScratchDirectoryName);

        public static string ResolveInstallationDirectory(
            string executableDirectory,
            Func<string, bool>? fileExists = null,
            Func<string, bool>? directoryExists = null
        )
        {
            fileExists ??= File.Exists;
            directoryExists ??= Directory.Exists;

            string normalizedDirectory = NormalizeDirectoryPath(executableDirectory);
            if (!string.Equals(
                    Path.GetFileName(normalizedDirectory),
                    BundledModernAppDirectoryName,
                    StringComparison.OrdinalIgnoreCase
                ))
            {
                return normalizedDirectory;
            }

            string? parentDirectory = Path.GetDirectoryName(normalizedDirectory);
            if (string.IsNullOrEmpty(parentDirectory))
            {
                return normalizedDirectory;
            }

            parentDirectory = NormalizeDirectoryPath(parentDirectory);
            return IsInstallRoot(parentDirectory, fileExists, directoryExists)
                ? parentDirectory
                : normalizedDirectory;
        }

        public static string? ResolvePortableDataDirectory(string installationDirectory)
        {
            if (!File.Exists(Path.Join(installationDirectory, PortableMarkerFileName)))
            {
                return null;
            }

            string path = Path.Join(installationDirectory, PortableDataDirectoryName);
            try
            {
                bool created = !Directory.Exists(path);
                if (created)
                    Directory.CreateDirectory(path);

                File.WriteAllText(
                    Path.Join(path, PortablePermissionTestFileName),
                    "https://www.youtube.com/watch?v=dQw4w9WgXcQ"
                );

                if (created)
                    File.WriteAllText(Path.Join(path, FirstRunMarkerFileName), "");

                return path;
            }
            catch (Exception ex)
            {
                Logger.Error(
                    $"Could not acces/write path {path}. UniGetUI will NOT be run in portable mode, and User settings will be used instead"
                );
                Logger.Error(ex);
                return null;
            }
        }

        private static string NormalizeDirectoryPath(string path)
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }

        private static bool IsInstallRoot(
            string directory,
            Func<string, bool> fileExists,
            Func<string, bool> directoryExists
        )
        {
            return fileExists(Path.Join(directory, WindowsExecutableName))
                   || fileExists(Path.Join(directory, BundledPingetExecutableName))
                   || fileExists(Path.Join(directory, "IntegrityTree.json"))
                   || directoryExists(Path.Join(directory, "Assets", "Utilities"))
                   || directoryExists(Path.Join(directory, "Assets", "Data"));
        }
    }
}
