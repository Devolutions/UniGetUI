using System.Security.Cryptography;
using UniGetUI.Core.Logging;

namespace UniGetUI.Extensions.Xcode;

public sealed record XcodeSimulatorRuntimeInstallPlan(
    XcodeSimulatorRuntime Runtime,
    string DeveloperDirectory,
    XcodeVersion XcodeVersion);

public enum XcodeSimulatorRuntimeInstallOutcome
{
    Installed,
    AlreadyAvailable,
}

public sealed record XcodeSimulatorRuntimeExportPaths(string TemporaryDirectory, string ImagePath)
{
    public string PartialPath => ImagePath + ".partial";
}

public sealed record XcodeSimulatorRuntimeExportResult(string ImagePath, string? RetainedTemporaryDirectory);

public interface IXcodeSimulatorRuntimeInstaller
{
    Task<XcodeSimulatorRuntimeInstallPlan> PrepareAsync(XcodeSimulatorRuntime runtime, CancellationToken cancellationToken);
    Task<XcodeSimulatorRuntimeInstallPlan> PrepareExportAsync(XcodeSimulatorRuntime runtime, CancellationToken cancellationToken);
    Task<XcodeSimulatorRuntimeInstallOutcome> InstallAsync(
        XcodeSimulatorRuntimeInstallPlan plan, Action<XcodesOutputLine>? output, CancellationToken cancellationToken);
    Task<XcodeSimulatorRuntimeExportResult> ExportAsync(
        XcodeSimulatorRuntimeInstallPlan plan, string destinationDirectory,
        Action<XcodeSimulatorRuntimeExportPaths> started, Action<XcodesOutputLine>? output,
        CancellationToken cancellationToken);
}

/// <summary>Manages modern simulator assets through the selected Xcode, not the Apple ID used to download Xcode.</summary>
public sealed class XcodeSimulatorRuntimeInstaller : IXcodeSimulatorRuntimeInstaller
{
    private const int CopyBufferSize = 1024 * 1024;

