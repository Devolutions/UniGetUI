using System.Diagnostics;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Enums;
using UniGetUI.PackageEngine.Classes.Manager;
using UniGetUI.PackageEngine.Classes.Manager.ManagerHelpers;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.ManagerClasses.Classes;
using UniGetUI.PackageEngine.ManagerClasses.Manager;
using UniGetUI.PackageEngine.Managers.Choco;
using UniGetUI.PackageEngine.Managers.PowerShellManager;
using UniGetUI.PackageEngine.PackageClasses;
using Architecture = UniGetUI.PackageEngine.Enums.Architecture;

namespace UniGetUI.PackageEngine.Managers.ChocolateyManager
{
    public class Chocolatey : BaseNuGet
    {
        public static readonly string[] FALSE_PACKAGE_IDS =
        [
            "Directory",
            "Output is Id",
            "",
            "Did",
            "Features?",
            "Validation",
            "-",
            "being",
            "It",
            "Error",
            "L'accs",
            "Maximum",
            "This",
            "Output is package name ",
            "operable",
            "Invalid",
        ];
        public static readonly string[] FALSE_PACKAGE_VERSIONS =
        [
            "",
            "Version",
            "of",
            "Did",
            "Features?",
            "Validation",
            "-",
            "being",
            "It",
            "Error",
            "L'accs",
            "Maximum",
            "This",
            "packages",
            "current version",
            "installed version",
            "is",
            "program",
            "validations",
            "argument",
            "no",
        ];
        private const string DefaultSystemChocoPath = @"C:\ProgramData\chocolatey\bin\choco.exe";
        private static readonly string[] LegacyBundledChocolateyPaths =
        [
            Path.Join(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs\\WingetUI\\choco-cli"
            ),
            Path.Join(CoreData.UniGetUIDataDirectory, "Chocolatey"),
        ];

        public Chocolatey()
        {
            Capabilities = new ManagerCapabilities
            {
                CanRunAsAdmin = true,
                CanDownloadInstaller = true,
                CanSkipIntegrityChecks = true,
                CanRunInteractively = true,
                SupportsCustomVersions = true,
                CanListDependencies = true,
                SupportsCustomArchitectures = true,
                SupportedCustomArchitectures = [Architecture.x86],
                SupportsPreRelease = true,
                SupportsCustomSources = true,
                SupportsCustomPackageIcons = true,
                Sources = new SourceCapabilities
                {
                    KnowsPackageCount = false,
                    KnowsUpdateDate = false,
                },
                SupportsProxy = ProxySupport.Yes,
                SupportsProxyAuth = true,
            };

            Properties = new ManagerProperties
            {
                Id = "chocolatey",
                Name = "Chocolatey",
                Description = CoreTools.Translate(
                    "The classical package manager for windows. You'll find everything there. <br>Contains: <b>General Software</b>"
                ),
                IconId = IconType.Chocolatey,
                ColorIconId = "choco_color",
                ExecutableFriendlyName = "choco.exe",
                InstallVerb = "install",
                UninstallVerb = "uninstall",
                UpdateVerb = "upgrade",
                KnownSources =
                [
                    new ManagerSource(
                        this,
                        "community",
                        new Uri("https://community.chocolatey.org/api/v2/")
                    ),
                ],
                DefaultSource = new ManagerSource(
                    this,
                    "community",
                    new Uri("https://community.chocolatey.org/api/v2/")
                ),
            };

            SourcesHelper = new ChocolateySourceHelper(this);
            DetailsHelper = new ChocolateyDetailsHelper(this);
            OperationHelper = new ChocolateyPkgOperationHelper(this);
        }

        public static string GetProxyArgument()
        {
            if (!Settings.Get(Settings.K.EnableProxy))
                return "";
            var proxyUri = Settings.GetProxyUrl();
            if (proxyUri is null)
                return "";

            if (Settings.Get(Settings.K.EnableProxyAuth) is false)
                return $"--proxy {proxyUri.ToString()}";

            var creds = Settings.GetProxyCredentials();
            if (creds is null)
                return $"--proxy {proxyUri.ToString()}";

            return $"--proxy={proxyUri.ToString()} --proxy-user={Uri.EscapeDataString(creds.UserName)}"
                + $" --proxy-password={Uri.EscapeDataString(creds.Password)}";
        }

        public static bool HasLegacyBundledInstallation()
        {
            foreach (string path in LegacyBundledChocolateyPaths)
            {
                if (
                    File.Exists(Path.Join(path, "choco.exe"))
                    || File.Exists(Path.Join(path, "bin", "choco.exe"))
                )
                {
                    return true;
                }
            }

            return false;
        }

