namespace UniGetUI.PackageEngine.Managers.UvManager.Models;

/// <summary>
/// One entry of `uv python list --output-format json`. An entry without a path or symlink is a
/// download uv can fetch on demand, an entry with either one is an interpreter already on disk.
/// </summary>
internal sealed class UvPythonInstall
{
    public required string Key { get; init; }
    public required string Version { get; init; }
    public required string Implementation { get; init; }
    public required string Variant { get; init; }
    public string? Path { get; init; }
    public string? DownloadUrl { get; init; }

    public bool IsInstalled => Path is not null;
}
