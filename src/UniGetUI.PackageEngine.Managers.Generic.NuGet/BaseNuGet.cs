using System.Collections.Concurrent;
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
using UniGetUI.PackageEngine.Managers.Generic.NuGet.Internal;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.Structs;

namespace UniGetUI.PackageEngine.Managers.PowerShellManager
{
    public abstract class BaseNuGet : PackageManager
    {
        /// <summary>
        /// Only applies to V2 sources. When true, searches use
        /// Packages()?$filter=substringof(query,Id) which searches by package name only but
        /// returns reliable results (e.g. PSGallery's Search() endpoint silently omits some
        /// packages). When false, the standard Search() endpoint is used which supports
        /// full-text search across name, description, and tags. V3 sources use
        /// SearchQueryService and fall back to an exact package-id lookup when the feed
        /// advertises no search service or returns no results.
        /// </summary>
        protected virtual bool UseSubstringSearch => false;

        /// <summary>
        /// Only applies to V3 sources. When set, searches are restricted to packages
        /// advertising this package type (for example "DotnetTool"). V2 sources have no
        /// equivalent filter and ignore it.
        /// </summary>
        protected virtual string? V3PackageType => null;

        public static Dictionary<long, string> Manifests = new();

        internal static readonly ConcurrentDictionary<long, string> V3IconUrls = new();
        internal static readonly ConcurrentDictionary<long, V3CatalogEntry> V3Entries = new();

        public override bool InstallerUrlFollowsPackageVersion => true;

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

                        SearchUrl = null;
                        Match next = Regex.Match(
                            SearchResults,
                            "<link rel=\"next\" href=\"([^\"]+)\" ?\\/>"
                        );
                        if (next.Success)
                        {
                            SearchUrl = new Uri(next.Groups[1].Value.Replace("&amp;", "&"));
                            logger.Log($"Adding extra info from URL={SearchUrl}");
                        }
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
            return Packages;
        }

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
                    V3PackageType,
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
                try
                {
                    if (NuGetV3ServiceIndex.IsV3Source(pair.Key))
                    {
                        var v3Updates = GetAvailableUpdatesV3(
                            pair.Key,
                            pair.Value,
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
                            Packages.AddRange(v3Updates);
                            errors += v3Errors;
                        }

                        continue;
                    }

                    var packageIds = new StringBuilder();
                    var packageVers = new StringBuilder();
                    var packageIdVersion = new Dictionary<string, string>();
                    foreach (var package in pair.Value)
                    {
                        packageIds.Append(package.Id + "|");
                        packageVers.Append(package.VersionString + "|");
                        packageIdVersion[package.Id.ToLower()] = package.VersionString;
                    }
                    var packageIdScope = BuildInstalledScopeMap(pair.Value);

                    var SearchUrl =
                        $"{pair.Key.Url.ToString().Trim('/')}/GetUpdates()"
                        + $"?packageIds=%27{HttpUtility.UrlEncode(packageIds.ToString().Trim('|'))}%27"
                        + $"&versions=%27{HttpUtility.UrlEncode(packageVers.ToString().Trim('|'))}%27"
                        + $"&includePrerelease={(canPrerelease ? "true" : "false")}"
                        + $"&includeAllVersions=0";

                    using HttpClient client = new(CoreTools.GenericHttpClientParameters);
                    client.DefaultRequestHeaders.UserAgent.ParseAdd(CoreData.UserAgentString);
                    using var request = new HttpRequestMessage(HttpMethod.Get, SearchUrl);
                    using HttpResponseMessage response = client.Send(request);

                    if (!response.IsSuccessStatusCode)
                    {
                        logger.Error(
                            $"Failed to fetch api at Url={SearchUrl} with status code {response.StatusCode}"
                        );
                        errors++;
                    }
                    else
                    {
                        string SearchResults = response
                            .Content.ReadAsStringAsync()
                            .GetAwaiter()
                            .GetResult();
                        Packages.AddRange(
                            ParseUpdatesResponse(
                                SearchResults,
                                packageIdVersion,
                                packageIdScope,
                                pair.Key,
                                this,
                                logger
                            )
                        );
                    }
                }
                catch (Exception ex)
                {
                    logger.Error(
                        $"Source {pair.Key.Name} on manager {pair.Key.Manager.Name} failed to load updates info with exception"
                    );
                    logger.Error(ex);
                }
            }

            logger.Close(errors);
            return KeepUpdatesNewerThanInstalled(Packages, installedPackages);
        }

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

            var installed = new Dictionary<string, (string Id, string Version)>();
            foreach (var package in installedPackages)
                installed[package.Id.ToLower()] = (package.Id, package.VersionString);

            var scopeMap = BuildInstalledScopeMap(installedPackages);
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
            var highestInstalledById = new Dictionary<string, string>();
            foreach (var package in installedPackages)
            {
                string key = package.Id.ToLower();
                if (
                    !highestInstalledById.TryGetValue(key, out string? highest)
                    || CompareVersions(package.VersionString, highest) > 0
                )
                {
                    highestInstalledById[key] = package.VersionString;
                }
            }

            List<Package> kept = [];
            foreach (var candidate in candidates)
            {
                if (
                    !highestInstalledById.TryGetValue(
                        candidate.Id.ToLower(),
                        out string? highestInstalled
                    )
                )
                {
                    kept.Add(candidate);
                    continue;
                }

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
        /// Maps each installed package id (lowercased) to the scope its update should target.
        /// Mirrors the last-wins keying of the version map so the scope and the installed
        /// version always come from the same enumerated package (issue #5163). A module
        /// installed in a single scope updates in that scope; a module installed in both
        /// resolves to whichever scope is enumerated last (as its version does) — surfacing
        /// an independent update per scope would require scope-aware package identity, which
        /// the upgrade loader does not currently support.
        /// </summary>
        internal static Dictionary<string, string?> BuildInstalledScopeMap(
            IEnumerable<IPackage> installedPackages
        )
        {
            var scopeMap = new Dictionary<string, string?>();
            foreach (var package in installedPackages)
                scopeMap[package.Id.ToLower()] = package.OverridenOptions.Scope;
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

                if (!packageIdVersion.TryGetValue(id.ToLower(), out string? installedVersion))
                    continue;

                logger?.Log($"Found package {id} version {new_version} on source {source.Name}");

                var nativePackage = new Package(
                    CoreTools.FormatAsName(id),
                    id,
                    installedVersion,
                    new_version,
                    source,
                    manager,
                    new OverridenInstallationOptions(packageIdScope.GetValueOrDefault(id.ToLower()))
                );
                packages.Add(nativePackage);
                Manifests[nativePackage.GetVersionedHash()] = match.Value;
            }

            return packages;
        }

        protected sealed override IReadOnlyList<Package> GetInstalledPackages_UnSafe() =>
            TaskRecycler<IReadOnlyList<Package>>.RunOrAttach(_getInstalledPackages_UnSafe);

        protected abstract IReadOnlyList<Package> _getInstalledPackages_UnSafe();
    }
}
