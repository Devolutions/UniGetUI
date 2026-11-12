using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using UniGetUI.Core.Classes;
using UniGetUI.Core.Data;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.ManagerClasses.Classes;
using UniGetUI.PackageEngine.ManagerClasses.Manager;
using UniGetUI.PackageEngine.Managers.Generic.NuGet;
using UniGetUI.PackageEngine.Managers.Generic.NuGet.Internal;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.Structs;

namespace UniGetUI.PackageEngine.Managers.PowerShellManager
{
    public abstract class BaseNuGet : PackageManager
    {
        /// <summary>
        /// Applies to V2 sources and to local folder feeds. When true, V2 searches use
        /// Packages()?$filter=substringof(query,Id) which searches by package name only but
        /// returns reliable results (e.g. PSGallery's Search() endpoint silently omits some
        /// packages), and a local feed matches the package id alone instead of also matching
        /// title, tags, summary, description, and authors. When false, the standard Search()
        /// endpoint is used which supports full-text search across name, description, and
        /// tags. V3 sources use SearchQueryService and fall back to an exact package-id
        /// lookup when the feed advertises no search service or returns no results.
        /// </summary>
        protected virtual bool UseSubstringSearch => false;

        /// <summary>
        /// Applies to V3 sources and to local folder feeds. When set, searches are restricted
        /// to packages advertising this package type (for example "DotnetTool"). V2 sources
        /// have no equivalent filter and ignore it.
        /// </summary>
        protected virtual string? SearchPackageType => null;

        public static Dictionary<long, string> Manifests = new();

        internal static readonly ConcurrentDictionary<long, string> V3IconUrls = new();
        internal static readonly ConcurrentDictionary<long, V3CatalogEntry> V3Entries = new();

        public override bool InstallerUrlFollowsPackageVersion => true;

        protected override bool? IsEligibleUpdateTarget(IPackage update, string version)
        {
            if (NuGetLocalFeed.TryGetDirectory(update.Source, out _) || !NuGetV3ServiceIndex.IsV3Source(update.Source))
                return true;

            if (NuGetV3ServiceIndex.Resolve(update.Source) is not { } index)
                return null;

            if (index.RegistrationsBaseUrl is null)
                return true;

            return NuGetV3Client.GetListedStatus(index, update.Id, version) switch
            {
                ListedStatus.Listed => true,
                ListedStatus.Unlisted => false,
                _ => null,
            };
        }

        public override int? CompareVersions(string versionA, string versionB)
        {
            if (
                SemanticVersion.TryParse(
                    versionA,
                    SemVerLabels.CaseInsensitive,
                    out SemanticVersion parsedA
                )
                && SemanticVersion.TryParse(
                    versionB,
                    SemVerLabels.CaseInsensitive,
                    out SemanticVersion parsedB
                )
            )
                return parsedA.CompareTo(parsedB);

            return base.CompareVersions(versionA, versionB);
        }

        public sealed override void Initialize()
        {
            static void ThrowIC(string name)
            {
                throw new InvalidOperationException(
                    $"NuGet-based package managers must have Capabilities.{name} set to true"
                );
            }

            if (DetailsHelper is not BaseNuGetDetailsHelper)
            {
                throw new InvalidOperationException(
                    "NuGet-based package managers must not reassign the PackageDetailsProvider property"
                );
            }

            if (!Capabilities.SupportsCustomVersions)
                ThrowIC(nameof(Capabilities.SupportsCustomVersions));
            if (!Capabilities.SupportsCustomPackageIcons)
                ThrowIC(nameof(Capabilities.SupportsCustomPackageIcons));
            if (!Capabilities.CanListDependencies)
                ThrowIC(nameof(Capabilities.CanListDependencies));

            base.Initialize();
        }

        private struct SearchResult
        {
            public string version;
            public CoreTools.Version version_float;
            public string id;
            public string manifest;
        }

