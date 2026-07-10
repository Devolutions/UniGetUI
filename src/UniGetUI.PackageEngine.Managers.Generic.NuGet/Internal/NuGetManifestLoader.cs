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
        /// Returns the URL to the manifest of a NuGet-based package
        /// </summary>
        /// <param name="package">A valid Package object</param>
        /// <returns>A Uri object</returns>
        public static Uri GetManifestUrl(IPackage package)
        {
            return new Uri(
                $"{package.Source.Url}/Packages(Id='{package.Id}',Version='{package.VersionString}')"
            );
        }

        /// <summary>
        /// Returns the URL to the NuPkg file
        /// </summary>
        /// <param name="package">A valid Package object</param>
        /// <returns>A Uri object</returns>
        public static Uri GetNuPkgUrl(IPackage package)
        {
            return new Uri($"{package.Source.Url}/package/{package.Id}/{package.VersionString}");
        }

        /// <summary>
        /// Returns the contents of the manifest of a NuGet-based package
        /// </summary>
        /// <param name="package">The package for which to obtain the manifest</param>
        /// <returns>A string containing the contents of the manifest</returns>
        public static string? GetManifestContent(IPackage package)
        {
            if (BaseNuGet.Manifests.TryGetValue(package.GetHash(), out string? manifest))
            {
                Logger.Debug(
                    $"Loading cached NuGet manifest for package {package.Id} on manager {package.Manager.Name}"
                );
                return manifest;
            }

            string PackageManifestUrl = GetManifestUrl(package).ToString();

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

                    if (!initialResponse.IsSuccessStatusCode && package.VersionString.EndsWith(".0"))
                    {
                        using var fallbackRequest = new HttpRequestMessage(
                            HttpMethod.Get,
                            new Uri(PackageManifestUrl.ToString().Replace(".0')", "')"))
                        );
                        using HttpResponseMessage fallbackResponse = client.Send(fallbackRequest);
                        return CacheManifestContent(package, PackageManifestUrl, fallbackResponse);
                    }

                    return CacheManifestContent(package, PackageManifestUrl, initialResponse);
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
            IPackage package,
            string packageManifestUrl,
            HttpResponseMessage response
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
            BaseNuGet.Manifests[package.GetHash()] = packageManifestContent;
            return packageManifestContent;
        }
    }
}
