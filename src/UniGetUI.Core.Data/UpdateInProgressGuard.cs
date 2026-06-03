using System.Diagnostics;

namespace UniGetUI.Core.Data
{
    // Blocks UI startup while the Windows installer is replacing files in {app} (see UniGetUI.iss),
    // so an instance launched mid-update doesn't load a half-written binary set and crash.
    public static class UpdateInProgressGuard
    {
        // MUST match the marker name written by UniGetUI.iss. The file holds the installer's PID.
        public const string MarkerFileName = ".unigetui-update-in-progress";

        public static bool IsUpdateInProgress()
        {
            if (!OperatingSystem.IsWindows())
                return false;

            return IsUpdateInProgress(AppContext.BaseDirectory);
        }

        // Checks the running dir and its parent (the Avalonia UI runs from {app}\Avalonia).
        internal static bool IsUpdateInProgress(string baseDirectory)
            => IsUpdateInProgress(baseDirectory, IsProcessRunning);

        internal static bool IsUpdateInProgress(string baseDirectory, Func<int, bool> isProcessRunning)
        {
            try
            {
                if (MarkerIsActive(baseDirectory, isProcessRunning))
                    return true;

                string? parent = Directory
                    .GetParent(baseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                    ?.FullName;
                return parent is not null && MarkerIsActive(parent, isProcessRunning);
            }
            catch
            {
                return false;
            }
        }

        // Active only while the installer that wrote the PID is still running, so the guard tracks
        // the real copy window (any duration) and self-heals if the installer dies without cleanup.
        private static bool MarkerIsActive(string directory, Func<int, bool> isProcessRunning)
        {
            string marker = Path.Combine(directory, MarkerFileName);
            if (!File.Exists(marker))
                return false;

            if (!int.TryParse(File.ReadAllText(marker).Trim(), out int pid))
                return false; // unreadable (e.g. racing the installer's write) — leave it alone

            if (isProcessRunning(pid))
                return true;

            try { File.Delete(marker); } catch { /* stale: installer is gone */ }
            return false;
        }

        private static bool IsProcessRunning(int pid)
        {
            if (pid <= 0)
                return false;

            try
            {
                using var process = Process.GetProcessById(pid);
                return !process.HasExited;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
    }
}
