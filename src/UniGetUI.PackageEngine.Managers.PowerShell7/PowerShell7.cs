using System.Diagnostics;
using System.Formats.Asn1;
using System.Text;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
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
        public override bool CommandLineIsShellInterpreted => true;

        public PowerShell7()
        {
            Capabilities = new ManagerCapabilities
            {
                CanRunAsAdmin = true,
                SupportsCustomVersions = true,
                SupportsCustomScopes = true,
                // Uninstall-PSResource is invoked without a scope, so the selector is a no-op there
                SupportsCustomScopesOnUninstall = false,
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
                KnowsPackageReleaseDate = PackageReleaseDateSupport.Yes,
            };

            Properties = new ManagerProperties
            {
                Id = "pwsh",
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
            using Process p = new()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Status.ExecutablePath,
                    Arguments =
                        Status.ExecutableCallArgs
                        + " \"Write-Output '##SCOPE:AllUsers##';"
                        + " Get-InstalledPSResource -Scope AllUsers | ForEach-Object { $_.Name + [char]9 + $_.Version + [char]9 + $_.Repository };"
                        + " Write-Output '##SCOPE:CurrentUser##';"
                        + " Get-InstalledPSResource -Scope CurrentUser | ForEach-Object { $_.Name + [char]9 + $_.Version + [char]9 + $_.Repository }\"",
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

        internal static IReadOnlyList<Package> ParseInstalledPackages(
            IEnumerable<string> outputLines,
            PowerShell7 manager
        )
        {
            List<Package> packages = [];
            string currentScope = "AllUsers";

            foreach (string line in outputLines)
            {
                if (line.StartsWith("##SCOPE:"))
                {
                    currentScope = line.Trim('#').Split(':')[1];
                    continue;
                }

                string[] elements = line.Split('\t');
                if (elements.Length < 3)
                    continue;

                for (int i = 0; i < elements.Length; i++)
                    elements[i] = elements[i].Trim();

                if (elements[0].Length == 0)
                    continue;

                packages.Add(
                    new Package(
                        CoreTools.FormatAsName(elements[0]),
                        elements[0],
                        elements[1],
                        manager.SourcesHelper.Factory.GetSourceOrDefault(elements[2]),
                        manager,
                        new(currentScope == "CurrentUser" ? PackageScope.User : PackageScope.Machine)
                    )
                );
            }

            return packages;
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

        protected override IReadOnlyList<string> _getOperationCallArgs(
            string executablePath,
            string callArguments
        )
        {
            // Degrade to the concatenated -Command form when the launcher cannot run: operations
            // keep working exactly as they did before, and the parameter validation in
            // BasePkgOperationHelper still rejects anything a shell could reinterpret.
            string launcher = CoreData.PowerShellOperationLauncher;
            if (!CoreTools.PowerShellLauncherWorks(executablePath, launcher))
            {
                Logger.Warn(
                    $"Not using the PowerShell operation launcher at {launcher}; falling back to -Command"
                );
                return [];
            }

            return ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", launcher, "plain"];
        }

        protected override void _loadManagerVersion(out string version)
        {
            using Process process = new()
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
