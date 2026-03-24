using System.Diagnostics;
using System.Text.RegularExpressions;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Enums;
using UniGetUI.PackageEngine.Classes.Manager;
using UniGetUI.PackageEngine.Classes.Manager.ManagerHelpers;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.ManagerClasses.Classes;
using UniGetUI.PackageEngine.ManagerClasses.Manager;
using UniGetUI.PackageEngine.PackageClasses;

namespace UniGetUI.PackageEngine.Managers.PipManager
{
    public class Pip : PackageManager
    {
        public static string[] FALSE_PACKAGE_IDS =
        [
            "",
            "WARNING:",
            "[notice]",
            "Package",
            "DEPRECATION:",
        ];
        public static string[] FALSE_PACKAGE_VERSIONS = ["", "Ignoring", "invalid"];

        public Pip()
        {
            Dependencies = [];
            /*Dependencies = [
                // parse_pip_search is required for pip package finding to work
                new ManagerDependency(
                    "parse-pip-search",
                    CoreData.PowerShell5,
                    "-ExecutionPolicy Bypass -NoLogo -NoProfile -Command \"& {python.exe "
                        + "-m pip install parse_pip_search; if($error.count -ne 0){pause}}\"",
                    "python -m pip install parse_pip_search",
                    async () =>
                    {
                        bool found = (await CoreTools.WhichAsync("parse_pip_search.exe")).Item1;
                        if (found) return true;
                        else if (Status.ExecutablePath.Contains("WindowsApps\\python.exe"))
                        {
                            Logger.Warn("parse_pip_search could was not found but the user will not be prompted to install it.");
                            Logger.Warn("NOTE: Microsoft Store python is not fully supported on UniGetUI");
                            return true;
                        }
                        else return false;
                    }
                )
            ];*/

            Capabilities = new ManagerCapabilities
            {
                CanRunAsAdmin = true,
                SupportsCustomVersions = true,
                SupportsCustomScopes = true,
                CanDownloadInstaller = true,
                SupportsPreRelease = true,
                CanListDependencies = true,
                SupportsProxy = ProxySupport.Yes,
                SupportsProxyAuth = true,
            };

            Properties = new ManagerProperties
            {
                Name = "Pip",
                Description = CoreTools.Translate(
                    "Python's library manager. Full of python libraries and other python-related utilities<br>Contains: <b>Python libraries and related utilities</b>"
                ),
                IconId = IconType.Python,
                ColorIconId = "pip_color",
                ExecutableFriendlyName = "pip",
                InstallVerb = "install",
                UninstallVerb = "uninstall",
                UpdateVerb = "install --upgrade",
                DefaultSource = new ManagerSource(this, "pip", new Uri("https://pypi.org/")),
                KnownSources = [new ManagerSource(this, "pip", new Uri("https://pypi.org/"))],
            };

            DetailsHelper = new PipPkgDetailsHelper(this);
            OperationHelper = new PipPkgOperationHelper(this);
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

            return $"--proxy {proxyUri.Scheme}://{Uri.EscapeDataString(creds.UserName)}:{Uri.EscapeDataString(creds.Password)}"
                + $"@{proxyUri.AbsoluteUri.Replace($"{proxyUri.Scheme}://", "")}";
        }

        protected override IReadOnlyList<Package> FindPackages_UnSafe(string query)
        {
            List<Package> Packages = [];

            var (found, path) = CoreTools.Which("parse_pip_search.exe");
            if (!found)
            {
                Process proc = new()
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = Status.ExecutablePath,
                        Arguments =
                            Status.ExecutableCallArgs
                            + " install parse_pip_search "
                            + GetProxyArgument(),
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                    },
                };
                IProcessTaskLogger aux_logger = TaskLogger.CreateNew(
                    LoggableTaskType.InstallManagerDependency,
                    proc
                );
                proc.Start();

                aux_logger.AddToStdOut(proc.StandardOutput.ReadToEnd());
                aux_logger.AddToStdErr(proc.StandardError.ReadToEnd());

                proc.WaitForExit();
                aux_logger.Close(proc.ExitCode);
                path = "parse_pip_search.exe";
            }

