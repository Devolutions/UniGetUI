using System.Text.Json.Nodes;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Managers.UvManager.Models;

namespace UniGetUI.PackageEngine.Managers.UvManager.Services;

/// <summary>
/// Reads the interpreter catalogue from `uv python list --output-format json`, which reports
/// both the interpreters already on disk and, for every supported combination, the release uv
/// can download on demand.
/// </summary>
internal static class UvPythonService
{
    /// <exception cref="InvalidDataException">The response was malformed.</exception>
    internal static IReadOnlyList<UvPythonInstall> ParseJson(string json)
    {
        if (JsonNode.Parse(json) is not JsonArray entries)
            throw new InvalidDataException("uv python list --output-format json returned no array");

        List<UvPythonInstall> interpreters = [];
        foreach (JsonNode? entry in entries)
        {
            if (entry is not JsonObject item)
                continue;

            string? key = item["key"]?.GetValue<string>();
            string? version = item["version"]?.GetValue<string>();
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(version))
                throw new InvalidDataException(
                    "uv python list entry is missing its key or version"
                );

            interpreters.Add(
                new UvPythonInstall
                {
                    Key = key,
                    Version = version,
                    Implementation = item["implementation"]?.GetValue<string>() ?? "unknown",
                    Variant = item["variant"]?.GetValue<string>() ?? "default",
                    Path = item["path"]?.GetValue<string>(),
                    DownloadUrl = item["url"]?.GetValue<string>(),
                }
            );
        }

        if (interpreters.Count == 0)
            throw new InvalidDataException("uv python list returned 0 entries");

        return interpreters;
    }

    internal static IReadOnlyList<UvPythonInstall> ListInstalled(
        Uv manager,
        bool allVersions = false
    ) => manager.ListPythonInterpreters(
        "--only-installed" + (allVersions ? " --all-versions" : ""),
        LoggableTaskType.LoadPackageDetails
    );

    internal static IReadOnlyList<UvPythonInstall> List(
        Uv manager,
        bool allVersions = false
    ) => manager.ListPythonInterpreters(
        allVersions ? "--all-versions" : "",
        LoggableTaskType.LoadPackageDetails
    );
}
