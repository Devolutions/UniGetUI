using System.Collections.Concurrent;
using System.Text.Json;
using UniGetUI.Core.Logging;
using UniGetUI.PackageEngine.Interfaces;

namespace UniGetUI.PackageEngine.Managers.Generic.NuGet.Internal
{
    internal sealed class NuGetV3ServiceIndex
    {
        private static readonly string[] SearchQueryServiceTypes =
        [
            "SearchQueryService/3.5.0",
            "SearchQueryService/3.0.0-rc",
            "SearchQueryService/3.0.0-beta",
            "SearchQueryService",
        ];

        private static readonly string[] RegistrationsBaseUrlTypes =
        [
            "RegistrationsBaseUrl/3.6.0",
            "RegistrationsBaseUrl/3.4.0",
            "RegistrationsBaseUrl/3.0.0-rc",
            "RegistrationsBaseUrl/3.0.0-beta",
            "RegistrationsBaseUrl",
        ];

        private static readonly string[] PackageBaseAddressTypes =
        [
            "PackageBaseAddress/3.0.0",
            "PackageBaseAddress",
        ];

        private static readonly ConcurrentDictionary<string, NuGetV3ServiceIndex> ResolvedIndexes =
            new();

        public Uri ServiceIndexUrl { get; }
        public Uri? SearchQueryService { get; }
        public Uri? RegistrationsBaseUrl { get; }
        public Uri? PackageBaseAddress { get; }

        private NuGetV3ServiceIndex(
            Uri serviceIndexUrl,
            Uri? searchQueryService,
            Uri? registrationsBaseUrl,
            Uri? packageBaseAddress
        )
        {
            ServiceIndexUrl = serviceIndexUrl;
            SearchQueryService = searchQueryService;
            RegistrationsBaseUrl = registrationsBaseUrl;
            PackageBaseAddress = packageBaseAddress;
        }

        public static bool IsV3Source(IManagerSource? source) =>
            GetServiceIndexUrl(source) is not null;

        internal static Uri? GetServiceIndexUrl(IManagerSource? source)
        {
            if (source?.Url is not { IsAbsoluteUri: true } url)
                return null;

            string path = url.AbsolutePath.TrimEnd('/');

            if (path.EndsWith("/index.json", StringComparison.OrdinalIgnoreCase))
                return new Uri(url.GetLeftPart(UriPartial.Path).TrimEnd('/'));

            if (path.EndsWith("/v3", StringComparison.OrdinalIgnoreCase))
                return new Uri($"{url.GetLeftPart(UriPartial.Path).TrimEnd('/')}/index.json");

            return null;
        }

        public static NuGetV3ServiceIndex? Resolve(IManagerSource? source)
        {
            if (GetServiceIndexUrl(source) is not { } serviceIndexUrl)
                return null;

            string key = NormalizeCacheKey(serviceIndexUrl);
            if (ResolvedIndexes.TryGetValue(key, out NuGetV3ServiceIndex? cached))
                return cached;

            NuGetV3ServiceIndex? resolved = Fetch(serviceIndexUrl);
            if (resolved is not null)
                ResolvedIndexes[key] = resolved;

            return resolved;
        }

        private static string NormalizeCacheKey(Uri serviceIndexUrl) =>
            $"{serviceIndexUrl.Scheme.ToLowerInvariant()}://"
            + $"{serviceIndexUrl.Authority.ToLowerInvariant()}"
            + serviceIndexUrl.AbsolutePath.TrimEnd('/');

        internal static void ClearCache() => ResolvedIndexes.Clear();

        private static NuGetV3ServiceIndex? Fetch(Uri serviceIndexUrl)
        {
            if (!NuGetV3Client.TryDownloadString(serviceIndexUrl, out string content))
            {
                Logger.Warn($"Could not load the NuGet V3 service index at Url={serviceIndexUrl}");
                return null;
            }

            V3ServiceIndex? index;
            try
            {
                index = NuGetV3Json.DeserializeServiceIndex(content);
            }
            catch (Exception e)
            {
                Logger.Warn($"Malformed NuGet V3 service index at Url={serviceIndexUrl}");
                Logger.Warn(e);
                return null;
            }

            if (index?.Resources is not { Count: > 0 })
            {
                Logger.Warn(
                    $"The NuGet V3 service index at Url={serviceIndexUrl} advertises no resources"
                );
                return null;
            }

            Dictionary<string, string> resourcesByType = new(StringComparer.OrdinalIgnoreCase);
            foreach (V3Resource resource in index.Resources)
            {
                if (string.IsNullOrWhiteSpace(resource.Id))
                    continue;

                foreach (string type in ReadTypes(resource.Type))
                    resourcesByType.TryAdd(type, resource.Id);
            }

            Uri? search = SelectResource(resourcesByType, SearchQueryServiceTypes);
            Uri? registration = SelectResource(resourcesByType, RegistrationsBaseUrlTypes);
            Uri? packageBase = SelectResource(resourcesByType, PackageBaseAddressTypes);

            if (packageBase is null && registration is null)
            {
                Logger.Warn(
                    $"The NuGet V3 service index at Url={serviceIndexUrl} exposes neither "
                        + "PackageBaseAddress nor RegistrationsBaseUrl"
                );
                return null;
            }

            Logger.Debug(
                $"Resolved NuGet V3 service index at Url={serviceIndexUrl} "
                    + $"(search={search}, registration={registration}, content={packageBase})"
            );

            return new NuGetV3ServiceIndex(serviceIndexUrl, search, registration, packageBase);
        }

        private static IEnumerable<string> ReadTypes(JsonElement type)
        {
            switch (type.ValueKind)
            {
                case JsonValueKind.String:
                    string? single = type.GetString();
                    if (!string.IsNullOrWhiteSpace(single))
                        yield return single;
                    break;

                case JsonValueKind.Array:
                    foreach (JsonElement item in type.EnumerateArray())
                    {
                        if (item.ValueKind is not JsonValueKind.String)
                            continue;

                        string? value = item.GetString();
                        if (!string.IsNullOrWhiteSpace(value))
                            yield return value;
                    }
                    break;
            }
        }

        private static Uri? SelectResource(
            IReadOnlyDictionary<string, string> resourcesByType,
            IReadOnlyList<string> preferredTypes
        )
        {
            foreach (string type in preferredTypes)
            {
                if (
                    resourcesByType.TryGetValue(type, out string? id)
                    && Uri.TryCreate(id, UriKind.Absolute, out Uri? uri)
                )
                    return uri;
            }

            return null;
        }
    }
}
