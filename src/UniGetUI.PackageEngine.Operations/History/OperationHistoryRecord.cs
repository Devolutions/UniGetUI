using System.Globalization;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageOperations;

namespace UniGetUI.PackageEngine.Operations.History;

/// <summary>A single console output line of a recorded operation.</summary>
public sealed class OperationHistoryOutputLine
{
    public string Text { get; set; } = "";
    public string Type { get; set; } = "";
}

/// <summary>
/// A structured, persisted record of a finished operation. This is the single source of truth
/// consumed by both the history UI and the IPC/agent layer, replacing the old raw text blob.
/// </summary>
public sealed class OperationHistoryRecord
{
    public string Id { get; set; } = "";
    /// <summary>install-package, update-package, uninstall-package, download-package, add-source, remove-source.</summary>
    public string Kind { get; set; } = "";
    /// <summary><see cref="OperationType"/> as int (Install/Update/Uninstall/None).</summary>
    public int Role { get; set; }
    public string PackageId { get; set; } = "";
    public string PackageName { get; set; } = "";
    /// <summary>Public manager id (<see cref="Interfaces.IPackageManager.Id"/>).</summary>
    public string ManagerName { get; set; } = "";
    public string SourceName { get; set; } = "";
    public string VersionBefore { get; set; } = "";
    public string VersionAfter { get; set; } = "";
    /// <summary>succeeded, failed or canceled.</summary>
    public string Status { get; set; } = "";
    /// <summary>ISO-8601 (round-trip "O") UTC timestamp of when the operation finished.</summary>
    public string TimestampUtc { get; set; } = "";
    /// <summary>Serialized <see cref="Serializable.InstallOptions"/> (pre-serialized string) used to re-run or revert.</summary>
    public string OptionsJson { get; set; } = "";
    /// <summary>Process exit code, when the operation ran a process (null otherwise).</summary>
    public int? ExitCode { get; set; }
    /// <summary>
    /// Whether the operation actually ran elevated, for package operations. Null on records
    /// written before this was tracked, and for operations that never ran a process.
    /// </summary>
    public bool? RanElevated { get; set; }
    public bool? RequiresReinstallToUpdate { get; set; }
    public bool SystemRestartRequired { get; set; }
    /// <summary>Short human-readable reason, derived from the last error line (mainly for failures).</summary>
    public string FailureSummary { get; set; } = "";
    public List<OperationHistoryOutputLine> Output { get; set; } = [];

    public const string StatusSucceeded = "succeeded";
    public const string StatusFailed = "failed";
    public const string StatusCanceled = "canceled";

    /// <summary>Kind assigned to the single record imported from the pre-structured text log.</summary>
    public const string KindLegacyLog = "legacy-log";

    /// <summary>
    /// Safety guard on stored output: virtually all real operations are far below this, so complete
    /// logs are kept; only pathological outliers are trimmed, and when they are, an explicit marker
    /// line makes it visible rather than silently dropping content.
    /// </summary>
    public const int MaxOutputLines = 5000;

    public const int MaxOutputLineChars = 4096;

    public const int MaxOutputChars = 256 * 1024;

    private const int MarkerReserveChars = 256;

    /// <summary>
    /// Builds a record from a finished operation. <paramref name="status"/> is passed explicitly
    /// because the caller knows the terminal outcome at record time.
    /// </summary>
    public static OperationHistoryRecord FromOperation(AbstractOperation op, string status)
    {
        var record = new OperationHistoryRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Kind = GetKind(op),
            Role = (int)OperationType.None,
            Status = status,
            TimestampUtc = DateTime.UtcNow.ToString("O"),
            SystemRestartRequired = op.SystemRestartRequired,
        };

