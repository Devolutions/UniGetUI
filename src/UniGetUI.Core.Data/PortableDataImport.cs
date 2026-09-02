using UniGetUI.Core.Logging;

namespace UniGetUI.Core.Data
{
    /// <summary>
    /// Brings a per-user installation's settings into a freshly created portable folder.
    /// Only the directories a user would miss are considered; caches are left behind because
    /// they are rebuilt on demand and dwarf the rest.
    /// </summary>
    public static class PortableDataImport
    {
        private static readonly string[] ImportableDirectoryNames =
        [
            "Configuration",
            "InstallationOptions",
        ];

        /// <summary>
        /// The per-user directory worth importing from, or null when there is nothing to offer.
        /// Requires that the portable folder has not completed a first run: an established
        /// portable copy has settings of its own, and merging a per-user installation's settings
        /// into it is not what the offer promises. The folder's own contents cannot answer that,
        /// because startup writes settings files before anything could ask.
        /// </summary>
        public static string? FindImportableSource() =>
            FindImportableSource(AppPaths.IsFirstPortableRun);

        public static string? FindImportableSource(bool isFirstPortableRun)
        {
            if (!AppPaths.IsPortable || !isFirstPortableRun)
                return null;

            try
            {
                string source = CoreData.PerUserDataDirectoryPath;
                if (PathsAreEqual(source, CoreData.UniGetUIDataDirectory) || !HasImportableContent(source))
                    return null;

                return source;
            }
            catch (Exception ex)
            {
                Logger.Warn("Could not determine whether portable settings can be imported");
                Logger.Warn(ex);
                return null;
            }
        }

        /// <summary>
        /// Copies the importable directories into the portable data directory and returns the
        /// number of files copied. Existing files are never overwritten, and the source is left
        /// untouched so a co-installed per-user instance keeps working.
        /// </summary>
        public static int Import(string sourceDirectory)
        {
            string destination = CoreData.UniGetUIDataDirectory;
            int copied = 0;

            foreach (string directoryName in ImportableDirectoryNames)
            {
                string source = Path.Join(sourceDirectory, directoryName);
                if (!Directory.Exists(source))
                    continue;

                copied += CopyDirectory(source, Path.Join(destination, directoryName));
            }

            Logger.ImportantInfo($"Imported {copied} settings file(s) into the portable folder");
            return copied;
        }

        private static int CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            int copied = 0;

            foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                string relativePath = Path.GetRelativePath(source, file);
                string target = Path.Join(destination, relativePath);

                if (File.Exists(target))
                    continue;

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
                copied++;
            }

            return copied;
        }

        private static bool HasImportableContent(string directory)
        {
            foreach (string directoryName in ImportableDirectoryNames)
            {
                string candidate = Path.Join(directory, directoryName);
                if (Directory.Exists(candidate)
                    && Directory.EnumerateFiles(candidate, "*", SearchOption.AllDirectories).Any())
                {
                    return true;
                }
            }

            return false;
        }

        private static bool PathsAreEqual(string left, string right)
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal
            );
        }
    }
}
