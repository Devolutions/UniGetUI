using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
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
    public partial class Pip : PackageManager
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
                KnowsPackageReleaseDate = PackageReleaseDateSupport.Yes,
            };

            Properties = new ManagerProperties
            {
                Id = "pip",
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

            using var request = new HttpRequestMessage(HttpMethod.Get, "https://pypi.org/simple/");
            using HttpResponseMessage response = client.Send(request);
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
                using var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    $"https://pypi.org/pypi/{Uri.EscapeDataString(packageName)}/json"
                );
                using HttpResponseMessage response = await _httpClient
                    .SendAsync(request)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
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

        private static readonly ConcurrentDictionary<
            (string Interpreter, string PackageId),
            CachedResolution
        > _latestVersionCache = new();

        private readonly record struct CachedResolution(string ETag, string? Version);

        private readonly record struct VersionResolution(string? Version, bool Failed);

        private readonly object _indexUpdateCheckLock = new();
        private bool _indexUpdateCheckProbed;
        private bool _indexUpdateCheckSupported;
        private PythonVersion _interpreterVersion;

        [GeneratedRegex(
            @"\(python (?<version>[0-9]+(?:\.[0-9]+)*)\)",
            RegexOptions.CultureInvariant
        )]
        private static partial Regex InterpreterVersionPattern();

        [GeneratedRegex(@"[-_.]+", RegexOptions.CultureInvariant)]
        private static partial Regex ProjectNameSeparatorPattern();

        protected override void _performPreInitializationSteps()
        {
            lock (_indexUpdateCheckLock)
            {
                _indexUpdateCheckProbed = false;
                _indexUpdateCheckSupported = false;
                _interpreterVersion = default;
            }
        }

        private bool TryGetIndexUpdateCheckInterpreter(out PythonVersion interpreter)
        {
            lock (_indexUpdateCheckLock)
            {
                if (!_indexUpdateCheckProbed)
                {
                    _indexUpdateCheckProbed = true;
                    _indexUpdateCheckSupported = ProbeIndexUpdateCheckSupport(
                        out _interpreterVersion
                    );
                }

                interpreter = _interpreterVersion;
                return _indexUpdateCheckSupported
                    && !Settings.Get(Settings.K.DisablePipHttpUpdateCheck);
            }
        }

        private bool ProbeIndexUpdateCheckSupport(out PythonVersion interpreter)
        {
            interpreter = default;

            if (HasCustomIndexConfigured())
            {
                Logger.Info(
                    "Pip: a custom package index is configured, available updates will keep "
                        + "being listed through the pip CLI"
                );
                return false;
            }

            string? detected = DetectInterpreterVersion();
            if (detected is null || !PythonVersion.TryParse(detected, out interpreter))
            {
                Logger.Warn(
                    "Pip: the Python version could not be determined, available updates will "
                        + "keep being listed through the pip CLI"
                );
                return false;
            }

            Logger.Info($"Pip: listing available updates against {DefaultSource.Url} over HTTP");
            return true;
        }

        private static readonly string[] IndexEnvironmentVariables =
        [
            "PIP_INDEX_URL",
            "PIP_EXTRA_INDEX_URL",
            "PIP_NO_INDEX",
            "PIP_FIND_LINKS",
        ];

        private static readonly string[] IndexConfigurationKeys =
        [
            "index-url",
            "extra-index-url",
            "no-index",
            "find-links",
        ];

        internal static bool IsIndexConfigurationLine(string line)
        {
            int separator = line.IndexOf('=');
            if (separator < 0)
                return false;

            string key = line[..separator].Trim();
            int lastDot = key.LastIndexOf('.');
            string name = lastDot >= 0 ? key[(lastDot + 1)..] : key;

            return IndexConfigurationKeys.Contains(name, StringComparer.OrdinalIgnoreCase);
        }

        private bool HasCustomIndexConfigured()
        {
            foreach (string variable in IndexEnvironmentVariables)
            {
                if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable)))
                    return true;
            }

            try
            {
                foreach (string line in RunPipCommand(" config list", LoggableTaskType.OtherTask))
                {
                    if (IsIndexConfigurationLine(line))
                        return true;
                }
            }
            catch (Exception e)
            {
                Logger.Warn($"Pip: the pip configuration could not be read ({e.Message})");
            }

            return false;
        }

        private string? DetectInterpreterVersion()
        {
            if (Status.ExecutableCallArgs.Contains("-m pip", StringComparison.Ordinal))
            {
                try
                {
                    using Process p = new()
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = Status.ExecutablePath,
                            Arguments =
                                "-c \"import sys; print('%d.%d.%d' % sys.version_info[:3])\"",
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            StandardOutputEncoding = System.Text.Encoding.UTF8,
                        },
                    };

                    p.Start();
                    RegisterListingProcess(p);
                    string output = p.StandardOutput.ReadToEnd().Trim();
                    p.WaitForExit();

                    if (p.ExitCode is 0 && output.Length > 0)
                        return output;
                }
                catch (Exception e)
                {
                    Logger.Warn($"Pip: the Python version could not be queried ({e.Message})");
                }
            }

            return ParseInterpreterVersion(Status.Version);
        }

        internal static string? ParseInterpreterVersion(string? pipVersionOutput)
        {
            if (pipVersionOutput is null)
                return null;

            Match match = InterpreterVersionPattern().Match(pipVersionOutput);
            return match.Success ? match.Groups["version"].Value : null;
        }

        private static readonly SemaphoreSlim _updateFetchSemaphore = new(6, 6);
        private static readonly TimeSpan UpdateResolutionBudget = TimeSpan.FromSeconds(45);

        private static async Task<VersionResolution> ResolveLatestVersionAsync(
            HttpClient client,
            string packageId,
            PythonVersion interpreter,
            CancellationToken token
        )
        {
            await _updateFetchSemaphore.WaitAsync(token).ConfigureAwait(false);
            try
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    $"https://pypi.org/simple/{NormalizeProjectNameForUrl(packageId)}/"
                );
                request.Headers.Accept.ParseAdd("application/vnd.pypi.simple.v1+json");

                var cacheKey = (interpreter.Original, packageId);
                bool wasCached = _latestVersionCache.TryGetValue(
                    cacheKey,
                    out CachedResolution cached
                );
                if (wasCached)
                    request.Headers.TryAddWithoutValidation("If-None-Match", cached.ETag);

                using HttpResponseMessage response = await client
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                    .ConfigureAwait(false);

                if (wasCached && response.StatusCode is HttpStatusCode.NotModified)
                    return new VersionResolution(cached.Version, false);

                if (response.StatusCode is HttpStatusCode.NotFound)
                    return new VersionResolution(null, false);

                response.EnsureSuccessStatusCode();

                string? resolved;
                using (
                    Stream stream = await response
                        .Content.ReadAsStreamAsync(token)
                        .ConfigureAwait(false)
                )
                using (
                    JsonDocument document = await JsonDocument
                        .ParseAsync(stream, cancellationToken: token)
                        .ConfigureAwait(false)
                )
                {
                    resolved = ResolveLatestCompatibleVersion(
                        packageId,
                        document.RootElement,
                        interpreter
                    );
                }

                if (response.Headers.ETag?.Tag is string etag)
                    _latestVersionCache[cacheKey] = new CachedResolution(etag, resolved);

                return new VersionResolution(resolved, false);
            }
            catch (Exception e)
            {
                Logger.Debug(
                    $"Pip: the latest version of {packageId} could not be resolved: {e.Message}"
                );

                if (
                    _latestVersionCache.TryGetValue(
                        (interpreter.Original, packageId),
                        out CachedResolution known
                    )
                )
                    return new VersionResolution(known.Version, false);

                return new VersionResolution(null, true);
            }
            finally
            {
                _updateFetchSemaphore.Release();
            }
        }

        internal static string NormalizeProjectNameForUrl(string name) =>
            Uri.EscapeDataString(
                ProjectNameSeparatorPattern().Replace(name, "-").ToLowerInvariant()
            );

        internal static string NormalizeForFileMatch(string value)
        {
            StringBuilder builder = new(value.Length);
            bool lastWasSeparator = false;

            foreach (char character in value)
            {
                if (character is '-' or '_' or '.')
                {
                    if (!lastWasSeparator)
                        builder.Append('_');
                    lastWasSeparator = true;
                    continue;
                }

                builder.Append(char.ToLowerInvariant(character));
                lastWasSeparator = false;
            }

            return builder.ToString();
        }

        internal static bool IsInterpreterAllowed(string? requiresPython, PythonVersion interpreter)
        {
            if (string.IsNullOrWhiteSpace(requiresPython))
                return true;

            if (!PythonVersionSpecifier.TryParse(requiresPython, out PythonVersionSpecifier spec))
                return false;

            return spec.IsSatisfiedBy(interpreter);
        }

        private static bool IsYanked(JsonElement file) =>
            file.TryGetProperty("yanked", out JsonElement yanked)
            && yanked.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.String => !string.IsNullOrEmpty(yanked.GetString()),
                _ => false,
            };

        private static string? ReadString(JsonElement owner, string property) =>
            owner.TryGetProperty(property, out JsonElement value)
            && value.ValueKind is JsonValueKind.String
                ? value.GetString()
                : null;

        internal static string? ResolveLatestCompatibleVersion(
            string projectName,
            string json,
            PythonVersion interpreter
        )
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return ResolveLatestCompatibleVersion(projectName, document.RootElement, interpreter);
        }

        private static string? ResolveLatestCompatibleVersion(
            string projectName,
            JsonElement root,
            PythonVersion interpreter
        )
        {
            if (
                root.ValueKind is not JsonValueKind.Object
                || !root.TryGetProperty("files", out JsonElement files)
                || files.ValueKind is not JsonValueKind.Array
            )
                throw new InvalidDataException(
                    "The package index response does not contain a file list"
                );

            if (
                !root.TryGetProperty("versions", out JsonElement versions)
                || versions.ValueKind is not JsonValueKind.Array
            )
                throw new InvalidDataException(
                    "The package index response does not contain a version list"
                );

            List<(PythonVersion Parsed, string Raw)> candidates = [];
            foreach (JsonElement node in versions.EnumerateArray())
            {
                if (node.ValueKind is not JsonValueKind.String)
                    continue;

                string raw = node.GetString()!;
                if (!PythonVersion.TryParse(raw, out PythonVersion parsed) || parsed.IsPreRelease)
                    continue;

                candidates.Add((parsed, raw));
            }

            candidates.Sort((a, b) => b.Parsed.CompareTo(a.Parsed));

            List<(string Normalized, JsonElement File)> normalizedFiles = [];
            foreach (JsonElement file in files.EnumerateArray())
            {
                if (file.ValueKind is not JsonValueKind.Object)
                    continue;

                if (ReadString(file, "filename") is not string filename)
                    continue;

                normalizedFiles.Add((NormalizeForFileMatch(filename), file));
            }

            string normalizedName = NormalizeForFileMatch(projectName);
            foreach ((PythonVersion _, string raw) in candidates)
            {
                string prefix = $"{normalizedName}_{NormalizeForFileMatch(raw)}";
                foreach ((string normalizedFile, JsonElement file) in normalizedFiles)
                {
                    if (!normalizedFile.StartsWith(prefix, StringComparison.Ordinal))
                        continue;

                    if (
                        normalizedFile.Length > prefix.Length
                        && normalizedFile[prefix.Length] is not '_'
                    )
                        continue;

                    if (IsYanked(file))
                        continue;

                    if (!IsInterpreterAllowed(ReadString(file, "requires-python"), interpreter))
                        continue;

                    return raw;
                }
            }

            return null;
        }

        protected override IReadOnlyList<Package> GetAvailableUpdates_UnSafe()
        {
            if (TryGetIndexUpdateCheckInterpreter(out PythonVersion interpreter))
                return GetAvailableUpdatesFromIndex(interpreter);

            return GetAvailableUpdatesFromCli();
        }

        private IReadOnlyList<Package> GetAvailableUpdatesFromCli()
        {
            return ParseAvailableUpdates(
                RunPipCommand(" list --outdated " + GetProxyArgument(), LoggableTaskType.ListUpdates),
                DefaultSource,
                this
            );
        }

        private IReadOnlyList<Package> GetAvailableUpdatesFromIndex(PythonVersion interpreter)
        {
            INativeTaskLogger logger = TaskLogger.CreateNew(LoggableTaskType.ListUpdates);
            try
            {
                IReadOnlyList<Package> installed = ParseInstalledPackages(
                    RunPipCommand(" list " + GetProxyArgument(), LoggableTaskType.ListInstalledPackages),
                    DefaultSource,
                    this
                );

                logger.Log(
                    $"Resolving the newest version compatible with Python {interpreter.Original} "
                        + $"for {installed.Count} installed packages"
                );

                using HttpClient client = new(CoreTools.GenericHttpClientParameters)
                {
                    Timeout = TimeSpan.FromSeconds(20),
                };
                client.DefaultRequestHeaders.UserAgent.ParseAdd(CoreData.UserAgentString);

                using CancellationTokenSource budget = new(UpdateResolutionBudget);

                var resolutions = installed
                    .Select(package =>
                        ResolveLatestVersionAsync(
                            client,
                            package.Id,
                            interpreter,
                            budget.Token
                        )
                    )
                    .ToArray();
                Task.WhenAll(resolutions).GetAwaiter().GetResult();

                int failures = 0;
                List<Package> packages = [];
                for (int i = 0; i < installed.Count; i++)
                {
                    VersionResolution resolution = resolutions[i].Result;
                    if (resolution.Failed)
                    {
                        failures++;
                        continue;
                    }

                    if (resolution.Version is null)
                        continue;

                    if (CompareVersions(installed[i].VersionString, resolution.Version) is null or >= 0)
                        continue;

                    packages.Add(
                        new Package(
                            installed[i].Name,
                            installed[i].Id,
                            installed[i].VersionString,
                            resolution.Version,
                            DefaultSource,
                            this,
                            new(PackageScope.Global)
                        )
                    );
                }

                if (failures > installed.Count / 4)
                    throw new HttpRequestException(
                        $"{failures} of {installed.Count} version lookups against "
                            + $"{DefaultSource.Url} failed, the update list would be incomplete"
                    );

                logger.Log($"Found {packages.Count} updates, {failures} lookups failed");
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

        protected override IReadOnlyList<Package> GetInstalledPackages_UnSafe()
        {
            return ParseInstalledPackages(
                RunPipCommand(" list " + GetProxyArgument(), LoggableTaskType.ListInstalledPackages),
                DefaultSource,
                this
            );
        }

        private IReadOnlyList<string> RunPipCommand(string arguments, LoggableTaskType taskType)
        {
            using Process p = new()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Status.ExecutablePath,
                    Arguments = Status.ExecutableCallArgs + arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                },
            };

            IProcessTaskLogger logger = TaskLogger.CreateNew(taskType, p);

            p.Start();
            RegisterListingProcess(p);

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

            return outputLines;
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

        public override int? CompareVersions(string versionA, string versionB)
        {
            if (
                PythonVersion.TryParse(versionA, out PythonVersion parsedA)
                && PythonVersion.TryParse(versionB, out PythonVersion parsedB)
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

            Environment.SetEnvironmentVariable(
                "PIP_DISABLE_PIP_VERSION_CHECK",
                "1",
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