        protected sealed override IReadOnlyList<Package> FindPackages_UnSafe(string query)
        {
            List<Package> Packages = [];
            INativeTaskLogger logger = TaskLogger.CreateNew(LoggableTaskType.FindPackages);

            IReadOnlyList<IManagerSource> sources;
            if (Capabilities.SupportsCustomSources)
            {
                sources = SourcesHelper.GetSources();
            }
            else
            {
                sources = [Properties.DefaultSource];
            }

            bool canPrerelease = InstallOptionsFactory.LoadForManager(this).PreRelease;

            foreach (IManagerSource source in sources)
            {
                try
                {
                    if (NuGetLocalFeed.TryGetDirectory(source, out string localDirectory))
                    {
                        Packages.AddRange(
                            FindPackagesLocal(source, localDirectory, query, canPrerelease, logger)
                        );
                        continue;
                    }

                    if (NuGetV3ServiceIndex.IsV3Source(source))
                    {
                        Packages.AddRange(FindPackagesV3(source, query, canPrerelease, logger));
                        continue;
                    }

                    string versionFilter = canPrerelease ? "IsAbsoluteLatestVersion eq true" : "IsLatestVersion eq true";
                    string odataQuery = HttpUtility.UrlEncode(query.Replace("'", "''"));
                    Uri? SearchUrl = UseSubstringSearch
                        ? new Uri(
                            $"{source.Url}/Packages()"
                                + $"?$filter=substringof('{odataQuery}',Id) and {versionFilter}"
                                + $"&$orderby=DownloadCount desc"
                                + $"&$skip=0"
                                + $"&$top=50"
                        )
                        : new Uri(
                            $"{source.Url}/Search()"
                                + $"?$filter=IsLatestVersion"
                                + $"&$orderby=Id&searchTerm='{odataQuery}'"
                                + $"&targetFramework=''"
                                + $"&includePrerelease={(canPrerelease ? "true" : "false")}"
                                + $"&$skip=0"
                                + $"&$top=50"
                                + $"&semVerLevel=2.0.0"
                        );
                    logger.Log($"Begin package search with url={SearchUrl} on manager {Name}");
                    Dictionary<string, SearchResult> AlreadyProcessedPackages = [];

                    using HttpClient client = new(CoreTools.GenericHttpClientParameters);
                    client.DefaultRequestHeaders.UserAgent.ParseAdd(CoreData.UserAgentString);

                    while (SearchUrl is not null)
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Get, SearchUrl);
                        NuGetFeedCredentials.ApplyTo(request, source.Url);
                        using HttpResponseMessage response = client.Send(request);

                        if (!response.IsSuccessStatusCode)
                        {
                            logger.Error(
                                $"Failed to fetch api at Url={SearchUrl} with status code {response.StatusCode}"
                            );
                            SearchUrl = null;
                            continue;
                        }

                        string SearchResults = response
                            .Content.ReadAsStringAsync()
                            .GetAwaiter()
                            .GetResult();
                        MatchCollection matches = Regex.Matches(
                            SearchResults,
                            "<entry>([\\s\\S]*?)<\\/entry>"
                        );

                        foreach (Match match in matches)
                        {
                            if (!match.Success)
                            {
                                continue;
                            }

                            string id = Regex.Match(match.Value, "Id='([^<>']+)'").Groups[1].Value;
                            string version = Regex
                                .Match(match.Value, "Version='([^<>']+)'")
                                .Groups[1]
                                .Value;
                            var float_version = CoreTools.VersionStringToStruct(version);
                            // Match title = Regex.Match(match.Value, "<title[ \\\"\\=A-Za-z0-9]+>([^<>]+)<\\/title>");

                            if (
                                AlreadyProcessedPackages.TryGetValue(id, out var value)
                                && value.version_float >= float_version
                            )
                            {
                                continue;
                            }

                            AlreadyProcessedPackages[id] = new SearchResult
                            {
                                id = id,
                                version = version,
                                version_float = float_version,
                                manifest = match.Value,
                            };
                        }

                        SearchUrl = FindNextPageUrl(SearchResults, SearchUrl);
                        if (SearchUrl is not null)
                            logger.Log($"Adding extra info from URL={SearchUrl}");
                    }

                    foreach (SearchResult package in AlreadyProcessedPackages.Values)
                    {
                        logger.Log(
                            $"Found package {package.id} version {package.version} on source {source.Name}"
                        );
                        var nativePackage = new Package(
                            CoreTools.FormatAsName(package.id),
                            package.id,
                            package.version,
                            source,
                            this
                        );
                        Packages.Add(nativePackage);
                        Manifests[nativePackage.GetVersionedHash()] = package.manifest;
                    }
                }
                catch (Exception ex)
                {
                    logger.Error(
                        $"Source {source.Name} on manager {source.Manager.Name} failed to find package data"
                    );
                    logger.Error(ex);
                }
            }

