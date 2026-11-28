namespace UniGetUI.PackageEngine.Managers.UvManager.Models;

/// <summary>
/// One entry of `uv tool list` after parsing. Mirrors what the CLI reports per installed tool:
/// the package (distribution) id, its installed version, the version specifier used at install
/// time, the Python interpreter of the tool environment, the environment and executable
/// locations and the executable names. LatestVersion is only present in `--outdated` listings.
/// </summary>
internal sealed class UvTool
{
    public required string PackageId { get; init; }
    public required string Version { get; init; }
    public string? VersionSpecifier { get; init; }
    public string? PythonVersion { get; init; }
    public string? ToolEnvironmentPath { get; init; }
    public IReadOnlyList<string> Executables { get; init; } = [];
    public string? LatestVersion { get; init; }

    /// <summary>
    /// True when the tool was installed with an exact `==` pin. uv refuses to upgrade such a
    /// tool ("Nothing to upgrade"), so it must not be offered as an update candidate.
    /// </summary>
    public bool IsPinnedToExactVersion =>
        VersionSpecifier is not null && VersionSpecifier.StartsWith("==", StringComparison.Ordinal);
}
