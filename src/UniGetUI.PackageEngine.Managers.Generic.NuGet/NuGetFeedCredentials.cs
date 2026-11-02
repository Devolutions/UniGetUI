using System.Net.Http.Headers;

namespace UniGetUI.PackageEngine.Managers.Generic.NuGet
{
    public static class NuGetFeedCredentials
    {
        private static IReadOnlyDictionary<string, AuthenticationHeaderValue> _byOrigin =
            new Dictionary<string, AuthenticationHeaderValue>();

        public static void Replace(IEnumerable<(Uri Source, AuthenticationHeaderValue Header)> credentials)
        {
            Dictionary<string, AuthenticationHeaderValue> byOrigin = [];
            foreach (var (source, header) in credentials)
                byOrigin.TryAdd(Origin(source), header);

            Volatile.Write(ref _byOrigin, byOrigin);
        }

        public static void ApplyTo(HttpRequestMessage request)
        {
            if (
                request.RequestUri is { IsAbsoluteUri: true } uri
                && Volatile.Read(ref _byOrigin).TryGetValue(Origin(uri), out var header)
            )
                request.Headers.Authorization = header;
        }

        private static string Origin(Uri uri) =>
            uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
    }
}
