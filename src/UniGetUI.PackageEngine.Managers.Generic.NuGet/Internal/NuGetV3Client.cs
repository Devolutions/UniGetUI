using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Web;
using System.Xml.Linq;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;

namespace UniGetUI.PackageEngine.Managers.Generic.NuGet.Internal
{
    internal enum ListedStatus
    {
        Listed,
        Unlisted,
        Unknown,
    }

    internal static class NuGetV3Client
    {
        public const int MaxConcurrentRequests = 8;

        private static readonly TimeSpan VersionCacheLifetime = TimeSpan.FromMinutes(10);
        private const int MaxSearchPages = 5;

        private static readonly ConcurrentDictionary<
            string,
            (DateTime FetchedAt, IReadOnlyList<string> Versions)
        > VersionCache = new();

        private static readonly ConcurrentDictionary<
            string,
            (DateTime FetchedAt, V3RegistrationLeaf? Leaf)
        > LeafCache = new();

        internal static void ClearCaches()
        {
            VersionCache.Clear();
            LeafCache.Clear();
        }

        public static bool TryDownloadString(Uri url, out string content)
        {
            content = string.Empty;

            try
            {
                using HttpClient client = new(CoreTools.GenericHttpClientParameters);

                client.DefaultRequestHeaders.UserAgent.ParseAdd(CoreData.UserAgentString);
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                using HttpResponseMessage response = client.Send(request);

                if (!response.IsSuccessStatusCode)
                {
                    Logger.Debug(
                        $"NuGet V3 request to Url={url} failed with status code {response.StatusCode}"
                    );
                    return false;
                }

                content = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                return true;
            }
            catch (Exception e)
            {
                Logger.Warn($"NuGet V3 request to Url={url} threw an exception");
                Logger.Warn(e);
                return false;
            }
        }

        public static IReadOnlyList<V3SearchResult> Search(
            NuGetV3ServiceIndex index,
            string query,
            bool includePreRelease,
            string? packageType,
            int take
        )
        {
            if (index.SearchQueryService is null)
                return SearchByExactId(index, query, includePreRelease, packageType);

            Dictionary<string, V3SearchResult> highestById = new(
                StringComparer.OrdinalIgnoreCase
            );
            List<string> order = [];
            int skip = 0;

            for (int page = 0; page < MaxSearchPages && order.Count < take; page++)
            {
                int requestedTake = take - order.Count;
                (int TotalHits, IReadOnlyList<V3SearchResult> Results)? pageResult = SearchPage(
                    index,
                    query,
                    includePreRelease,
                    packageType,
                    skip,
                    requestedTake
                );

                if (pageResult is not { Results.Count: > 0 } current)
                    break;

                foreach (V3SearchResult result in current.Results)
                {
                    string id = result.Id!;
                    if (!highestById.TryGetValue(id, out V3SearchResult? existing))
                    {
                        highestById[id] = result;
                        order.Add(id);
                        continue;
                    }

                    if (IsHigherVersion(result.Version!, existing.Version!))
                        highestById[id] = result;
                }

                skip += current.Results.Count;

                bool moreAvailable =
                    current.TotalHits > 0
                        ? skip < current.TotalHits
                        : current.Results.Count >= requestedTake;

                if (!moreAvailable)
                    break;
            }

            if (order.Count is 0)
                return SearchByExactId(index, query, includePreRelease, packageType);

            List<V3SearchResult> results = [];
            foreach (string id in order)
                results.Add(highestById[id]);

            return results;
        }

