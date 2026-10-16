using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace UniGetUI.Core.Logging
{
    public static partial class AppPaths
    {
        private const string PortableMarkerFileName = "ForceUniGetUIPortable";
        private const string PortableDataDirectoryName = "Settings";
        private const string PortablePermissionTestFileName = "PermissionTestFile";
        private const string PortableScratchDirectoryName = "Temp";
        private const string FirstRunMarkerFileName = "FirstRun.pending";
        private const string ScratchDirectoryName = "UniGetUI";
        private const string ElevatedTempDirectoryName = "ElevatedWinGetTemp";
        private const uint FileReadAttributes = 0x00000080;
        private const uint FileShareAll = 0x00000007;
        private const uint OpenExisting = 3;
        private const uint FileFlagBackupSemantics = 0x02000000;
        private const uint FileNameNormalized = 0x00000000;
        private const uint VolumeNameGuid = 0x00000001;
        private const string VolumeGuidPathPrefix = @"\\?\Volume{";
        private const int PathBufferLength = 1024;
        private const string ExtendedPathPrefix = @"\\?\";
        private const string ExtendedUncPathPrefix = @"\\?\UNC\";
        private const string BundledModernAppDirectoryName = "Avalonia";
        private const string WindowsExecutableName = "UniGetUI.exe";
        private const string BundledPingetExecutableName = "pinget.exe";

        private static readonly Lock PortableModeLock = new();
        private static string? __installation_directory;
        private static volatile bool __portable_mode_resolved;
        private static string? __portable_data_directory;
        private static ElevatedTempDirectoryCache? __elevated_temp_directory;

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

        public static string ElevatedTempDirectory
        {
            get
            {
                string unresolved = Path.Join(ScratchDirectory, ElevatedTempDirectoryName);
                if (__elevated_temp_directory is { } cached && cached.Unresolved == unresolved)
                    return cached.Resolved;

                if (!TryResolveLinkedDirectories(unresolved, out string resolved))
                    return resolved;

                __elevated_temp_directory = new ElevatedTempDirectoryCache(unresolved, resolved);
                return resolved;
            }
        }

        private sealed record ElevatedTempDirectoryCache(string Unresolved, string Resolved);

        public static string RedirectTempToElevatedDirectory(ProcessStartInfo startInfo)
        {
            string directory = ElevatedTempDirectory;
            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logger.Warn($"Could not create the elevated temporary directory {directory}");
                Logger.Warn(ex);
            }

            startInfo.Environment["TEMP"] = directory;
            startInfo.Environment["TMP"] = directory;
            return directory;
        }

        public static string ResolveLinkedDirectories(string path)
        {
            TryResolveLinkedDirectories(path, out string resolved);
            return resolved;
        }

        private static bool TryResolveLinkedDirectories(string path, out string resolved)
        {
            string fullPath = Path.GetFullPath(path);
            resolved = fullPath;
            if (!OperatingSystem.IsWindows())
                return true;

            string existing = fullPath;
            while (!Directory.Exists(existing))
            {
                string? parent = Path.GetDirectoryName(existing);
                if (parent is null)
                    return false;
                existing = parent;
            }

            string? finalPath = GetFinalDirectoryPath(existing);
            if (finalPath is null)
            {
                Logger.Warn($"Could not resolve the links in {existing}, using the path as-is");
                return false;
            }

            resolved = Path.Join(finalPath, fullPath[existing.Length..]);
            return true;
        }

        private static string? GetFinalDirectoryPath(string directory)
        {
            using SafeFileHandle handle = CreateFile(
                directory,
                FileReadAttributes,
                FileShareAll,
                0,
                OpenExisting,
                FileFlagBackupSemantics,
                0
            );
            if (handle.IsInvalid)
                return null;

            if (GetFinalPathName(handle, FileNameNormalized) is { } dosPath)
                return StripExtendedPathPrefix(dosPath);

            if (
                GetFinalPathName(handle, VolumeNameGuid) is not { } guidPath
                || !guidPath.StartsWith(VolumeGuidPathPrefix, StringComparison.OrdinalIgnoreCase)
            )
                return null;

            int volumeEnd = guidPath.IndexOf('\\', VolumeGuidPathPrefix.Length);
            if (volumeEnd < 0)
                return null;

            string? mountPath = GetVolumeMountPath(guidPath[..(volumeEnd + 1)]);
            return mountPath is null ? null : Path.Join(mountPath, guidPath[(volumeEnd + 1)..]);
        }

        private static string? GetFinalPathName(SafeFileHandle handle, uint flags)
        {
            char[] buffer = new char[PathBufferLength];
            uint length = GetFinalPathNameByHandle(handle, ref buffer[0], (uint)buffer.Length, flags);
            if (length >= buffer.Length)
            {
                buffer = new char[length];
                length = GetFinalPathNameByHandle(handle, ref buffer[0], (uint)buffer.Length, flags);
            }

            return length is 0 || length >= buffer.Length ? null : new string(buffer, 0, (int)length);
        }

        private static string? GetVolumeMountPath(string volumeName)
        {
            char[] buffer = new char[PathBufferLength];
            if (!GetVolumePathNamesForVolumeName(volumeName, ref buffer[0], (uint)buffer.Length, out uint required))
            {
                if (required <= buffer.Length)
                    return null;

                buffer = new char[required];
                if (!GetVolumePathNamesForVolumeName(volumeName, ref buffer[0], (uint)buffer.Length, out _))
                    return null;
            }

            int end = Array.IndexOf(buffer, '\0');
            return end > 0 ? new string(buffer, 0, end) : null;
        }

        private static string StripExtendedPathPrefix(string path)
        {
            if (path.StartsWith(ExtendedUncPathPrefix, StringComparison.Ordinal))
                return @"\\" + path[ExtendedUncPathPrefix.Length..];

            return path.StartsWith(ExtendedPathPrefix, StringComparison.Ordinal)
                ? path[ExtendedPathPrefix.Length..]
                : path;
        }

        [LibraryImport(
            "kernel32.dll",
            EntryPoint = "CreateFileW",
            SetLastError = true,
            StringMarshalling = StringMarshalling.Utf16)]
        private static partial SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            nint securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            nint templateFile);

        [LibraryImport(
            "kernel32.dll",
            EntryPoint = "GetFinalPathNameByHandleW",
            SetLastError = true,
            StringMarshalling = StringMarshalling.Utf16)]
        private static partial uint GetFinalPathNameByHandle(
            SafeFileHandle file,
            ref char filePath,
            uint charCount,
            uint flags);

        [LibraryImport(
            "kernel32.dll",
            EntryPoint = "GetVolumePathNamesForVolumeNameW",
            SetLastError = true,
            StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetVolumePathNamesForVolumeName(
            string volumeName,
            ref char volumePathNames,
            uint bufferLength,
            out uint returnLength);

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
