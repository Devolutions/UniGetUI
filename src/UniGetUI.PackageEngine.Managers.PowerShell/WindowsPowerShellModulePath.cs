namespace UniGetUI.PackageEngine.Managers.PowerShellManager
{
    internal static class WindowsPowerShellModulePath
    {
        internal const string VariableName = "PSModulePath";

        internal static void Apply(IDictionary<string, string?> environment)
        {
            if (!environment.TryGetValue(VariableName, out string? current) || current is null)
                return;

            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string? repaired = Repair(current, documents, programFiles, IsPowerShellCoreHome);
            if (repaired is null)
                return;

            if (repaired.Length == 0)
                environment.Remove(VariableName);
            else
                environment[VariableName] = repaired;
        }

        internal static string? Repair(
            string value,
            string documents,
            string programFiles,
            Func<string, bool> isPowerShellCoreHome
        )
        {
            string? coreUserPath =
                documents.Length > 0 ? Path.Join(documents, "PowerShell", "Modules") : null;
            string? coreSharedPath =
                programFiles.Length > 0 ? Path.Join(programFiles, "PowerShell", "Modules") : null;
            string? windowsUserPath =
                documents.Length > 0 ? Path.Join(documents, "WindowsPowerShell", "Modules") : null;

            List<string> kept = [];
            bool removed = false;
            foreach (string rawEntry in value.Split(Path.PathSeparator))
            {
                string entry = rawEntry.Trim();
                if (entry.Length == 0)
                    continue;

                if (
                    SamePath(entry, coreUserPath)
                    || SamePath(entry, coreSharedPath)
                    || IsInsidePowerShellCoreHome(entry, isPowerShellCoreHome)
                )
                {
                    removed = true;
                    continue;
                }

                kept.Add(entry);
            }

            if (!removed)
                return null;

            if (kept.Count == 0)
                return "";

            if (windowsUserPath is not null && !kept.Any(entry => SamePath(entry, windowsUserPath)))
                kept.Insert(0, windowsUserPath);

            return string.Join(Path.PathSeparator, kept);
        }

        private static bool SamePath(string entry, string? path) =>
            path is not null
            && string.Equals(
                Path.TrimEndingDirectorySeparator(entry),
                Path.TrimEndingDirectorySeparator(path),
                StringComparison.OrdinalIgnoreCase
            );

        private static bool IsInsidePowerShellCoreHome(
            string entry,
            Func<string, bool> isPowerShellCoreHome
        )
        {
            string? parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(entry));
            return !string.IsNullOrEmpty(parent) && isPowerShellCoreHome(parent);
        }

        private static bool IsPowerShellCoreHome(string directory)
        {
            try
            {
                return File.Exists(Path.Join(directory, "pwsh.dll"));
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