        private static (int TotalHits, IReadOnlyList<V3SearchResult> Results)? SearchPage(
            NuGetV3ServiceIndex index,
            string query,
            bool includePreRelease,
            string? packageType,
            int skip,
            int take
        )
        {
            var url = new StringBuilder(index.SearchQueryService!.AbsoluteUri.TrimEnd('/'));
            url.Append("?q=").Append(HttpUtility.UrlEncode(query));
            url.Append(CultureInfo.InvariantCulture, $"&skip={skip}");
            url.Append(CultureInfo.InvariantCulture, $"&take={take}");
            url.Append(CultureInfo.InvariantCulture, $"&prerelease={(includePreRelease ? "true" : "false")}");
            url.Append("&semVerLevel=2.0.0");

            if (!string.IsNullOrEmpty(packageType))
                url.Append("&packageType=").Append(HttpUtility.UrlEncode(packageType));

            if (!Uri.TryCreate(url.ToString(), UriKind.Absolute, out Uri? searchUrl))
                return null;

            if (!TryDownloadString(searchUrl, out string content))
                return null;

            try
            {
                V3SearchResponse? response = NuGetV3Json.DeserializeSearchResponse(content);
                if (response?.Data is not { Count: > 0 })
                    return (response?.TotalHits ?? 0, []);

                List<V3SearchResult> results = [];
                foreach (V3SearchResult result in response.Data)
                {
                    if (
                        !string.IsNullOrWhiteSpace(result.Id)
                        && !string.IsNullOrWhiteSpace(result.Version)
                    )
                        results.Add(result);
                }

                return (response.TotalHits, results);
            }
            catch (Exception e)
            {
                Logger.Warn($"Malformed NuGet V3 search response at Url={searchUrl}");
                Logger.Warn(e);
                return null;
            }
        }

        private static bool IsHigherVersion(string candidate, string current)
        {
            if (!TryParseNuGetVersion(candidate, out SemanticVersion parsedCandidate))
                return false;
            if (!TryParseNuGetVersion(current, out SemanticVersion parsedCurrent))
                return true;

            return parsedCandidate > parsedCurrent;
        }

        private static IReadOnlyList<V3SearchResult> SearchByExactId(
            NuGetV3ServiceIndex index,
            string query,
            bool includePreRelease,
            string? requiredPackageType
        )
        {
            string candidate = query.Trim();
            if (candidate.Length is 0)
                return [];

            List<(SemanticVersion Parsed, string Raw)> available = DescendingCandidates(
                GetVersions(index, candidate),
                includePreRelease,
                null
            );

            string? version = SelectNewestNotUnlisted(
                index,
                candidate,
                available,
                acceptUnresolvedStatus: true,
                out _
            );
            if (version is null)
                return [];

            if (!string.IsNullOrEmpty(requiredPackageType))
            {
                V3CatalogEntry? entry = GetNuspecMetadata(index, candidate, version);
                if (entry is null || !entry.HasPackageType(requiredPackageType))
                {
                    Logger.Debug(
                        $"Discarding exact-id match {candidate} {version}: it does not advertise "
                            + $"the {requiredPackageType} package type"
                    );
                    return [];
                }
            }

            return
            [
                new V3SearchResult
                {
                    Id = candidate,
                    Version = version,
                    IsExactIdFallback = true,
                },
            ];
        }

        public static IReadOnlyList<string> GetVersions(
            NuGetV3ServiceIndex index,
            string packageId
        ) => GetVersions(index, packageId, out _);

        public static IReadOnlyList<string> GetVersions(
            NuGetV3ServiceIndex index,
            string packageId,
            out bool requestFailed
        )
        {
            requestFailed = false;

            if (index.PackageBaseAddress is null)
            {
                Logger.Warn(
                    $"The feed at {index.ServiceIndexUrl} advertises no PackageBaseAddress, "
                        + $"so the versions of {packageId} cannot be enumerated"
                );
                requestFailed = true;
                return [];
            }

            string cacheKey = $"{index.PackageBaseAddress.AbsoluteUri}|{packageId.ToLowerInvariant()}";
            if (
                VersionCache.TryGetValue(cacheKey, out var cached)
                && DateTime.UtcNow - cached.FetchedAt < VersionCacheLifetime
            )
                return cached.Versions;

            if (
                !Uri.TryCreate(
                    $"{index.PackageBaseAddress.AbsoluteUri.TrimEnd('/')}/{EscapeId(packageId)}/index.json",
                    UriKind.Absolute,
                    out Uri? versionsUrl
                )
            )
                return [];

            if (!TryDownloadString(versionsUrl, out string content))
            {
                requestFailed = true;
                return [];
            }

            try
            {
                V3FlatContainerIndex? parsed = NuGetV3Json.DeserializeFlatContainerIndex(content);
                IReadOnlyList<string> versions = parsed?.Versions is { Count: > 0 }
                    ? parsed.Versions
                    : [];

                VersionCache[cacheKey] = (DateTime.UtcNow, versions);
                return versions;
            }
            catch (Exception e)
            {
                Logger.Warn($"Malformed NuGet V3 version index at Url={versionsUrl}");
                Logger.Warn(e);
                requestFailed = true;
                return [];
            }
        }

