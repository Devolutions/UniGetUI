// Shared data types (port of types.ts and friends).

using System.Text.Json.Nodes;

namespace Skills;

internal static class Constants
{
    public const string AgentsDir = ".agents";
    public const string SkillsSubdir = "skills";

    /// Maximum skill-directory depth searched inside a known container by default.
    public const int DefaultSkillContainerDepth = 3;
}

/// A file held in memory (blob snapshot or well-known download).
internal sealed record SnapshotFile(string Path, byte[] Contents);

internal sealed record BlobData(List<SnapshotFile> Files, string SnapshotHash, string RepoPath);

internal sealed class Skill
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";

    /// Directory on disk ("" for blob skills).
    public string Path { get; set; } = "";
    public string? RawContent { get; set; }
    public string? PluginName { get; set; }
    public JsonObject? Metadata { get; set; }

    /// Snapshot data for skills resolved from the blob fast path.
    public BlobData? Blob { get; set; }
}

internal sealed class ParsedSource
{
    /// github | gitlab | git | local | well-known | download
    public string Kind { get; set; } = "";
    public string Url { get; set; } = "";
    public string? Subpath { get; set; }
    public string? LocalPath { get; set; }
    public string? Ref { get; set; }
    public string? SkillFilter { get; set; }

    public ParsedSource(string kind, string url)
    {
        Kind = kind;
        Url = url;
    }
}