            logger.Close(0);
            return [.. Packages.Where(package => CanResolveFromSource(package.Id, package.Source))];
        }

        internal IReadOnlyList<Package> FindPackagesLocal(
            IManagerSource source,
            string directory,
            string query,
            bool canPrerelease,
            INativeTaskLogger logger
        )
        {
            logger.Log(
                $"Begin local folder package search for query={query} on source {source.Name} "
                    + $"at Directory={directory} of manager {Name}"
            );

            Dictionary<string, LocalNuGetPackage> latest = new(StringComparer.OrdinalIgnoreCase);
            foreach (LocalNuGetPackage candidate in NuGetLocalFeed.Enumerate(directory))
            {
                if (candidate.IsPreRelease && !canPrerelease)
                    continue;

                if (!NuGetLocalFeed.MatchesQuery(candidate, query, UseSubstringSearch))
                    continue;

                if (
                    SearchPackageType is { } packageType
                    && !candidate.PackageTypes.Contains(packageType, StringComparer.OrdinalIgnoreCase)
                )
                    continue;

                if (
                    latest.TryGetValue(candidate.Id, out LocalNuGetPackage? current)
                    && !IsNewerVersion(candidate.Version, current.Version)
                )
                    continue;

                latest[candidate.Id] = candidate;
            }

            List<Package> packages = [];
            foreach (LocalNuGetPackage found in latest.Values)
            {
                logger.Log(
                    $"Found package {found.Id} version {found.Version} on source {source.Name}"
                );

                packages.Add(
                    new Package(
                        CoreTools.FormatAsName(found.Id),
                        found.Id,
                        found.Version,
                        source,
                        this
                    )
                );
            }

            return packages;
        }

        internal IReadOnlyList<Package> GetAvailableUpdatesLocal(
            IManagerSource source,
            string directory,
            IReadOnlyList<IPackage> installedPackages,
            bool canPrerelease,
            INativeTaskLogger logger
        )
        {
            Dictionary<string, List<LocalNuGetPackage>> availableById = new(
                StringComparer.OrdinalIgnoreCase
            );

            foreach (LocalNuGetPackage candidate in NuGetLocalFeed.Enumerate(directory))
            {
                if (candidate.IsPreRelease && !canPrerelease)
                    continue;

                if (!availableById.TryGetValue(candidate.Id, out List<LocalNuGetPackage>? entries))
                    availableById[candidate.Id] = entries = [];

                entries.Add(candidate);
            }

            var newestInstalled = SelectNewestInstalled(installedPackages);
            var scopeMap = BuildInstalledScopeMap(newestInstalled.Values);
            List<Package> packages = [];

            foreach (IPackage installedPackage in newestInstalled.Values)
            {
                string id = installedPackage.Id;
                string installedVersion = installedPackage.VersionString;

                if (!availableById.TryGetValue(id, out List<LocalNuGetPackage>? entries))
                    continue;

                string? newest = null;
                foreach (LocalNuGetPackage candidate in entries)
                {
                    if (!IsNewerVersion(candidate.Version, installedVersion))
                        continue;

                    if (newest is null || IsNewerVersion(candidate.Version, newest))
                        newest = candidate.Version;
                }

                if (newest is null)
                    continue;

                logger.Log($"Found package {id} version {newest} on source {source.Name}");

                packages.Add(
                    new Package(
                        CoreTools.FormatAsName(id),
                        id,
                        installedVersion,
                        newest,
                        source,
                        this,
                        new OverridenInstallationOptions(scopeMap.GetValueOrDefault(id.ToLowerInvariant()))
                    )
                );
            }

            return packages;
        }

        private bool IsNewerVersion(string candidate, string current) =>
            CompareVersions(candidate, current) is { } comparison
                ? comparison > 0
                : CoreTools.VersionStringToStruct(candidate)
                    > CoreTools.VersionStringToStruct(current);

        internal IReadOnlyList<Package> FindPackagesV3(
            IManagerSource source,
            string query,
            bool canPrerelease,
            INativeTaskLogger logger
        )
        {
            NuGetV3ServiceIndex? index = NuGetV3ServiceIndex.Resolve(source);
            if (index is null)
            {
                logger.Error(
                    $"Could not resolve the NuGet V3 service index for source {source.Name} "
                        + $"at Url={source.Url} on manager {Name}"
                );
                return [];
            }

            logger.Log(
                $"Begin V3 package search for query={query} on source {source.Name} of manager {Name}"
            );

            List<Package> packages = [];
            foreach (
                V3SearchResult result in NuGetV3Client.Search(
                    index,
                    query,
                    canPrerelease,
                    SearchPackageType,
                    50
                )
            )
            {
                if (result.Id is null || result.Version is null)
                    continue;

                logger.Log(
                    $"Found package {result.Id} version {result.Version} on source {source.Name}"
                );

                var nativePackage = new Package(
                    CoreTools.FormatAsName(result.Id),
                    result.Id,
                    result.Version,
                    source,
                    this
                );
                packages.Add(nativePackage);

                if (!result.IsExactIdFallback)
                    V3IconUrls[nativePackage.GetVersionedHash()] = result.IconUrl ?? string.Empty;
            }

            return packages;
        }

        protected override IReadOnlyList<Package> GetAvailableUpdates_UnSafe()
        {
            int errors = 0;
            var logger = TaskLogger.CreateNew(LoggableTaskType.ListUpdates);

            var installedPackages = TaskRecycler<IReadOnlyList<IPackage>>.RunOrAttach(
                GetInstalledPackages
            );
            var Packages = new List<Package>();

            Dictionary<IManagerSource, List<IPackage>> sourceMapping = new();

            foreach (var package in installedPackages)
            {
                var uri = package.Source;
                if (!sourceMapping.ContainsKey(uri))
                    sourceMapping[uri] = new();
                sourceMapping[uri].Add(package);
            }
            bool canPrerelease = InstallOptionsFactory.LoadForManager(this).PreRelease;

            foreach (var pair in sourceMapping)
            {
                List<Package> found = [];
                foreach (IManagerSource source in GetUpdateSources(pair.Key))
                {
                    List<IPackage> allowed =
                    [
                        .. pair.Value.Where(package => CanResolveFromSource(package.Id, source)),
                    ];
                    if (allowed.Count is 0)
                        continue;

                    found.AddRange(
                        GetAvailableUpdatesFromSource(
                            source,
                            allowed,
                            canPrerelease,
                            logger,
                            out int sourceErrors
                        )
                    );
                    errors += sourceErrors;
                }

                Packages.AddRange(KeepNewestUpdatePerId(found));
            }

            logger.Close(errors);
            return KeepUpdatesNewerThanInstalled(Packages, installedPackages);
        }

        protected virtual IReadOnlyList<IManagerSource> GetUpdateSources(
            IManagerSource installedSource
        ) => [installedSource];

        protected virtual bool CanResolveFromSource(string packageId, IManagerSource source) =>
            true;

        internal IReadOnlyList<Package> GetAvailableUpdatesFromSource(
            IManagerSource source,
            IReadOnlyList<IPackage> installedPackages,
            bool canPrerelease,
            INativeTaskLogger logger,
            out int errors
        )
        {
            errors = 0;
            List<Package> packages = [];
            try
            {
                if (NuGetLocalFeed.TryGetDirectory(source, out string localDirectory))
                {
                    packages.AddRange(
                        GetAvailableUpdatesLocal(
                            source,
                            localDirectory,
                            installedPackages,
                            canPrerelease,
                            logger
                        )
                    );
                    return packages;
                }

                if (NuGetV3ServiceIndex.IsV3Source(source))
                {
                    var v3Updates = GetAvailableUpdatesV3(
                        source,
                        installedPackages,
                        canPrerelease,
                        logger,
                        out int v3Errors
                    );
                    if (v3Updates is null)
                    {
                        errors++;
                    }
                    else
                    {
                        packages.AddRange(v3Updates);
                        errors += v3Errors;
                    }

                    return packages;
                }

                var newestInstalled = SelectNewestInstalled(installedPackages);
                var packageIdVersion = newestInstalled.ToDictionary(
                    entry => entry.Key,
                    entry => entry.Value.VersionString
                );
                var packageIdScope = BuildInstalledScopeMap(newestInstalled.Values);

                using HttpClient client = new(CoreTools.GenericHttpClientParameters);
                client.DefaultRequestHeaders.UserAgent.ParseAdd(CoreData.UserAgentString);

                int requestErrors = 0;
                string? Fetch(string url)
                {
                    try
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Get, url);
                        NuGetFeedCredentials.ApplyTo(request, source.Url);
                        using HttpResponseMessage response = client.Send(request);

                        if (!response.IsSuccessStatusCode)
                        {
                            logger.Error(
                                $"Failed to fetch api at Url={url} with status code {response.StatusCode}"
                            );
                            requestErrors++;
                            return null;
                        }

                        return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        logger.Error($"Failed to fetch api at Url={url} on source {source.Name}");
                        logger.Error(ex);
                        requestErrors++;
                        return null;
                    }
                }

                List<Package> Parse(string content) =>
                    ParseUpdatesResponse(
                        content,
                        packageIdVersion,
                        packageIdScope,
                        source,
                        this,
                        logger
                    );

                bool foundAny = false;
                foreach (
                    string SearchUrl in BuildV2UpdateUrls(source, installedPackages, canPrerelease)
                )
                {
                    if (Fetch(SearchUrl) is not { } SearchResults)
                        continue;

                    List<Package> found = Parse(SearchResults);
                    foundAny |= found.Count > 0;
                    packages.AddRange(found);
                }

                if (
                    !foundAny
                    && requestErrors is 0
                    && BuildV2UpdateUrls(
                            source,
                            installedPackages,
                            canPrerelease,
                            includeAllVersions: true
                        )
                        .Any(url => Fetch(url) is { } content && HasV2Entries(content))
                )
                {
                    logger.Log(
                        $"GetUpdates() on source {source.Name} returned no update although newer versions "
                            + "exist, so each package is looked up by id instead"
                    );

                    string baseUrl = source.Url.ToString().Trim('/');
                    string latestFilter = canPrerelease
                        ? "IsAbsoluteLatestVersion"
                        : "IsLatestVersion";
                    foreach (IPackage package in newestInstalled.Values)
                    {
                        string id = HttpUtility.UrlEncode(package.Id.Replace("'", "''"));
                        if (
                            Fetch($"{baseUrl}/FindPackagesById()?id='{id}'&$filter={latestFilter}")
                            is { } content
                        )
                            packages.AddRange(Parse(content));
                    }
                }

                errors += requestErrors;
            }
            catch (Exception ex)
            {
                logger.Error(
                    $"Source {source.Name} on manager {source.Manager.Name} failed to load updates info with exception"
                );
                logger.Error(ex);
            }

            return packages;
        }

        internal IReadOnlyList<Package> KeepNewestUpdatePerId(IReadOnlyList<Package> updates)
        {
            Dictionary<string, Package> newest = [];
            foreach (Package update in updates)
            {
                string key = update.Id.ToLower();
                if (
                    !newest.TryGetValue(key, out Package? current)
                    || IsNewerVersion(update.NewVersionString, current.NewVersionString)
                )
                    newest[key] = update;
            }

            return [.. newest.Values];
        }

        internal const int MaxV2UpdateUrlLength = 8000;

        private static bool HasV2Entries(string content) =>
            Regex.IsMatch(content, "<entry[\\s>]");

        internal static Uri? FindNextPageUrl(string content, Uri currentUrl)
        {
            foreach (Match link in Regex.Matches(content, "<link\\b[^>]*>"))
            {
                if (!Regex.IsMatch(link.Value, "\\brel\\s*=\\s*([\"'])next\\1"))
                    continue;

                Match href = Regex.Match(link.Value, "\\bhref\\s*=\\s*([\"'])(.*?)\\1");
                if (!href.Success)
                    continue;

                return Uri.TryCreate(
                    currentUrl,
                    WebUtility.HtmlDecode(href.Groups[2].Value),
                    out Uri? next
                )
                    ? next
                    : null;
            }

            return null;
        }

        internal IReadOnlyList<string> BuildV2UpdateUrls(
            IManagerSource source,
            IReadOnlyList<IPackage> installedPackages,
            bool canPrerelease,
            bool includeAllVersions = false
        )
        {
            var entries = new List<(string Id, string Version)>();
            var indexById = new Dictionary<string, int>();
            foreach (var package in installedPackages)
            {
                string key = package.Id.ToLowerInvariant();
                if (!indexById.TryGetValue(key, out int index))
                {
                    indexById[key] = entries.Count;
                    entries.Add((package.Id, package.VersionString));
                }
                else if (IsOlderVersion(package.VersionString, entries[index].Version))
                {
                    entries[index] = (entries[index].Id, package.VersionString);
                }
            }

            string prefix = $"{source.Url.ToString().Trim('/')}/GetUpdates()?packageIds=%27";
            const string middle = "%27&versions=%27";
            string suffix =
                $"%27&includePrerelease={(canPrerelease ? "true" : "false")}"
                + $"&includeAllVersions={(includeAllVersions ? "true" : "0")}";
            string separator = HttpUtility.UrlEncode("|");
            int fixedLength = prefix.Length + middle.Length + suffix.Length;

            var urls = new List<string>();
            var ids = new StringBuilder();
            var versions = new StringBuilder();
            int count = 0;

            foreach (var (id, version) in entries)
            {
                string encodedId = HttpUtility.UrlEncode(id);
                string encodedVersion = HttpUtility.UrlEncode(version);
                int added =
                    encodedId.Length + encodedVersion.Length + (count > 0 ? 2 * separator.Length : 0);

                if (
                    count > 0
                    && fixedLength + ids.Length + versions.Length + added > MaxV2UpdateUrlLength
                )
                {
                    urls.Add(prefix + ids + middle + versions + suffix);
                    ids.Clear();
                    versions.Clear();
                    count = 0;
                }

                if (count > 0)
                {
                    ids.Append(separator);
                    versions.Append(separator);
                }
                ids.Append(encodedId);
                versions.Append(encodedVersion);
                count++;
            }

            if (count > 0)
                urls.Add(prefix + ids + middle + versions + suffix);

            return urls;
        }

        private bool IsOlderVersion(string version, string other) =>
            CompareVersions(version, other) is { } comparison
                ? comparison < 0
                : CoreTools.VersionStringToStruct(version) < CoreTools.VersionStringToStruct(other);

        internal IReadOnlyList<Package>? GetAvailableUpdatesV3(
            IManagerSource source,
            IReadOnlyList<IPackage> installedPackages,
            bool canPrerelease,
            INativeTaskLogger logger
        ) => GetAvailableUpdatesV3(source, installedPackages, canPrerelease, logger, out _);

        internal IReadOnlyList<Package>? GetAvailableUpdatesV3(
            IManagerSource source,
            IReadOnlyList<IPackage> installedPackages,
            bool canPrerelease,
            INativeTaskLogger logger,
            out int errors
        )
        {
            errors = 0;
            NuGetV3ServiceIndex? index = NuGetV3ServiceIndex.Resolve(source);
            if (index is null)
            {
                logger.Error(
                    $"Could not resolve the NuGet V3 service index for source {source.Name} "
                        + $"at Url={source.Url} on manager {Name}"
                );
                return null;
            }

            var newestInstalled = SelectNewestInstalled(installedPackages);
            var installed = newestInstalled.ToDictionary(
                entry => entry.Key,
                entry => (entry.Value.Id, Version: entry.Value.VersionString)
            );

            var scopeMap = BuildInstalledScopeMap(newestInstalled.Values);
            var candidates = new ConcurrentDictionary<string, string>();
            var failures = new ConcurrentDictionary<string, Exception?>();

            Parallel.ForEach(
                installed,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = NuGetV3Client.MaxConcurrentRequests,
                },
                entry =>
                {
                    try
                    {
                        string? candidate = NuGetV3Client.GetUpdateCandidate(
                            index,
                            entry.Value.Id,
                            entry.Value.Version,
                            canPrerelease,
                            out bool requestFailed
                        );

                        if (requestFailed)
                            failures[entry.Key] = null;
                        else if (candidate is not null)
                            candidates[entry.Key] = candidate;
                    }
                    catch (Exception ex)
                    {
                        failures[entry.Key] = ex;
                    }
                }
            );

            foreach (var failure in failures)
            {
                logger.Error(
                    $"Failed to check updates for {installed[failure.Key].Id} on source {source.Name}"
                );
                if (failure.Value is { } exception)
                    logger.Error(exception);
            }

            errors = failures.Count;

            List<Package> packages = [];
            foreach (var candidate in candidates)
            {
                (string id, string installedVersion) = installed[candidate.Key];
                logger.Log(
                    $"Found package {id} version {candidate.Value} on source {source.Name}"
                );

                packages.Add(
                    new Package(
                        CoreTools.FormatAsName(id),
                        id,
                        installedVersion,
                        candidate.Value,
                        source,
                        this,
                        new OverridenInstallationOptions(scopeMap.GetValueOrDefault(candidate.Key))
                    )
                );
            }

            return packages;
        }

        /// <summary>
        /// Drops any update whose new version is not newer than the highest version already
        /// installed under the same id, so a copy installed in another scope does not get
        /// offered an update it already satisfies. Comparisons go through the manager so a
        /// NuGet pre-release is ordered below its stable release.
        /// </summary>
        internal IReadOnlyList<Package> KeepUpdatesNewerThanInstalled(
            IReadOnlyList<Package> candidates,
            IReadOnlyList<IPackage> installedPackages
        )
        {
            var newestInstalled = SelectNewestInstalled(installedPackages);

            List<Package> kept = [];
            foreach (var candidate in candidates)
            {
                if (
                    !newestInstalled.TryGetValue(
                        candidate.Id.ToLowerInvariant(),
                        out IPackage? newestPackage
                    )
                )
                {
                    kept.Add(candidate);
                    continue;
                }

                string highestInstalled = newestPackage.VersionString;
                bool isNewer =
                    CompareVersions(highestInstalled, candidate.NewVersionString)
                    is { } comparison
                        ? comparison < 0
                        : CoreTools.VersionStringToStruct(highestInstalled)
                            < candidate.NormalizedNewVersion;

                if (isNewer)
                    kept.Add(candidate);
            }

            return kept;
        }

        /// <summary>
        /// Picks, for each installed package id (lowercased), the copy its update applies to:
        /// the highest installed version, the last one enumerated on a tie. Update-PSResource
        /// installs a new version side by side with the old ones, and Get-InstalledPSResource
        /// lists them sorted as strings (1.9.0, 1.12.6, 1.10.1), so taking whichever copy came
        /// last reported a stale installed version that never moved after an update.
        /// </summary>
        internal Dictionary<string, IPackage> SelectNewestInstalled(
            IEnumerable<IPackage> installedPackages
        )
        {
            var newest = new Dictionary<string, IPackage>();
            foreach (var package in installedPackages)
            {
                string key = package.Id.ToLowerInvariant();
                if (
                    !newest.TryGetValue(key, out IPackage? current)
                    || !IsOlderVersion(package.VersionString, current.VersionString)
                )
                {
                    newest[key] = package;
                }
            }

            return newest;
        }

        /// <summary>
        /// Maps each installed package id (lowercased) to the scope its update should target.
        /// Callers pass the copies chosen by <see cref="SelectNewestInstalled"/>, so the scope
        /// and the installed version always come from the same enumerated package (issue
        /// #5163). A module installed in a single scope updates in that scope; a module
        /// installed in both resolves to the scope holding its newest version — surfacing an
        /// independent update per scope would require scope-aware package identity, which the
        /// upgrade loader does not currently support.
        /// </summary>
        internal static Dictionary<string, string?> BuildInstalledScopeMap(
            IEnumerable<IPackage> installedPackages
        )
        {
            var scopeMap = new Dictionary<string, string?>();
            foreach (var package in installedPackages)
                scopeMap[package.Id.ToLowerInvariant()] = package.OverridenOptions.Scope;
            return scopeMap;
        }

        /// <summary>
        /// Parses a V2 NuGet OData GetUpdates() response into update packages, carrying each
        /// installed package's scope onto its update so operations (e.g. Update-PSResource
        /// -Scope) don't silently fall back to CurrentUser (regression guard for issue #5163).
        /// </summary>
        internal static List<Package> ParseUpdatesResponse(
            string searchResults,
            IReadOnlyDictionary<string, string> packageIdVersion,
            IReadOnlyDictionary<string, string?> packageIdScope,
            IManagerSource source,
            BaseNuGet manager,
            INativeTaskLogger? logger = null
        )
        {
            var packages = new List<Package>();
            MatchCollection matches = Regex.Matches(searchResults, "<entry>([\\s\\S]*?)<\\/entry>");

            foreach (Match match in matches)
            {
                if (!match.Success)
                    continue;

                string id = Regex.Match(match.Value, "<d:Id>([^<]+)</d:Id>").Groups[1].Value;
                string new_version = Regex
                    .Match(match.Value, "<d:Version>([^<]+)</d:Version>")
                    .Groups[1]
                    .Value;

                if (!packageIdVersion.TryGetValue(id.ToLowerInvariant(), out string? installedVersion))
                    continue;

                logger?.Log($"Found package {id} version {new_version} on source {source.Name}");

                var nativePackage = new Package(
                    CoreTools.FormatAsName(id),
                    id,
                    installedVersion,
                    new_version,
                    source,
                    manager,
                    new OverridenInstallationOptions(packageIdScope.GetValueOrDefault(id.ToLowerInvariant()))
                );
                packages.Add(nativePackage);
                Manifests[nativePackage.GetVersionedHash()] = match.Value;
            }

            return packages;
        }

        protected sealed override IReadOnlyList<Package> GetInstalledPackages_UnSafe() =>
            TaskRecycler<IReadOnlyList<Package>>.RunOrAttach(
                ListInstalledPackagesForGeneration,
                InstalledPackagesGeneration
            );

        private IReadOnlyList<Package> ListInstalledPackagesForGeneration(
            long installedPackagesGeneration
        ) => _getInstalledPackages_UnSafe();

        protected abstract IReadOnlyList<Package> _getInstalledPackages_UnSafe();
    }
}
