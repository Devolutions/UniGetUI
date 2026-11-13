using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using UniGetUI.Core.Logging;

namespace UniGetUI.Extensions.Drivers;

public sealed record DriverInstallReport(IReadOnlyList<DriverInstallResult> Results, bool RebootRequired);

public sealed class DriverUpdateService(IDriverScriptRunner runner)
{
    public static bool IsSupported => OperatingSystem.IsWindows();

    public async Task<IReadOnlyList<DriverUpdate>> ScanAsync(CancellationToken cancellationToken = default)
    {
        EnsureSupported();
        ScriptOutput output = await runner.RunAsync(DriverScripts.Scan(), elevated: false, cancellationToken);
        ScanPayload payload = Parse(output, DriversJsonContext.Default.ScanPayload);
        ThrowIfFailed(payload.Error, payload.HResult);
        return [.. (payload.Updates ?? []).Where(update => update.Id.Length > 0).DistinctBy(update => update.Id)];
    }

    public async Task<DriverInstallReport> InstallAsync(IReadOnlyCollection<string> updateIds)
    {
        EnsureSupported();
        if (updateIds.Count == 0) return new([], false);
        ScriptOutput output = await runner.RunAsync(DriverScripts.Install(updateIds), elevated: true, CancellationToken.None);
        InstallPayload payload = Parse(output, DriversJsonContext.Default.InstallPayload);
        ThrowIfFailed(payload.Error, payload.HResult);
        return new(payload.Results ?? [], payload.RebootRequired || (payload.Results ?? []).Any(result => result.RebootRequired));
    }

    internal static T Parse<T>(ScriptOutput output, JsonTypeInfo<T> typeInfo)
    {
        string? line = output.StandardOutput
            .Split('\n')
            .Select(line => line.Trim())
            .LastOrDefault(line => line.StartsWith(DriverScripts.OutputMarker, StringComparison.Ordinal));
        if (line is null)
        {
            Logger.Error($"The driver script exited with code {output.ExitCode} without a result: {output.StandardOutput.Trim()} {output.StandardError.Trim()}");
            throw new DriversException(DriversErrorKind.UnreadableOutput, "Windows Update did not return a result.");
        }

        try
        {
            return JsonSerializer.Deserialize(line[DriverScripts.OutputMarker.Length..], typeInfo)
                ?? throw new DriversException(DriversErrorKind.UnreadableOutput, "Windows Update returned an empty result.");
        }
        catch (JsonException ex)
        {
            throw new DriversException(DriversErrorKind.UnreadableOutput, "Windows Update returned a result that could not be read.", 0, ex);
        }
    }

    private static void ThrowIfFailed(string? error, int hResult)
    {
        if (error is null) return;
        Logger.Error($"Windows Update failed with 0x{hResult:X8}: {error}");
        throw new DriversException(DriversException.Classify(hResult), error, hResult);
    }

    private static void EnsureSupported()
    {
        if (!IsSupported)
            throw new DriversException(DriversErrorKind.NotSupported, "Driver updates are only available on Windows.");
    }
}
