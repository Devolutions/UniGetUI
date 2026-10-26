using System.Text.Json.Serialization;

namespace UniGetUI.PackageEngine.Managers.MacPkgManager.Internal;

/// <summary>
/// The index.json of a web feed, written by <c>unigetui pkg-index</c> next to the .pkg files.
/// A folder feed needs none: its packages are read directly.
/// </summary>
internal sealed class PkgFeedIndex
{
    public const int CurrentVersion = 1;

    [JsonPropertyName("version")]
    public required int Version { get; init; }

    [JsonPropertyName("packages")]
    public required PkgFeedIndexEntry[] Packages { get; init; }
}

internal sealed class PkgFeedIndexEntry
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }

    // Relative to the index, with forward slashes.
    [JsonPropertyName("file")]
    public required string File { get; init; }

    [JsonPropertyName("sha256")]
    public required string Sha256 { get; init; }

    [JsonPropertyName("size")]
    public long Size { get; init; }

    [JsonPropertyName("architectures")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? Architectures { get; init; }

    [JsonPropertyName("icon")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Icon { get; init; }

    // The app the package installs, which also marks it installed when it got there another way.
    [JsonPropertyName("app")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PkgFeedIndexApp? App { get; init; }

    // A disk image that holds an installer package rather than an app.
    [JsonPropertyName("wrapsPackage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? WrapsPackage { get; init; }
}

internal sealed class PkgFeedIndexApp
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Version { get; init; }

    public static PkgFeedIndexApp? From(AppBundleInfo? app) =>
        app is null ? null : new() { Id = app.BundleId, Path = app.Path, Version = app.Version };

    // Only an absolute .app path is kept: it is read to detect the app, never written to.
    public AppBundleInfo? ToInfo() =>
        !string.IsNullOrWhiteSpace(Id) && Path is { Length: > 1 } path && path.StartsWith('/')
            && path.TrimEnd('/').EndsWith(".app", StringComparison.OrdinalIgnoreCase) && !path.Split('/').Contains("..")
            ? new AppBundleInfo(Id.Trim(), path.TrimEnd('/'), string.IsNullOrWhiteSpace(Version) ? null : Version.Trim())
            : null;
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(PkgFeedIndex))]
internal partial class PkgFeedJsonContext : JsonSerializerContext;
