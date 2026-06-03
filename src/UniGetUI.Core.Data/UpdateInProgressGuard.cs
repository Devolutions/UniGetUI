namespace UniGetUI.Core.Data
{
    // Blocks UI startup while the Windows installer is replacing files in {app} (see UniGetUI.iss),
    // so an instance launched mid-update doesn't load a half-written binary set and crash.
    public static class UpdateInProgressGuard
    {
        // MUST match the marker name written by UniGetUI.iss.
        public const string MarkerFileName = ".unigetui-update-in-progress";

        // Older markers are treated as leftovers from an interrupted install and ignored.
        private static readonly TimeSpan FreshnessWindow = TimeSpan.FromMinutes(10);

        public static bool IsUpdateInProgress()
        {
            if (!OperatingSystem.IsWindows())
                return false;

            return IsUpdateInProgress(AppContext.BaseDirectory);
        }

        // Checks the running dir and its parent (the Avalonia UI runs from {app}\Avalonia).
        internal static bool IsUpdateInProgress(string baseDirectory)
        {
            try
            {
                if (MarkerIsFresh(baseDirectory))
                    return true;

                string? parent = Directory
                    .GetParent(baseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                    ?.FullName;
                return parent is not null && MarkerIsFresh(parent);
            }
            catch
            {
                return false;
            }
        }

        private static bool MarkerIsFresh(string directory)
        {
            string marker = Path.Combine(directory, MarkerFileName);
            if (!File.Exists(marker))
                return false;

            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(marker) <= FreshnessWindow)
                return true;

            try { File.Delete(marker); } catch { /* stale marker */ }
            return false;
        }
    }
}