        public static IReadOnlyList<string> GetVersionsDescending(
            NuGetV3ServiceIndex index,
            string packageId
        )
        {
            List<(SemanticVersion Parsed, string Raw)> parsed = [];
            foreach (string version in GetVersions(index, packageId))
            {
                parsed.Add(
                    TryParseNuGetVersion(version, out SemanticVersion semVer)
                        ? (semVer, version)
                        : (SemanticVersion.Invalid(version), version)
                );
            }

            parsed.Sort((left, right) => right.Parsed.CompareTo(left.Parsed));
            return parsed.Select(entry => entry.Raw).ToArray();
        }

        public static string? SelectHighestVersion(
            IEnumerable<string> versions,
            bool includePreRelease
        )
        {
            string? best = null;
            SemanticVersion bestParsed = default;

            foreach (string version in versions)
            {
                if (!TryParseNuGetVersion(version, out SemanticVersion parsed))
                    continue;
                if (parsed.IsPreRelease && !includePreRelease)
                    continue;

                if (best is null || parsed > bestParsed)
                {
                    best = version;
                    bestParsed = parsed;
                }
            }

            return best;
        }

        public static string? GetUpdateCandidate(
            NuGetV3ServiceIndex index,
            string packageId,
            string installedVersion,
            bool includePreRelease
        ) => GetUpdateCandidate(index, packageId, installedVersion, includePreRelease, out _);

        public static string? GetUpdateCandidate(
            NuGetV3ServiceIndex index,
            string packageId,
            string installedVersion,
            bool includePreRelease,
            out bool requestFailed
        )
        {
            requestFailed = false;

            if (!TryParseNuGetVersion(installedVersion, out SemanticVersion installed))
                return null;

            IReadOnlyList<string> allVersions = GetVersions(
                index,
                packageId,
                out bool versionsFailed
            );
            if (versionsFailed)
            {
                requestFailed = true;
                return null;
            }

            List<(SemanticVersion Parsed, string Raw)> newer = DescendingCandidates(
                allVersions,
                includePreRelease,
                installed
            );

            string? selected = SelectNewestNotUnlisted(
                index,
                packageId,
                newer,
                acceptUnresolvedStatus: false,
                out bool statusUnresolved
            );

            if (statusUnresolved)
                requestFailed = true;

            return selected;
        }

        private static List<(SemanticVersion Parsed, string Raw)> DescendingCandidates(
            IEnumerable<string> versions,
            bool includePreRelease,
            SemanticVersion? mustExceed
        )
        {
            List<(SemanticVersion Parsed, string Raw)> candidates = [];
            foreach (string version in versions)
            {
                if (!TryParseNuGetVersion(version, out SemanticVersion parsed))
                    continue;
                if (parsed.IsPreRelease && !includePreRelease)
                    continue;
                if (mustExceed is { } floor && parsed <= floor)
                    continue;

                candidates.Add((parsed, version));
            }

            candidates.Sort((left, right) => right.Parsed.CompareTo(left.Parsed));
            return candidates;
        }

