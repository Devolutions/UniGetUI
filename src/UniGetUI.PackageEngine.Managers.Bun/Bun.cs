using System.Diagnostics;
using System.Text.Json.Nodes;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
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
                SupportsCustomScopes = true,
                CanListDependencies = true,
                SupportsPreRelease = true,
                SupportsProxy = ProxySupport.No,
                SupportsProxyAuth = false
            };

            Properties = new ManagerProperties
            {
                Id = "bun",
                Name = "Bun",
                Description = CoreTools.Translate("A npmjs package manager written in Zig. Full of libraries and other utilities that orbit the javascript world<br>Contains: <b>Node javascript libraries and other related utilities</b>"),
                IconId = IconType.Node,
                ColorIconId = "node_color",
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
            List<Package> Packages = [];

            if (strContents.Any())
            {
                try
                {
                    JsonArray? results = JsonNode.Parse(strContents) as JsonArray;
                    foreach (JsonNode? entry in results ?? [])
                    {
                        string? id = entry?["name"]?.ToString();
                        string? version = entry?["version"]?.ToString();
                        if (id is not null && version is not null)
                        {
                            Packages.Add(new Package(CoreTools.FormatAsName(id), id, version, DefaultSource, this));
                        }
                    }
                }
                catch (Exception e)
                {
                    logger.AddToStdErr($"Failed to parse search results: {e.Message}");
                }
            }

            logger.AddToStdErr(p.StandardError.ReadToEnd());
            p.WaitForExit();
            logger.Close(p.ExitCode);

            return Packages;
        }

        protected override IReadOnlyList<Package> GetAvailableUpdates_UnSafe()
        {
            List<Package> Packages = [];

            // bun outdated checks the project in the current directory, not a --global flag.
            // Global packages live in ~/.bun/install/global which has its own package.json.
            string globalDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".bun", "install", "global");

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
                    WorkingDirectory = Directory.Exists(globalDir) ? globalDir
                        : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
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

            // Parse stdout first; fall back to stderr if stdout has no table rows.
            string tableSrc = ParseBunOutdatedTable(strOut).Any() ? strOut : strErr;
            foreach (var (packageId, version, newVersion) in ParseBunOutdatedTable(tableSrc))
            {
                Packages.Add(new Package(CoreTools.FormatAsName(packageId), packageId, version, newVersion,
                    DefaultSource, this, new(PackageScope.Global)));
            }

            p.WaitForExit();
            logger.Close(p.ExitCode);
            return Packages;
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

            string strContents = p.StandardOutput.ReadToEnd();
            logger.AddToStdOut(strContents);

            // bun pm ls --global outputs a tree:
            // /home/user/.bun/install/global node_modules (3)
            // ├── @devcontainers/cli@0.81.1
            // └── typescript@5.7.3
            foreach (string line in strContents.Split('\n'))
            {
                if (!line.Contains("──")) continue;
                string entry = line[(line.IndexOf("──") + 2)..].Trim();

                // Use LastIndexOf to handle scoped packages: @scope/name@version
                int atIdx = entry.LastIndexOf('@');
                if (atIdx <= 0) continue;

                string packageName = entry[..atIdx];
                string version = entry[(atIdx + 1)..];

                if (string.IsNullOrWhiteSpace(packageName) || string.IsNullOrWhiteSpace(version)) continue;

                Packages.Add(new Package(CoreTools.FormatAsName(packageName), packageName, version,
                    DefaultSource, this, new(PackageScope.Global)));
            }

            logger.AddToStdErr(p.StandardError.ReadToEnd());
            p.WaitForExit();
            logger.Close(p.ExitCode);

            return Packages;
        }

        public override IReadOnlyList<string> FindCandidateExecutableFiles()
            => CoreTools.WhichMultiple(OperatingSystem.IsWindows() ? "bun.exe" : "bun");

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
        /// Parses the Unicode box-drawing table produced by <c>bun outdated</c>.
        /// Each yielded tuple contains (packageId, currentVersion, latestVersion).
        /// Columns: │ Package │ Current │ Update │ Latest │
        /// </summary>
        private static IEnumerable<(string Id, string Version, string NewVersion)> ParseBunOutdatedTable(string output)
        {
            foreach (string line in output.Split('\n'))
            {
                if (!line.TrimStart().StartsWith('│')) continue;
                string[] parts = line.Split('│');
                if (parts.Length < 5) continue;

                string id = parts[1].Trim();
                string version = parts[2].Trim();
                string newVersion = parts[4].Trim();

                if (id is "Package" || string.IsNullOrWhiteSpace(id)
                    || string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(newVersion)) continue;

                yield return (id, version, newVersion);
            }
        }
    }
}
