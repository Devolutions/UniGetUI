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
        public override bool CommandLineIsShellInterpreted => true;

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
                KnowsPackageReleaseDate = PackageReleaseDateSupport.Yes,
            };

            Properties = new ManagerProperties
            {
                Id = "npm",
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
            var startInfo = new ProcessStartInfo
            {
                FileName = Status.ExecutablePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Environment.GetFolderPath(
                    Environment.SpecialFolder.UserProfile
                ),
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            };

            if (
                Status.OperationCallArgs.Count is 0
                && !string.IsNullOrWhiteSpace(Status.ExecutableCallArgs)
            )
                startInfo.Arguments = BuildSearchArguments(query);
            else
                Status.ApplyArguments(startInfo, "search", query, "--json");

            using Process p = new() { StartInfo = startInfo };

            IProcessTaskLogger logger = TaskLogger.CreateNew(LoggableTaskType.FindPackages, p);
            CoreTools.StartAndCloseStandardInput(p);

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
                CoreTools.StartAndCloseStandardInput(p);

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
                CoreTools.StartAndCloseStandardInput(p);

                string strContents = p.StandardOutput.ReadToEnd();
                logger.AddToStdOut(strContents);
                Packages.AddRange(ParseInstalledPackagesOutput(strContents, DefaultSource, this, options));

                logger.AddToStdErr(p.StandardError.ReadToEnd());
                p.WaitForExit();
                logger.Close(p.ExitCode);
            }

            return Packages;
        }

        /// <summary>
        /// When npm is reached through its own npm.ps1 the query travels as a separate argument, so
        /// nothing has to be escaped. The remaining case is a Windows install without npm.ps1, where
        /// the call still goes through powershell -Command and the query has to be quoted.
        /// </summary>
        private string BuildSearchArguments(string query) =>
            Status.ExecutableCallArgs
            + " search "
            + CoreTools.EscapePowerShellSingleQuoted(query)
            + " --json";

        public override IReadOnlyList<string> FindCandidateExecutableFiles()
        {
            if (OperatingSystem.IsWindows())
                return CoreTools.WhichMultiple("npm.cmd");

            return CoreTools.WhichMultiple("npm")
                .Concat(
                    GetNvmBinDirectories(
                            Environment.GetEnvironmentVariable,
                            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                        )
                        .Select(directory => Path.Join(directory, "npm"))
                        .Where(IsExecutableFile)
                )
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        internal static IReadOnlyList<string> GetNvmBinDirectories(
            Func<string, string?> readEnvironmentVariable,
            string userProfileDirectory
        )
        {
            string nvmDirectory;
            if (readEnvironmentVariable("NVM_DIR")?.Trim() is { Length: > 0 } configured)
                nvmDirectory = configured;
            else if (userProfileDirectory.Trim() is { Length: > 0 } userProfile)
                nvmDirectory = Path.Join(userProfile, ".nvm");
            else
                return [];

            List<(Version Version, string Directory)> installed = [];
            try
            {
                string versionsDirectory = Path.Join(nvmDirectory, "versions", "node");
                if (!Directory.Exists(versionsDirectory))
                    return [];

                foreach (string directory in Directory.EnumerateDirectories(versionsDirectory))
                {
                    string name = Path.GetFileName(directory);
                    if (name.StartsWith('v') && Version.TryParse(name[1..], out Version? version))
                        installed.Add((version, directory));
                }
            }
            catch (IOException)
            {
                return [];
            }
            catch (UnauthorizedAccessException)
            {
                return [];
            }

            installed.Sort((a, b) => b.Version.CompareTo(a.Version));

            if (ResolveNvmAlias(nvmDirectory, "default") is { } defaultVersion)
            {
                int index = installed.FindIndex(entry => MatchesNvmVersion(entry.Version, defaultVersion));
                if (index > 0)
                {
                    var entry = installed[index];
                    installed.RemoveAt(index);
                    installed.Insert(0, entry);
                }
            }

            return installed.Select(entry => Path.Join(entry.Directory, "bin")).ToArray();
        }

        private static string? ResolveNvmAlias(string nvmDirectory, string alias)
        {
            for (int hop = 0; hop < 8; hop++)
            {
                string value;
                try
                {
                    string aliasFile = Path.Join(nvmDirectory, "alias", alias);
                    if (!File.Exists(aliasFile))
                        return null;
                    value = File.ReadAllText(aliasFile).Trim();
                }
                catch (IOException)
                {
                    return null;
                }
                catch (UnauthorizedAccessException)
                {
                    return null;
                }

                if (value.Length is 0 || value is "node" or "stable")
                    return null;
                if (IsNvmVersionSpecification(value))
                    return value;
                alias = value;
            }

            return null;
        }

        private static bool IsNvmVersionSpecification(string value)
        {
            string[] parts = (value.StartsWith('v') ? value[1..] : value).Split('.');
            return parts.Length is >= 1 and <= 3
                && parts.All(part => part.Length > 0 && part.All(char.IsAsciiDigit));
        }

        private static bool MatchesNvmVersion(Version version, string specification)
        {
            string[] parts = specification.TrimStart('v').Split('.');
            int[] components = [version.Major, version.Minor, version.Build];
            if (parts.Length > components.Length)
                return false;

            for (int i = 0; i < parts.Length; i++)
            {
                if (!int.TryParse(parts[i], out int value) || value != components[i])
                    return false;
            }

            return true;
        }

        private static bool IsExecutableFile(string path)
        {
            if (!File.Exists(path))
                return false;

            if (OperatingSystem.IsWindows())
                return true;

            const UnixFileMode ExecutableBits =
                UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

            try
            {
                return (File.GetUnixFileMode(path) & ExecutableBits) is not 0;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        internal static string AppendDirectoryToPath(string? currentPath, string directory)
        {
            if (string.IsNullOrEmpty(currentPath))
                return directory;

            string trimmedDirectory = Path.TrimEndingDirectorySeparator(directory);
            bool alreadyPresent = currentPath
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Any(entry => Path.TrimEndingDirectorySeparator(entry) == trimmedDirectory);

            return alreadyPresent ? currentPath : currentPath + Path.PathSeparator + directory;
        }

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

            if (found && Path.GetDirectoryName(_executable) is { Length: > 0 } executableDirectory)
            {
                string? currentPath = Environment.GetEnvironmentVariable("PATH");
                string updatedPath = AppendDirectoryToPath(currentPath, executableDirectory);
                if (updatedPath != currentPath)
                {
                    Environment.SetEnvironmentVariable("PATH", updatedPath);
                    Logger.Info($"Added {executableDirectory} to the PATH so npm can reach its node");
                }
            }

            path = _executable;
            callArguments = "";
        }

        protected override IReadOnlyList<string> _getOperationCallArgs(
            string executablePath,
            string callArguments
        )
        {
            if (!OperatingSystem.IsWindows())
                return [];

            var (found, executable) = GetExecutableFile();
            if (!found)
                return [];

            string? script = ResolvePowerShellEntryPoint(executable);
            if (script is null)
                return [];

            // Finding npm.ps1 does not prove this shell may run it, and npm.cmd still works, so
            // the capability is confirmed before giving up the working fallback.
            if (!CoreTools.PowerShellLauncherWorks(executablePath, CoreData.PowerShellOperationLauncher))
            {
                Logger.Warn("Not using -File for npm operations; falling back to -Command");
                return [];
            }

            return ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script];
        }

        /// <summary>
        /// npm ships an npm.ps1 next to the npm.cmd shim. Running that script with -File keeps the
        /// operation parameters as argv instead of handing them to a shell to re-parse.
        /// </summary>
        internal static string? ResolvePowerShellEntryPoint(string executable)
        {
            string? directory = Path.GetDirectoryName(executable);
            if (string.IsNullOrEmpty(directory))
                return null;

            string candidate = Path.Join(
                directory,
                Path.GetFileNameWithoutExtension(executable) + ".ps1"
            );
            return File.Exists(candidate) ? candidate : null;
        }

        public override int? CompareVersions(string versionA, string versionB)
        {
            if (
                SemanticVersion.TryParse(versionA, out SemanticVersion parsedA)
                && SemanticVersion.TryParse(versionB, out SemanticVersion parsedB)
            )
                return parsedA.CompareTo(parsedB);

            return base.CompareVersions(versionA, versionB);
        }

        protected override void _loadManagerVersion(out string version)
        {
            using Process process = new()
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
            CoreTools.StartAndCloseStandardInput(process);
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