        try
        {
            switch (op)
            {
                case PackageOperation pop:
                    record.PackageId = pop.Package.Id;
                    record.PackageName = pop.Package.Name;
                    record.ManagerName = pop.Package.Manager.Id;
                    record.SourceName = pop.Package.Source.Name;
                    record.Role = (int)pop.Role;
                    // Only populate the side of the transition that actually existed: an install has no
                    // "before" version and an uninstall has no "after" version.
                    record.VersionBefore = pop.Role is OperationType.Install ? "" : pop.Package.VersionString;
                    record.VersionAfter = pop.Role switch
                    {
                        OperationType.Uninstall => "",
                        OperationType.Update
                            when UpdateVersionPin.HonoredUpdateVersion(pop.Package, pop.Options) is { } honoredVersion
                            => honoredVersion,
                        not OperationType.Update when pop.Options.Version is { Length: > 0 } pinnedVersion
                            => pinnedVersion,
                        OperationType.Update => pop.Package.NewVersionString,
                        _ => pop.Package.VersionString,
                    };
                    record.OptionsJson = pop.Options.AsJsonString();
                    record.RanElevated = pop.WillRunElevated;
                    record.RequiresReinstallToUpdate = pop.Package.RequiresReinstallToUpdate ? true : null;
                    break;
                case DownloadOperation dop:
                    record.PackageId = dop.Package.Id;
                    record.PackageName = dop.Package.Name;
                    record.ManagerName = dop.Package.Manager.Id;
                    record.SourceName = dop.Package.Source.Name;
                    record.VersionBefore = dop.Package.VersionString;
                    record.VersionAfter = dop.Package.VersionString;
                    break;
                case SourceOperation sop:
                    record.PackageName = sop.ManagerSource.Name;
                    record.ManagerName = sop.ManagerSource.Manager.Id;
                    record.SourceName = sop.ManagerSource.Name;
                    break;
            }

            record.Output = BuildOutput(op.GetOutput());

            if (op is AbstractProcessOperation processOp)
                record.ExitCode = processOp.LastReturnCode;

            record.FailureSummary = DeriveFailureSummary(record.Output, status);
        }
        catch (Exception ex)
        {
            Logger.Warn("Failed to fully build an operation history record");
            Logger.Warn(ex);
        }

