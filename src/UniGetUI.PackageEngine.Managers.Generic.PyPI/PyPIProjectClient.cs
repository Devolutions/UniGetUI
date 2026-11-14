using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.ManagerClasses.Classes;

namespace UniGetUI.PackageEngine.Managers.Generic.PyPI
{
    /// <summary>
    /// Reads package metadata, the version list and the latest version of a PyPI project from
    /// the JSON API at https://pypi.org/pypi/{id}/json. Shared by every manager backed by PyPI.
    /// Versions whose every release file is yanked are dropped from the version list.
    /// </summary>
    internal static class PyPIProjectClient
    {
        private const string JsonApiBaseUrl = "https://pypi.org/pypi/";

        private static readonly HttpClient _client = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient(CoreTools.GenericHttpClientParameters);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(CoreData.UserAgentString);
            return client;
        }

        internal readonly record struct LatestVersion(string? Version, bool Failed);

        internal static string BuildJsonApiUrl(string packageId) =>
            $"{JsonApiBaseUrl}{Uri.EscapeDataString(packageId)}/json";

        /// <summary>
        /// Performs a GET request against the PyPI JSON API and returns the response body.
        /// Non-success responses are surfaced to the caller as an HttpRequestException or an
        /// item failure.
        /// </summary>
        internal static string FetchJson(string packageId)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildJsonApiUrl(packageId));
            using HttpResponseMessage response = _client.Send(request);
            response.EnsureSuccessStatusCode();
            return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        }

        /// <summary>
        /// Reads info.version for many packages at once, bounded in concurrency and with a
        /// wall-clock timeout so a stuck request cannot take down a whole search. Failures
        /// yield null so the rest of the search results still surface.
        /// </summary>
        internal static Dictionary<string, string?> FetchLatestVersions(
            IReadOnlyCollection<string> packageIds,
            int maxConcurrency = 6
        )
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
            SemaphoreSlim gate = new(maxConcurrency, maxConcurrency);
            var lookups = packageIds
                .Select(async id =>
                {
                    await gate.WaitAsync(timeout.Token).ConfigureAwait(false);
                    try
                    {
                        using var request = new HttpRequestMessage(
                            HttpMethod.Get,
                            BuildJsonApiUrl(id)
                        );
                        using HttpResponseMessage response = await _client
                            .SendAsync(request, timeout.Token)
                            .ConfigureAwait(false);
                        response.EnsureSuccessStatusCode();
                        string json = await response
                            .Content.ReadAsStringAsync(timeout.Token)
                            .ConfigureAwait(false);
                        return (Id: id, Version: ParseMetadataVersion(json));
                    }
                    catch
                    {
                        return (Id: id, Version: null);
                    }
                    finally
                    {
                        gate.Release();
                    }
                })
                .ToArray();
            Task.WhenAll(lookups).GetAwaiter().GetResult();
            return lookups.ToDictionary(lookup => lookup.Result.Id, lookup => lookup.Result.Version);
        }

        internal static string? ParseMetadataVersion(string json) =>
            (JsonNode.Parse(json) as JsonObject)?["info"]?["version"]?.GetValue<string>();

        /// <summary>
        /// Orders the releases of a project newest first. When <paramref name="includePreReleases"/>
        /// is false, pre-release versions are left out; yanked releases are dropped in both modes.
        /// </summary>
        internal static IReadOnlyList<string> ParseVersions(string json, bool includePreReleases)
        {
            JsonObject? releases = (JsonNode.Parse(json) as JsonObject)?["releases"] as JsonObject;
            if (releases is null)
                throw new InvalidDataException(
                    "PyPI metadata response does not contain a releases object"
                );

            List<(PythonVersion Parsed, string Raw)> candidates = [];
            foreach ((string key, JsonNode? value) in releases)
            {
                if (!PythonVersion.TryParse(key, out PythonVersion parsed))
                    continue;

                if (!includePreReleases && parsed.IsPreRelease)
                    continue;

                // A partial yank keeps the version: at least one installable file remains.
                if (value is JsonArray files && files.Count > 0
                    && files.All(file => file is JsonObject entry && IsYanked(entry)))
                    continue;

                candidates.Add((parsed, key));
            }

            return candidates
                .OrderByDescending(candidate => candidate.Parsed)
                .Select(candidate => candidate.Raw)
                .ToList();
        }

        private static bool IsYanked(JsonObject file) =>
            file["yanked"] is JsonValue yanked
            && yanked.GetValueKind() switch
            {
                JsonValueKind.True => true,
                JsonValueKind.String => !string.IsNullOrEmpty(yanked.GetValue<string>()),
                _ => false,
            };

        internal static PyPIPackageMetadata? ParseMetadata(string json)
        {
            if (JsonNode.Parse(json) as JsonObject is not { } contents
                || contents["info"] is not JsonObject info)
                return null;

            return new PyPIPackageMetadata
            {
                Version = ReadString(info, "version"),
                Summary = ReadString(info, "summary"),
                Author = ReadString(info, "author"),
                Maintainer = ReadString(info, "maintainer"),
                License = ReadString(info, "license"),
                Homepage = ReadUri(info, "home_page"),
                PackageUrl = ReadUri(info, "package_url"),
                Classifiers = (info["classifiers"] as JsonArray)?
                    .Select(entry => entry?.GetValue<string>())
                    .OfType<string>()
                    .Where(line => line is not null)
                    .ToArray() ?? [],
                RequiresPython = ReadString(info, "requires_python"),
                RequiresDist = (info["requires_dist"] as JsonArray)?
                    .Select(entry => entry?.GetValue<string>())
                    .OfType<string>()
                    .Where(line => !string.IsNullOrWhiteSpace(line))
                    .ToArray() ?? [],
            };
        }

        internal static PyPIInstallerInfo? ParseInstaller(string json)
        {
            if (JsonNode.Parse(json) as JsonObject is not { } contents)
                return null;

            JsonObject? file = contents["url"] as JsonObject
                ?? (contents["urls"] as JsonArray)?[0] as JsonObject;
            if (file is null)
                return null;

            Uri? installerUrl = ReadUri(file, "url");
            if (installerUrl is null)
                return null;

            return new PyPIInstallerInfo
            {
                InstallerUrl = installerUrl,
                InstallerHash = (file["digests"] as JsonObject)?["sha256"]?.ToString(),
                InstallerType = installerUrl.ToString().Split('.')[^1].Replace("whl", "Wheel"),
                UploadTime = ReadString(file, "upload_time"),
            };
        }

        private static string? ReadString(JsonObject owner, string property) =>
            owner[property] is not JsonValue value
                ? null
                : value.GetValueKind() is JsonValueKind.String
                    ? value.GetValue<string>()
                    : null;
        private static Uri? ReadUri(JsonObject owner, string property)
        {
            if (owner[property] is not JsonValue value
                || value.GetValueKind() is not JsonValueKind.String)
                return null;

            return Uri.TryCreate(value.GetValue<string>(), UriKind.RelativeOrAbsolute, out var uri)
                ? uri
                : null;
        }
    }

    internal sealed record PyPIPackageMetadata
    {
        public required string? Version { get; init; }
        public required string? Summary { get; init; }
        public required string? Author { get; init; }
        public required string? Maintainer { get; init; }
        public required string? License { get; init; }
        public required Uri? Homepage { get; init; }
        public required Uri? PackageUrl { get; init; }
        public required IReadOnlyList<string> Classifiers { get; init; }
        public required string? RequiresPython { get; init; }
        public required IReadOnlyList<string> RequiresDist { get; init; }
    }

    internal sealed record PyPIInstallerInfo
    {
        public required Uri InstallerUrl { get; init; }
        public required string? InstallerHash { get; init; }
        public required string InstallerType { get; init; }
        public required string? UploadTime { get; init; }
    }
}
