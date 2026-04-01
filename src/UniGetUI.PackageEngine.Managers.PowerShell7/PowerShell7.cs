using System.Diagnostics;
using System.Formats.Asn1;
using System.Text;
using System.Text.RegularExpressions;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Enums;
using UniGetUI.PackageEngine.Classes.Manager;
using UniGetUI.PackageEngine.Classes.Manager.ManagerHelpers;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.ManagerClasses.Classes;
using UniGetUI.PackageEngine.ManagerClasses.Manager;
using UniGetUI.PackageEngine.Managers.Chocolatey;
using UniGetUI.PackageEngine.Managers.PowerShellManager;
using UniGetUI.PackageEngine.PackageClasses;

namespace UniGetUI.PackageEngine.Managers.PowerShell7Manager
{
    public class PowerShell7 : BaseNuGet
    {
        public PowerShell7()
        {
            Capabilities = new ManagerCapabilities
            {
                CanRunAsAdmin = true,
                SupportsCustomVersions = true,
                SupportsCustomScopes = true,
                SupportsCustomSources = true,
                SupportsPreRelease = true,
                CanDownloadInstaller = true,
                CanListDependencies = true,
                SupportsCustomPackageIcons = true,
                CanUninstallPreviousVersionsAfterUpdate = true,
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
                Name = "PowerShell7",
                DisplayName = "PowerShell 7.x",
                Description = CoreTools.Translate(
                    "PowerShell's package manager. Find libraries and scripts to expand PowerShell capabilities<br>Contains: <b>Modules, Scripts, Cmdlets</b>"
                ),
                IconId = IconType.PowerShell,
                ColorIconId = "powershell_color",
                ExecutableFriendlyName = OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh",
                InstallVerb = "Install-PSResource",
                UninstallVerb = "Uninstall-PSResource",
                UpdateVerb = "Update-PSResource",
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

            DetailsHelper = new PowerShell7DetailsHelper(this);
            SourcesHelper = new PowerShell7SourceHelper(this);
            OperationHelper = new PowerShell7PkgOperationHelper(this);
        }

        protected override IReadOnlyList<Package> _getInstalledPackages_UnSafe()
        {
            List<Package> Packages = [];

            using Process p = new()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Status.ExecutablePath,
                    Arguments =
                        Status.ExecutableCallArgs
                        + " \"Write-Output '##SCOPE:AllUsers##';"
                        + " Get-InstalledPSResource -Scope AllUsers | Format-Table -Property Name,Version,Repository;"
                        + " Write-Output '##SCOPE:CurrentUser##';"
                        + " Get-InstalledPSResource -Scope CurrentUser | Format-Table -Property Name,Version,Repository\"",
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
            bool DashesPassed = false;
            string currentScope = "AllUsers";
            while ((line = p.StandardOutput.ReadLine()) is not null)
            {
                logger.AddToStdOut(line);
                if (line.StartsWith("##SCOPE:"))
                {
                    currentScope = line.Trim('#').Split(':')[1];
                    DashesPassed = false;
                    continue;
                }

                if (!DashesPassed)
                {
                    if (line.Contains("-----"))
                        DashesPassed = true;
                }
                else
                {
                    string[] elements = Regex.Replace(line, " {2,}", " ").Split(' ');
                    if (elements.Length < 3)
                        continue;

                    for (int i = 0; i < elements.Length; i++)
                        elements[i] = elements[i].Trim();

                    Packages.Add(
                        new Package(
                            CoreTools.FormatAsName(elements[0]),
                            elements[0],
                            elements[1],
                            SourcesHelper.Factory.GetSourceOrDefault(elements[2]),
                            this,
                            new(currentScope == "CurrentUser" ? PackageScope.User : PackageScope.Machine)
                        )
                    );
                }
            }

            logger.AddToStdErr(p.StandardError.ReadToEnd());
            p.WaitForExit();
            logger.Close(p.ExitCode);

            return Packages;
        }

        protected override bool UseSubstringSearch => true;

        public override IReadOnlyList<string> FindCandidateExecutableFiles() =>
            CoreTools.WhichMultiple(OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh");

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
                    Arguments = "-NoProfile -Version",
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
    }
}
