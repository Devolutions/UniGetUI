#if WINDOWS
using System.Text.RegularExpressions;
using Microsoft.Win32;
#endif

namespace UniGetUI.PackageEngine.Classes.Manager.BaseProviders
{
    /// <summary>
    /// Resolves the install location of a package from the Windows "Add/Remove programs" (ARP)
    /// registry. This is a manager-agnostic fallback: any package that registered an uninstall
    /// entry (which most Windows installers do, regardless of the manager that ran them) can be
    /// located here when the manager's own logic cannot find the folder. On non-Windows platforms
    /// every method is a no-op.
    /// </summary>
    public static partial class ArpRegistryHelper
    {
#if WINDOWS
        private const long CacheTtlMs = 30_000;
        private static readonly Lock _lock = new();
        private static Dictionary<string, string>? _index; // normalized DisplayName -> install folder
        private static long _indexBuiltAt = long.MinValue;

        private const string UninstallSubKey =
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

        // Open both registry views explicitly (instead of relying on process bitness + a hard-coded
        // WOW6432Node path) so 64-bit and 32-bit entries are indexed regardless of the process arch,
        // including per-user 32-bit apps under HKCU.
        private static readonly (RegistryHive Hive, RegistryView View)[] UninstallRoots =
        [
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32),
            (RegistryHive.CurrentUser, RegistryView.Registry64),
            (RegistryHive.CurrentUser, RegistryView.Registry32),
        ];

        /// <summary>
        /// Resolves the install folder by matching the given candidate names against the system's
        /// ARP display names. Returns null when no confident match has a resolvable folder.
        /// </summary>
        public static string? ResolveByName(params string?[] candidateNames)
        {
            var index = GetIndex();
            if (index.Count == 0)
                return null;

            foreach (var name in candidateNames)
            {
                var target = Normalize(name);
                if (target.Length < 3)
                    continue;

                // Exact normalized match is the most reliable.
                if (index.TryGetValue(target, out var exact))
                    return exact;

                // Otherwise accept only the ARP name being the package name plus a version/edition
                // suffix (e.g. "Mozilla Firefox" vs "Mozilla Firefox (x64 en-US)" once normalized,
                // or "7-Zip" vs "7-Zip 23.01"). The reverse direction — the package name merely
                // starting with a shorter ARP name — is intentionally excluded, as it can resolve an
                // unrelated app (e.g. a package "javascript-x" matching an ARP entry "Java").
                if (target.Length < 4)
                    continue;
                foreach (var (key, location) in index)
                    if (key.StartsWith(target, StringComparison.Ordinal))
                        return location;
            }

            return null;
        }

