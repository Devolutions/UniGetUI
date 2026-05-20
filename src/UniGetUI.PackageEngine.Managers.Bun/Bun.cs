using System.Diagnostics;
using System.Text.Json.Nodes;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Enums;
using UniGetUI.PackageEngine.Classes.Manager;
using UniGetUI.PackageEngine.Classes.Manager.ManagerHelpers;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.ManagerClasses.Classes;
using UniGetUI.PackageEngine.ManagerClasses.Manager;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.Structs;

namespace UniGetUI.PackageEngine.Managers.BunManager
{
    public class Bun : PackageManager
    {
        public Bun()
        {
            Capabilities = new ManagerCapabilities
            {
                CanRunAsAdmin = true,
                SupportsCustomVersions = true,
                CanDownloadInstaller = true,
                SupportsCustomScopes = false,
                CanListDependencies = true,
                SupportsPreRelease = true,
                SupportsProxy = ProxySupport.No,
                SupportsProxyAuth = false,
                KnowsPackageReleaseDate = PackageReleaseDateSupport.Yes,
            };

            Properties = new ManagerProperties
            {
                Id = "bun",
                Name = "Bun",
                Description = CoreTools.Translate("Fast JavaScript runtime, bundler, and package manager"),
                IconId = IconType.Bun,
                ColorIconId = "bun_color",
                ExecutableFriendlyName = "bun",
                InstallVerb = "add",
                UninstallVerb = "remove",
                UpdateVerb = "add",
                DefaultSource = new ManagerSource(this, "Bun", new Uri("https://www.npmjs.com/")),
                KnownSources = [new ManagerSource(this, "Bun", new Uri("https://www.npmjs.com/"))],
            };

            DetailsHelper = new BunPkgDetailsHelper(this);
            OperationHelper = new BunPkgOperationHelper(this);
        }

        protected override IReadOnlyList<Package> FindPackages_UnSafe(string query)
        {
            using Process p = new()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Status.ExecutablePath,
                    Arguments = Status.ExecutableCallArgs + " search \"" + query + "\" --json",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    StandardOutputEncoding = System.Text.Encoding.UTF8
                }
            };

            IProcessTaskLogger logger = TaskLogger.CreateNew(LoggableTaskType.FindPackages, p);
            p.Start();

            string strContents = p.StandardOutput.ReadToEnd();
            logger.AddToStdOut(strContents);
            logger.AddToStdErr(p.StandardError.ReadToEnd());
            p.WaitForExit();
            logger.Close(p.ExitCode);

