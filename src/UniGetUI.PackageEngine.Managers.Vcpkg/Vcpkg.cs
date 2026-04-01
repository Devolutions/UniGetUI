using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Enums;
using UniGetUI.PackageEngine.Classes.Manager;
using UniGetUI.PackageEngine.Classes.Manager.Classes;
using UniGetUI.PackageEngine.Classes.Manager.ManagerHelpers;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.ManagerClasses.Classes;
using UniGetUI.PackageEngine.ManagerClasses.Manager;
using UniGetUI.PackageEngine.PackageClasses;
using Architecture = System.Runtime.InteropServices.Architecture;

namespace UniGetUI.PackageEngine.Managers.VcpkgManager
{
    public class Vcpkg : PackageManager
    {
        public Dictionary<string, ManagerSource> TripletSourceMap;
        public static Uri URI_VCPKG_IO = new Uri("https://vcpkg.io/");

        public Vcpkg()
        {
            Dependencies =
            [
                // GIT is required for vcpkg updates to work
                new ManagerDependency(
                    "Git",
                    CoreData.PowerShell5,
                    "-ExecutionPolicy Bypass -NoLogo -NoProfile -Command \"& {winget install --id Git.Git --exact "
                        + "--source winget --accept-source-agreements --accept-package-agreements --force; if($error.count -ne 0){pause}}\"",
                    "winget install --id Git.Git --exact --source winget",
                    async () => (await CoreTools.WhichAsync("git.exe")).Item1
                ),
            ];

            Capabilities = new ManagerCapabilities
            {
                CanRunAsAdmin = true,
                SupportsCustomSources = true,
                CanListDependencies = true,
                SupportsProxy = ProxySupport.No,
                SupportsProxyAuth = false,
            };

            string DefaultTriplet = GetDefaultTriplet();

            TripletSourceMap = new Dictionary<string, ManagerSource>
            {
                { "arm-neon-android", new ManagerSource(this, "arm-neon-android", URI_VCPKG_IO) },
                { "arm64-android", new ManagerSource(this, "arm64-android", URI_VCPKG_IO) },
                { "arm64-uwp", new ManagerSource(this, "arm64-uwp", URI_VCPKG_IO) },
                { "arm64-windows", new ManagerSource(this, "arm64-windows", URI_VCPKG_IO) },
                { "x64-android", new ManagerSource(this, "x64-android", URI_VCPKG_IO) },
                { "x64-linux", new ManagerSource(this, "x64-linux", URI_VCPKG_IO) },
                { "x64-osx", new ManagerSource(this, "x64-osx", URI_VCPKG_IO) },
                { "x64-uwp", new ManagerSource(this, "x64-uwp", URI_VCPKG_IO) },
                {
                    "x64-windows-static",
                    new ManagerSource(this, "x64-windows-static", URI_VCPKG_IO)
                },
                { "x64-windows", new ManagerSource(this, "x64-windows", URI_VCPKG_IO) },
                { "x86-windows", new ManagerSource(this, "x86-windows", URI_VCPKG_IO) },
            };

            Properties = new ManagerProperties
            {
                Name = "vcpkg",
                Description = CoreTools.Translate(
                    "A popular C/C++ library manager. Full of C/C++ libraries and other C/C++-related utilities<br>Contains: <b>C/C++ libraries and related utilities</b>"
                ),
                IconId = IconType.Vcpkg,
                ColorIconId = "vcpkg_color",
                ExecutableFriendlyName = "vcpkg",
                InstallVerb = "install",
                UninstallVerb = "remove",
                UpdateVerb = "upgrade",
                DefaultSource = new ManagerSource(this, DefaultTriplet, URI_VCPKG_IO),
                KnownSources = [.. TripletSourceMap.Values],
            };

            SourcesHelper = new VcpkgSourceHelper(this);
            DetailsHelper = new VcpkgPkgDetailsHelper(this);
            OperationHelper = new VcpkgPkgOperationHelper(this);
        }

        protected override IReadOnlyList<Package> FindPackages_UnSafe(string query)
        {
            string Triplet = GetDefaultTriplet();

            using Process p = new()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Status.ExecutablePath,
                    Arguments =
                        Status.ExecutableCallArgs
                        + $" search \"{CoreTools.EnsureSafeQueryString(query)}\"",
                    // vcpkg has an --x-json flag that would list installed packages in JSON, but it doesn't work for this call (as of 2024-09-30-ab8988503c7cffabfd440b243a383c0a352a023d)
                    // TODO: Perhaps use --x-json when it is fixed
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                },
            };