        /// <summary>
        /// Resolves the install folder for a package whose Id encodes its registry location (as
        /// produced by the WinGet LocalPC source), e.g. "ARP\Machine\X64\{key}".
        /// </summary>
        public static string? GetLocationFromEncodedId(string packageId)
        {
            var bits = packageId.Split('\\');
            if (bits.Length < 4)
                return null;

            var hive = bits[1] == "Machine" ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;
            var view = bits[2] == "X86" ? RegistryView.Registry32 : RegistryView.Registry64;

            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var entry = baseKey.OpenSubKey(UninstallSubKey + "\\" + bits[3]);
                return entry is null ? null : ReadLocation(entry);
            }
            catch
            {
                return null;
            }
        }

        private static Dictionary<string, string> GetIndex()
        {
            lock (_lock)
            {
                var now = Environment.TickCount64;
                if (_index is null || now - _indexBuiltAt > CacheTtlMs)
                {
                    _index = BuildIndex();
                    _indexBuiltAt = now;
                }
                return _index;
            }
        }

        private static Dictionary<string, string> BuildIndex()
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var (hive, view) in UninstallRoots)
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var root = baseKey.OpenSubKey(UninstallSubKey);
                    if (root is null)
                        continue;

                    foreach (var name in root.GetSubKeyNames())
                    {
                        try
                        {
                            using var entry = root.OpenSubKey(name);
                            if (entry?.GetValue("DisplayName") is not string displayName)
                                continue;
                            if ((entry.GetValue("SystemComponent") as int?) == 1)
                                continue;

                            var normalized = Normalize(displayName);
                            if (normalized.Length < 3 || map.ContainsKey(normalized))
                                continue;

                            var location = ReadLocation(entry);
                            if (location is not null)
                                map[normalized] = location;
                        }
                        catch
                        {
                            // Skip unreadable entries.
                        }
                    }
                }
                catch
                {
                    // Skip unreadable hives.
                }
            }

            return map;
        }

        /// <summary>
        /// Resolves the install folder from a single ARP entry, trying the most-to-least reliable
        /// signals: the recorded InstallLocation, the folder of the DisplayIcon, then the folder of
        /// the uninstaller (which for EXE/NSIS/Inno installers lives in the install directory).
        /// </summary>
        private static string? ReadLocation(RegistryKey entry)
        {
            // Registry values can contain unexpanded environment variables (e.g. %ProgramFiles%),
            // so expand before testing/returning any path.
            if (entry.GetValue("InstallLocation") is string rawLocation && rawLocation.Length > 0)
            {
                var location = Environment
                    .ExpandEnvironmentVariables(rawLocation)
                    .Trim()
                    .Trim('"')
                    .TrimEnd('\\');
                if (location.Length > 0 && Directory.Exists(location))
                    return location;
            }

            if (entry.GetValue("DisplayIcon") is string displayIcon && displayIcon.Length > 0)
            {
                var iconPath = Environment
                    .ExpandEnvironmentVariables(displayIcon)
                    .Split(',')[0]
                    .Trim()
                    .Trim('"');
                var dir = DirectoryOf(iconPath);
                // MSI cached icons live under C:\Windows\Installer; never treat a system folder as
                // an install location.
                if (dir is not null && !IsSystemDirectory(dir))
                    return dir;
            }

            if (entry.GetValue("UninstallString") is string uninstall && uninstall.Length > 0)
            {
                var exe = ExtractExecutable(Environment.ExpandEnvironmentVariables(uninstall));
                if (exe is not null
                    && !Path.GetFileName(exe).Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase))
                {
                    var dir = DirectoryOf(exe);
                    if (dir is not null && !IsSystemDirectory(dir))
                        return dir;
                }
            }

            return null;
        }

        private static string? DirectoryOf(string filePath)
        {
            try
            {
                var dir = Path.GetDirectoryName(filePath);
                return !string.IsNullOrEmpty(dir) && Directory.Exists(dir) ? dir : null;
            }
            catch
            {
                return null;
            }
        }

        private static string? ExtractExecutable(string command)
        {
            command = command.Trim();
            if (command.StartsWith('"'))
            {
                var end = command.IndexOf('"', 1);
                return end > 1 ? command[1..end] : null;
            }

            var idx = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            return idx >= 0 ? command[..(idx + 4)] : command.Split(' ')[0];
        }

        private static bool IsSystemDirectory(string dir)
        {
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            return !string.IsNullOrEmpty(windows)
                && dir.StartsWith(windows, StringComparison.OrdinalIgnoreCase);
        }

        private static string Normalize(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "";
            value = ParentheticalRegex().Replace(value, " ");
            return NonAlphanumericRegex().Replace(value, "").ToLowerInvariant();
        }

        [GeneratedRegex(@"\([^)]*\)")]
        private static partial Regex ParentheticalRegex();

        [GeneratedRegex("[^a-zA-Z0-9]")]
        private static partial Regex NonAlphanumericRegex();
#else
        public static string? ResolveByName(params string?[] candidateNames) => null;

        public static string? GetLocationFromEncodedId(string packageId) => null;
#endif
    }
}