    internal delegate Task<XcodesResult> CommandRunner(
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?> environment,
        bool stream,
        Action<XcodesOutputLine>? output,
        TimeSpan? timeout,
        CancellationToken cancellationToken);

    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);
    private readonly CommandRunner _run;
    private readonly Func<bool> _isMacOS;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public XcodeSimulatorRuntimeInstaller()
        : this(RunSystemToolAsync, OperatingSystem.IsMacOS, Task.Delay)
    {
    }

    internal XcodeSimulatorRuntimeInstaller(
        CommandRunner run,
        Func<bool> isMacOS,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _run = run;
        _isMacOS = isMacOS;
        _delay = delay ?? Task.Delay;
    }

    public static bool Supports(XcodeSimulatorRuntime runtime)
        => runtime.Origin == XcodeSimulatorRuntimeOrigin.AppleCatalog
           && runtime.Distribution == XcodeSimulatorRuntimeDistribution.XcodeManaged
           && runtime.Architecture is XcodeArchitecture.AppleSilicon or XcodeArchitecture.Universal
           && PlatformArgument(runtime.Platform) is not null
           && XcodeSimulatorImageFile.IsValidBuild(runtime.Build)
           && XcodeVersion.TryParse(runtime.Version, out _);

    public Task<XcodeSimulatorRuntimeInstallPlan> PrepareAsync(
        XcodeSimulatorRuntime runtime, CancellationToken cancellationToken)
        => PrepareAsync(runtime, requireExport: false, cancellationToken);

    public Task<XcodeSimulatorRuntimeInstallPlan> PrepareExportAsync(
        XcodeSimulatorRuntime runtime, CancellationToken cancellationToken)
        => PrepareAsync(runtime, requireExport: true, cancellationToken);

    private async Task<XcodeSimulatorRuntimeInstallPlan> PrepareAsync(
        XcodeSimulatorRuntime runtime, bool requireExport, CancellationToken cancellationToken)
    {
        if (!_isMacOS())
            throw new XcodesException("Simulator runtimes can only be managed on macOS.");
        if (!Supports(runtime))
            throw new XcodesException("This simulator runtime cannot be managed by the selected Xcode.");

        XcodesResult selection = await RunReadAsync(
            "/usr/bin/xcode-select", ["-p"], EnvironmentFor(null), cancellationToken).ConfigureAwait(false);
        string directory = selection.StandardOutput.Trim();
        if (!IsDeveloperDirectory(directory))
            throw new XcodesException("The selected developer tools have no usable directory.");

        XcodesResult version = await RunReadAsync(
            "/usr/bin/xcodebuild", ["-version"], EnvironmentFor(directory), cancellationToken).ConfigureAwait(false);
        string? firstLine = XcodesOutputParser.SplitLines(version.StandardOutput).FirstOrDefault();
        if (!XcodeVersion.TryParse(firstLine, out XcodeVersion selected))
            throw new XcodesException("The selected developer tools did not report an Xcode version.");
        ValidateVersion(runtime, selected);
        await RequireArchitectureSelectionAsync(runtime, EnvironmentFor(directory), cancellationToken, requireExport)
            .ConfigureAwait(false);
        return new XcodeSimulatorRuntimeInstallPlan(runtime, directory, selected);
    }

    public async Task<XcodeSimulatorRuntimeInstallOutcome> InstallAsync(
        XcodeSimulatorRuntimeInstallPlan plan, Action<XcodesOutputLine>? output, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, string?> environment =
            await RecheckPlanAsync(plan, requireExport: false, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<XcodeSimulatorRuntime> installed =
            await GetInstalledAsync(environment, cancellationToken).ConfigureAwait(false);
        if (installed.Any(item => IsReadyMatch(plan.Runtime, item)))
            return XcodeSimulatorRuntimeInstallOutcome.AlreadyAvailable;

        XcodesResult result = await _run("/usr/bin/xcodebuild", DownloadArguments(plan.Runtime), environment, true, output,
            null, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
            throw new XcodesException(XcodesError.Summarize(result.Output, result.ExitCode));

        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (attempt > 0)
                await _delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            installed = await GetInstalledAsync(environment, cancellationToken).ConfigureAwait(false);
            if (installed.Any(item => IsReadyMatch(plan.Runtime, item)))
                return XcodeSimulatorRuntimeInstallOutcome.Installed;
        }
        throw new XcodesException("Xcode finished, but the selected runtime build and architecture could not be "
            + "confirmed as ready. The system may still be processing it; refresh the runtime list.");
    }

    public async Task<XcodeSimulatorRuntimeExportResult> ExportAsync(
        XcodeSimulatorRuntimeInstallPlan plan, string destinationDirectory,
        Action<XcodeSimulatorRuntimeExportPaths> started, Action<XcodesOutputLine>? output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(started);
        if (string.IsNullOrWhiteSpace(destinationDirectory) || !Path.IsPathFullyQualified(destinationDirectory)
            || destinationDirectory.Any(char.IsControl) || !Directory.Exists(destinationDirectory))
            throw new XcodesException("Choose an existing absolute folder for the simulator runtime image.");

        string destination = Path.GetFullPath(destinationDirectory);
        IReadOnlyDictionary<string, string?> environment =
            await RecheckPlanAsync(plan, requireExport: true, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        string variant = ArchitectureArgument(plan.Runtime);
        string imageName = $"{plan.Runtime.Platform}-{plan.Runtime.Build}-{variant}-{Guid.NewGuid():N}.dmg";
        string imagePath = Path.Combine(destination, imageName);
        string temporaryDirectory = Directory.CreateTempSubdirectory("UniGetUI-simruntime-").FullName;
        var paths = new XcodeSimulatorRuntimeExportPaths(
            temporaryDirectory, imagePath);
        started(paths);

        string image;
        byte[] expectedHash;
        await using (var destinationStream = new FileStream(
            paths.PartialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBufferSize, useAsync: true))
        {
            List<string> arguments = DownloadArguments(plan.Runtime);
            arguments.AddRange(["-exportPath", temporaryDirectory]);
            XcodesResult result = await _run("/usr/bin/xcodebuild", arguments, environment, true, output,
                null, cancellationToken).ConfigureAwait(false);
            if (!result.Success)
                throw new XcodesException(XcodesError.Summarize(result.Output, result.ExitCode));

            cancellationToken.ThrowIfCancellationRequested();
            string[] images = [.. Directory.EnumerateFiles(temporaryDirectory)
                .Where(path => Path.GetExtension(path).Equals(".dmg", StringComparison.OrdinalIgnoreCase))
                .Take(2)];
            if (images.Length != 1)
                throw new XcodesException("Xcode did not export exactly one simulator runtime disk image.");

            image = images[0];
            FileAttributes attributes = File.GetAttributes(image);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new XcodesException("Xcode exported an unsafe simulator runtime disk image.");

            expectedHash = await CopyImageAsync(image, destinationStream, output, cancellationToken).ConfigureAwait(false);
        }
        output?.Invoke(new XcodesOutputLine("Verifying copied runtime image..."));
        await XcodeSimulatorImageFile.VerifyAsync(paths.PartialPath, expectedHash, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if ((File.GetAttributes(paths.PartialPath) & FileAttributes.ReparsePoint) != 0)
            throw new XcodesException("The copied simulator runtime disk image is not a regular file.");
        File.Move(paths.PartialPath, paths.ImagePath, overwrite: false);

        string? retainedTemporaryDirectory = null;
        try
        {
            File.Delete(image);
            Directory.Delete(temporaryDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            retainedTemporaryDirectory = temporaryDirectory;
            Logger.Warn($"Could not remove temporary simulator export data from {temporaryDirectory}: {ex.Message}");
        }
        return new XcodeSimulatorRuntimeExportResult(paths.ImagePath, retainedTemporaryDirectory);
    }

    private async Task<IReadOnlyDictionary<string, string?>> RecheckPlanAsync(
        XcodeSimulatorRuntimeInstallPlan plan, bool requireExport, CancellationToken cancellationToken)
    {
        if (!_isMacOS())
            throw new XcodesException("Simulator runtimes can only be managed on macOS.");
        if (!Supports(plan.Runtime) || !IsDeveloperDirectory(plan.DeveloperDirectory))
            throw new XcodesException("The simulator runtime request is invalid.");
        ValidateVersion(plan.Runtime, plan.XcodeVersion);

        IReadOnlyDictionary<string, string?> environment = EnvironmentFor(plan.DeveloperDirectory);
        XcodesResult version = await RunReadAsync(
            "/usr/bin/xcodebuild", ["-version"], environment, cancellationToken).ConfigureAwait(false);
        string? firstLine = XcodesOutputParser.SplitLines(version.StandardOutput).FirstOrDefault();
        if (!XcodeVersion.TryParse(firstLine, out XcodeVersion current) || current != plan.XcodeVersion)
            throw new XcodesException("The selected Xcode changed after confirmation. Try again.");
        await RequireArchitectureSelectionAsync(plan.Runtime, environment, cancellationToken, requireExport)
            .ConfigureAwait(false);
        return environment;
    }

    private static List<string> DownloadArguments(XcodeSimulatorRuntime runtime) =>
    [
        "-downloadPlatform", PlatformArgument(runtime.Platform)!,
        "-buildVersion", runtime.Build!,
        "-architectureVariant", ArchitectureArgument(runtime),
    ];

    private static string ArchitectureArgument(XcodeSimulatorRuntime runtime)
        => runtime.Architecture == XcodeArchitecture.AppleSilicon ? "arm64" : "universal";

    private async Task<IReadOnlyList<XcodeSimulatorRuntime>> GetInstalledAsync(
        IReadOnlyDictionary<string, string?> environment, CancellationToken cancellationToken)
    {
        XcodesResult result = await RunReadAsync(
            "/usr/bin/xcrun", ["simctl", "runtime", "list", "-j"], environment, cancellationToken).ConfigureAwait(false);
        return XcodeSimulatorRuntimeService.ParseInstalled(result.StandardOutput);
    }

    private async Task<XcodesResult> RunReadAsync(
        string executable, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?> environment, CancellationToken cancellationToken)
    {
        XcodesResult result = await _run(executable, arguments, environment, false, null, ReadTimeout, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Success)
            throw new XcodesException(XcodesError.Summarize(result.Output, result.ExitCode));
        return result;
    }

    private async Task RequireArchitectureSelectionAsync(
        XcodeSimulatorRuntime runtime, IReadOnlyDictionary<string, string?> environment,
        CancellationToken cancellationToken, bool requireExport = false)
    {
        XcodesResult help = await RunReadAsync("/usr/bin/xcodebuild", ["-help"], environment, cancellationToken)
            .ConfigureAwait(false);
        string architecture = ArchitectureArgument(runtime);
        string[] commands = [.. XcodesOutputParser.SplitLines(help.Output).Where(line =>
                line.Contains("-downloadPlatform", StringComparison.Ordinal)
                && line.Contains("-buildVersion", StringComparison.Ordinal)
                && line.Contains("-architectureVariant", StringComparison.Ordinal)
                && line.Contains(architecture, StringComparison.OrdinalIgnoreCase))];
        if (commands.Length == 0)
            throw new XcodesException("The selected Xcode cannot explicitly choose the runtime architecture.");
        if (requireExport && !commands.Any(line => line.Contains("-exportPath", StringComparison.Ordinal)))
            throw new XcodesException("The selected Xcode cannot export a simulator runtime with the requested build and architecture.");
    }

    private static async Task<byte[]> CopyImageAsync(
        string image, FileStream destination, Action<XcodesOutputLine>? output,
        CancellationToken cancellationToken)
    {
        await using FileStream source = XcodeSimulatorImageFile.OpenWithoutFollowingLinks(image);
        if (!source.CanSeek || source.Length <= 0)
            throw new XcodesException("Xcode exported an empty or unreadable simulator runtime disk image.");
        long length = source.Length;
        DateTime modified = File.GetLastWriteTimeUtc(image);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        output?.Invoke(new XcodesOutputLine("Copying runtime image: 0%"));
        int previousProgress = 0;
        long total = 0;
        byte[] buffer = new byte[CopyBufferSize];
        int count;
        while ((count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            hash.AppendData(buffer, 0, count);
            total += count;
            int progress = (int)Math.Min(100, total * 100d / length);
            if (progress != previousProgress)
            {
                output?.Invoke(new XcodesOutputLine($"Copying runtime image: {progress}%", ReplacesPrevious: true));
                previousProgress = progress;
            }
        }
        if (total != length || source.Length != length || File.GetLastWriteTimeUtc(image) != modified)
            throw new XcodesException("The exported simulator runtime disk image changed while it was being copied.");
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        return hash.GetHashAndReset();
    }

    private static Task<XcodesResult> RunSystemToolAsync(
        string executable, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string?> environment,
        bool stream, Action<XcodesOutputLine>? output, TimeSpan? timeout, CancellationToken cancellationToken)
        => stream
            ? XcodeSystemProcessRunner.RunAsync(executable, arguments, environment, output, cancellationToken)
            : new XcodesRunner(executable, environment)
                .RunAsync(arguments, timeout, cancellationToken);

    private static IReadOnlyDictionary<string, string?> EnvironmentFor(string? developerDirectory)
    {
        Dictionary<string, string?> environment = new()
        {
            ["XCODES_USERNAME"] = null,
            ["XCODES_PASSWORD"] = null,
        };
        if (developerDirectory is not null)
            environment["DEVELOPER_DIR"] = developerDirectory;
        return environment;
    }

    private static bool IsDeveloperDirectory(string directory)
        => Path.IsPathFullyQualified(directory) && !directory.Any(char.IsControl) && Directory.Exists(directory);

    private static string? PlatformArgument(string platform) => platform switch
    {
        "iOS" => "iOS",
        "watchOS" => "watchOS",
        "tvOS" => "tvOS",
        "visionOS" => "visionOS",
        _ => null,
    };

    private static void ValidateVersion(XcodeSimulatorRuntime runtime, XcodeVersion xcode)
    {
        if (xcode.Major < 16 || xcode.Major == 16 && xcode.Minor < 1)
            throw new XcodesException("Using this runtime requires Xcode 16.1 or later to be selected.");
        if (runtime.Architecture == XcodeArchitecture.AppleSilicon && xcode.Major < 26)
            throw new XcodesException("Using an Apple Silicon runtime requires Xcode 26 or later to be selected.");
    }

    private static bool IsReadyMatch(XcodeSimulatorRuntime requested, XcodeSimulatorRuntime installed)
        => installed.Origin is XcodeSimulatorRuntimeOrigin.Installed or XcodeSimulatorRuntimeOrigin.Bundled
           && string.Equals(installed.State, "Ready", StringComparison.OrdinalIgnoreCase)
           && installed.Platform == requested.Platform
           && string.Equals(installed.Build, requested.Build, StringComparison.OrdinalIgnoreCase)
           && installed.Architecture == requested.Architecture
           && XcodeVersion.TryParse(installed.Version, out XcodeVersion actual)
           && XcodeVersion.TryParse(requested.Version, out XcodeVersion expected)
           && actual.CompareTo(expected) == 0;

}