            IProcessTaskLogger logger = TaskLogger.CreateNew(LoggableTaskType.FindPackages, p);
            string? line;
            List<Package> Packages = [];

            p.Start();

            Dictionary<string, string> PackageVersions = [];
            string optionString = CoreTools.Translate("option");
            string unknownVersion = CoreTools.Translate("Unknown");
            while ((line = p.StandardOutput.ReadLine()) is not null)
            {
                logger.AddToStdOut(line);

                // Sample line:
                // name(source)             version          description
                // ffmpeg[sdl2]                              Sdl2 support
                // sdl1 - net               1.2.8#6          Networking library for SDL
                // sdl2                     2.30.8           Simple DirectMedia Layer is a cross - platform development library designed...
                // (note that the suboptions, with the `name[build-option]` syntax have no version)

                //                                           to get rid of many spaces of padding
                string[] PackageData = Regex.Replace(line, @"\s+", " ").Split(' ');
                string PackageId = PackageData[0]; // the id with the suboption
                string PackageName = PackageId; // the actual name (id - suboption)
                string PackageDetailedName = PackageName; // the name with a reformatted suboption reapplied (display name)
                string PackageVersion = PackageData[1];

                if (
                    PackageName.Contains('[') /* meaning its a suboption, and thus has no version */
                )
                {
                    PackageName = PackageId.Split('[')[0]; //..PackageName.IndexOf("[", StringComparison.Ordinal)];
                    PackageDetailedName =
                        PackageName + $" ({optionString}: {PackageId.Split('[')[1][..^1]})";

                    if (PackageVersions.TryGetValue(PackageName, out string? value))
                    {
                        PackageVersion = value;
                    }
                    else
                    {
                        PackageVersion = unknownVersion;
                    }
                }
                else // If the package has a specified version (it is not a suboption)
                {
                    PackageVersions[PackageName] = PackageVersion;
                }

                if (!TripletSourceMap.TryGetValue(Triplet, out ManagerSource? source))
                {
                    source = new ManagerSource(this, Triplet, URI_VCPKG_IO);
                    TripletSourceMap.Add(Triplet, source);
                }

                Packages.Add(
                    new Package(
                        CoreTools.FormatAsName(PackageDetailedName),
                        PackageId + ":" + Triplet,
                        PackageVersion,
                        source,
                        this
                    )
                );
            }

