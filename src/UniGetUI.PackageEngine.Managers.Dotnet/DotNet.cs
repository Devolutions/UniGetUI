using System.Diagnostics;
using System.Text.RegularExpressions;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Enums;
using UniGetUI.PackageEngine.Classes.Manager;
using UniGetUI.PackageEngine.Classes.Manager.ManagerHelpers;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.ManagerClasses.Classes;
using UniGetUI.PackageEngine.ManagerClasses.Manager;
using UniGetUI.PackageEngine.Managers.Chocolatey;
using UniGetUI.PackageEngine.Managers.PowerShellManager;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.Structs;
using Architecture = UniGetUI.PackageEngine.Enums.Architecture;

namespace UniGetUI.PackageEngine.Managers.DotNetManager
{
    public class DotNet : BaseNuGet
    {
        protected override IEnumerable<ManagerMaintenanceAction> GetExtraMaintenanceActions()
        {
            yield return new ManagerMaintenanceAction
            {
                Id = "clear-cache",
                Title = CoreTools.Translate("Clear cache"),
                Description = CoreTools.Translate("Delete the NuGet HTTP cache used to look up .NET tools."),
                RunAsync = () => RunMaintenanceCommandAsync(Status.ExecutablePath, "nuget locals http-cache --clear"),
            };
        }

        public static string[] FALSE_PACKAGE_IDS = [""];
        public static string[] FALSE_PACKAGE_VERSIONS = [""];

        public DotNet()
        {
            Capabilities = new ManagerCapabilities
            {
                InstalledPackagesKeepTheirSource = false,
                CanRunAsAdmin = true,
                CanDownloadInstaller = true,
                SupportsReleaseNotes = true,
                SupportsCustomScopes = true,
                SupportsCustomArchitectures = true,
                SupportedCustomArchitectures =
                [
                    Architecture.x86,
                    Architecture.x64,
                    Architecture.arm64,
                    Architecture.arm32,
                ],
                SupportsPreRelease = true,
                SupportsCustomSources = true,
                Sources = new SourceCapabilities
                {
                    KnowsPackageCount = false,
                    KnowsUpdateDate = false,
                    CanBeEdited = false,
                },
                CanListDependencies = true,
                SupportsCustomLocations = true,
                SupportsCustomPackageIcons = true,
                SupportsCustomVersions = true,
                SupportsProxy = ProxySupport.Partially,
                SupportsProxyAuth = true,
                KnowsPackageReleaseDate = PackageReleaseDateSupport.Yes,
            };

            Properties = new ManagerProperties
            {
                Id = "dotnet-tool",
                Name = ".NET Tool",
                SetupGuide = "PACKAGE_MANAGERS.md#net-tool",
                InstallCommands = new(Windows: "winget install --exact --id Microsoft.DotNet.SDK.10", MacOS: "brew install --cask dotnet-sdk"),
                Description = CoreTools.Translate(
                    "A repository full of tools and executables designed with Microsoft's .NET ecosystem in mind.<br>Contains: <b>.NET related tools and scripts</b>"
                ),
                IconId = IconType.DotNet,
                ColorIconId = "dotnet_color",
                ExecutableFriendlyName = "dotnet tool",
                InstallVerb = "install",
                UninstallVerb = "uninstall",
                UpdateVerb = "update",
                DefaultSource = new ManagerSource(
                    this,
                    "nuget.org",
                    new Uri("https://api.nuget.org/v3/index.json")
                ),
                KnownSources =
                [
                    new ManagerSource(
                        this,
                        "nuget.org",
                        new Uri("https://api.nuget.org/v3/index.json")
                    ),
                ],
            };

            SourcesHelper = new DotNetSourceHelper(this);
            DetailsHelper = new DotNetDetailsHelper(this);
            OperationHelper = new DotNetPkgOperationHelper(this);
        }

        protected override string? SearchPackageType => "DotnetTool";

        protected override IReadOnlyList<IManagerSource> GetUpdateSources(
            IManagerSource installedSource
        ) => SourcesHelper.GetSources();

        protected override bool CanResolveFromSource(string packageId, IManagerSource source) =>
            SourcesHelper is not DotNetSourceHelper helper
            || helper.GetConfiguration().Allows(packageId, source.Url);

        internal IManagerSource GetInstalledPackagesSource()
        {
            var sources = SourcesHelper.GetSources();
            if (sources.Count is 0 || sources.Any(source => source.Url == DefaultSource.Url))
                return DefaultSource;

            return sources[0];
        }

        protected override IReadOnlyList<Package> _getInstalledPackages_UnSafe()
        {
            List<Package> Packages = [];
            IManagerSource installedSource = GetInstalledPackagesSource();
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
                            + " list"
                            + (options.Scope == PackageScope.Global ? " --global" : ""),
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
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

                List<string> outputLines = [];
                string? line;
                while ((line = p.StandardOutput.ReadLine()) is not null)
                {
                    logger.AddToStdOut(line);
                    outputLines.Add(line);
                }
                logger.AddToStdErr(p.StandardError.ReadToEnd());
                p.WaitForExit();
                logger.Close(p.ExitCode);

                Packages.AddRange(ParseInstalledPackages(outputLines, installedSource, this, options));
            }
            return Packages;
        }

        public override IReadOnlyList<string> FindCandidateExecutableFiles() =>
            CoreTools.WhichMultiple(OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");

        protected override void _loadManagerExecutableFile(
            out bool found,
            out string path,
            out string callArguments
        )
        {
            var (_found, _path) = GetExecutableFile();
            found = _found;
            path = _path;
            callArguments = "tool ";

            // Ensure .NET SDK is installed
            if (!found)
                return;
            var process = new Process()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = path,
                    Arguments = callArguments + "-h",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                },
            };
            process.Start();
            process.WaitForExit();
            found = process.ExitCode is 0;
        }

        protected override void _loadManagerVersion(out string version)
        {
            var process = new Process()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Status.ExecutablePath,
                    Arguments = "--version",
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

        internal static IReadOnlyList<Package> ParseInstalledPackages(
            IEnumerable<string> outputLines,
            IManagerSource source,
            IPackageManager manager,
            OverridenInstallationOptions options
        )
        {
            List<Package> packages = [];
            bool dashesPassed = false;

            foreach (string rawLine in outputLines)
            {
                if (!dashesPassed)
                {
                    if (rawLine.Contains("----"))
                    {
                        dashesPassed = true;
                    }

                    continue;
                }

                string[] elements = Regex.Replace(rawLine, " {2,}", " ").Split(' ');
                if (elements.Length < 2)
                {
                    continue;
                }

                for (int i = 0; i < elements.Length; i++)
                {
                    elements[i] = elements[i].Trim();
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
                        source,
                        manager,
                        options
                    )
                );
            }

            return packages;
        }
    }
}