            using Process p = new()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = path,
                    Arguments = "\"" + query + "\" " + GetProxyArgument(),
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                },
            };

            p.StartInfo = CoreTools.UpdateEnvironmentVariables(p.StartInfo);
            IProcessTaskLogger logger = TaskLogger.CreateNew(LoggableTaskType.FindPackages, p);
            p.Start();

            string? line;
            bool DashesPassed = false;
            while ((line = p.StandardOutput.ReadLine()) is not null)
            {
                logger.AddToStdOut(line);
                if (!DashesPassed)
                {
                    if (line.Contains("----"))
                    {
                        DashesPassed = true;
                    }
                }
                else
                {
                    string[] elements = line.Split('|');
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

                    Packages.Add(
                        new Package(
                            CoreTools.FormatAsName(elements[0]),
                            elements[0],
                            elements[1],
                            DefaultSource,
                            this,
                            new(PackageScope.Global)
                        )
                    );
                }
            }

            logger.AddToStdErr(p.StandardError.ReadToEnd());
            p.WaitForExit();
            logger.Close(p.ExitCode);

            return Packages;
        }

        protected override IReadOnlyList<Package> GetAvailableUpdates_UnSafe()
        {
            using Process p = new()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Status.ExecutablePath,
                    Arguments =
                        Status.ExecutableCallArgs + " list --outdated " + GetProxyArgument(),
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                },
            };

            IProcessTaskLogger logger = TaskLogger.CreateNew(LoggableTaskType.ListUpdates, p);

            p.Start();

            string? line;
            bool DashesPassed = false;
            List<Package> Packages = [];
            while ((line = p.StandardOutput.ReadLine()) is not null)
            {
                logger.AddToStdOut(line);
                if (!DashesPassed)
                {
                    if (line.Contains("----"))
                    {
                        DashesPassed = true;
                    }
                }
                else
                {
                    string[] elements = Regex.Replace(line, " {2,}", " ").Split(' ');
                    if (elements.Length < 3)
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

                    Packages.Add(
                        new Package(
                            CoreTools.FormatAsName(elements[0]),
                            elements[0],
                            elements[1],
                            elements[2],
                            DefaultSource,
                            this,
                            new(PackageScope.Global)
                        )
                    );
                }
            }

            logger.AddToStdErr(p.StandardError.ReadToEnd());
            p.WaitForExit();
            logger.Close(p.ExitCode);

            return Packages;
        }

        protected override IReadOnlyList<Package> GetInstalledPackages_UnSafe()
        {
            using Process p = new()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Status.ExecutablePath,
                    Arguments = Status.ExecutableCallArgs + " list " + GetProxyArgument(),
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

            string? line;
            bool DashesPassed = false;
            List<Package> Packages = [];
            while ((line = p.StandardOutput.ReadLine()) is not null)
            {
                logger.AddToStdOut(line);
                if (!DashesPassed)
                {
                    if (line.Contains("----"))
                    {
                        DashesPassed = true;
                    }
                }
                else
                {
                    string[] elements = Regex.Replace(line, " {2,}", " ").Split(' ');
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

                    Packages.Add(
                        new Package(
                            CoreTools.FormatAsName(elements[0]),
                            elements[0],
                            elements[1],
                            DefaultSource,
                            this,
                            new(PackageScope.Global)
                        )
                    );
                }
            }

            logger.AddToStdErr(p.StandardError.ReadToEnd());
            p.WaitForExit();
            logger.Close(p.ExitCode);

            return Packages;
        }

        public override IReadOnlyList<string> FindCandidateExecutableFiles()
        {
            var FoundPaths = CoreTools.WhichMultiple("python");
            if (!OperatingSystem.IsWindows() && !FoundPaths.Any())
                FoundPaths = CoreTools.WhichMultiple("python3");
            List<string> Paths = [];

            if (FoundPaths.Any())
                foreach (var Path in FoundPaths)
                    Paths.Add(Path);

            try
            {
                List<string> DirsToSearch = [];
                string ProgramFiles = @"C:\Program Files";
                string? UserPythonInstallDir = null;
                string? AppData = Environment.GetEnvironmentVariable("APPDATA");

                if (AppData != null)
                    UserPythonInstallDir = Path.Combine(AppData, "Programs", "Python");

                if (Directory.Exists(ProgramFiles))
                    DirsToSearch.Add(ProgramFiles);
                if (Directory.Exists(UserPythonInstallDir))
                    DirsToSearch.Add(UserPythonInstallDir);

                foreach (var Dir in DirsToSearch)
                {
                    string DirName = Path.GetFileName(Dir);
                    string PythonPath = Path.Join(Dir, "python.exe");
                    if (DirName.StartsWith("Python") && File.Exists(PythonPath))
                        Paths.Add(PythonPath);
                }
            }
            catch (Exception) { }

            return Paths;
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
            callArguments = "-m pip ";
        }

        protected override void _loadManagerVersion(out string version)
        {
            Process process = new()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Status.ExecutablePath,
                    Arguments = Status.ExecutableCallArgs + "--version " + GetProxyArgument(),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                },
            };
            process.Start();
            version = process.StandardOutput.ReadToEnd().Trim();

            if (process.ExitCode is 9009)
            {
                throw new InvalidOperationException(
                    "Microsoft Store python alias is not a valid python install"
                );
            }
        }

        protected override void _performExtraLoadingSteps()
        {
            Environment.SetEnvironmentVariable(
                "PIP_REQUIRE_VIRTUALENV",
                "false",
                EnvironmentVariableTarget.Process
            );
        }
    }
}