        /// <summary>
        /// Returns the newest candidate that is not known to be unlisted. The flat container
        /// lists withdrawn versions, so every selection made from it goes through here.
        /// A feed advertising no registration resource cannot answer at all and its newest
        /// candidate is taken as-is. When the status of a candidate cannot be established,
        /// update selection reports the check as failed rather than guessing, while an
        /// exact-id search - where the user named the package explicitly and there is no
        /// failure channel - shows it instead of hiding it.
        /// </summary>
        private static string? SelectNewestNotUnlisted(
            NuGetV3ServiceIndex index,
            string packageId,
            List<(SemanticVersion Parsed, string Raw)> descending,
            bool acceptUnresolvedStatus,
            out bool statusUnresolved
        )
        {
            statusUnresolved = false;

            if (descending.Count is 0)
                return null;

            if (index.RegistrationsBaseUrl is null)
                return descending[0].Raw;

            foreach ((SemanticVersion _, string raw) in descending)
            {
                switch (GetListedStatus(index, packageId, raw))
                {
                    case ListedStatus.Listed:
                        return raw;

                    case ListedStatus.Unlisted:
                        Logger.Debug($"Skipping unlisted NuGet version {packageId} {raw}");
                        continue;

                    default:
                        if (acceptUnresolvedStatus)
                            return raw;

                        Logger.Warn(
                            $"Could not establish the listed status of {packageId} {raw}; "
                                + "reporting the update check for this package as failed"
                        );
                        statusUnresolved = true;
                        return null;
                }
            }

            return null;
        }

        public static ListedStatus GetListedStatus(
            NuGetV3ServiceIndex index,
            string packageId,
            string version
        )
        {
            if (index.RegistrationsBaseUrl is null)
                return ListedStatus.Unknown;

            V3RegistrationLeaf? leaf = GetRegistrationLeaf(index, packageId, version);
            if (leaf is null)
                return ListedStatus.Unknown;

            return leaf.Listed switch
            {
                false => ListedStatus.Unlisted,
                _ => ListedStatus.Listed,
            };
        }

        private static V3RegistrationLeaf? GetRegistrationLeaf(
            NuGetV3ServiceIndex index,
            string packageId,
            string version
        )
        {
            if (GetRegistrationLeafUrl(index, packageId, version) is not { } leafUrl)
                return null;

            string cacheKey = leafUrl.AbsoluteUri;
            if (
                LeafCache.TryGetValue(cacheKey, out var cached)
                && DateTime.UtcNow - cached.FetchedAt < VersionCacheLifetime
            )
                return cached.Leaf;

            if (!TryDownloadString(leafUrl, out string content))
                return null;

            try
            {
                V3RegistrationLeaf? leaf = NuGetV3Json.DeserializeRegistrationLeaf(content);
                LeafCache[cacheKey] = (DateTime.UtcNow, leaf);
                return leaf;
            }
            catch (Exception e)
            {
                Logger.Warn($"Malformed NuGet V3 registration leaf at Url={leafUrl}");
                Logger.Warn(e);
                return null;
            }
        }

        public static V3CatalogEntry? GetCatalogEntry(
            NuGetV3ServiceIndex index,
            string packageId,
            string version
        )
        {
            try
            {
                V3RegistrationLeaf? leaf = GetRegistrationLeaf(index, packageId, version);
                if (leaf is null)
                    return GetNuspecMetadata(index, packageId, version);

                V3CatalogEntry? inline = NuGetV3Json.DeserializeCatalogEntry(leaf.CatalogEntry);
                if (inline is not null)
                {
                    inline.PackageContent ??= leaf.PackageContent;
                    inline.Published ??= leaf.Published;
                    inline.Listed ??= leaf.Listed;
                    return inline;
                }

                if (
                    leaf.CatalogEntry.ValueKind is System.Text.Json.JsonValueKind.String
                    && Uri.TryCreate(
                        leaf.CatalogEntry.GetString(),
                        UriKind.Absolute,
                        out Uri? catalogUrl
                    )
                    && TryDownloadString(catalogUrl, out string catalogContent)
                )
                {
                    V3CatalogEntry? entry = NuGetV3Json.DeserializeCatalogEntry(catalogContent);
                    if (entry is not null)
                    {
                        entry.PackageContent ??= leaf.PackageContent;
                        entry.Published ??= leaf.Published;
                        entry.Listed ??= leaf.Listed;
                    }

                    return entry;
                }

                return GetNuspecMetadata(index, packageId, version);
            }
            catch (Exception e)
            {
                Logger.Warn(
                    $"Malformed NuGet V3 package metadata for {packageId} {version} on feed "
                        + index.ServiceIndexUrl
                );
                Logger.Warn(e);
                return null;
            }
        }