        return record;
    }

    /// <summary>
    /// Projects an operation's raw output into stored lines, keeping the tail (the end holds the
    /// result/errors): earlier lines past <see cref="MaxOutputLines"/> lines or
    /// <see cref="MaxOutputChars"/> characters are replaced by a visible marker, and a line longer
    /// than <see cref="MaxOutputLineChars"/> keeps both ends around a visible marker.
    /// </summary>
    public static List<OperationHistoryOutputLine> BuildOutput(
        IReadOnlyList<(string Text, AbstractOperation.LineType Type)> output)
    {
        int first = FirstKeptLine(output.Count, 0, MaxOutputLines, MaxOutputChars, i => output[i].Text);
        var result = new List<OperationHistoryOutputLine>(output.Count - first + 1);
        AddOmittedMarker(result, first);
        for (int i = first; i < output.Count; i++)
            result.Add(new OperationHistoryOutputLine { Text = ClampLine(output[i].Text), Type = output[i].Type.ToString() });
        return result;
    }

    public bool ClampStoredOutput()
    {
        if (Output is null)
        {
            Output = [];
            return true;
        }

        bool droppedNullLines = Output.RemoveAll(line => line is null) > 0;
        long maxChars = Kind == KindLegacyLog ? long.MaxValue : MaxOutputChars;
        if (!NeedsCharacterClamp(Output, maxChars))
            return droppedNullLines;

        int start = 0;
        int alreadyOmitted = 0;
        if (Output.Count > 0 && TryReadOmittedMarker(Output[0], out alreadyOmitted))
            start = 1;

        int first = FirstKeptLine(Output.Count, start, int.MaxValue, maxChars, i => Output[i].Text);
        var result = new List<OperationHistoryOutputLine>(Output.Count - first + 1);
        AddOmittedMarker(result, first - start + alreadyOmitted);
        for (int i = first; i < Output.Count; i++)
            result.Add(new OperationHistoryOutputLine { Text = ClampLine(Output[i].Text), Type = Output[i].Type });
        Output = result;
        return true;
    }

    private static int FirstKeptLine(int count, int start, int maxLines, long maxChars, Func<int, string?> textAt)
    {
        long budget = maxChars - MarkerReserveChars;
        long used = 0;
        int first = count;
        while (first > start && count - first < maxLines)
        {
            int length = Math.Min(textAt(first - 1)?.Length ?? 0, MaxOutputLineChars);
            if (used + length > budget)
                break;
            used += length;
            first--;
        }
        return first;
    }

    private static void AddOmittedMarker(List<OperationHistoryOutputLine> result, int omitted)
    {
        if (omitted <= 0)
            return;
        result.Add(new OperationHistoryOutputLine
        {
            Text = CoreTools.Translate("[... {0} earlier lines omitted ...]", omitted),
            Type = "Information",
        });
    }

    private static bool TryReadOmittedMarker(OperationHistoryOutputLine line, out int omitted)
    {
        omitted = 0;
        string? text = line.Text;
        if (line.Type != "Information"
            || text is null
            || text.Length > MarkerReserveChars
            || !text.StartsWith("[... ", StringComparison.Ordinal)
            || !text.EndsWith(" ...]", StringComparison.Ordinal))
            return false;

        int digitsStart = -1;
        int digitsEnd = -1;
        for (int i = 0; i < text.Length; i++)
        {
            if (!char.IsAsciiDigit(text[i]))
                continue;
            if (digitsStart < 0)
                digitsStart = i;
            else if (digitsEnd != i)
                return false;
            digitsEnd = i + 1;
        }

        return digitsStart >= 0
            && int.TryParse(
                text.AsSpan(digitsStart, digitsEnd - digitsStart),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out omitted);
    }

    private static bool NeedsCharacterClamp(IReadOnlyList<OperationHistoryOutputLine> output, long maxChars)
    {
        long total = 0;
        foreach (var line in output)
        {
            int length = line.Text?.Length ?? 0;
            if (length > MaxOutputLineChars || line.Text is null)
                return true;
            total += length;
        }
        return total > maxChars;
    }

    private static string ClampLine(string? text)
    {
        if (text is null)
            return "";
        if (text.Length <= MaxOutputLineChars)
            return text;

        string widestMarker = CoreTools.Translate("[... {0} characters omitted ...]", text.Length);
        int keep = Math.Max(0, MaxOutputLineChars - widestMarker.Length);
        int head = keep / 2;
        if (head > 0 && char.IsHighSurrogate(text[head - 1]))
            head--;
        int tailStart = text.Length - (keep - head);
        if (tailStart < text.Length && char.IsLowSurrogate(text[tailStart]))
            tailStart++;
        string marker = CoreTools.Translate("[... {0} characters omitted ...]", tailStart - head);
        return string.Concat(text.AsSpan(0, head), marker, text.AsSpan(tailStart));
    }

    /// <summary>
    /// Best-effort one-line "why" for a failed operation. Managers usually print errors to stdout
    /// (stored as Information), not stderr (Error), so prefer the last Error line but fall back to the
    /// last real output line. Verbose/diagnostic lines are ignored, and the result is length-capped.
    /// </summary>
    public static string DeriveFailureSummary(IReadOnlyList<OperationHistoryOutputLine> output, string status)
    {
        if (status != StatusFailed) return "";

        var line = LastOfType(output, nameof(AbstractOperation.LineType.Error))
                   ?? LastOfType(output, nameof(AbstractOperation.LineType.Information));

        string text = line?.Text.Trim() ?? "";
        return text.Length > 300 ? text[..300] + "…" : text;
    }

    private static OperationHistoryOutputLine? LastOfType(IReadOnlyList<OperationHistoryOutputLine> output, string type)
    {
        for (int i = output.Count - 1; i >= 0; i--)
            if (output[i].Type == type && !string.IsNullOrWhiteSpace(output[i].Text))
                return output[i];
        return null;
    }

    private static string GetKind(AbstractOperation op) => op switch
    {
        InstallPackageOperation => "install-package",
        UpdatePackageOperation => "update-package",
        UninstallPackageOperation => "uninstall-package",
        DownloadOperation => "download-package",
        AddSourceOperation => "add-source",
        RemoveSourceOperation => "remove-source",
        _ => op.GetType().Name,
    };
}