        internal IReadOnlyList<Package> ParseAvailableUpdates(IEnumerable<string> lines)
        {
            List<Package> packages = [];
            foreach (string line in lines)
            {
                if (line.StartsWith("Chocolatey"))
                {
                    continue;
                }

                string[] elements = line.Split('|');
                for (int i = 0; i < elements.Length; i++)
                {
                    elements[i] = elements[i].Trim();
                }

                if (elements.Length <= 2)
                {
                    continue;
                }

                if (
                    FALSE_PACKAGE_IDS.Contains(elements[0])
                    || FALSE_PACKAGE_VERSIONS.Contains(elements[1])
                    || elements[1] == elements[2]
                )
                {
                    continue;
                }

                packages.Add(
                    new Package(
                        CoreTools.FormatAsName(elements[0]),
                        elements[0],
                        elements[1],
                        elements[2],
                        DefaultSource,
                        this
                    )
                );
            }

            return packages;
        }

        internal IReadOnlyList<Package> ParseInstalledPackages(IEnumerable<string> lines)
        {
            List<Package> packages = [];
            foreach (string line in lines)
            {
                if (line.StartsWith("Chocolatey"))
                {
                    continue;
                }

                string[] elements = line.Split(' ');
                for (int i = 0; i < elements.Length; i++)
                {
                    elements[i] = elements[i].Trim();
                }

                if (elements.Length <= 1)
                {
                    continue;
                }

                if (
                    FALSE_PACKAGE_IDS.Contains(elements[0])
                    || FALSE_PACKAGE_VERSIONS.Contains(elements[1])
                )
                {
                    continue;
                }

                packages.Add(
                    new Package(
                        CoreTools.FormatAsName(elements[0]),
                        elements[0],
                        elements[1],
                        DefaultSource,
                        this
                    )
                );
            }

            return packages;
        }

        protected override IReadOnlyList<Package> GetAvailableUpdates_UnSafe()
        {
            using Process p = new()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Status.ExecutablePath,
                    Arguments = Status.ExecutableCallArgs + " outdated " + GetProxyArgument(),
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                },
            };

            IProcessTaskLogger logger = TaskLogger.CreateNew(LoggableTaskType.ListUpdates, p);
            p.Start();

            string? line;
            List<string> lines = [];
            while ((line = p.StandardOutput.ReadLine()) is not null)
            {
                logger.AddToStdOut(line);
                lines.Add(line);
            }

            logger.AddToStdErr(p.StandardError.ReadToEnd());
            p.WaitForExit();
            logger.Close(p.ExitCode);

            return ParseAvailableUpdates(lines);
        }

        protected override IReadOnlyList<Package> _getInstalledPackages_UnSafe()
        {
            using Process p = new()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Status.ExecutablePath,
                    Arguments = Status.ExecutableCallArgs + " list " + GetProxyArgument(),
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                },
            };

            IProcessTaskLogger logger = TaskLogger.CreateNew(
                LoggableTaskType.ListInstalledPackages,
                p
            );
            p.Start();

            string? line;
            List<string> lines = [];
            while ((line = p.StandardOutput.ReadLine()) is not null)
            {
                logger.AddToStdOut(line);
                lines.Add(line);
            }

            logger.AddToStdErr(p.StandardError.ReadToEnd());
            p.WaitForExit();
            logger.Close(p.ExitCode);

            return ParseInstalledPackages(lines);
        }

        public override IReadOnlyList<string> FindCandidateExecutableFiles()
        {
            List<string> candidates = [];
            if (File.Exists(DefaultSystemChocoPath))
            {
                candidates.Add(DefaultSystemChocoPath);
            }

            foreach (string candidate in CoreTools.WhichMultiple("choco.exe"))
            {
                if (!candidates.Exists(existing => existing.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
                {
                    candidates.Add(candidate);
                }
            }

            return candidates;
        }

        protected override void _loadManagerExecutableFile(
            out bool found,
            out string path,
            out string callArguments
        )
        {
            var (_found, _path) = GetExecutableFile();
            found = _found;
            path = _path;
            callArguments = "";
        }

        protected override void _loadManagerVersion(out string version)
        {
            Process process = new()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Status.ExecutablePath,
                    Arguments = "--version " + GetProxyArgument(),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                },
            };
            process.Start();
            version = process.StandardOutput.ReadToEnd().Trim();
        }

        protected override void _performExtraLoadingSteps()
        {
            // Ensure the selected Chocolatey executable uses a matching installation root.
            var choco_dir =
                Path.GetDirectoryName(Status.ExecutablePath)?.Replace('/', '\\').Trim('\\') ?? "";
            if (choco_dir.EndsWith("bin"))
            {
                choco_dir = choco_dir[..^3].Trim('\\');
            }
            Environment.SetEnvironmentVariable(
                "chocolateyinstall",
                choco_dir,
                EnvironmentVariableTarget.Process
            );
        }
    }
}
