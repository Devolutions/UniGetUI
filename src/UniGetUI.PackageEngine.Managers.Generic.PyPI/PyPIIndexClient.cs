using System.Text;
using System.Text.Json;
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
        private const string LegacyCacheFileName = "pip_simple_index.cache";

        // The index is close to a million names (tens of MB once loaded), so it is only kept
        // while searches are being made: it is dropped after MemoryCacheIdleLifetime without
        // one, and read back from the file cache by the next search.
        private static string[]? _cachedNames;
        private static DateTime _cacheTimestamp = DateTime.MinValue;
        private static readonly object _cacheLock = new();
        private static long _cacheUseStamp;

        // Pip and uv both start by pre-warming the index, so two loads can overlap. They share
        // one task instead of downloading and parsing the index twice and writing the cache
        // file twice.
        private static Task<string[]>? _inflight;

        internal static TimeSpan MemoryCacheIdleLifetime { get; set; } = TimeSpan.FromMinutes(2);

        internal static string? TEST_CacheFilePathOverride { get; set; }

        internal static Func<string[]>? TEST_IndexDownloadOverride { get; set; }

        internal static void TEST_ResetState()
        {
            lock (_cacheLock)
            {
                _cachedNames = null;
                _cacheTimestamp = DateTime.MinValue;
                _inflight = null;
            }
        }

        internal static string GetCacheFilePath() =>
            TEST_CacheFilePathOverride
            ?? Path.Join(CoreData.UniGetUICacheDirectory_Data, "pypi_simple_index.cache");

        private static bool IsIndexFileFresh(string cacheFile) =>
            File.Exists(cacheFile)
            && (DateTime.Now - File.GetLastWriteTime(cacheFile)).TotalHours < CacheMaxAgeHours;

        /// <summary>
        /// Returns every project name on the simple index, from the in-memory cache, the file
        /// cache or a fresh ~38 MB download, in that order of preference.
        /// </summary>
        /// <exception cref="HttpRequestException">The fresh download failed.</exception>
        /// <exception cref="InvalidDataException">The fresh download was malformed.</exception>
        internal static string[] GetProjectNames(INativeTaskLogger? logger = null)
        {
            // A use starts here: a release scheduled by an earlier search must not drop the
            // index this search is about to read.
            lock (_cacheLock) { _cacheUseStamp++; }
            try
            {
                return GetProjectNamesCore(logger);
            }
            finally
            {
                ScheduleIndexRelease();
            }
        }

        // Drops the in-memory index once no search has used it for MemoryCacheIdleLifetime.
        // The use stamp is only changed and compared under _cacheLock, so a release that has
        // been overtaken by a newer use can never clear the index.
        internal static void ScheduleIndexRelease()
        {
            long stamp;
            lock (_cacheLock) { stamp = ++_cacheUseStamp; }
            TimeSpan lifetime = MemoryCacheIdleLifetime;
            _ = Task.Run(async () =>
            {
                await Task.Delay(lifetime).ConfigureAwait(false);
                lock (_cacheLock)
                {
                    if (stamp == _cacheUseStamp)
                        _cachedNames = null;
                }
            });
        }

        private static string[] GetProjectNamesCore(INativeTaskLogger? logger)
        {
            Task<string[]> load;
            lock (_cacheLock)
            {
                if (_cachedNames is not null && (DateTime.Now - _cacheTimestamp).TotalHours < CacheMaxAgeHours)
                    return _cachedNames;

                if (_inflight is null)
                {
                    _inflight = Task.Run(() => LoadProjectNames(logger));
                }
                else
                {
                    logger?.Log("Waiting for the PyPI index another manager is loading");
                }
                load = _inflight;
            }

            return load.GetAwaiter().GetResult();
        }

        /// <summary>
        /// Reads the index from the file cache or downloads it, and leaves the in-flight slot
        /// empty whether it succeeded or not, so a failed load is retried by the next search.
        /// </summary>
        private static string[] LoadProjectNames(INativeTaskLogger? logger)
        {
            try
            {
                logger?.Log("Loading PyPI index");

                string cacheFile = GetCacheFilePath();
                MigrateLegacyCache(cacheFile, logger);

                // Use file cache if fresh enough
                if (IsIndexFileFresh(cacheFile))
                {
                    logger?.Log($"Loading PyPI index from file cache ({File.GetLastWriteTime(cacheFile):g})");
                    string[] cached = File.ReadAllLines(cacheFile);
                    if (cached.Length > 0)
                    {
                        lock (_cacheLock) { _cachedNames = cached; _cacheTimestamp = File.GetLastWriteTime(cacheFile); }
                        return cached;
                    }
                    LogError(logger, "PyPI index file cache was empty, re-downloading...");
                }

                string[] names = TEST_IndexDownloadOverride is { } download
                    ? download()
                    : DownloadProjectNames(logger);

                logger?.Log($"Downloaded {names.Length} package names from PyPI");

                lock (_cacheLock) { _cachedNames = names; _cacheTimestamp = DateTime.Now; }

                WriteCacheFile(cacheFile, names, logger);

                return names;
            }
            finally
            {
                lock (_cacheLock) { _inflight = null; }
            }
        }

        private static string[] DownloadProjectNames(INativeTaskLogger? logger)
        {
            logger?.Log("Downloading PyPI simple index (one-time ~38 MB download, cached for 24 h)...");

            using HttpClient client = new(CoreTools.GenericHttpClientParameters);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(CoreData.UserAgentString);
            client.DefaultRequestHeaders.Add("Accept", "application/vnd.pypi.simple.v1+json");

            using var request = new HttpRequestMessage(HttpMethod.Get, SimpleIndexUrl);
            using HttpResponseMessage response = client.Send(request);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"PyPI simple index returned {(int)response.StatusCode} {response.ReasonPhrase}");

            // Parsed straight from the response bytes: as a string plus a JSON document tree the
            // ~38 MB index transiently cost several hundred MB.
            byte[] json = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            return ParseSimpleIndexProjectNames(json);
        }

        /// <summary>
        /// Pip used to keep the index under its own name. Moves a fresh one to the shared name
        /// instead of downloading ~38 MB again, and removes a stale one.
        /// </summary>
        private static void MigrateLegacyCache(string cacheFile, INativeTaskLogger? logger)
        {
            string? directory = Path.GetDirectoryName(cacheFile);
            if (directory is null)
                return;

            string legacyFile = Path.Join(directory, LegacyCacheFileName);
            if (!File.Exists(legacyFile))
                return;

            try
            {
                if (IsIndexFileFresh(legacyFile) && !IsIndexFileFresh(cacheFile))
                {
                    Directory.CreateDirectory(directory);
                    File.Move(legacyFile, cacheFile, overwrite: true);
                    logger?.Log("Moved the PyPI index cache to its shared name");
                }
                else
                {
                    File.Delete(legacyFile);
                }
            }
            catch (Exception e)
            {
                LogError(logger, $"Could not migrate the old PyPI index cache {legacyFile}: {e.Message}");
            }
        }

        /// <summary>
        /// Writes the cache through a temporary file, so a search reading it never sees a
        /// half-written index, and a second process writing at the same time cannot corrupt it.
        /// </summary>
        private static void WriteCacheFile(string cacheFile, string[] names, INativeTaskLogger? logger)
        {
            string directory = Path.GetDirectoryName(cacheFile)!;
            string temporary = Path.Join(
                directory,
                $"{Path.GetFileName(cacheFile)}.{Guid.NewGuid():N}.tmp"
            );
            try
            {
                Directory.CreateDirectory(directory);
                File.WriteAllLines(temporary, names);
                File.Move(temporary, cacheFile, overwrite: true);
            }
            catch (Exception e)
            {
                LogError(logger, $"Could not write PyPI index file cache to {cacheFile}: {e.Message}");
                try
                {
                    File.Delete(temporary);
                }
                catch
                {
                    // Nothing else to do: the index is in memory either way.
                }
            }
        }

        /// <summary>
        /// Downloads the index in the background so the first search does not have to wait for
        /// the ~38 MB download inside the search timeout window. Only the file needs to be
        /// ready; a fresh one is not loaded until a search.
        /// </summary>
        internal static Task PreWarmIndexAsync()
        {
            return Task.Run(() =>
            {
                try
                {
                    if (!IsIndexFileFresh(GetCacheFilePath()))
                        _ = GetProjectNames();
                    Logger.Info("PyPI: the simple index is cached and ready");
                }
                catch (Exception e)
                {
                    Logger.Warn($"PyPI: background index pre-warm failed: {e.Message}");
                }
            });
        }

        private static void LogError(INativeTaskLogger? logger, string message)
        {
            if (logger is not null)
                logger.Error(message);
            else
                Logger.Error(message);
        }

        /// <summary>
        /// Splits project-name entries out of a PEP 691 simple index JSON document. An index
        /// that parses but yields zero names is malformed, not an empty feed.
        /// </summary>
        internal static string[] ParseSimpleIndexProjectNames(string json) =>
            ParseSimpleIndexProjectNames(Encoding.UTF8.GetBytes(json));

        // Reads {"projects": [{"name": "..."}, ...]} token by token, keeping only the names.
        internal static string[] ParseSimpleIndexProjectNames(ReadOnlySpan<byte> utf8Json)
        {
            List<string> found = [];
            var reader = new Utf8JsonReader(utf8Json);
            bool inProjects = false;
            while (reader.Read())
            {
                if (!inProjects)
                {
                    if (reader.TokenType is JsonTokenType.PropertyName && reader.CurrentDepth == 1)
                    {
                        bool isProjects = reader.ValueTextEquals("projects");
                        reader.Read();
                        if (isProjects && reader.TokenType is JsonTokenType.StartArray)
                            inProjects = true;
                        else
                            reader.Skip();
                    }
                    continue;
                }

                if (reader.TokenType is JsonTokenType.EndArray && reader.CurrentDepth == 1)
                    break;

                if (reader.TokenType is JsonTokenType.PropertyName && reader.CurrentDepth == 3)
                {
                    bool isName = reader.ValueTextEquals("name");
                    reader.Read();
                    if (isName && reader.TokenType is JsonTokenType.String)
                    {
                        string? name = reader.GetString();
                        if (!string.IsNullOrEmpty(name))
                            found.Add(name);
                    }
                    else
                    {
                        reader.Skip();
                    }
                }
            }

            string[] names = found.ToArray();

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
