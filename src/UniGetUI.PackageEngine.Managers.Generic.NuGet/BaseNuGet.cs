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
using UniGetUI.PackageEngine.PackageClasses;

namespace UniGetUI.PackageEngine.Managers.PowerShellManager
{
    public abstract class BaseNuGet : PackageManager
    {
        /// <summary>
        /// When true, searches use Packages()?$filter=substringof(query,Id) which searches by
        /// package name only but returns reliable results (e.g. PSGallery's Search() endpoint
        /// silently omits some packages). When false, the standard Search() endpoint is used
        /// which supports full-text search across name, description, and tags.
        /// </summary>
        protected virtual bool UseSubstringSearch => false;
        public static Dictionary<long, string> Manifests = new();

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
                        HttpResponseMessage response = client
                            .GetAsync(SearchUrl)
                            .GetAwaiter()
                            .GetResult();

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
                        Manifests[nativePackage.GetHash()] = package.manifest;
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
                    var packageIds = new StringBuilder();
                    var packageVers = new StringBuilder();
                    var packageIdVersion = new Dictionary<string, string>();
                    foreach (var package in pair.Value)
                    {
                        packageIds.Append(package.Id + "|");
                        packageVers.Append(package.VersionString + "|");
                        packageIdVersion[package.Id.ToLower()] = package.VersionString;
                    }

                    var SearchUrl =
                        $"{pair.Key.Url.ToString().Trim('/')}/GetUpdates()"
                        + $"?packageIds=%27{HttpUtility.UrlEncode(packageIds.ToString().Trim('|'))}%27"
                        + $"&versions=%27{HttpUtility.UrlEncode(packageVers.ToString().Trim('|'))}%27"
                        + $"&includePrerelease={(canPrerelease ? "true" : "false")}"
                        + $"&includeAllVersions=0";

                    using HttpClient client = new(CoreTools.GenericHttpClientParameters);
                    client.DefaultRequestHeaders.UserAgent.ParseAdd(CoreData.UserAgentString);
                    HttpResponseMessage response = client
                        .GetAsync(SearchUrl)
                        .GetAwaiter()
                        .GetResult();

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
                        MatchCollection matches = Regex.Matches(
                            SearchResults,
                            "<entry>([\\s\\S]*?)<\\/entry>"
                        );

                        foreach (Match match in matches)
                        {
                            if (!match.Success)
                                continue;

                            string id = Regex
                                .Match(match.Value, "<d:Id>([^<]+)</d:Id>")
                                .Groups[1]
                                .Value;
                            string new_version = Regex
                                .Match(match.Value, "<d:Version>([^<]+)</d:Version>")
                                .Groups[1]
                                .Value;
                            // Match title = Regex.Match(match.Value, "<title[ \\\"\\=A-Za-z0-9]+>([^<>]+)<\\/title>");

                            logger.Log(
                                $"Found package {id} version {new_version} on source {pair.Key.Name}"
                            );

                            var nativePackage = new Package(
                                CoreTools.FormatAsName(id),
                                id,
                                packageIdVersion[id.ToLower()],
                                new_version,
                                pair.Key,
                                this
                            );
                            Packages.Add(nativePackage);
                            Manifests[nativePackage.GetHash()] = match.Value;
                        }
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

            var maxVersions = new Dictionary<string, CoreTools.Version?>();
            foreach (var pkg in installedPackages)
            {
                maxVersions.TryGetValue(pkg.Id, out var ver);
                if (ver is null || ver < pkg.NormalizedVersion)
                {
                    maxVersions[pkg.Id.ToLower()] = pkg.NormalizedVersion;
                }
            }

            logger.Close(errors);
            return Packages
                .Where(p => maxVersions[p.Id.ToLower()] < p.NormalizedNewVersion)
                .ToArray();
        }

        protected sealed override IReadOnlyList<Package> GetInstalledPackages_UnSafe() =>
            TaskRecycler<IReadOnlyList<Package>>.RunOrAttach(_getInstalledPackages_UnSafe);

        protected abstract IReadOnlyList<Package> _getInstalledPackages_UnSafe();
    }
}
