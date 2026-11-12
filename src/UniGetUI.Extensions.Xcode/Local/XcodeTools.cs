namespace UniGetUI.Extensions.Xcode.Local;

/// <summary>The real runners for the macOS tools the native Xcode installer uses.</summary>
internal static class XcodeTools
{
    /// <summary>For quick queries: plists, xcode-select, codesign, sw_vers.</summary>
    public static readonly XcodeToolRunner Quick = (executable, arguments, cancellationToken)
        => new XcodesRunner(executable)
            .RunAsync(arguments, TimeSpan.FromMinutes(2), cancellationToken);

    /// <summary>
    /// For work that takes as long as it takes: expanding an archive, the administrator steps. Only cancellation
    /// stops it.
    /// </summary>
    public static readonly XcodeToolRunner Long = (executable, arguments, cancellationToken)
        => new XcodesRunner(executable)
            .RunAsync(arguments, TimeSpan.FromHours(3), cancellationToken);
}
