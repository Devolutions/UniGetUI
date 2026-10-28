using System.Text.Json.Serialization;

namespace UniGetUI.PackageEngine.Managers.InstallerFeeds;

/// <summary>
/// The index.json of a web feed, written by <c>unigetui pkg-index</c> next to the packages.
/// A folder feed needs none: its packages are read directly.
/// </summary>
public sealed class FeedIndex
{
    public const int CurrentVersion = 1;

    [JsonPropertyName("version")]
    public required int Version { get; init; }

    // The macOS packages. Their key predates Windows feeds, and Macs on older releases read every entry in it.
    [JsonPropertyName("packages")]
    public required FeedIndexEntry[] Packages { get; init; }

    // Under their own key, so that older Macs never see them.
    [JsonPropertyName("windowsPackages")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FeedIndexEntry[]? WindowsPackages { get; init; }

    public FeedIndexEntry[] EntriesFor(FeedPlatform platform) =>
        (platform == FeedPlatform.Windows ? WindowsPackages : Packages) ?? [];
}

public enum FeedPlatform
{
    MacOS,
    Windows,
}

public sealed class FeedIndexEntry
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
    public FeedIndexApp? App { get; init; }

    // A disk image that holds an installer package rather than an app.
    [JsonPropertyName("wrapsPackage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? WrapsPackage { get; init; }

    [JsonPropertyName("windows")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FeedIndexWindows? Windows { get; init; }
}

public sealed class FeedIndexApp
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Version { get; init; }
}

public sealed class FeedIndexWindows
{
    [JsonPropertyName("installer")]
    public required string Installer { get; init; }

    [JsonPropertyName("publisher")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Publisher { get; init; }

    [JsonPropertyName("productCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProductCode { get; init; }

    [JsonPropertyName("upgradeCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UpgradeCode { get; init; }

    [JsonPropertyName("uninstallKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UninstallKey { get; init; }

    [JsonPropertyName("displayName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayName { get; init; }

    [JsonPropertyName("installArguments")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InstallArguments { get; init; }

    [JsonPropertyName("uninstallArguments")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UninstallArguments { get; init; }

    [JsonPropertyName("scope")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Scope { get; init; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(FeedIndex))]
public partial class FeedIndexJsonContext : JsonSerializerContext;