            logger.AddToStdErr(p.StandardError.ReadToEnd());
            p.WaitForExit();
            logger.Close(p.ExitCode);
            return Packages;
        }

        protected override IReadOnlyList<Package> GetAvailableUpdates_UnSafe()
        {
            List<Package> Packages = [];

            using Process p = new()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Status.ExecutablePath,
                    Arguments = Status.ExecutableCallArgs + " update",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                },
            };
            IProcessTaskLogger logger = TaskLogger.CreateNew(LoggableTaskType.ListUpdates, p);

            p.Start();

            string? line;
            while ((line = p.StandardOutput.ReadLine()) is not null)
            {
                logger.AddToStdOut(line);

                // Sample line:
                // (spaces) package name: package triplet   current -> latest
                //         brotli:x64-mingw-dynamic         1.0.9#5 -> 1.1.0#1
                if (line.StartsWith('\t'))
                {
                    line = line.Substring(1);
                    string[] PackageData = Regex.Replace(line, @"\s+", " ").Split(' ');
                    string PackageId = PackageData[0];
                    string PackageName = PackageId.Split(':')[0],
                        PackageTriplet = PackageId.Split(':')[1],
                        PackageVersionCurrent = PackageData[1],
                        PackageVersionLatest = PackageData[3];

                    if (!TripletSourceMap.TryGetValue(PackageTriplet, out ManagerSource? value))
                    {
                        value = new ManagerSource(this, PackageTriplet, URI_VCPKG_IO);
                        TripletSourceMap[PackageTriplet] = value;
                    }

                    Packages.Add(
                        new Package(
                            CoreTools.FormatAsName(PackageName),
                            PackageId,
                            PackageVersionCurrent,
                            PackageVersionLatest,
                            value,
                            this
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
                    Arguments = Status.ExecutableCallArgs + " list",
                    // vcpkg has an --x-json flag that would list installed packages in JSON, but it's experimental
                    // TODO: Once --x-json is stable migrate to --x-json
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                },
            };

            IProcessTaskLogger logger = TaskLogger.CreateNew(
                LoggableTaskType.ListInstalledPackages,
                p
            );
            string? line;
            List<Package> Packages = [];

            p.Start();

            Dictionary<string, string> PackageVersions = [];
            while ((line = p.StandardOutput.ReadLine()) is not null)
            {
                logger.AddToStdOut(line);

                // Sample line:
                // name:triplet (source)             version     description
                // curl:x64-mingw-dynamic            8.1.2#2     A library for transferring data with URLs
                // curl[non-http]:x64-mingw-dynamic              Enables protocols beyond HTTP/HTTPS/HTTP2
                // (note that the suboptions, with the `name[build-option]` syntax have no version)

                //                                           to get rid of many spaces of padding
                string[] PackageData = Regex.Replace(line, @"\s+", " ").Split(' ');
                string PackageId = PackageData[0];
                string PackageName = PackageId.Split(':')[0],
                    PackageTriplet = PackageId.Split(':')[1],
                    PackageVersion = PackageData[1];
                if (
                    PackageId.Contains('[') /* meaning its a suboption, and thus has no version */
                )
                {
                    PackageVersion = PackageVersions[PackageName.Split("[")[0]];
                }
                else
                {
                    PackageVersions[PackageName] = PackageVersion;
                }

                if (!TripletSourceMap.TryGetValue(PackageTriplet, out ManagerSource? value))
                {
                    value = new ManagerSource(this, PackageTriplet, URI_VCPKG_IO);
                    TripletSourceMap[PackageTriplet] = value;
                }

                Packages.Add(
                    new Package(
                        CoreTools.FormatAsName(PackageName),
                        PackageId,
                        PackageVersion,
                        value,
                        this
                    )
                );
            }

            logger.AddToStdErr(p.StandardError.ReadToEnd());
            p.WaitForExit();
            logger.Close(p.ExitCode);
            return Packages;
        }

        public override IReadOnlyList<string> FindCandidateExecutableFiles()
        {
            string vcpkgExe = OperatingSystem.IsWindows() ? "vcpkg.exe" : "vcpkg";
            var candidates = CoreTools.WhichMultiple(vcpkgExe);

            var (rootFound, rootPath) = GetVcpkgRoot();
            if (rootFound)
            {
                string VcpkgLocation = Path.Join(rootPath, vcpkgExe);
                if (File.Exists(VcpkgLocation))
                    candidates.Add(VcpkgLocation);
            }

            return candidates;
        }

        protected override void _loadManagerExecutableFile(
            out bool found,
            out string path,
            out string callArguments
        )
        {
            var (exeFound, exePath) = GetExecutableFile();
            var (rootFound, _) = GetVcpkgRoot();

            if (!rootFound)
                Logger.Error(
                    "Vcpkg root was not found. Please define the %VCPKG_ROOT% environment variable or define it from UniGetUI Settings"
                );
            found = exeFound && rootFound;
            path = exePath;

            string vcpkgRoot = Settings.GetValue(Settings.K.CustomVcpkgRoot);
            callArguments = vcpkgRoot == "" ? "" : $" --vcpkg-root=\"{vcpkgRoot}\"";
        }

        protected override void _loadManagerVersion(out string version)
        {
            var (_, rootPath) = GetVcpkgRoot();

            Process process = new()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Status.ExecutablePath,
                    Arguments = Status.ExecutableCallArgs + " version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                },
            };
            process.Start();
            version = process.StandardOutput.ReadLine()?.Trim() ?? "";
            version += RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? $"\n%VCPKG_ROOT% = {rootPath}"
                : $"\n$VCPKG_ROOT = {rootPath}";
        }

        protected override void _performExtraLoadingSteps()
        {
            var (_, rootPath) = GetVcpkgRoot();
            bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            using Process p2 = new()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = isWindows ? "cmd.exe" : "sh",
                    WorkingDirectory = rootPath,
                    Arguments = isWindows ? "/C .\\bootstrap-vcpkg.bat" : "./bootstrap-vcpkg.sh",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            IProcessTaskLogger processLogger2 = TaskLogger.CreateNew(
                LoggableTaskType.RefreshIndexes,
                p2
            );
            p2.Start();
            p2.WaitForExit();
            processLogger2.Close(p2.ExitCode);
        }

        public override void RefreshPackageIndexes()
        {
            var (vcpkgRootFound, vcpkgRoot) = GetVcpkgRoot();
            var (gitFound, gitPath) = CoreTools.Which("git");

            if (!Status.Found || !gitFound || !vcpkgRootFound)
            {
                INativeTaskLogger logger = TaskLogger.CreateNew(LoggableTaskType.RefreshIndexes);
                if (Settings.Get(Settings.K.DisableUpdateVcpkgGitPorts))
                    logger.Error("User has disabled updating sources");
                if (!Status.Found)
                    logger.Error("Vcpkg was not found???");
                if (!gitFound)
                    logger.Error("Vcpkg sources won't be updated since git was not found");
                if (!vcpkgRootFound)
                    logger.Error(
                        "Cannot update vcpkg port files as requested: the VCPKG_ROOT environment variable or custom vcpkg root setting was not set"
                    );
                logger.Close(Settings.Get(Settings.K.DisableUpdateVcpkgGitPorts) ? 0 : 1);
                return;
            }

            using Process p = new()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = gitPath,
                    WorkingDirectory = vcpkgRoot,
                    Arguments = "pull --all",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };
            IProcessTaskLogger processLogger = TaskLogger.CreateNew(
                LoggableTaskType.RefreshIndexes,
                p
            );
            p.Start();
            p.WaitForExit();
            processLogger.AddToStdOut(p.StandardOutput.ReadToEnd());
            processLogger.AddToStdErr(p.StandardError.ReadToEnd());
            processLogger.Close(p.ExitCode);
        }

        public static Tuple<bool, string> GetVcpkgRoot()
        {
            string? vcpkgRoot = Settings.GetValue(Settings.K.CustomVcpkgRoot);
            if (vcpkgRoot == "")
            {
                vcpkgRoot = Environment.GetEnvironmentVariable("VCPKG_ROOT");
            }

            if (vcpkgRoot == null)
            {
                // Unfortunately, we can't use `GetVcpkgPath` or `GetExecutableFile`
                // for this as it would become a bunch of functions calling each other
                var paths = CoreTools.WhichMultiple("vcpkg");
                foreach (string path in paths)
                {
                    string? dir = Path.GetDirectoryName(path);
                    if (string.IsNullOrEmpty(dir))
                    {
                        continue;
                    }

                    // Make sure the root is a valid root not just a random directory
                    if (Path.Exists(Path.Join(dir, "triplets")))
                    {
                        vcpkgRoot = dir;
                        break;
                    }
                }
            }

            return Tuple.Create(vcpkgRoot != null, vcpkgRoot ?? "");
        }

        public static string GetDefaultTriplet()
        {
            string DefaultTriplet = Settings.GetValue(Settings.K.DefaultVcpkgTriplet);
            if (DefaultTriplet == "")
            {
                DefaultTriplet = Environment.GetEnvironmentVariable("VCPKG_DEFAULT_TRIPLET") ?? "";
            }

            if (DefaultTriplet == "")
            {
                if (RuntimeInformation.OSArchitecture is Architecture.X64)
                    DefaultTriplet = "x64-";
                else if (RuntimeInformation.OSArchitecture is Architecture.X86)
                    DefaultTriplet = "x86-";
                else if (RuntimeInformation.OSArchitecture is Architecture.Arm64)
                    DefaultTriplet = "arm64-";

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    DefaultTriplet += "windows";
                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    DefaultTriplet += "osx";
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                    DefaultTriplet += "linux";
            }

            return DefaultTriplet;
        }

        public static List<string> GetSystemTriplets()
        {
            List<string> Triplets = [];
            // Retrieve all triplets on the system (in %VCPKG_ROOT%\triplets{\community})
            var (vcpkgRootFound, vcpkgRoot) = GetVcpkgRoot();
            if (vcpkgRootFound)
            {
                string tripletLocation = Path.Join(vcpkgRoot, "triplets");
                string communityTripletLocation = Path.Join(vcpkgRoot, "triplets", "community");

                IEnumerable<string> tripletFiles = [];
                if (Path.Exists(tripletLocation))
                {
                    tripletFiles = tripletFiles.Concat(Directory.EnumerateFiles(tripletLocation));
                }
                else
                {
                    Logger.Warn(
                        $"The built-in triplet directory {tripletLocation} does not exist; triplets will not be loaded."
                    );
                }

                if (Path.Exists(communityTripletLocation))
                {
                    tripletFiles = tripletFiles.Concat(
                        Directory.EnumerateFiles(communityTripletLocation)
                    );
                }
                else
                {
                    Logger.Warn(
                        $"The community triplet directory {communityTripletLocation} does not exist; community triplets will not be loaded."
                    );
                }

                foreach (string tripletFile in tripletFiles)
                {
                    Triplets.Add(Path.GetFileNameWithoutExtension(tripletFile));
                }
            }

            return Triplets.Distinct().ToList();
        }
    }
}
