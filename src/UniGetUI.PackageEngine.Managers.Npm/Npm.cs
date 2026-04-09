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

namespace UniGetUI.PackageEngine.Managers.NpmManager
{
    public class Npm : PackageManager
    {
        public Npm()
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
                SupportsProxyAuth = false,
            };

            Properties = new ManagerProperties
            {
                Name = "Npm",
                Description = CoreTools.Translate(
                    "Node JS's package manager. Full of libraries and other utilities that orbit the javascript world<br>Contains: <b>Node javascript libraries and other related utilities</b>"
                ),
                IconId = IconType.Node,
                ColorIconId = "node_color",
                ExecutableFriendlyName = "npm",
                InstallVerb = "install",
                UninstallVerb = "uninstall",
                UpdateVerb = "install",
                DefaultSource = new ManagerSource(this, "npm", new Uri("https://www.npmjs.com/")),
                KnownSources = [new ManagerSource(this, "npm", new Uri("https://www.npmjs.com/"))],
            };

            DetailsHelper = new NpmPkgDetailsHelper(this);
            OperationHelper = new NpmPkgOperationHelper(this);
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
                    WorkingDirectory = Environment.GetFolderPath(
                        Environment.SpecialFolder.UserProfile
                    ),
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                },
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
            List<Package> Packages = [];
            foreach (
                var options in new OverridenInstallationOptions[]
                {
                    new(PackageScope.Local),
                    new(PackageScope.Global),
                }
            )
            {
                using Process p = new()
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = Status.ExecutablePath,
                        Arguments =
                            Status.ExecutableCallArgs
                            + " outdated --json"
                            + (options.Scope == PackageScope.Global ? " --global" : ""),
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        RedirectStandardInput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WorkingDirectory = Environment.GetFolderPath(
                            Environment.SpecialFolder.UserProfile
                        ),
                        StandardOutputEncoding = System.Text.Encoding.UTF8,
                    },
                };

                IProcessTaskLogger logger = TaskLogger.CreateNew(LoggableTaskType.ListUpdates, p);
                p.Start();

                string strContents = p.StandardOutput.ReadToEnd();
                logger.AddToStdOut(strContents);
                Packages.AddRange(ParseAvailableUpdatesOutput(strContents, DefaultSource, this, options));

                logger.AddToStdErr(p.StandardError.ReadToEnd());
                p.WaitForExit();
                logger.Close(p.ExitCode);
            }
            return Packages;
        }

        protected override IReadOnlyList<Package> GetInstalledPackages_UnSafe()
        {
            List<Package> Packages = [];
            foreach (
                var options in new OverridenInstallationOptions[]
                {
                    new(PackageScope.Local),
                    new(PackageScope.Global),
                }
            )
            {
                using Process p = new()
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = Status.ExecutablePath,
                        Arguments =
                            Status.ExecutableCallArgs
                            + " list --json"
                            + (options.Scope == PackageScope.Global ? " --global" : ""),
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        RedirectStandardInput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WorkingDirectory = Environment.GetFolderPath(
                            Environment.SpecialFolder.UserProfile
                        ),
                        StandardOutputEncoding = System.Text.Encoding.UTF8,
                    },
                };

                IProcessTaskLogger logger = TaskLogger.CreateNew(
                    LoggableTaskType.ListInstalledPackages,
                    p
                );
                p.Start();

                string strContents = p.StandardOutput.ReadToEnd();
                logger.AddToStdOut(strContents);
                Packages.AddRange(ParseInstalledPackagesOutput(strContents, DefaultSource, this, options));

                logger.AddToStdErr(p.StandardError.ReadToEnd());
                p.WaitForExit();
                logger.Close(p.ExitCode);
            }

            return Packages;
        }

        public override IReadOnlyList<string> FindCandidateExecutableFiles() =>
            CoreTools.WhichMultiple(OperatingSystem.IsWindows() ? "npm.cmd" : "npm");

        protected override void _loadManagerExecutableFile(
            out bool found,
            out string path,
            out string callArguments
        )
        {
            var (_found, _executable) = GetExecutableFile();

            found = _found;

            if (OperatingSystem.IsWindows())
            {
                path = CoreData.PowerShell5;
                callArguments =
                    $"-NoProfile -ExecutionPolicy Bypass -Command \"{_executable.Replace(" ", "` ")}\" ";
                return;
            }

            path = _executable;
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
                    WorkingDirectory = Environment.GetFolderPath(
                        Environment.SpecialFolder.UserProfile
                    ),
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                },
            };
            process.Start();
            version = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
        }

        internal static IReadOnlyList<Package> ParseSearchOutput(
            string output,
            IManagerSource source,
            IPackageManager manager
        )
        {
            List<Package> packages = [];

            void TryAdd(JsonNode? node)
            {
                string? id = node?["name"]?.ToString();
                string? version = node?["version"]?.ToString();
                if (id is not null && version is not null)
                    packages.Add(new Package(CoreTools.FormatAsName(id), id, version, source, manager));
            }

            bool parsedAsArray = false;
            int arrayStart = output.IndexOf('[');
            if (arrayStart >= 0)
            {
                try
                {
                    JsonArray? results = JsonNode.Parse(output[arrayStart..]) as JsonArray;
                    foreach (JsonNode? entry in results ?? [])
                        TryAdd(entry);
                    parsedAsArray = true;
                }
                catch (Exception e)
                {
                    Logger.Warn($"npm search JSON array parse failed, falling back to NDJSON: {e.Message}");
                }
            }

            if (!parsedAsArray)
            {
                foreach (string line in output.Split('\n'))
                {
                    string trimmed = line.Trim();
                    if (!trimmed.StartsWith("{"))
                        continue;

                    try
                    {
                        TryAdd(JsonNode.Parse(trimmed));
                    }
                    catch (Exception e)
                    {
                        Logger.Warn($"npm search NDJSON line parse failed: {e.Message}");
                    }
                }
            }

            return packages;
        }

        internal static IReadOnlyList<Package> ParseAvailableUpdatesOutput(
            string output,
            IManagerSource source,
            IPackageManager manager,
            OverridenInstallationOptions options
        )
        {
            List<Package> packages = [];
            if (!output.Any())
                return packages;

            JsonObject? contents = JsonNode.Parse(output) as JsonObject;
            foreach (var (packageId, packageData) in contents?.ToDictionary() ?? [])
            {
                string? version = packageData?["current"]?.ToString();
                string? newVersion = packageData?["latest"]?.ToString();
                if (version is not null && newVersion is not null)
                {
                    packages.Add(
                        new Package(
                            CoreTools.FormatAsName(packageId),
                            packageId,
                            version,
                            newVersion,
                            source,
                            manager,
                            options
                        )
                    );
                }
            }

            return packages;
        }

        internal static IReadOnlyList<Package> ParseInstalledPackagesOutput(
            string output,
            IManagerSource source,
            IPackageManager manager,
            OverridenInstallationOptions options
        )
        {
            List<Package> packages = [];
            if (!output.Any())
                return packages;

            JsonObject? contents = (JsonNode.Parse(output) as JsonObject)?["dependencies"] as JsonObject;
            foreach (var (packageId, packageData) in contents?.ToDictionary() ?? [])
            {
                string? version = packageData?["version"]?.ToString();
                if (version is not null)
                {
                    packages.Add(
                        new Package(
                            CoreTools.FormatAsName(packageId),
                            packageId,
                            version,
                            source,
                            manager,
                            options
                        )
                    );
                }
            }

            return packages;
        }
    }
}
