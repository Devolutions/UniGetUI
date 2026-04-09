using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Enums;
using UniGetUI.PackageEngine.Classes.Manager;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
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

        // In-memory cache of all PyPI package names, shared across searches
        private static string[]? _cachedNames;
        private static DateTime _cacheTimestamp = DateTime.MinValue;
        private static readonly object _cacheLock = new();
        private const int CacheMaxAgeHours = 24;
        private const int MaxSearchResults = 20;

        // Shared HTTP client and bounded concurrency for version fetches
        private static readonly HttpClient _httpClient = CreateSharedHttpClient();
        private static readonly SemaphoreSlim _versionFetchSemaphore = new(6, 6);

        private static HttpClient CreateSharedHttpClient()
        {
            var client = new HttpClient(CoreTools.GenericHttpClientParameters);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(CoreData.UserAgentString);
            return client;
        }

        protected override IReadOnlyList<Package> FindPackages_UnSafe(string query)
        {
            INativeTaskLogger logger = TaskLogger.CreateNew(LoggableTaskType.FindPackages);
            try
            {
                string[] allNames = GetOrRefreshIndex(logger);

                string[] matches = SelectSearchMatches(query, allNames);

                logger.Log($"Matched {matches.Length} packages for query '{query}'");

                // Fetch latest version for each match in parallel, bounded to 6 concurrent requests
                var versionTasks = matches
                    .Select(FetchLatestVersionAsync)
                    .ToArray();
                Task.WhenAll(versionTasks).GetAwaiter().GetResult();

                List<Package> packages = [];
                for (int i = 0; i < matches.Length; i++)
                {
                    string version = versionTasks[i].Result ?? "latest";
                    packages.Add(new Package(
                        CoreTools.FormatAsName(matches[i]),
                        matches[i],
                        version,
                        DefaultSource,
                        this,
                        new(PackageScope.Global)
                    ));
                }

                logger.Close(0);
                return packages;
            }
            catch (Exception e)
            {
                logger.Error(e);
                logger.Close(1);
                throw;
            }
        }

        private static string[] GetOrRefreshIndex(INativeTaskLogger logger)
        {
            lock (_cacheLock)
            {
                if (_cachedNames is not null && (DateTime.Now - _cacheTimestamp).TotalHours < CacheMaxAgeHours)
                    return _cachedNames;
            }

            string cacheFile = Path.Join(CoreData.UniGetUICacheDirectory_Data, "pip_simple_index.cache");

            // Use file cache if fresh enough
            if (File.Exists(cacheFile) && (DateTime.Now - File.GetLastWriteTime(cacheFile)).TotalHours < CacheMaxAgeHours)
            {
                logger.Log($"Loading PyPI index from file cache ({File.GetLastWriteTime(cacheFile):g})");
                string[] cached = File.ReadAllLines(cacheFile);
                if (cached.Length > 0)
                {
                    lock (_cacheLock) { _cachedNames = cached; _cacheTimestamp = File.GetLastWriteTime(cacheFile); }
                    return cached;
                }
                logger.Error("PyPI index file cache was empty, re-downloading...");
            }

            // Download fresh index
            logger.Log("Downloading PyPI simple index (one-time ~38 MB download, cached for 24 h)...");
            using HttpClient client = new(CoreTools.GenericHttpClientParameters);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(CoreData.UserAgentString);
            client.DefaultRequestHeaders.Add("Accept", "application/vnd.pypi.simple.v1+json");

            HttpResponseMessage response = client.GetAsync("https://pypi.org/simple/").GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"PyPI simple index returned {(int)response.StatusCode} {response.ReasonPhrase}");

            string json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            string[] names = ParseSimpleIndexProjectNames(json);

            logger.Log($"Downloaded {names.Length} package names from PyPI");

            // Update memory cache before attempting file write so searches work even if file write fails
            lock (_cacheLock) { _cachedNames = names; _cacheTimestamp = DateTime.Now; }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
                File.WriteAllLines(cacheFile, names);
            }
            catch (Exception e)
            {
                logger.Error($"Could not write PyPI index file cache to {cacheFile}: {e.Message}");
            }

            return names;
        }

        internal static string[] ParseSimpleIndexProjectNames(string json)
        {
            var projects = (JsonNode.Parse(json) as JsonObject)?["projects"] as JsonArray;
            string[] names = projects?
                .Select(p => p?["name"]?.GetValue<string>())
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n!)
                .ToArray() ?? [];

            if (names.Length == 0)
                throw new InvalidDataException("PyPI simple index returned 0 packages — response may be malformed");

            return names;
        }

        internal static string[] SelectSearchMatches(string query, IEnumerable<string> allNames)
        {
            string queryLower = query.ToLowerInvariant();
            return allNames
                .Where(n => n.Contains(queryLower, StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n.StartsWith(queryLower, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(n => n.Length)
                .Take(MaxSearchResults)
                .ToArray();
        }

        private static async Task<string?> FetchLatestVersionAsync(string packageName)
        {
            await _versionFetchSemaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                string json = await _httpClient
                    .GetStringAsync($"https://pypi.org/pypi/{Uri.EscapeDataString(packageName)}/json")
                    .ConfigureAwait(false);
                return (JsonNode.Parse(json) as JsonObject)?["info"]?["version"]?.GetValue<string>();
            }
            catch
            {
                return null;
            }
            finally
            {
                _versionFetchSemaphore.Release();
            }
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
            List<string> outputLines = [];
            while ((line = p.StandardOutput.ReadLine()) is not null)
            {
                logger.AddToStdOut(line);
                outputLines.Add(line);
            }

            logger.AddToStdErr(p.StandardError.ReadToEnd());
            p.WaitForExit();
            logger.Close(p.ExitCode);

            return ParseAvailableUpdates(outputLines, DefaultSource, this);
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
            List<string> outputLines = [];
            while ((line = p.StandardOutput.ReadLine()) is not null)
            {
                logger.AddToStdOut(line);
                outputLines.Add(line);
            }

            logger.AddToStdErr(p.StandardError.ReadToEnd());
            p.WaitForExit();
            logger.Close(p.ExitCode);

            return ParseInstalledPackages(outputLines, DefaultSource, this);
        }

        internal static IReadOnlyList<Package> ParseAvailableUpdates(
            IEnumerable<string> outputLines,
            IManagerSource source,
            Pip manager
        )
        {
            return ParsePackages(outputLines, source, manager, expectAvailableVersion: true);
        }

        internal static IReadOnlyList<Package> ParseInstalledPackages(
            IEnumerable<string> outputLines,
            IManagerSource source,
            Pip manager
        )
        {
            return ParsePackages(outputLines, source, manager, expectAvailableVersion: false);
        }

        private static IReadOnlyList<Package> ParsePackages(
            IEnumerable<string> outputLines,
            IManagerSource source,
            Pip manager,
            bool expectAvailableVersion
        )
        {
            bool dashesPassed = false;
            List<Package> packages = [];
            int requiredElements = expectAvailableVersion ? 3 : 2;

            foreach (string line in outputLines)
            {
                if (!dashesPassed)
                {
                    if (line.Contains("----"))
                    {
                        dashesPassed = true;
                    }
                    continue;
                }

                string[] elements = Regex
                    .Replace(line.Trim(), " {2,}", " ")
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (elements.Length < requiredElements)
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
                    expectAvailableVersion
                        ? new Package(
                            CoreTools.FormatAsName(elements[0]),
                            elements[0],
                            elements[1],
                            elements[2],
                            source,
                            manager,
                            new(PackageScope.Global)
                        )
                        : new Package(
                            CoreTools.FormatAsName(elements[0]),
                            elements[0],
                            elements[1],
                            source,
                            manager,
                            new(PackageScope.Global)
                        )
                );
            }

            return packages;
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
            // On non-Windows, prefer pip3/pip as standalone executables (avoids "No module named pip"
            // errors on systems where pip is installed as a command but not as a Python module).
            // Fall back to python/python3 + "-m pip" if no standalone pip is found.
            if (!OperatingSystem.IsWindows())
            {
                var pipPaths = CoreTools.WhichMultiple("pip3").Concat(CoreTools.WhichMultiple("pip")).ToList();
                if (pipPaths.Count > 0)
                {
                    found = true;
                    path = pipPaths[0];
                    callArguments = "";
                    return;
                }
            }

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
            process.WaitForExit();

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

            // Pre-warm the package name index in the background so the first search doesn't
            // need to wait for the ~38 MB download inside the search timeout window.
            Task.Run(() =>
            {
                var logger = TaskLogger.CreateNew(LoggableTaskType.FindPackages);
                try
                {
                    GetOrRefreshIndex(logger);
                    logger.Close(0);
                }
                catch (Exception e)
                {
                    logger.Error(e);
                    logger.Close(1);
                    Logger.Warn($"Pip: background index pre-warm failed: {e.Message}");
                }
            });
        }
    }
}
