using System.Net.Http.Headers;

namespace UniGetUI.PackageEngine.Managers.Generic.NuGet
{
    public static class NuGetFeedCredentials
    {
        private const string ServiceIndexSuffix = "/index.json";

        private static IReadOnlyDictionary<string, AuthenticationHeaderValue> _byFeed =
            new Dictionary<string, AuthenticationHeaderValue>();

        public static void Replace(IEnumerable<(Uri Source, AuthenticationHeaderValue Header)> credentials)
        {
            Dictionary<string, AuthenticationHeaderValue> byFeed = [];
            foreach (var (source, header) in credentials)
                byFeed.TryAdd(FeedKey(source), header);

            Volatile.Write(ref _byFeed, byFeed);
        }

        public static void ApplyTo(HttpRequestMessage request, Uri? feed)
        {
            if (
                feed is { IsAbsoluteUri: true }
                && request.RequestUri is { IsAbsoluteUri: true } uri
                && Origin(uri) == Origin(feed)
                && Volatile.Read(ref _byFeed).TryGetValue(FeedKey(feed), out var header)
            )
                request.Headers.Authorization = header;
        }

        private static string FeedKey(Uri feed)
        {
            string path = feed.AbsolutePath.TrimEnd('/');
            if (path.EndsWith(ServiceIndexSuffix, StringComparison.OrdinalIgnoreCase))
                path = path[..^ServiceIndexSuffix.Length];

            return Origin(feed) + path;
        }

        private static string Origin(Uri uri) =>
            uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
    }
}
