using System.Collections.Concurrent;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Managers.PowerShellManager;

namespace UniGetUI.PackageEngine.Managers.Generic.NuGet.Internal
{
    internal static class NuGetManifestLoader
    {
        /// <summary>
        /// Cache key for the manifest and catalog caches. Reproduces the shape of
        /// IPackage.GetVersionedHash() so that an entry cached for a concrete package is
        /// reused when another package resolves to that same version, and so that a package
        /// whose listed version is a placeholder does not share one slot across versions.
        /// </summary>
        /// <param name="package">A valid Package object</param>
        /// <param name="version">The version to key on; the package's own when omitted</param>
        /// <returns>A cache key</returns>
        public static long GetCacheKey(IPackage package, string? version = null)
        {
            return CoreTools.HashStringAsLong(
                $"{package.Manager.Name}\\{package.Source.AsString_DisplayName}\\{package.Id}\\{version ?? package.VersionString}"
            );
        }

        /// <summary>
        /// Returns the URL to the manifest of a NuGet-based package
        /// </summary>
        /// <param name="package">A valid Package object</param>
        /// <param name="version">The version to address; the package's own when omitted</param>
        /// <returns>A Uri object</returns>
        public static Uri GetManifestUrl(IPackage package, string? version = null)
        {
            version ??= package.VersionString;
            // A package without a version, such as an unpinned Software Catalog entry, gets the
            // newest listed one; "Version=''" matches nothing.
            if (version.Length == 0)
                return new Uri(
                    $"{package.Source.Url}/Packages()?$filter=Id eq '{Uri.EscapeDataString(package.Id)}' and IsLatestVersion"
                );
            return new Uri($"{package.Source.Url}/Packages(Id='{package.Id}',Version='{version}')");
        }

        /// <summary>
        /// Returns the URL to the NuPkg file
        /// </summary>
        /// <param name="package">A valid Package object</param>
        /// <returns>A Uri object</returns>
        public static Uri GetNuPkgUrl(IPackage package)
        {
            if (NuGetV3ServiceIndex.IsV3Source(package.Source))
            {
                if (
                    NuGetV3ServiceIndex.Resolve(package.Source) is { } index
                    && NuGetV3Client.GetPackageContentUrl(
                        index,
                        package.Id,
                        package.VersionString
                    )
                        is { } contentUrl
                )
                    return contentUrl;

                throw new InvalidOperationException(
                    $"Could not resolve a V3 package content address for {package.Id} "
                        + $"{package.VersionString} on source {package.Source.Url}"
                );
            }

            return new Uri($"{package.Source.Url}/package/{package.Id}/{package.VersionString}");
        }

        /// <summary>
        /// Returns the contents of the manifest of a NuGet-based package
        /// </summary>
        /// <param name="package">The package for which to obtain the manifest</param>
        /// <param name="version">The version to fetch; the package's own when omitted</param>
        /// <returns>A string containing the contents of the manifest</returns>
        // A version's manifest never changes, but "the latest" does when a version is published, so
        // the version-less one is kept only for a while.
        internal static readonly ConcurrentDictionary<long, (DateTime FetchedUtc, string Content)> LatestManifests = new();
        internal static readonly TimeSpan LatestManifestLifetime = TimeSpan.FromHours(1);

        public static string? GetManifestContent(IPackage package, string? version = null)
        {
            version ??= package.VersionString;
            long cacheKey = GetCacheKey(package, version);
            if (version.Length == 0)
            {
                if (LatestManifests.TryGetValue(cacheKey, out var latest)
                    && DateTime.UtcNow - latest.FetchedUtc < LatestManifestLifetime)
                    return latest.Content;
            }
            else if (BaseNuGet.Manifests.TryGetValue(cacheKey, out string? manifest))
            {
                Logger.Debug(
                    $"Loading cached NuGet manifest for package {package.Id} on manager {package.Manager.Name}"
                );
                return manifest;
            }

            string PackageManifestUrl = GetManifestUrl(package, version).ToString();

            try
            {
                using (HttpClient client = new(CoreTools.GenericHttpClientParameters))
                {
                    client.DefaultRequestHeaders.UserAgent.ParseAdd(CoreData.UserAgentString);
                    using var initialRequest = new HttpRequestMessage(
                        HttpMethod.Get,
                        PackageManifestUrl
                    );
                    using HttpResponseMessage initialResponse = client.Send(initialRequest);

                    if (!initialResponse.IsSuccessStatusCode && version.EndsWith(".0"))
                    {
                        using var fallbackRequest = new HttpRequestMessage(
                            HttpMethod.Get,
                            new Uri(PackageManifestUrl.ToString().Replace(".0')", "')"))
                        );
                        using HttpResponseMessage fallbackResponse = client.Send(fallbackRequest);
                        return CacheManifestContent(cacheKey, package, PackageManifestUrl, fallbackResponse, latest: false);
                    }

                    return CacheManifestContent(cacheKey, package, PackageManifestUrl, initialResponse, latest: version.Length == 0);
                }
            }
            catch (Exception e)
            {
                Logger.Warn(
                    $"Failed to download the {package.Manager.Name} manifest at Url={PackageManifestUrl.ToString()}"
                );
                Logger.Warn(e);
                return null;
            }
        }

        private static string? CacheManifestContent(
            long cacheKey,
            IPackage package,
            string packageManifestUrl,
            HttpResponseMessage response,
            bool latest
        )
        {
            if (!response.IsSuccessStatusCode)
            {
                Logger.Warn(
                    $"Failed to download the {package.Manager.Name} manifest at Url={packageManifestUrl} with status code {response.StatusCode}"
                );
                return null;
            }

            string packageManifestContent = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (latest)
                LatestManifests[cacheKey] = (DateTime.UtcNow, packageManifestContent);
            else
                BaseNuGet.Manifests[cacheKey] = packageManifestContent;
            return packageManifestContent;
        }
    }
}