        public static Uri? GetNuspecUrl(
            NuGetV3ServiceIndex index,
            string packageId,
            string version
        )
        {
            if (index.PackageBaseAddress is null)
                return null;

            string id = EscapeId(packageId);
            string url =
                $"{index.PackageBaseAddress.AbsoluteUri.TrimEnd('/')}"
                + $"/{id}/{EscapeVersion(version)}/{id}.nuspec";

            return Uri.TryCreate(url, UriKind.Absolute, out Uri? nuspecUrl) ? nuspecUrl : null;
        }

        internal static V3CatalogEntry? GetNuspecMetadata(
            NuGetV3ServiceIndex index,
            string packageId,
            string version
        )
        {
            if (GetNuspecUrl(index, packageId, version) is not { } nuspecUrl)
                return null;

            if (!TryDownloadString(nuspecUrl, out string content))
                return null;

            try
            {
                return ParseNuspec(content, packageId, version, GetPackageContentUrl(index, packageId, version));
            }
            catch (Exception e)
            {
                Logger.Warn($"Malformed NuGet nuspec at Url={nuspecUrl}");
                Logger.Warn(e);
                return null;
            }
        }

        internal static V3CatalogEntry? ParseNuspec(
            string content,
            string packageId,
            string version,
            Uri? packageContent
        )
        {
            XElement? metadata = XDocument.Parse(content).Root?.Elements()
                .FirstOrDefault(element =>
                    element.Name.LocalName.Equals("metadata", StringComparison.OrdinalIgnoreCase)
                );

            if (metadata is null)
                return null;

            string? Value(string name) =>
                metadata
                    .Elements()
                    .FirstOrDefault(element =>
                        element.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase)
                    )
                    ?.Value.Trim() is { Length: > 0 } value
                    ? value
                    : null;

            var entry = new V3CatalogEntry
            {
                Id = Value("id") ?? packageId,
                Version = Value("version") ?? version,
                Description = Value("description"),
                Summary = Value("summary"),
                LicenseUrl = Value("licenseUrl"),
                ProjectUrl = Value("projectUrl"),
                ReleaseNotes = Value("releaseNotes"),
                IconUrl = Value("iconUrl"),
                IconFile = Value("icon"),
                PackageContent = packageContent?.AbsoluteUri,
            };

            if (Value("license") is { } license)
                entry.LicenseExpression = license;

            if (Value("authors") is { } authors)
                entry.AuthorsOverride = authors;

            if (Value("tags") is { } tags)
                entry.TagsOverride = tags.Split(
                    ' ',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                );

            XElement? packageTypes = metadata
                .Elements()
                .FirstOrDefault(element =>
                    element.Name.LocalName.Equals(
                        "packageTypes",
                        StringComparison.OrdinalIgnoreCase
                    )
                );

