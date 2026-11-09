using System.Text.Json.Nodes;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.ManagerClasses.Classes;

namespace UniGetUI.PackageEngine.Managers.Generic.PyPI
{
    /// <summary>
    /// In-memory and on-disk cache of all PyPI package names, plus the local ranking used to
    /// turn a search query into a bounded list of matches. Shared by every manager backed by
    /// PyPI so each of them ranks and caps results the same way.
    /// </summary>
    internal static class PyPIIndexClient
    {
        private const string SimpleIndexUrl = "https://pypi.org/simple/";
        public const int MaxSearchResults = 20;

        private const int CacheMaxAgeHours = 24;

        private static string[]? _cachedNames;
        private static DateTime _cacheTimestamp = DateTime.MinValue;
        private static readonly object _cacheLock = new();

        internal static string GetCacheFilePath() =>
            Path.Join(CoreData.UniGetUICacheDirectory_Data, "pypi_simple_index.cache");

        /// <summary>
        /// Returns every project name on the simple index, from the in-memory cache, the file
        /// cache or a fresh ~38 MB download, in that order of preference.
        /// </summary>
        /// <exception cref="HttpRequestException">The fresh download failed.</exception>
        /// <exception cref="InvalidDataException">The fresh download was malformed.</exception>
        internal static string[] GetProjectNames(INativeTaskLogger? logger = null)
        {
            lock (_cacheLock)
            {
                if (_cachedNames is not null && (DateTime.Now - _cacheTimestamp).TotalHours < CacheMaxAgeHours)
                    return _cachedNames;
            }

            LogOrForget(logger, "Loading PyPI index");

            string cacheFile = GetCacheFilePath();
            if (File.Exists(cacheFile) && (DateTime.Now - File.GetLastWriteTime(cacheFile)).TotalHours < CacheMaxAgeHours)
            {
                LogOrForget(logger, $"Loading PyPI index from file cache ({File.GetLastWriteTime(cacheFile):g})");
                string[] cached = File.ReadAllLines(cacheFile);
                if (cached.Length > 0)
                {
                    lock (_cacheLock) { _cachedNames = cached; _cacheTimestamp = File.GetLastWriteTime(cacheFile); }
                    return cached;
                }
                LogOrForget(logger, "PyPI index file cache was empty, re-downloading...", message => Logger.Error(message));
            }

            LogOrForget(logger, "Downloading PyPI simple index (one-time ~38 MB download, cached for 24 h)...");

            using HttpClient client = new(CoreTools.GenericHttpClientParameters);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(CoreData.UserAgentString);
            client.DefaultRequestHeaders.Add("Accept", "application/vnd.pypi.simple.v1+json");

            using var request = new HttpRequestMessage(HttpMethod.Get, SimpleIndexUrl);
            using HttpResponseMessage response = client.Send(request);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"PyPI simple index returned {(int)response.StatusCode} {response.ReasonPhrase}");

            string json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            string[] names = ParseSimpleIndexProjectNames(json);

            LogOrForget(logger, $"Downloaded {names.Length} package names from PyPI");

            lock (_cacheLock) { _cachedNames = names; _cacheTimestamp = DateTime.Now; }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
                File.WriteAllLines(cacheFile, names);
            }
            catch (Exception e)
            {
                LogOrForget(logger, $"Could not write PyPI index file cache to {cacheFile}: {e.Message}", message => Logger.Error(message));
            }

            return names;
        }

        /// <summary>
        /// Downloads the index in the background so the first search does not have to wait for
        /// the ~38 MB download inside the search timeout window.
        /// </summary>
        internal static Task PreWarmIndexAsync()
        {
            return Task.Run(() =>
            {
                try
                {
                    _ = GetProjectNames();
                    Logger.Info("PyPI: the simple index is cached and ready");
                }
                catch (Exception e)
                {
                    Logger.Warn($"PyPI: background index pre-warm failed: {e.Message}");
                }
            });
        }

        private static void LogOrForget(INativeTaskLogger? logger, string message) =>
            logger?.Log(message);

        private static void LogOrForget(INativeTaskLogger? logger, string message, Action<string> fallback)
        {
            if (logger is not null)
                logger.Log(message);
            else
                fallback(message);
        }

        /// <summary>
        /// Splits project-name entries out of a PEP 691 simple index JSON document. An index
        /// that parses but yields zero names is malformed, not an empty feed.
        /// </summary>
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

        /// <summary>
        /// Ranks index names against a query: prefix matches first, then by length, capped at
        /// <see cref="MaxSearchResults"/> entries.
        /// </summary>
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
    }
}
