using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Enums;

namespace UniGetUI.PackageOperations;

/// <summary>
/// Formats a generic <see cref="OperationProgress"/> for operation cards, log lines,
/// and screen-reader status. Unknown progress maps to a short stage label
/// (indeterminate); determinate progress uses translatable positional templates so
/// translators control ordering, separators, and placement (e.g.
/// "{0} · {1}% · {2} / {3} · {4}"). No ETA is shown.
/// Size units reuse <see cref="CoreTools.FormatAsSize"/> conventions; stage labels and
/// the composed templates go through <see cref="CoreTools.Translate"/>.
/// </summary>
public static class OperationProgressFormatter
{
    public static string Format(OperationProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (!progress.IsDeterminate)
            return IndeterminateLabel(progress.Stage);

        int percent = (int)Math.Round(progress.Percentage!.Value);
        string label = StageLabel(progress.Stage);
        if (
            progress.BytesDownloaded.HasValue
            && progress.BytesTotal.HasValue
            && progress.BytesTotal.Value > 0
        )
        {
            string downloaded = CoreTools.FormatAsSize((long)progress.BytesDownloaded.Value);
            string total = CoreTools.FormatAsSize((long)progress.BytesTotal.Value);
            string? throughput = FormatThroughput(progress.BytesPerSecond);
            if (throughput is null)
            {
                return CoreTools.Translate(
                    "{0} · {1}% · {2} / {3}",
                    label,
                    percent,
                    downloaded,
                    total
                );
            }

            return CoreTools.Translate(
                "{0} · {1}% · {2} / {3} · {4}",
                label,
                percent,
                downloaded,
                total,
                throughput
            );
        }

        return CoreTools.Translate("{0} · {1}%", label, percent);
    }

    public static string StageLabel(OperationProgressStage stage) =>
        stage switch
        {
            OperationProgressStage.Downloading => CoreTools.Translate("Downloading"),
            OperationProgressStage.Installing => CoreTools.Translate("Installing"),
            OperationProgressStage.Updating => CoreTools.Translate("Updating"),
            OperationProgressStage.Uninstalling => CoreTools.Translate("Uninstalling"),
            _ => CoreTools.Translate("Please wait..."),
        };

    private static string IndeterminateLabel(OperationProgressStage stage) =>
        stage switch
        {
            OperationProgressStage.Downloading
            or OperationProgressStage.Installing
            or OperationProgressStage.Updating
            or OperationProgressStage.Uninstalling => CoreTools.Translate(
                "{0}...",
                StageLabel(stage)
            ),
            _ => CoreTools.Translate("Please wait..."),
        };

    /// <summary>
    /// Formats a measured throughput reusing <see cref="CoreTools.FormatAsSize"/> units
    /// with a "/s" suffix. Returns null when there is no usable speed, in which case the
    /// caller keeps the established speed-less format.
    /// </summary>
    private static string? FormatThroughput(double? bytesPerSecond)
    {
        if (OperationProgress.NormalizeBytesPerSecond(bytesPerSecond) is not { } value)
            return null;

        return $"{CoreTools.FormatAsSize((long)value)}/s";
    }
}
