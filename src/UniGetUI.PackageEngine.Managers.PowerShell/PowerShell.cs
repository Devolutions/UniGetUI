using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using UniGetUI.Core.Data;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Enums;
using UniGetUI.PackageEngine.Classes.Manager;
using UniGetUI.PackageEngine.Classes.Manager.ManagerHelpers;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.ManagerClasses.Classes;
using UniGetUI.PackageEngine.ManagerClasses.Manager;
using UniGetUI.PackageEngine.Managers.Chocolatey;
using UniGetUI.PackageEngine.PackageClasses;

namespace UniGetUI.PackageEngine.Managers.PowerShellManager
{
    public class PowerShell : BaseNuGet
    {
        public PowerShell()
        {
            Capabilities = new ManagerCapabilities
            {
                CanRunAsAdmin = true,
                CanSkipIntegrityChecks = true,
                SupportsCustomVersions = true,
                CanDownloadInstaller = true,
                SupportsCustomScopes = true,
                CanListDependencies = true,
                SupportsCustomSources = true,
                SupportsPreRelease = true,
                SupportsCustomPackageIcons = true,
                Sources = new SourceCapabilities
                {
                    KnowsPackageCount = false,
                    KnowsUpdateDate = false,
                },
                SupportsProxy = ProxySupport.Partially,
                SupportsProxyAuth = true,
            };

            Properties = new ManagerProperties
            {
                Name = "PowerShell",
                DisplayName = "PowerShell 5.x",
                Description = CoreTools.Translate(
                    "PowerShell's package manager. Find libraries and scripts to expand PowerShell capabilities<br>Contains: <b>Modules, Scripts, Cmdlets</b>"
                ),
                IconId = IconType.PowerShell,
                ColorIconId = "powershell_color",
                ExecutableFriendlyName = "powershell.exe",
                InstallVerb = "Install-Module",
                UninstallVerb = "Uninstall-Module",
                UpdateVerb = "Update-Module",
                KnownSources =
                [
                    new ManagerSource(
                        this,
                        "PSGallery",
                        new Uri("https://www.powershellgallery.com/api/v2")
                    ),
                    new ManagerSource(
                        this,
                        "PoshTestGallery",
                        new Uri("https://www.poshtestgallery.com/api/v2")
                    ),
                ],
                DefaultSource = new ManagerSource(
                    this,
                    "PSGallery",
                    new Uri("https://www.powershellgallery.com/api/v2")
                ),
            };

            DetailsHelper = new PowerShellDetailsHelper(this);
            SourcesHelper = new PowerShellSourceHelper(this);
            OperationHelper = new PowerShellPkgOperationHelper(this);
        }

        protected override IReadOnlyList<Package> _getInstalledPackages_UnSafe()
        {
            using Process p = new()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Status.ExecutablePath,
                    Arguments = Status.ExecutableCallArgs + " Get-InstalledModule",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                },
            };

            IProcessTaskLogger logger = TaskLogger.CreateNew(
                LoggableTaskType.ListInstalledPackages,
                p
            );

            p.Start();
            string? line;
            List<string> outputLines = [];
            while ((line = p.StandardOutput.ReadLine()) is not null)
            {
                logger.AddToStdOut(line);
                outputLines.Add(line);
            }

            logger.AddToStdErr(p.StandardError.ReadToEnd());
            p.WaitForExit();
            logger.Close(p.ExitCode);

            return ParseInstalledPackages(outputLines, this);
        }

        public override List<string> FindCandidateExecutableFiles()
        {
            var candidates = CoreTools.WhichMultiple("powershell.exe");
            if (candidates.Count is 0)
                candidates.Add(CoreData.PowerShell5);
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
            callArguments = " -NoProfile -Command";
        }

        protected override void _loadManagerVersion(out string version)
        {
            Process process = new()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Status.ExecutablePath,
                    Arguments = Status.ExecutableCallArgs + " \"echo $PSVersionTable\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                },
            };
            process.Start();
            version = process.StandardOutput.ReadToEnd().Trim();
        }

        internal static IReadOnlyList<Package> ParseInstalledPackages(
            IEnumerable<string> outputLines,
            PowerShell manager
        )
        {
            List<Package> packages = [];
            bool dashesPassed = false;

            foreach (string rawLine in outputLines)
            {
                if (!dashesPassed)
                {
                    if (rawLine.Contains("-----"))
                    {
                        dashesPassed = true;
                    }

                    continue;
                }

                string[] elements = Regex.Replace(rawLine, " {2,}", " ").Split(' ');
                if (elements.Length < 3)
                {
                    continue;
                }

                for (int i = 0; i < elements.Length; i++)
                {
                    elements[i] = elements[i].Trim();
                }

                packages.Add(
                    new Package(
                        CoreTools.FormatAsName(elements[1]),
                        elements[1],
                        elements[0],
                        manager.SourcesHelper.Factory.GetSourceOrDefault(elements[2]),
                        manager
                    )
                );
            }

            return packages;
        }
    }
}