            return ParseSearchOutput(strContents, DefaultSource, this);
        }

        protected override IReadOnlyList<Package> GetAvailableUpdates_UnSafe()
        {
            // bun outdated checks the project in the current directory, not a --global flag.
            // Until Bun supports per-project working directories in UniGetUI, expose Bun as
            // a global-only manager and query the dedicated global package.json.
            string globalDir = GetGlobalPackagesDirectory(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

            if (!HasGlobalPackageManifest(globalDir))
            {
                Logger.Info($"Bun: Skipping global update detection because {globalDir} is missing package.json");
                return [];
            }

            using Process p = new()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Status.ExecutablePath,
                    Arguments = Status.ExecutableCallArgs + " outdated",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = globalDir,
                    StandardOutputEncoding = System.Text.Encoding.UTF8
                }
            };

            IProcessTaskLogger logger = TaskLogger.CreateNew(LoggableTaskType.ListUpdates, p);
            p.Start();

            // Read both streams concurrently to avoid deadlock when the process writes
            // to both. Bun may write the table to stderr when stdout is not a TTY.
            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();
            Task.WaitAll(stdoutTask, stderrTask);

            string strOut = stdoutTask.Result;
            string strErr = stderrTask.Result;
            logger.AddToStdOut(strOut);
            logger.AddToStdErr(strErr);

            // Read the preference first
            bool preferLatest = Settings.Get(Settings.K.BunPreferLatestVersions);

            // Parse stdout first; fall back to stderr if stdout has no table rows.
            string tableSrc = ParseBunOutdatedTable(strOut, preferLatest).Any() ? strOut : strErr;
            var result = ParseAvailableUpdates(tableSrc, DefaultSource, this, preferLatest);

            Logger.Info($"Bun: Found {result.Count} packages with available updates (preferLatest={preferLatest})");
            foreach (var pkg in result)
            {
                Logger.Info($"  - {pkg.Id}: {pkg.VersionString} → {pkg.NewVersionString}");
            }

            p.WaitForExit();
            logger.Close(p.ExitCode);
            return result;
        }

        protected override IReadOnlyList<Package> GetInstalledPackages_UnSafe()
        {
            List<Package> Packages = [];

            using Process p = new()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Status.ExecutablePath,
                    Arguments = Status.ExecutableCallArgs + " pm ls --global",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    StandardOutputEncoding = System.Text.Encoding.UTF8
                }
            };

            IProcessTaskLogger logger = TaskLogger.CreateNew(LoggableTaskType.ListInstalledPackages, p);
            p.Start();

            // Read both streams concurrently to avoid deadlock when the process writes
            // to both. Bun may write to stderr when stdout is not a TTY.
            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();
            Task.WaitAll(stdoutTask, stderrTask);

            string strContents = stdoutTask.Result;
            logger.AddToStdOut(strContents);

            Packages.AddRange(ParseInstalledPackages(strContents, DefaultSource, this, new OverridenInstallationOptions(PackageScope.Global)));

            logger.AddToStdErr(stderrTask.Result);
            p.WaitForExit();
            logger.Close(p.ExitCode);

            return Packages;
        }

        public override IReadOnlyList<string> FindCandidateExecutableFiles()
            => CoreTools.WhichMultiple(OperatingSystem.IsWindows() ? "bun.exe" : "bun");

        internal static string GetGlobalPackagesDirectory(string userProfile)
            => Path.Combine(userProfile, ".bun", "install", "global");

        internal static bool HasGlobalPackageManifest(string globalDir)
            => Directory.Exists(globalDir) && File.Exists(Path.Combine(globalDir, "package.json"));

        protected override void _loadManagerExecutableFile(out bool found, out string path, out string callArguments)
        {
            var (_found, _executablePath) = GetExecutableFile();
            found = _found;
            path = _executablePath;
            callArguments = "";
        }

        protected override void _loadManagerVersion(out string version)
        {
            Process process = new()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Status.ExecutablePath,
                    Arguments = Status.ExecutableCallArgs + "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    CreateNoWindow = true,
                    WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    StandardOutputEncoding = System.Text.Encoding.UTF8
                }
            };
            process.Start();
            version = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
        }

        /// <summary>
        /// Parses JSON search results from 'bun search &lt;query&gt; --json'.
        /// Each result object contains 'name' and 'version' fields.
        /// </summary>
        internal static IReadOnlyList<Package> ParseSearchOutput(
            string output,
            IManagerSource source,
            IPackageManager manager)
        {
            List<Package> packages = [];

            if (!output.Any()) return packages;

            try
            {
                JsonArray? results = JsonNode.Parse(output) as JsonArray;
                foreach (JsonNode? entry in results ?? [])
                {
                    string? id = entry?["name"]?.ToString();
                    string? version = entry?["version"]?.ToString();
                    if (id is not null && version is not null)
                    {
                        packages.Add(new Package(CoreTools.FormatAsName(id), id, version, source, manager));
                    }
                }
            }
            catch (Exception e)
            {
                Logger.Warn($"Failed to parse Bun search results: {e.Message}");
            }

            return packages;
        }

        /// <summary>
        /// Parses the outdated packages table from 'bun outdated'.
        /// Creates packages with current and available versions and sets scope to Global.
        /// </summary>
        internal static IReadOnlyList<Package> ParseAvailableUpdates(
            string output,
            IManagerSource source,
            IPackageManager manager,
            bool preferLatest = false)
        {
            List<Package> packages = [];

            foreach (var (packageId, version, newVersion) in ParseBunOutdatedTable(output, preferLatest))
            {
                packages.Add(new Package(CoreTools.FormatAsName(packageId), packageId, version, newVersion,
                    source, manager, new(PackageScope.Global)));
            }

            return packages;
        }

        /// <summary>
        /// Parses the installed packages tree from 'bun pm ls --global'.
        /// Each package entry is formatted as: [├/└]── [@scope/]name@version
        /// </summary>
        internal static IReadOnlyList<Package> ParseInstalledPackages(
            string output,
            IManagerSource source,
            IPackageManager manager,
            OverridenInstallationOptions options)
        {
            List<Package> packages = [];

            // bun pm ls --global outputs a tree:
            // /home/user/.bun/install/global node_modules (3)
            // ├── @devcontainers/cli@0.81.1
            // └── typescript@5.7.3
            foreach (string line in output.Split('\n'))
            {
                if (!line.Contains("──")) continue;
                string entry = line[(line.IndexOf("──") + 2)..].Trim();

                // Use LastIndexOf to handle scoped packages: @scope/name@version
                int atIdx = entry.LastIndexOf('@');
                if (atIdx <= 0) continue;

                string packageName = entry[..atIdx];
                string version = entry[(atIdx + 1)..];

                if (string.IsNullOrWhiteSpace(packageName) || string.IsNullOrWhiteSpace(version)) continue;

                packages.Add(new Package(CoreTools.FormatAsName(packageName), packageName, version,
                    source, manager, options));
            }

            return packages;
        }

        /// <summary>
        /// Parses the outdated packages table from 'bun outdated'.
        /// Supports both ASCII pipe format (|) and Unicode box-drawing format (│).
        /// Each yielded tuple contains (packageId, currentVersion, recommendedUpdateVersion).
        /// Columns: [Package | Current | Update | Latest]
        /// If preferLatest is true, uses "Latest" (parts[4], may have breaking changes).
        /// If preferLatest is false (default), uses "Update" (parts[3], safe semantic version).
        /// </summary>
        // TODO: Replace table parsing with JSON deserialization when bun outdated adds --json flag.
        // Track: https://github.com/oven-sh/bun/issues — once --json is available, this entire
        // method should be swapped for a simple JsonNode.Parse() call.
        internal static IEnumerable<(string Id, string Version, string NewVersion)> ParseBunOutdatedTable(
            string output,
            bool preferLatest = false)
        {
            int columnIndex = preferLatest ? 4 : 3; // 4 = Latest, 3 = Update
            string columnName = preferLatest ? "Latest" : "Update";

            foreach (string line in output.Split('\n'))
            {
                string trimmed = line.TrimStart();
                // Skip lines that don't contain package data (headers, separators, etc.)
                if (!trimmed.StartsWith('│') && !trimmed.StartsWith('|'))
                {
                    continue;

                }
                // Split by either Unicode box-drawing or ASCII pipe characters
                string[] parts = line.Split(new[] { '│', '|' }, StringSplitOptions.None);
                if (parts.Length < columnIndex + 1)
                {
                    continue;
                }

                string id = parts[1].Trim();
                string version = parts[2].Trim();
                string recommendedUpdate = parts[columnIndex].Trim();

                // Skip header row, empty rows, and border lines (which contain only dashes or box-drawing chars)
                if (id is "Package" || string.IsNullOrWhiteSpace(id)
                    || string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(recommendedUpdate)
                    || id.All(c => c == '-' || c == '─' || c == '┬' || c == '┼' || c == '┴' || c == '├' || c == '┤' || c == '┌' || c == '└' || c == '┘' || c == '┐')
                    || version.All(c => c == '-' || c == '─' || c == '┬' || c == '┼' || c == '┴' || c == '├' || c == '┤' || c == '┌' || c == '└' || c == '┘' || c == '┐'))
                {
                    continue;
                }

                // Only include packages that have a different update version
                if (version != recommendedUpdate)
                {
                    Logger.Debug($"Bun: Found update for {id}: {version} → {recommendedUpdate} (using {columnName} column)");
                    yield return (id, version, recommendedUpdate);
                }
                else
                {
                    Logger.Debug($"Bun: Skipping {id} (no update available: {version} == {recommendedUpdate})");
                }
            }
        }
    }
}