            if (packageTypes is not null)
            {
                List<V3PackageType> types = [];
                foreach (XElement packageType in packageTypes.Elements())
                {
                    if (
                        !packageType.Name.LocalName.Equals(
                            "packageType",
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                        continue;

                    string? name = packageType.Attribute("name")?.Value;
                    if (!string.IsNullOrWhiteSpace(name))
                        types.Add(new V3PackageType { Name = name });
                }

                if (types.Count > 0)
                    entry.PackageTypes = types;
            }

            XElement? dependencies = metadata
                .Elements()
                .FirstOrDefault(element =>
                    element.Name.LocalName.Equals(
                        "dependencies",
                        StringComparison.OrdinalIgnoreCase
                    )
                );

            if (dependencies is not null)
            {
                List<V3Dependency> flat = [];
                foreach (XElement dependency in dependencies.Descendants())
                {
                    if (
                        !dependency.Name.LocalName.Equals(
                            "dependency",
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                        continue;

                    string? id = dependency.Attribute("id")?.Value;
                    if (string.IsNullOrWhiteSpace(id))
                        continue;

                    flat.Add(
                        new V3Dependency { Id = id, Range = dependency.Attribute("version")?.Value }
                    );
                }

                if (flat.Count > 0)
                    entry.DependencyGroups =
                    [
                        new V3DependencyGroup { Dependencies = flat },
                    ];
            }

            return entry;
        }

        public static Uri? GetRegistrationLeafUrl(
            NuGetV3ServiceIndex index,
            string packageId,
            string version
        )
        {
            if (index.RegistrationsBaseUrl is null)
                return null;

            string url =
                $"{index.RegistrationsBaseUrl.AbsoluteUri.TrimEnd('/')}"
                + $"/{EscapeId(packageId)}/{EscapeVersion(version)}.json";

            return Uri.TryCreate(url, UriKind.Absolute, out Uri? leafUrl) ? leafUrl : null;
        }

        internal static bool SupportsEmbeddedIconRoute(Uri packageBaseAddress) =>
            packageBaseAddress.Host.Equals("nuget.org", StringComparison.OrdinalIgnoreCase)
            || packageBaseAddress.Host.EndsWith(".nuget.org", StringComparison.OrdinalIgnoreCase);

        public static Uri? GetEmbeddedIconUrl(
            NuGetV3ServiceIndex index,
            string packageId,
            string version
        )
        {
            if (
                index.PackageBaseAddress is not { } packageBase
                || !SupportsEmbeddedIconRoute(packageBase)
            )
                return null;

            string url =
                $"{packageBase.AbsoluteUri.TrimEnd('/')}"
                + $"/{EscapeId(packageId)}/{EscapeVersion(version)}/icon";

            return Uri.TryCreate(url, UriKind.Absolute, out Uri? iconUrl) ? iconUrl : null;
        }

        public static Uri? GetPackageContentUrl(
            NuGetV3ServiceIndex index,
            string packageId,
            string version
        )
        {
            if (index.PackageBaseAddress is null)
                return null;

            string id = EscapeId(packageId);
            string normalized = EscapeVersion(version);
            string url =
                $"{index.PackageBaseAddress.AbsoluteUri.TrimEnd('/')}"
                + $"/{id}/{normalized}/{id}.{normalized}.nupkg";

            return Uri.TryCreate(url, UriKind.Absolute, out Uri? contentUrl) ? contentUrl : null;
        }

        internal static bool TryParseNuGetVersion(string? value, out SemanticVersion version) =>
            SemanticVersion.TryParse(value, SemVerLabels.CaseInsensitive, out version);

        internal static string EscapeId(string packageId) =>
            Uri.EscapeDataString(packageId.Trim().ToLowerInvariant());

        internal static string EscapeVersion(string version) =>
            Uri.EscapeDataString(NormalizeVersion(version));

        internal static string NormalizeVersion(string version)
        {
            string candidate = version.Trim();
            if (candidate.Length is 0)
                return candidate;

            int metadataIndex = candidate.IndexOf('+');
            if (metadataIndex >= 0)
                candidate = candidate[..metadataIndex];

            string labels = string.Empty;
            int labelIndex = candidate.IndexOf('-');
            if (labelIndex >= 0)
            {
                labels = candidate[labelIndex..];
                candidate = candidate[..labelIndex];
            }

            string[] parts = candidate.Split('.');
            if (parts.Length is 0 or > 4)
                return version.Trim().ToLowerInvariant();

            int[] numbers = [0, 0, 0, 0];
            for (int i = 0; i < parts.Length; i++)
            {
                if (
                    !int.TryParse(
                        parts[i],
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out int number
                    )
                )
                    return version.Trim().ToLowerInvariant();

                numbers[i] = number;
            }

            var normalized = new StringBuilder();
            normalized.Append(CultureInfo.InvariantCulture, $"{numbers[0]}");
            normalized.Append(CultureInfo.InvariantCulture, $".{numbers[1]}");
            normalized.Append(CultureInfo.InvariantCulture, $".{numbers[2]}");
            if (numbers[3] is not 0)
                normalized.Append(CultureInfo.InvariantCulture, $".{numbers[3]}");

            normalized.Append(labels);
            return normalized.ToString().ToLowerInvariant();
        }
    }
}
