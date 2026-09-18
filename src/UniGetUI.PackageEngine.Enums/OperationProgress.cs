namespace UniGetUI.PackageEngine.Enums
{
    /// <summary>
    /// The phase a package operation is currently in. Manager-neutral: no WinGet, COM,
    /// or CLI-specific concepts leak into this model.
    /// </summary>
    public enum OperationProgressStage
    {
        Unknown,
        Downloading,
        Installing,
        Updating,
        Uninstalling,
    }

    /// <summary>
    /// Manager-neutral snapshot of package-operation progress.
    ///
    /// Unknown percentage is represented as a null <see cref="Percentage"/> (never zero):
    /// the UI must render null as indeterminate and a known value as determinate.
    /// Download and install phases are never merged into a synthetic overall total.
    /// Instances are immutable; the operation layer enriches download reports with a
    /// measured <see cref="BytesPerSecond"/> downstream.
    /// </summary>
    public sealed record OperationProgress(
        OperationProgressStage Stage,
        double? Percentage,
        ulong? BytesDownloaded,
        ulong? BytesTotal,
        double? BytesPerSecond
    )
    {
        /// <summary>Plain unknown: indeterminate UI, log-driven status text is kept.</summary>
        public static readonly OperationProgress Unknown = new(
            OperationProgressStage.Unknown,
            null,
            null,
            null,
            null
        );

        /// <summary>Unknown progress within a known stage (e.g. installing with no counters).</summary>
        public static OperationProgress ForStage(OperationProgressStage stage) =>
            new(stage, null, null, null, null);

        /// <summary>
        /// Download progress from real cumulative byte counters. Percentage is derived as
        /// <c>downloaded / total</c> and clamped to 100 when counters overshoot; a zero
        /// total yields indeterminate (unknown) progress rather than a fake zero.
        /// </summary>
        public static OperationProgress FromDownload(ulong downloaded, ulong total) =>
            total == 0
                ? new(OperationProgressStage.Downloading, null, downloaded, null, null)
                : new(
                    OperationProgressStage.Downloading,
                    Math.Min(downloaded * 100.0 / total, 100.0),
                    downloaded,
                    total,
                    null
                );

        /// <summary>
        /// True only for a real, finite percentage. Unknown progress is never zero.
        /// </summary>
        public bool IsDeterminate =>
            Percentage is { } value
            && !double.IsNaN(value)
            && !double.IsInfinity(value)
            && value >= 0;

        /// <summary>True when a usable measured throughput is attached.</summary>
        public bool HasThroughput => NormalizeBytesPerSecond(BytesPerSecond) is not null;

        /// <summary>
        /// Accepts only positive finite speeds. NaN, Infinity, zero, negatives, and null
        /// are rejected so they can never be displayed or averaged.
        /// </summary>
        public static double? NormalizeBytesPerSecond(double? bytesPerSecond) =>
            bytesPerSecond is { } value
            && !double.IsNaN(value)
            && !double.IsInfinity(value)
            && value > 0
                ? value
                : null;
    }
}
